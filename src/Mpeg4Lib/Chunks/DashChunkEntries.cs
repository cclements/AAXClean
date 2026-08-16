using Mpeg4Lib.Boxes;
using Mpeg4Lib.Util;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Mpeg4Lib.Chunks;

public class DashChunkEntries : IEnumerable<ChunkEntry>
{
	private Stream InputStream { get; }
	private uint TrackId { get; }
	private MoofBox FirstMoof { get; }
	private MdatBox FirstMdat { get; }
	private SidxBox Sidx { get; }
	private long MinimumSample { get; }
	private long MaximumSample { get; }
	private TrexBox? TrackExtends { get; }
	private uint MediaTimescale { get; }
	private bool RejectCencSampleGroups { get; }

	public DashChunkEntries(Stream inputStream, uint trakId, SidxBox sidx, MoofBox firstMoof, MdatBox firstMdat, long minimumSample, long maximumSample)
		: this(inputStream, trakId, sidx, firstMoof, firstMdat, minimumSample, maximumSample, trackExtends: null, checked((uint)sidx.Timescale))
	{
	}

	public DashChunkEntries(
		Stream inputStream,
		uint trakId,
		SidxBox sidx,
		MoofBox firstMoof,
		MdatBox firstMdat,
		long minimumSample,
		long maximumSample,
		TrexBox? trackExtends,
		uint mediaTimescale)
		: this(
			inputStream,
			trakId,
			sidx,
			firstMoof,
			firstMdat,
			minimumSample,
			maximumSample,
			trackExtends,
			mediaTimescale,
			rejectCencSampleGroups: false)
	{
	}

	public DashChunkEntries(
		Stream inputStream,
		uint trakId,
		SidxBox sidx,
		MoofBox firstMoof,
		MdatBox firstMdat,
		long minimumSample,
		long maximumSample,
		TrexBox? trackExtends,
		uint mediaTimescale,
		bool rejectCencSampleGroups)
	{
		InputStream = inputStream;
		TrackId = trakId;
		Sidx = sidx;
		FirstMoof = firstMoof;
		FirstMdat = firstMdat;
		MinimumSample = minimumSample;
		MaximumSample = maximumSample;
		TrackExtends = trackExtends;
		MediaTimescale = mediaTimescale;
		RejectCencSampleGroups = rejectCencSampleGroups;
	}

	public IEnumerator<ChunkEntry> GetEnumerator()
		=> EnumerateChunks().GetEnumerator();

	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

	private IEnumerable<ChunkEntry> EnumerateChunks()
	{
		if (!TrySkipToFirstMoof(out var moofBox, out var mdatBox, out var segmentIndex))
			yield break;

		long totalDataSize = Sidx.Segments.Aggregate(
			0L,
			(sum, segment) => checked(sum + segment.ReferenceSize));
		long endOfFile = checked(FirstMoof.Header.FilePosition + totalDataSize);
		long segmentStartPosition = FirstMoof.Header.FilePosition;
		for (int i = 0; i < segmentIndex; i++)
			segmentStartPosition = checked(segmentStartPosition + Sidx.Segments[i].ReferenceSize);
		long segmentEndPosition = checked(
			segmentStartPosition + Sidx.Segments[segmentIndex].ReferenceSize);
		long previousFragmentEnd = 0;
		bool hasPreviousFragment = false;

		while (InputStream.Position < endOfFile)
		{
			bool beginsSegment = moofBox.Header.FilePosition == segmentStartPosition;
			while (moofBox.Header.FilePosition >= segmentEndPosition)
			{
				if (moofBox.Header.FilePosition > segmentEndPosition)
				{
					throw new InvalidDataException(
						$"A media fragment starts beyond its indexed {nameof(SidxBox)} subsegment boundary.");
				}

				if (++segmentIndex >= Sidx.Segments.Length)
					throw new InvalidDataException($"There are more media fragments than references in the {nameof(SidxBox)}.");

				segmentStartPosition = segmentEndPosition;
				segmentEndPosition = checked(
					segmentStartPosition + Sidx.Segments[segmentIndex].ReferenceSize);
				beginsSegment = true;
			}

			long startSample = moofBox.Traf.Tfdt is { } tfdt
				? ValidateDecodeTime(tfdt.BaseMediaDecodeTime)
				: beginsSegment || !hasPreviousFragment
					? GetSegmentStartSample(segmentIndex)
					: previousFragmentEnd;
			if (startSample > MaximumSample)
				yield break; //No more samples in range

			var trackChunk = ValidateMdatSize(
				moofBox,
				mdatBox,
				startSample,
				Sidx.Segments[segmentIndex],
				beginsSegment);
			long fragmentBoxEnd = checked(mdatBox.Header.FilePosition + mdatBox.Header.TotalBoxSize);
			if (fragmentBoxEnd > segmentEndPosition)
			{
				throw new InvalidDataException(
					$"A media fragment extends beyond its indexed {nameof(SidxBox)} subsegment boundary.");
			}

			long fragmentEnd = trackChunk.FrameDurations.Aggregate(
				startSample,
				(sum, duration) => checked(sum + duration));
			//MinimumSample selected a SIDX subsegment whose first sample is a validated
			//SAP. Yield every fragment in that selected subsegment so a later requested
			//position cannot discard the SAP or the dependency run that follows it.
			yield return trackChunk;
			previousFragmentEnd = fragmentEnd;
			hasPreviousFragment = true;

			if (InputStream.Position < endOfFile)
			{
				moofBox = BoxFactory.CreateBox<MoofBox>(InputStream, parent: null);
				mdatBox = BoxFactory.CreateBox<MdatBox>(InputStream, parent: null);
			}
		}
	}

	private ChunkEntry ValidateMdatSize(
		MoofBox moofBox,
		MdatBox mdatBox,
		long startSample,
		SidxBox.Segment segment,
		bool beginsSegment)
	{
		if (RejectCencSampleGroups && SampleGroups.ContainsCencSampleGroup(moofBox))
			throw new NotSupportedException(
				"CENC sample-group protection overrides (seig) are not supported.");

		if (moofBox.Traf.Trun is not TrunBox trun)
			throw new InvalidDataException($"The {nameof(TrafBox)} doesn't contain a {nameof(TrunBox)}");

		var frameSizes
			= trun.sample_size_present ? trun.Samples.Select(s => s.SampleSize).OfType<int>().ToArray()
			: moofBox.Traf.Tfhd.DefaultSampleSize is uint sampleSize ? Enumerable.Repeat((int)sampleSize, trun.Samples.Length).ToArray()
			: TrackExtends is not null ? Enumerable.Repeat(checked((int)TrackExtends.DefaultSampleSize), trun.Samples.Length).ToArray()
			: throw new InvalidOperationException("Trun sample infos don't contain sample sizes and no default sample size is set.");

		var mdatSize = mdatBox.Header.TotalBoxSize - mdatBox.Header.HeaderSize;
		if (frameSizes.Sum() != mdatSize)
			throw new InvalidDataException("Mdat box size doesn't match sample sizes in track fragment");

		if (mdatSize > int.MaxValue)
			throw new InvalidDataException("Mdat is larger than Int32.MaxValue");

		var frameDurations
			= trun.sample_duration_present ? trun.Samples.Select(s => s.SampleDuration).OfType<uint>().ToArray()
			: moofBox.Traf.Tfhd.DefaultSampleDuration is uint sampleDuration ? Enumerable.Repeat(sampleDuration, trun.Samples.Length).ToArray()
			: TrackExtends is not null ? Enumerable.Repeat(TrackExtends.DefaultSampleDuration, trun.Samples.Length).ToArray()
			: throw new InvalidOperationException("Trun sample infos don't contain sample durations and no default sample duration is set.");

		if (frameDurations.Length != frameSizes.Length)
			throw new InvalidDataException($"The number of frame sizes ({frameSizes.Length}) does not match the number of durations ({frameDurations.Length}) in fragment {moofBox.Mfhd.SequenceNumber}");

		object? extraData = null;

		if (moofBox.Traf.Senc is { } senc)
		{
			extraData = frameSizes.Length == senc.IVs.Length ? senc.IVs
				: throw new InvalidDataException($"The number of IVs ({senc.IVs.Length}) does not match the number of samples ({frameSizes.Length}) in fragment {moofBox.Mfhd.SequenceNumber}");
		}

		return new ChunkEntry
		{
			TrackId = TrackId,
			ChunkIndex = (uint)moofBox.Mfhd.SequenceNumber,
			ChunkOffset = InputStream.Position,
			ChunkSize = (int)mdatSize,
			FirstSample = startSample,
			FrameSizes = frameSizes,
			FrameDurations = frameDurations,
			ExtraData = extraData,
			SyncFlags = GetSyncFlags(moofBox.Traf.Tfhd, trun, TrackExtends, segment, beginsSegment)
		};
	}

	private static bool[]? GetSyncFlags(
		TfhdBox tfhd,
		TrunBox trun,
		TrexBox? trackExtends,
		SidxBox.Segment segment,
		bool beginsSegment)
	{
		//ISO/IEC 14496-12 § 8.8.3.1 sample flags: bit 16 is sample_is_non_sync_sample.
		const uint SampleIsNonSyncSample = 0x00010000;

		if (trun.sample_flags_present)
		{
			//Per-sample flags override any defaults, and per § 8.8.8 first-sample-flags
			//shall not be present alongside them.
			var syncFlags = new bool[trun.Samples.Length];
			for (int i = 0; i < syncFlags.Length; i++)
			{
				uint sampleFlags = trun.Samples[i].SampleFlags
					?? throw new InvalidDataException($"The {nameof(TrunBox)} sample info at index {i} doesn't contain sample flags.");

				syncFlags[i] = (sampleFlags & SampleIsNonSyncSample) == 0;
			}
			return syncFlags;
		}

		uint? defaultFlags = tfhd.DefaultSampleFlags ?? trackExtends?.DefaultSampleFlags;

		if (trun.HasFirstSampleFlags)
		{
			//Per § 8.8.8, first-sample-flags overrides the default flags for the first
			//sample only; the remaining samples use the fragment default, or are treated
			//as non-sync when no default is present.
			var syncFlags = new bool[trun.Samples.Length];
			if (syncFlags.Length > 0)
				syncFlags[0] = (trun.FirstSampleFlags & SampleIsNonSyncSample) == 0;
			if (defaultFlags is uint restFlags && (restFlags & SampleIsNonSyncSample) == 0)
			{
				for (int i = 1; i < syncFlags.Length; i++)
					syncFlags[i] = true;
			}
			return syncFlags;
		}

		if (defaultFlags is uint allSampleFlags)
		{
			bool sync = (allSampleFlags & SampleIsNonSyncSample) == 0;
			var syncFlags = new bool[trun.Samples.Length];
			for (int i = 0; i < syncFlags.Length; i++)
				syncFlags[i] = sync;
			return syncFlags;
		}

		//A SAP type 1 at delta zero proves that the referenced subsegment begins with an
		//independently decodable sample. It says nothing about the remaining samples, so
		//keep those conservative instead of treating the whole fragment as sync.
		if (beginsSegment && segment.StartsWithSAP && segment.SapType == 1 && segment.SapDeltaTime == 0)
		{
			var syncFlags = new bool[trun.Samples.Length];
			if (syncFlags.Length > 0)
				syncFlags[0] = true;
			return syncFlags;
		}

		//No sample flags or segment-level sync evidence are present.
		return null;
	}

	private bool TrySkipToFirstMoof(out MoofBox firstMoof, out MdatBox firstMdat, out int segmentIndex)
	{
		long startPosition = FirstMoof.Header.FilePosition;
		long dataOffset = 0;
		firstMoof = FirstMoof;
		firstMdat = FirstMdat;
		segmentIndex = 0;

		if (Sidx.Timescale <= 0)
			throw new InvalidDataException($"The {nameof(SidxBox)} timescale must be positive.");
		if (MediaTimescale == 0)
			throw new InvalidDataException("The media timescale must be positive.");
		if (Sidx.EarliestPresentationTime < 0)
			throw new InvalidDataException($"The {nameof(SidxBox)} earliest presentation time is outside the supported range.");

		if (Sidx.Segments.Any(s => s.ReferenceType || !s.StartsWithSAP || s.SapType != 1 || s.SapDeltaTime != 0))
			throw new InvalidOperationException($"AAXClean doesn't know how to inrepret segment index boxes other than " +
				$"{nameof(SidxBox.Segment.SapType)} = 1, " +
				$"{nameof(SidxBox.Segment.SapDeltaTime)} = 0, " +
				$"{nameof(SidxBox.Segment.StartsWithSAP)} = 1, " +
				$"{nameof(SidxBox.Segment.ReferenceType)} = 0");

		BigInteger segmentStart = Sidx.EarliestPresentationTime;
		for (; segmentIndex < Sidx.Segments.Length; segmentIndex++)
		{
			var segment = Sidx.Segments[segmentIndex];
			BigInteger segmentEnd = segmentStart + segment.SubsegmentDuration;
			if ((BigInteger)MinimumSample * Sidx.Timescale < segmentEnd * MediaTimescale)
				break;

			dataOffset = checked(dataOffset + segment.ReferenceSize);
			segmentStart = segmentEnd;
		}

		if (segmentIndex == Sidx.Segments.Length)
			return false;

		if (dataOffset == 0)
		{
			(firstMoof, firstMdat) = (FirstMoof, FirstMdat);
		}
		else
		{
			InputStream.SeekToOffset(startPosition + dataOffset);
			firstMoof = BoxFactory.CreateBox<MoofBox>(InputStream, parent: null);
			firstMdat = BoxFactory.CreateBox<MdatBox>(InputStream, parent: null);
		}

		return true;
	}

	private long GetSegmentStartSample(int segmentIndex)
	{
		BigInteger segmentStart = Sidx.EarliestPresentationTime;
		for (int i = 0; i < segmentIndex; i++)
			segmentStart += Sidx.Segments[i].SubsegmentDuration;

		return ScaleTimestampExactly(
			segmentStart,
			checked((uint)Sidx.Timescale),
			MediaTimescale);
	}

	private static long ValidateDecodeTime(long decodeTime)
		=> decodeTime >= 0
		? decodeTime
		: throw new InvalidDataException($"The {nameof(TfdtBox)} base media decode time is outside the supported range.");

	private static long ScaleTimestampExactly(BigInteger value, uint sourceTimescale, uint destinationTimescale)
	{
		BigInteger scaled = value * destinationTimescale;
		BigInteger result = BigInteger.DivRem(scaled, sourceTimescale, out BigInteger remainder);
		if (!remainder.IsZero)
			throw new NotSupportedException("A fragment without tfdt has a SIDX start time that cannot be represented exactly in the media timescale.");
		return checked((long)result);
	}

}
