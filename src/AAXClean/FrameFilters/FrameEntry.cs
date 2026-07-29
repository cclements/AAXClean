using Mpeg4Lib.Chunks;
using System;

namespace AAXClean.FrameFilters;

public class FrameEntry
{
	public ChunkEntry? Chunk { get; init; }
	public required uint SamplesInFrame { get; init; }
	public required Memory<byte> FrameData { get; init; }
	public object? ExtraData { get; set; }
	/// <summary>
	/// Whether this frame is a sync sample (independently decodable entry point) per the
	/// source's sync information, or null when the source provides none.
	/// </summary>
	public bool? IsSyncSample { get; init; }
}
