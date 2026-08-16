using AAXClean.FrameFilters;
using Mpeg4Lib.Boxes;
using Mpeg4Lib.Chunks;
using Mpeg4Lib.Util;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace AAXClean.Chunks;

public interface IChunkReader
{
	Task RunAsync(CancellationTokenSource cancellationSource);
	Action<ConversionProgressEventArgs>? OnProgressUpdateDelegate { get; set; }
	void AddTrack(TrakBox track, FrameFilterBase<FrameEntry> filter);
}

internal class ChunkReader : IChunkReader
{
	protected record TrackEntry(uint TrackId, uint Timescale, FrameFilterBase<FrameEntry> FirstFilter, TrakBox TrakBox,
		long DispatchStartSample, long DispatchEndSample);

	public Action<ConversionProgressEventArgs>? OnProgressUpdateDelegate { get; set; }
	protected Dictionary<uint, TrackEntry> TrackEntries { get; } = new();
	protected Stream InputStream { get; }
	protected TimeSpan StartTime { get; }
	protected TimeSpan EndTime { get; }

	public ChunkReader(Stream inputStream, TimeSpan startTime, TimeSpan endTime)
	{
		InputStream = inputStream;
		if (startTime >= endTime)
			throw new ArgumentException("Start time must be less than end time.", nameof(startTime));
		StartTime = startTime;
		EndTime = endTime;
	}

	protected virtual IEnumerable<ChunkEntry> EnumerateChunks()
	{
		return TrackEntries
			.Values
			.Select(e => e.TrakBox)
			.InterleaveBy(t => t.ChunkEntries(), t => t.ChunkOffset)
			.Where(ChunkHasFrameInRange);

		bool ChunkHasFrameInRange(ChunkEntry value)
		{
			var trackEntry = GetTrackEntryFromId(value.TrackId);
			return value.FirstSample <= trackEntry.DispatchEndSample
				&& (value.FirstSample + value.FrameDurations.Sum(d => d)) >= trackEntry.DispatchStartSample;
		}
	}

	protected TrackEntry GetTrackEntryFromId(uint trackId)
		=> TrackEntries.TryGetValue(trackId, out var trackEntry) ? trackEntry
		: throw new ArgumentOutOfRangeException(nameof(trackId), $"Track ID {trackId} is not present in this {nameof(ChunkReader)} instance.");

	public virtual void AddTrack(TrakBox track, FrameFilterBase<FrameEntry> filter)
	{
		uint timescale = track.Mdia.Mdhd.Timescale;
		long mediaOffset = track.Edts?.Elst?.SingleEdit?.MediaTime ?? 0;
		long requestedStart = Math.Max(0, checked((long)(StartTime.TotalSeconds * timescale) + mediaOffset));
		long start = requestedStart;

		if (track.Mdia.Hdlr.HandlerType == "soun" && requestedStart > 0)
		{
			if (track.Mdia.Minf.Stbl.Stss is not null)
				start = FindPrecedingSyncSampleStart(track, requestedStart);
			else if (TrackIsUsac(track))
				//Without stss, only the decrypted USAC access units reveal their real
				//independence frames. No maximum interval is guaranteed, so dispatch from
				//media start and let AacValidateFilter establish the usable sync run.
				start = 0;
		}

		long end = EndTime == TimeSpan.MaxValue
			? long.MaxValue
			: checked((long)(EndTime.TotalSeconds * timescale) + mediaOffset);

		var trackEntry = new TrackEntry(track.Tkhd.TrackID, timescale, filter, track, start, end);
		TrackEntries.Add(track.Tkhd.TrackID, trackEntry);
	}

	private static bool TrackIsUsac(TrakBox track)
		=> track.Mdia.Minf.Stbl.Stsd.AudioSampleEntry?
			.Esds?.ES_Descriptor.DecoderConfig.AudioSpecificConfig.AudioObjectType == 42;

	private static long FindPrecedingSyncSampleStart(TrakBox track, long requestedStart)
	{
		long? precedingStart = null;
		foreach (uint sampleNumber in track.Mdia.Minf.Stbl.Stss!.SampleNumbers)
		{
			long candidateStart = GetSampleStart(track.Mdia.Minf.Stbl.Stts, sampleNumber);
			if (candidateStart <= requestedStart
				&& (!precedingStart.HasValue || candidateStart > precedingStart.Value))
				precedingStart = candidateStart;
		}

		return precedingStart
			?? throw new InvalidDataException(
				$"The audio track has no sync sample at or before media sample {requestedStart}.");
	}

	private static long GetSampleStart(SttsBox stts, uint oneBasedSampleNumber)
	{
		if (oneBasedSampleNumber == 0)
			throw new InvalidDataException("An stss sample number must be one-based.");

		ulong remainingFrames = oneBasedSampleNumber - 1u;
		ulong start = 0;
		foreach (SttsBox.SampleEntry entry in stts.Samples)
		{
			if (remainingFrames < entry.FrameCount)
				return checked((long)(start + remainingFrames * entry.FrameDelta));

			start = checked(start + (ulong)entry.FrameCount * entry.FrameDelta);
			remainingFrames -= entry.FrameCount;
		}

		throw new InvalidDataException(
			$"stss sample {oneBasedSampleNumber} exceeds the track's stts sample count.");
	}

	public async Task RunAsync(CancellationTokenSource cancellationSource)
	{
		//All filters share the came cancellation source.
		foreach (var filter in TrackEntries.Values.Select(e => e.FirstFilter))
			filter.SetCancellationToken(cancellationSource.Token);

		OnInitialProgress();
		var token = cancellationSource.Token;
		ExceptionDispatchInfo? processingFailure = null;
		Exception? cleanupFailure = null;

		try
		{
			foreach (var c in EnumerateChunks())
			{
				Memory<byte> chunkData = new byte[c.ChunkSize];
				await InputStream.ReadNextChunkAsync(c.ChunkOffset, chunkData, token);
				await DispatchChunk(c, chunkData, token);
			}
		}
		catch (OperationCanceledException) { }
		catch (Exception ex)
		{
			cancellationSource.Cancel();
			processingFailure = ExceptionDispatchInfo.Capture(ex);
		}
		finally
		{
			OnFinalProgress();

			//Always call CompleteAsync() on all filters so that every
			//FilterLoop gets awaited and any exceptions are thrown.
			try
			{
				await Task.WhenAll(TrackEntries.Values.Select(e => e.FirstFilter.CompleteAsync()));
			}
			catch (Exception ex) when (processingFailure is not null)
			{
				cleanupFailure = ex;
			}
		}

		if (processingFailure is not null)
		{
			if (cleanupFailure is not null and not OperationCanceledException)
			{
				throw new AggregateException(
					"Audio processing and filter cleanup both failed.",
					processingFailure.SourceException,
					cleanupFailure);
			}

			processingFailure.Throw();
		}
	}

	protected virtual FrameEntry CreateFrameEntry(ChunkEntry chunk, int frameInChunk, uint frameDelta, long startSample, Memory<byte> frameData)
		=> new()
		{
			Chunk = chunk,
			SamplesInFrame = frameDelta,
			FrameData = frameData,
			IsSyncSample = chunk.SyncFlags?[frameInChunk],
			StartSample = startSample
		};

	private async Task DispatchChunk(ChunkEntry chunk, Memory<byte> chunkData, CancellationToken token)
	{
		long sampleIndex = chunk.FirstSample;

		var trackEntry = GetTrackEntryFromId(chunk.TrackId);

		long startSample = trackEntry.DispatchStartSample;
		long endSample = trackEntry.DispatchEndSample;
		uint frameDelta;

		for (int start = 0, f = 0; f < chunk.FrameSizes.Length; start += chunk.FrameSizes[f], f++, sampleIndex += frameDelta)
		{
			frameDelta = chunk.FrameDurations[f];

			if (startSample >= sampleIndex + frameDelta)
				continue;

			if (endSample < sampleIndex)
				break;

			OnProgressReport(sampleIndex, trackEntry.Timescale);

			var frameData = chunkData.Slice(start, chunk.FrameSizes[f]);
			var frameEntry = CreateFrameEntry(chunk, f, frameDelta, sampleIndex, frameData);
			token.ThrowIfCancellationRequested();
			await trackEntry.FirstFilter.AddInputAsync(frameEntry);
		}
	}

	private DateTime beginProcess;
	private DateTime nextUpdate;

	private void OnInitialProgress()
	{
		beginProcess = DateTime.UtcNow;
		nextUpdate = beginProcess;
		OnProgressUpdateDelegate?.Invoke(new ConversionProgressEventArgs(StartTime, EndTime, StartTime, 0));
	}

	private void OnFinalProgress()
	{
		OnProgressUpdateDelegate?.Invoke(new ConversionProgressEventArgs(StartTime, EndTime, EndTime, (EndTime - StartTime) / (DateTime.UtcNow - beginProcess)));
	}

	private void OnProgressReport(long sampleNumber, uint timeScale)
	{
		//Throttle update so it doesn't bog down UI
		if (DateTime.UtcNow > nextUpdate)
		{
			var trackPosition = TimeSpan.FromSeconds((double)sampleNumber / timeScale);
			double speed = (trackPosition - StartTime) / (DateTime.UtcNow - beginProcess);
			OnProgressUpdateDelegate?.Invoke(new ConversionProgressEventArgs(StartTime, EndTime, trackPosition, speed));
			nextUpdate = DateTime.UtcNow.AddMilliseconds(100);
		}
	}
}
