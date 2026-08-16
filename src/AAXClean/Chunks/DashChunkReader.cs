using AAXClean.FrameFilters;
using Mpeg4Lib.Boxes;
using Mpeg4Lib.Chunks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AAXClean.Chunks;

internal class DashChunkReader : ChunkReader
{
	private DashFile Dash { get; }
	private readonly Dictionary<uint, long> requestedStarts = new();

	public DashChunkReader(DashFile dash, Stream inputStream, TimeSpan startTime, TimeSpan endTime)
		: base(inputStream, startTime, endTime)
	{
		ArgumentNullException.ThrowIfNull(dash, nameof(dash));
		ArgumentNullException.ThrowIfNull(inputStream, nameof(inputStream));
		Dash = dash;
	}

	protected override FrameEntry CreateFrameEntry(ChunkEntry chunk, int frameInChunk, uint frameDelta, long startSample, Memory<byte> frameData)
	{
		var entry = base.CreateFrameEntry(chunk, frameInChunk, frameDelta, startSample, frameData);
		if (chunk.ExtraData is byte[][] IVs)
		{
			entry.ExtraData = IVs.Length > frameInChunk ? IVs[frameInChunk]
			: throw new InvalidDataException($"There are only {IVs.Length} in the chunk, but caller requesting frame at index {frameInChunk}.");
		}
		return entry;
	}

	public override void AddTrack(TrakBox track, FrameFilterBase<FrameEntry> filter)
	{
		if (TrackEntries.Count > 0)
			throw new InvalidOperationException($"The {nameof(DashChunkReader)} currently only supports a single track.");
		base.AddTrack(track, filter);

		//Every accepted SIDX reference begins with SAP type 1 at delta zero. Keep the
		//requested presentation position solely for indexed segment selection; the generic
		//USAC dispatch start remains at media start so every frame from the selected SAP is
		//delivered until AacValidateFilter establishes the exact bitstream sync run.
		uint timescale = track.Mdia.Mdhd.Timescale;
		long mediaOffset = track.Edts?.Elst?.SingleEdit?.MediaTime ?? 0;
		requestedStarts[track.Tkhd.TrackID] = Math.Max(
			0,
			checked((long)(StartTime.TotalSeconds * timescale) + mediaOffset));
	}

	protected override IEnumerable<ChunkEntry> EnumerateChunks()
	{
		//Currently support only a single DASH track
		var singleTrack = TrackEntries.Values.Single();

		bool needsBitstreamSyncDiscovery
			= singleTrack.TrakBox.Mdia.Minf.Stbl.Stss is null && Dash.AudioTrackIsUsac;
		long minimumSample = needsBitstreamSyncDiscovery
			? requestedStarts[singleTrack.TrackId]
			: singleTrack.DispatchStartSample;
		long maximumSample = singleTrack.DispatchEndSample;
		TrexBox trackExtends = Dash.Moov
			.GetChildOrThrow<MvexBox>()
			.GetTrackExtends(singleTrack.TrackId);

		return new DashChunkEntries(
			InputStream,
			singleTrack.TrackId,
			Dash.Sidx,
			Dash.FirstMoof,
			Dash.FirstMdat,
			minimumSample,
			maximumSample,
			trackExtends,
			singleTrack.Timescale,
			rejectCencSampleGroups: Dash.Tenc is not null);
	}
}
