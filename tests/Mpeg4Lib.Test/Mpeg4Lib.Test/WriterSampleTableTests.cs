using AAXClean;
using AAXClean.FrameFilters.Audio;
using Mpeg4Lib.Boxes;
using Mpeg4Lib.Chunks;

namespace Mpeg4Lib.Test;

[TestClass]
public class WriterSampleTableTests
{
	[TestMethod]
	public void WrittenWideSamples_KeepSizesPayloadAndChunkBoundariesAfterReopen()
	{
		using Mp4File source = MakeSource();
		using MemoryStream output = new();
		using Mp4aWriter writer = new(output, source.Ftyp, source.Moov);
		byte[] first = Enumerable.Repeat((byte)0x35, 65535).ToArray();
		byte[] second = Enumerable.Repeat((byte)0x36, 65536).ToArray();
		writer.AddFrame(first, true, 1000, true);
		writer.AddFrame(second, true, 1000, true);
		writer.Close();

		using Mp4File reopened = new(new MemoryStream(output.ToArray()));
		ChunkEntry[] chunks = new ChunkEntryList(reopened.Moov.AudioTrack).ToArray();
		Assert.HasCount(2, chunks);
		CollectionAssert.AreEqual(new[] { 65535 }, chunks[0].FrameSizes);
		CollectionAssert.AreEqual(new[] { 65536 }, chunks[1].FrameSizes);
		Assert.AreEqual(chunks[0].ChunkOffset + 65535, chunks[1].ChunkOffset);
		reopened.InputStream.Position = chunks[0].ChunkOffset;
		byte[] actual = new byte[first.Length + second.Length];
		reopened.InputStream.ReadExactly(actual);
		CollectionAssert.AreEqual(first.Concat(second).ToArray(), actual);
	}

	[TestMethod]
	public void LongSampleDeltas_KeepFullWidthInBitrateAndIncludeFinalWindow()
	{
		using Mp4File source = MakeSource();
		using MemoryStream output = new();
		using Mp4aWriter writer = new(output, source.Ftyp, source.Moov);
		writer.AddFrame(new byte[1000], true, 65536, true);
		writer.AddFrame(new byte[1000], true, 65536, true);
		writer.Close();

		using Mp4File reopened = new(new MemoryStream(output.ToArray()));
		BtrtBox bitrate = reopened.Moov.AudioTrack.Mdia.Minf.Stbl.Stsd.AudioSampleEntry!.GetChild<BtrtBox>()!;
		CollectionAssert.AreEqual(new uint[] { 65536, 65536 }, reopened.Moov.AudioTrack.Mdia.Minf.Stbl.Stts.EnumerateFrameDeltas().ToArray());
		Assert.AreEqual(122u, bitrate.AvgBitrate);
		Assert.AreEqual(122u, bitrate.MaxBitrate);
	}

	[TestMethod]
	public void ShortOutput_HasMeasuredPeakInsteadOfZero()
	{
		using Mp4File source = MakeSource();
		using MemoryStream output = new();
		using Mp4aWriter writer = new(output, source.Ftyp, source.Moov);
		writer.AddFrame(new byte[100], true, 500, true);
		writer.Close();
		using Mp4File reopened = new(new MemoryStream(output.ToArray()));
		BtrtBox bitrate = reopened.Moov.AudioTrack.Mdia.Minf.Stbl.Stsd.AudioSampleEntry!.GetChild<BtrtBox>()!;
		Assert.AreEqual(1600u, bitrate.AvgBitrate);
		Assert.AreEqual(1600u, bitrate.MaxBitrate);
	}

	private static Mp4File MakeSource() => new(new MemoryStream(PresentationWindowContractTests.CreateAc4Source(1000, 1000, 1000, [1])));
}
