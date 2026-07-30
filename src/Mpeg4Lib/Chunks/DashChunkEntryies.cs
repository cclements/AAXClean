using Mpeg4Lib.Boxes;
using Mpeg4Lib.Util;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Mpeg4Lib.Chunks;

public class DashChunkEntryies : IEnumerable<ChunkEntry>
{
	private Stream InputStream { get; }
	private uint TrackId { get; }
	private MoofBox FirstMoof { get; }
	private MdatBox FirstMdat { get; }
	private SidxBox Sidx { get; }
	private long MinimumSample { get; }
	private long MaximumSample { get; }

	public DashChunkEntryies(Stream inputStream, uint trakId, SidxBox sidx, MoofBox firstMoof, MdatBox firstMdat, long minimumSample, long maximumSample)
	{
		InputStream = inputStream;
		TrackId = trakId;
		Sidx = sidx;
		FirstMoof = firstMoof;
		FirstMdat = firstMdat;
		MinimumSample = minimumSample;
		MaximumSample = maximumSample;
	}

	public IEnumerator<ChunkEntry> GetEnumerator()
		=> EnumerateChunks().GetEnumerator();

	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

	private IEnumerable<ChunkEntry> EnumerateChunks()
	{
		SkipToFirstMoof(out var moofBox, out var mdatBox, out var startSample);

		var totalDataSize = Sidx.Segments.Sum(s => (long)s.ReferenceSize);
		var endOfFile = FirstMoof.Header.FilePosition + totalDataSize;

		while (InputStream.Position < endOfFile)
		{
			if (startSample > MaximumSample)
				yield break; //No more samples in range

			var trackChunk = ValidateMdatSize(moofBox, mdatBox, startSample);
			startSample += trackChunk.FrameDurations.Sum(d => d);
			if (startSample > MinimumSample)
			{
				yield return trackChunk;
			}
			else
			{
				//Skip over mdat to next moof
				var endOfMdat = InputStream.Position + mdatBox.Header.TotalBoxSize - mdatBox.Header.HeaderSize;
				InputStream.SeekToOffset(endOfMdat);
			}

			if (InputStream.Position < endOfFile)
			{
				moofBox = BoxFactory.CreateBox<MoofBox>(InputStream, parent: null);
				mdatBox = BoxFactory.CreateBox<MdatBox>(InputStream, parent: null);
			}
		}
	}

	private ChunkEntry ValidateMdatSize(MoofBox moofBox, MdatBox mdatBox, long startSample)
	{
		if (moofBox.Traf.Trun is not TrunBox trun)
			throw new InvalidDataException($"The {nameof(TrafBox)} doesn't contain a {nameof(TrunBox)}");

		var frameSizes
			= trun.sample_size_present ? trun.Samples.Select(s => s.SampleSize).OfType<int>().ToArray()
			: moofBox.Traf.Tfhd.DefaultSampleSize is uint sampleSize ? Enumerable.Repeat((int)sampleSize, trun.Samples.Length).ToArray()
			: throw new InvalidOperationException("Trun sample infos don't contain sample sizes and no default sample size is set.");

		var mdatSize = mdatBox.Header.TotalBoxSize - mdatBox.Header.HeaderSize;
		if (frameSizes.Sum() != mdatSize)
			throw new InvalidDataException("Mdat box size doesn't match sample sizes in track fragment");

		if (mdatSize > int.MaxValue)
			throw new InvalidDataException("Mdat is larger than Int32.MaxValue");

		var frameDurations
			= trun.sample_duration_present ? trun.Samples.Select(s => s.SampleDuration).OfType<uint>().ToArray()
			: moofBox.Traf.Tfhd.DefaultSampleDuration is uint sampleDuration ? Enumerable.Repeat(sampleDuration, trun.Samples.Length).ToArray()
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
			SyncFlags = GetSyncFlags(moofBox.Traf.Tfhd, trun)
		};
	}

	private static bool[]? GetSyncFlags(TfhdBox tfhd, TrunBox trun)
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

		if (trun.HasFirstSampleFlags)
		{
			//Per § 8.8.8, first-sample-flags overrides the default flags for the first
			//sample only; the remaining samples use the fragment default, or are treated
			//as non-sync when no default is present.
			var syncFlags = new bool[trun.Samples.Length];
			syncFlags[0] = (trun.FirstSampleFlags & SampleIsNonSyncSample) == 0;
			if (tfhd.DefaultSampleFlags is uint restFlags && (restFlags & SampleIsNonSyncSample) == 0)
			{
				for (int i = 1; i < syncFlags.Length; i++)
					syncFlags[i] = true;
			}
			return syncFlags;
		}

		if (tfhd.DefaultSampleFlags is uint defaultFlags)
		{
			bool sync = (defaultFlags & SampleIsNonSyncSample) == 0;
			var syncFlags = new bool[trun.Samples.Length];
			for (int i = 0; i < syncFlags.Length; i++)
				syncFlags[i] = sync;
			return syncFlags;
		}

		//No sample flags are present anywhere in the fragment, so sync status is unknown.
		//TODO: add Track Extends Box (trex) support to get default sample flags from the
		//track definition.
		return null;
	}

	private void SkipToFirstMoof(out MoofBox firstMoof, out MdatBox firstMdat, out long firstSample)
	{
		long startPosition = FirstMoof.Header.FilePosition;
		long dataOffset = 0;
		firstSample = 0;

		if (Sidx.Segments.Any(s => s.ReferenceType || !s.StartsWithSAP || s.SapType != 1 || s.SapDeltaTime != 0))
			throw new InvalidOperationException($"AAXClean doesn't know how to inrepret segment index boxes other than " +
				$"{nameof(SidxBox.Segment.SapType)} = 1, " +
				$"{nameof(SidxBox.Segment.SapDeltaTime)} = 0, " +
				$"{nameof(SidxBox.Segment.StartsWithSAP)} = 1, " +
				$"{nameof(SidxBox.Segment.ReferenceType)} = 0");

		foreach (var segment in Sidx.Segments)
		{
			if (MinimumSample < firstSample + segment.SubsegmentDuration)
				break;

			dataOffset += segment.ReferenceSize;
			firstSample += segment.SubsegmentDuration;
		}

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
	}
}
