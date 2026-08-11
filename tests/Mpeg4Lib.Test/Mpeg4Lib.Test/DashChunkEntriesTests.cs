using Mpeg4Lib.Boxes;
using Mpeg4Lib.Chunks;

namespace Mpeg4Lib.Test;

[TestClass]
public class DashChunkEntriesTests
{
	private static byte[] MakeBox(string type, params byte[][] payloads)
	{
		int payloadSize = payloads.Sum(p => p.Length);
		using var stream = new MemoryStream();
		WriteUInt32BE(stream, (uint)(8 + payloadSize));
		stream.Write(System.Text.Encoding.ASCII.GetBytes(type));
		foreach (var payload in payloads)
			stream.Write(payload);
		return stream.ToArray();
	}

	private static byte[] UInt32sBE(params uint[] values)
	{
		using var stream = new MemoryStream();
		foreach (uint value in values)
			WriteUInt32BE(stream, value);
		return stream.ToArray();
	}

	private static void WriteUInt32BE(Stream stream, uint value)
	{
		Span<byte> bytes = [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
		stream.Write(bytes);
	}

	private static void WriteUInt16BE(Stream stream, ushort value)
	{
		Span<byte> bytes = [(byte)(value >> 8), (byte)value];
		stream.Write(bytes);
	}

	private static byte[] TrexBytes(uint trackId, uint descriptionIndex, uint duration, uint size, uint flags)
		=> MakeBox("trex", UInt32sBE(0, trackId, descriptionIndex, duration, size, flags));

	private static TrexBox MakeTrex(uint duration, uint size, uint flags)
		=> BoxFactory.CreateBox<TrexBox>(new MemoryStream(TrexBytes(7, 1, duration, size, flags)), parent: null);

	private static byte[] MakeTfhd(uint? duration = null, uint? size = null, uint? flags = null)
	{
		uint boxFlags = (duration.HasValue ? 0x000008u : 0)
			| (size.HasValue ? 0x000010u : 0)
			| (flags.HasValue ? 0x000020u : 0);
		List<uint> values = [boxFlags, 7];
		if (duration.HasValue) values.Add(duration.Value);
		if (size.HasValue) values.Add(size.Value);
		if (flags.HasValue) values.Add(flags.Value);
		return MakeBox("tfhd", UInt32sBE(values.ToArray()));
	}

	private static byte[] MakeTrun(
		(uint Duration, uint Size, uint Flags)[] samples,
		bool durations = false,
		bool sizes = false,
		bool sampleFlags = false,
		uint? firstSampleFlags = null)
	{
		uint boxFlags = (firstSampleFlags.HasValue ? 0x000004u : 0)
			| (durations ? 0x000100u : 0)
			| (sizes ? 0x000200u : 0)
			| (sampleFlags ? 0x000400u : 0);
		List<uint> values = [boxFlags, (uint)samples.Length];
		if (firstSampleFlags.HasValue) values.Add(firstSampleFlags.Value);
		foreach (var sample in samples)
		{
			if (durations) values.Add(sample.Duration);
			if (sizes) values.Add(sample.Size);
			if (sampleFlags) values.Add(sample.Flags);
		}
		return MakeBox("trun", UInt32sBE(values.ToArray()));
	}

	private static byte[] MakeFragment(uint sequence, uint decodeTime, byte[] tfhd, byte[] trun, int mediaBytes)
	{
		byte[] tfdt = MakeBox("tfdt", UInt32sBE(0, decodeTime));
		byte[] mfhd = MakeBox("mfhd", UInt32sBE(0, sequence));
		byte[] traf = MakeBox("traf", tfhd, tfdt, trun);
		byte[] moof = MakeBox("moof", mfhd, traf);
		byte[] mdat = MakeBox("mdat", new byte[mediaBytes]);
		return moof.Concat(mdat).ToArray();
	}

	private static SidxBox MakeSidx(int timescale, long earliestPresentationTime, params (int Size, uint Duration)[] references)
	{
		using var payload = new MemoryStream();
		WriteUInt32BE(payload, 0); //version + flags
		WriteUInt32BE(payload, 7); //reference_ID
		WriteUInt32BE(payload, checked((uint)timescale));
		WriteUInt32BE(payload, checked((uint)earliestPresentationTime));
		WriteUInt32BE(payload, 0); //first_offset
		WriteUInt16BE(payload, 0); //reserved
		WriteUInt16BE(payload, checked((ushort)references.Length));
		foreach (var reference in references)
		{
			WriteUInt32BE(payload, checked((uint)reference.Size));
			WriteUInt32BE(payload, reference.Duration);
			WriteUInt32BE(payload, 0x90000000); //starts_with_SAP=1, SAP_type=1, SAP_delta_time=0
		}
		return BoxFactory.CreateBox<SidxBox>(new MemoryStream(MakeBox("sidx", payload.ToArray())), parent: null);
	}

	private static (MemoryStream Stream, MoofBox Moof, MdatBox Mdat) ParseFirstFragment(params byte[][] fragments)
	{
		var stream = new MemoryStream(fragments.SelectMany(f => f).ToArray());
		var moof = BoxFactory.CreateBox<MoofBox>(stream, parent: null);
		var mdat = BoxFactory.CreateBox<MdatBox>(stream, parent: null);
		return (stream, moof, mdat);
	}

	private static ChunkEntry ReadFirstChunk(
		MemoryStream stream,
		MoofBox moof,
		MdatBox mdat,
		SidxBox sidx,
		TrexBox? trex,
		long minimumSample = 0,
		uint mediaTimescale = 48_000)
	{
		var entries = new DashChunkEntries(stream, 7, sidx, moof, mdat, minimumSample, long.MaxValue, trex, mediaTimescale);
		using var enumerator = entries.GetEnumerator();
		Assert.IsTrue(enumerator.MoveNext());
		return enumerator.Current;
	}

	[TestMethod]
	public void TrexBox_ParsesRoundTripsAndMvexSelectsTrackDefaults()
	{
		byte[] source = MakeBox("mvex",
			TrexBytes(7, 2, 1024, 17, 0x00010000),
			TrexBytes(9, 3, 2048, 23, 0));

		var mvex = BoxFactory.CreateBox<MvexBox>(new MemoryStream(source), parent: null);
		var trex = mvex.GetTrackExtends(9);

		Assert.HasCount(2, mvex.TrackExtends);
		Assert.AreEqual(9u, trex.TrackID);
		Assert.AreEqual(3u, trex.DefaultSampleDescriptionIndex);
		Assert.AreEqual(2048u, trex.DefaultSampleDuration);
		Assert.AreEqual(23u, trex.DefaultSampleSize);
		Assert.AreEqual(0u, trex.DefaultSampleFlags);

		using var rendered = new MemoryStream();
		mvex.Save(rendered);
		CollectionAssert.AreEqual(source, rendered.ToArray());
	}

	[TestMethod]
	public void TrexOnlyDefaults_PopulateDurationSizeAndFlags()
	{
		const uint nonSync = 0x00010000;
		byte[] fragment = MakeFragment(1, 0, MakeTfhd(), MakeTrun(new (uint, uint, uint)[2]), mediaBytes: 6);
		var parsed = ParseFirstFragment(fragment);
		using var stream = parsed.Stream;
		var sidx = MakeSidx(48_000, 0, (fragment.Length, 2048));

		var chunk = ReadFirstChunk(stream, parsed.Moof, parsed.Mdat, sidx, MakeTrex(1024, 3, nonSync));

		CollectionAssert.AreEqual(new uint[] { 1024, 1024 }, chunk.FrameDurations);
		CollectionAssert.AreEqual(new[] { 3, 3 }, chunk.FrameSizes);
		CollectionAssert.AreEqual(new[] { false, false }, chunk.SyncFlags);
	}

	[TestMethod]
	public void MixedOverrides_UseTrunThenFirstSampleThenTfhdThenTrex()
	{
		const uint nonSync = 0x00010000;
		var samples = new[] { (301u, 0u, 0u), (302u, 0u, 0u) };
		byte[] fragment = MakeFragment(
			1,
			0,
			MakeTfhd(duration: 200, size: 3, flags: 0),
			MakeTrun(samples, durations: true, firstSampleFlags: nonSync),
			mediaBytes: 6);
		var parsed = ParseFirstFragment(fragment);
		using var stream = parsed.Stream;
		var sidx = MakeSidx(48_000, 0, (fragment.Length, 603));

		var chunk = ReadFirstChunk(stream, parsed.Moof, parsed.Mdat, sidx, MakeTrex(100, 2, nonSync));

		CollectionAssert.AreEqual(new uint[] { 301, 302 }, chunk.FrameDurations);
		CollectionAssert.AreEqual(new[] { 3, 3 }, chunk.FrameSizes);
		CollectionAssert.AreEqual(new[] { false, true }, chunk.SyncFlags);
	}

	[TestMethod]
	public void PerSampleFields_OverrideTfhdAndTrexDefaults()
	{
		const uint nonSync = 0x00010000;
		var samples = new[] { (301u, 4u, 0u), (302u, 5u, nonSync) };
		byte[] fragment = MakeFragment(
			1,
			0,
			MakeTfhd(duration: 200, size: 3, flags: nonSync),
			MakeTrun(samples, durations: true, sizes: true, sampleFlags: true),
			mediaBytes: 9);
		var parsed = ParseFirstFragment(fragment);
		using var stream = parsed.Stream;
		var sidx = MakeSidx(48_000, 0, (fragment.Length, 603));

		var chunk = ReadFirstChunk(stream, parsed.Moof, parsed.Mdat, sidx, MakeTrex(100, 2, nonSync));

		CollectionAssert.AreEqual(new uint[] { 301, 302 }, chunk.FrameDurations);
		CollectionAssert.AreEqual(new[] { 4, 5 }, chunk.FrameSizes);
		CollectionAssert.AreEqual(new[] { true, false }, chunk.SyncFlags);
	}

	[TestMethod]
	public void Trun_RejectsFirstSampleFlagsTogetherWithPerSampleFlags()
	{
		var samples = new[] { (100u, 2u, 0u) };
		byte[] fragment = MakeFragment(
			1,
			0,
			MakeTfhd(),
			MakeTrun(samples, durations: true, sizes: true, sampleFlags: true, firstSampleFlags: 0),
			mediaBytes: 2);

		Assert.ThrowsExactly<InvalidDataException>(() => ParseFirstFragment(fragment));
	}

	[TestMethod]
	public void FlaglessSap1_MarksOnlyTheEvidencedFirstSampleSync()
	{
		var samples = new[] { (100u, 2u, 0u), (100u, 2u, 0u), (100u, 2u, 0u) };
		byte[] fragment = MakeFragment(1, 0, MakeTfhd(), MakeTrun(samples, durations: true, sizes: true), mediaBytes: 6);
		var parsed = ParseFirstFragment(fragment);
		using var stream = parsed.Stream;
		var sidx = MakeSidx(48_000, 0, (fragment.Length, 300));

		var chunk = ReadFirstChunk(stream, parsed.Moof, parsed.Mdat, sidx, trex: null);

		CollectionAssert.AreEqual(new[] { true, false, false }, chunk.SyncFlags);
	}

	[TestMethod]
	public void Timing_UsesExactSidxBoundaryAndEachFragmentsTfdt()
	{
		var firstSamples = new[] { (11_988u, 1u, 0u), (11_989u, 1u, 0u) };
		var secondSamples = new[] { (100u, 1u, 0u), (100u, 1u, 0u) };
		byte[] first = MakeFragment(1, 4_800, MakeTfhd(), MakeTrun(firstSamples, durations: true, sizes: true, sampleFlags: true), mediaBytes: 2);
		byte[] second = MakeFragment(2, 60_000, MakeTfhd(), MakeTrun(secondSamples, durations: true, sizes: true, sampleFlags: true), mediaBytes: 2);
		var parsed = ParseFirstFragment(first, second);
		using var stream = parsed.Stream;
		var sidx = MakeSidx(1_001, 100, (first.Length, 500), (second.Length, 500));
		var entries = new DashChunkEntries(stream, 7, sidx, parsed.Moof, parsed.Mdat, minimumSample: 28_771, long.MaxValue, trackExtends: null, mediaTimescale: 48_000);

		using var enumerator = entries.GetEnumerator();
		Assert.IsTrue(enumerator.MoveNext(), "The first segment still contains sample 28,771 by exact rational comparison.");
		var firstChunk = enumerator.Current;
		Assert.AreEqual(4_800L, firstChunk.FirstSample);

		stream.Position = firstChunk.ChunkOffset + firstChunk.ChunkSize;
		Assert.IsTrue(enumerator.MoveNext());
		Assert.AreEqual(60_000L, enumerator.Current.FirstSample);
	}
}
