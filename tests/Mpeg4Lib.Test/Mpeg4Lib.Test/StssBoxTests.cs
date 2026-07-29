using Mpeg4Lib.Boxes;
using Mpeg4Lib.Chunks;

namespace Mpeg4Lib.Test;

[TestClass]
public class StssBoxTests
{
	//Compose a big-endian ISO-BMFF box: [size][type][payload]
	private static byte[] MakeBox(string type, params byte[][] payloads)
	{
		int payloadSize = payloads.Sum(p => p.Length);
		using var ms = new MemoryStream();
		WriteUInt32BE(ms, (uint)(8 + payloadSize));
		ms.Write(System.Text.Encoding.ASCII.GetBytes(type));
		foreach (var p in payloads)
			ms.Write(p);
		return ms.ToArray();
	}

	private static byte[] UInt32sBE(params uint[] values)
	{
		using var ms = new MemoryStream();
		foreach (var v in values)
			WriteUInt32BE(ms, v);
		return ms.ToArray();
	}

	private static void WriteUInt32BE(Stream s, uint value)
	{
		Span<byte> b = [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
		s.Write(b);
	}

	private static byte[] StssBytes(params uint[] sampleNumbers)
		=> MakeBox("stss", UInt32sBE(0 /*version+flags*/, (uint)sampleNumbers.Length), UInt32sBE(sampleNumbers));

	[TestMethod]
	public void Parse_KnownBytes_RoundTripsByteForByte()
	{
		byte[] source = StssBytes(1, 40, 41, 84428);

		var stss = BoxFactory.CreateBox<StssBox>(new MemoryStream(source), parent: null);

		CollectionAssert.AreEqual(new uint[] { 1, 40, 41, 84428 }, stss.SampleNumbers);
		Assert.AreEqual(source.Length, stss.RenderSize);

		using var rendered = new MemoryStream();
		stss.Save(rendered);
		CollectionAssert.AreEqual(source, rendered.ToArray());
	}

	[TestMethod]
	public void Parse_EmptyBox_RoundTrips()
	{
		byte[] source = StssBytes();

		var stss = BoxFactory.CreateBox<StssBox>(new MemoryStream(source), parent: null);

		Assert.IsEmpty(stss.SampleNumbers);

		using var rendered = new MemoryStream();
		stss.Save(rendered);
		CollectionAssert.AreEqual(source, rendered.ToArray());
	}

	[TestMethod]
	public void CreateBlank_AddsToParentAndRoundTrips()
	{
		var moov = BoxFactory.CreateBox<MoovBox>(new MemoryStream(MakeBox("moov")), parent: null);

		var created = StssBox.CreateBlank(moov);
		created.SampleNumbers.AddRange([1u, 2u, 41u, 42u]);

		Assert.AreSame(created, moov.GetChild<StssBox>());

		using var rendered = new MemoryStream();
		moov.Save(rendered);
		rendered.Position = 0;

		var reparsed = BoxFactory.CreateBox<MoovBox>(rendered, parent: null);
		CollectionAssert.AreEqual(new uint[] { 1, 2, 41, 42 }, reparsed.GetChild<StssBox>()!.SampleNumbers);
	}

	//A minimal audio trak: 5 samples of 100 bytes in two chunks (3 + 2), 1024-tick frames.
	private static TrakBox MakeTrak(byte[]? stss)
	{
		byte[] tkhd = MakeBox("tkhd",
			UInt32sBE(0, 0, 0),          //version+flags, creation, modification
			UInt32sBE(1, 0),             //track ID, reserved
			UInt32sBE(5 * 1024),         //duration
			UInt32sBE(0, 0),             //reserved2
			UInt32sBE(0, 0),             //layer+altGroup, volume+reserved3
			new byte[4 * 9],             //matrix
			UInt32sBE(0, 0));            //width, height

		byte[] mdhd = MakeBox("mdhd",
			UInt32sBE(0, 0, 0),          //version+flags, creation, modification
			UInt32sBE(44100, 5 * 1024),  //timescale, duration
			UInt32sBE(0));               //language + pre_defined

		byte[] stts = MakeBox("stts", UInt32sBE(0, 1, 5, 1024));
		byte[] stsc = MakeBox("stsc", UInt32sBE(0, 2, 1, 3, 1, 2, 2, 1));
		byte[] stsz = MakeBox("stsz", UInt32sBE(0, 0, 5, 100, 100, 100, 100, 100));
		byte[] stco = MakeBox("stco", UInt32sBE(0, 2, 5000, 5300));

		byte[] stbl = stss is null
			? MakeBox("stbl", stts, stsc, stsz, stco)
			: MakeBox("stbl", stts, stss, stsc, stsz, stco);
		byte[] minf = MakeBox("minf", stbl);
		byte[] mdia = MakeBox("mdia", mdhd, minf);
		byte[] trak = MakeBox("trak", tkhd, mdia);

		return BoxFactory.CreateBox<TrakBox>(new MemoryStream(trak), parent: null);
	}

	[TestMethod]
	public void ChunkEntryList_MapsStssToPerChunkSyncFlags()
	{
		var trak = MakeTrak(StssBytes(2, 5));

		var chunks = new ChunkEntryList(trak).ToList();

		Assert.HasCount(2, chunks);
		CollectionAssert.AreEqual(new[] { false, true, false }, chunks[0].SyncFlags);
		CollectionAssert.AreEqual(new[] { false, true }, chunks[1].SyncFlags);
	}

	[TestMethod]
	public void ChunkEntryList_NoStss_SyncFlagsAreNull()
	{
		var trak = MakeTrak(stss: null);

		var chunks = new ChunkEntryList(trak).ToList();

		Assert.HasCount(2, chunks);
		Assert.IsNull(chunks[0].SyncFlags);
		Assert.IsNull(chunks[1].SyncFlags);
	}
}
