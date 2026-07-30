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
	/// Whether this frame is a sync sample (independently decodable entry point), or null
	/// when unknown. Seeded from the source's sync metadata by the chunk readers; for codecs
	/// whose bitstream carries the truth directly (USAC), corrected by the audio filters once
	/// the decrypted frame data is available.
	/// </summary>
	public bool? IsSyncSample { get; set; }
}
