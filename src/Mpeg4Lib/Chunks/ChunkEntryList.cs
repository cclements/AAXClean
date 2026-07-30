using Mpeg4Lib.Boxes;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Mpeg4Lib.Chunks;

/// <summary>
/// A readonly list of <see cref="ChunkEntry"/> from a <see cref="TrakBox"/>
/// </summary>
public class ChunkEntryList : IReadOnlyCollection<ChunkEntry>
{
	private readonly ChunkOffsetList ChunkOffsets;
	private readonly IStszBox Stsz;
	private readonly SttsBox Stts;
	private readonly ChunkFrames[] ChunkFrameTable;
	private readonly uint TrackId;
	private readonly HashSet<uint>? SyncSampleNumbers;
	public int Count { get; }

	public ChunkEntryList(TrakBox track)
	{
		TrackId = track.Tkhd.TrackID;
		Stsz = track.Mdia.Minf.Stbl.Stsz ?? throw new ArgumentNullException(nameof(track));
		var coBox = track.Mdia.Minf.Stbl.COBox;
		ArgumentOutOfRangeException.ThrowIfGreaterThan(coBox.EntryCount, (uint)int.MaxValue, "COBox.EntryCount");
		ChunkOffsets = coBox.ChunkOffsets;
		Count = (int)coBox.EntryCount;
		Stts = track.Mdia.Minf.Stbl.Stts;
		ChunkFrameTable = track.Mdia.Minf.Stbl.Stsc.CalculateChunkFrameTable(coBox.EntryCount);
		//A present stss identifies the track's sync samples; absent means every sample is sync
		//(ISO/IEC 14496-12), which callers see as SyncFlags = null (no explicit information).
		SyncSampleNumbers = track.Mdia.Minf.Stbl.Stss?.SampleNumbers.ToHashSet();
	}

	public IEnumerator<ChunkEntry> GetEnumerator()
		=> EnumerateChunks().GetEnumerator();

	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

	private IEnumerable<ChunkEntry> EnumerateChunks()
	{
		long startSample = 0;
		for (int chunkIndex = 0; chunkIndex < Count; chunkIndex++)
		{
			long chunkOffset = ChunkOffsets.GetOffsetAtIndex(chunkIndex);
			var chunkFrames = ChunkFrameTable[chunkIndex];

			(int[] frameSizes, int totalChunkSize) = Stsz.GetFrameSizes(chunkFrames.FirstFrameIndex, chunkFrames.NumberOfFrames);

			var frameDurations = Stts.EnumerateFrameDeltas(chunkFrames.FirstFrameIndex).Take(frameSizes.Length).ToArray();

			bool[]? syncFlags = null;
			if (SyncSampleNumbers is not null)
			{
				syncFlags = new bool[frameSizes.Length];
				for (int i = 0; i < syncFlags.Length; i++)
					syncFlags[i] = SyncSampleNumbers.Contains((uint)(chunkFrames.FirstFrameIndex + i + 1));
			}

			var entry = new ChunkEntry
			{
				TrackId = TrackId,
				FrameSizes = frameSizes,
				ChunkIndex = (uint)chunkIndex,
				ChunkSize = totalChunkSize,
				ChunkOffset = chunkOffset,
				FirstSample = startSample,
				FrameDurations = frameDurations,
				SyncFlags = syncFlags
			};

			startSample += entry.FrameDurations.Sum(d => d);
			yield return entry;
		}
	}
}
