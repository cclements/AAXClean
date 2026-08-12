using Mpeg4Lib.Boxes;
using System.Text;

namespace Mpeg4Lib.Test;

[TestClass]
public class DashPresentationDurationTests
{
	[TestMethod]
	public void Fragment_duration_overrides_zero_mdhd_duration_through_base_contract()
	{
		const uint movieTimescale = 1001;
		const uint mediaTimescale = 48_000;
		const uint fragmentDuration = 501;
		using var dash = new AAXClean.DashFile(new MemoryStream(
			CreateDash(movieTimescale, mediaTimescale, fragmentDuration)));
		Mpeg4File baseView = dash;
		long expectedMediaSamples = checked((long)ElstBox.ScaleDuration(
			fragmentDuration, movieTimescale, mediaTimescale));

		Assert.AreEqual(0ul, dash.Moov.AudioTrack.Mdia.Mdhd.Duration,
			"The fixture must retain the fragmented-source zero mdhd duration.");
		Assert.AreEqual(24_024L, expectedMediaSamples,
			"The fixture must exercise exact non-identity movie-to-media scaling.");
		Assert.AreEqual(expectedMediaSamples, baseView.PresentedDurationSamples,
			"Codecs reads through the base contract and must receive the DASH override.");
		Assert.AreEqual(TimeSpan.FromTicks(5_005_000), dash.PresentedDuration);
		Assert.AreEqual(dash.PresentedDuration, dash.Duration);
	}

	[TestMethod]
	public void Fragment_duration_rejects_zero_movie_or_media_timescales()
	{
		foreach ((uint movieTimescale, uint mediaTimescale) in new[]
		{
			(0u, 48_000u),
			(1001u, 0u),
		})
		{
			using var dash = new AAXClean.DashFile(new MemoryStream(
				CreateDash(movieTimescale, mediaTimescale, fragmentDuration: 501)));
			Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = dash.PresentedDurationSamples);
		}
	}

	[TestMethod]
	public void Fragment_duration_rejects_media_sample_count_overflow()
	{
		using var dash = new AAXClean.DashFile(new MemoryStream(
			CreateDash(movieTimescale: 1, mediaTimescale: uint.MaxValue, fragmentDuration: uint.MaxValue)));

		Assert.ThrowsExactly<OverflowException>(() => _ = dash.PresentedDurationSamples);
	}

	private static byte[] CreateDash(uint movieTimescale, uint mediaTimescale, uint fragmentDuration)
	{
		byte[] ftyp = Box("ftyp", Encoding.ASCII.GetBytes("iso6"), UInt32s(0), Encoding.ASCII.GetBytes("dash"));
		ulong scaledDuration = movieTimescale == 0
			? 1
			: ElstBox.ScaleDuration(fragmentDuration, movieTimescale, mediaTimescale == 0 ? 1u : mediaTimescale);
		uint sampleDuration = (uint)Math.Min(scaledDuration, uint.MaxValue);
		byte[] mfhd = Box("mfhd", UInt32s(0, 1));
		byte[] tfhd = Box("tfhd", UInt32s(0x0002_0018, 1, sampleDuration, 2));
		byte[] tfdt = Box("tfdt", UInt32s(0, 0));
		byte[] trun = Box("trun", UInt32s(0, 1));
		byte[] moof = Box("moof", mfhd, Box("traf", tfhd, tfdt, trun));
		byte[] mdat = Box("mdat", [1, 0]);
		byte[] sidx = Box("sidx",
			UInt32s(0, 1, movieTimescale, 0, 0),
			UInt16s(0, 1),
			UInt32s(checked((uint)(moof.Length + mdat.Length)), fragmentDuration, 0x9000_0000));

		byte[] mvhd = Box("mvhd",
			UInt32s(0, 0, 0, movieTimescale, 0, 0x0001_0000),
			UInt16s(0x0100, 0), new byte[8], new byte[36], new byte[24], UInt32s(2));
		byte[] mehd = Box("mehd", UInt32s(0, fragmentDuration));
		byte[] trex = Box("trex", UInt32s(0, 1, 1, sampleDuration, 2, 0));
		byte[] mvex = Box("mvex", mehd, trex);
		byte[] tkhd = Box("tkhd",
			UInt32s(0, 0, 0, 1, 0, 0), new byte[8],
			UInt16s(0, 0, 0x0100, 0), new byte[36], UInt32s(0, 0));
		byte[] mdhd = Box("mdhd", UInt32s(0, 0, 0, mediaTimescale, 0, 0));
		byte[] hdlr = Box("hdlr", UInt32s(0, 0), Encoding.ASCII.GetBytes("soun"), new byte[12]);
		ushort sampleEntryRate = checked((ushort)Math.Min(mediaTimescale, 48_000u));
		byte[] sampleEntry = Box("ac-4",
			new byte[6], UInt16s(1), new byte[8],
			UInt16s(2, 16, 0, 0, sampleEntryRate, 0), Box("dac4", [0]));
		byte[] stsd = Box("stsd", UInt32s(0, 1), sampleEntry);
		byte[] stts = Box("stts", UInt32s(0, 0));
		byte[] stsc = Box("stsc", UInt32s(0, 0));
		byte[] stsz = Box("stsz", UInt32s(0, 0, 0));
		byte[] stco = Box("stco", UInt32s(0, 0));
		byte[] stbl = Box("stbl", stsd, stts, stsc, stsz, stco);
		byte[] minf = Box("minf", stbl);
		byte[] mdia = Box("mdia", mdhd, hdlr, minf);
		byte[] trak = Box("trak", tkhd, mdia);
		byte[] moov = Box("moov", mvhd, mvex, trak);

		return [.. ftyp, .. moov, .. sidx, .. moof, .. mdat];
	}

	private static byte[] Box(string type, params byte[][] payloads)
	{
		using var box = new MemoryStream();
		WriteUInt32BE(box, checked((uint)(8 + payloads.Sum(payload => payload.Length))));
		box.Write(Encoding.ASCII.GetBytes(type));
		foreach (byte[] payload in payloads)
			box.Write(payload);
		return box.ToArray();
	}

	private static byte[] UInt32s(params uint[] values)
	{
		using var bytes = new MemoryStream();
		foreach (uint value in values)
			WriteUInt32BE(bytes, value);
		return bytes.ToArray();
	}

	private static byte[] UInt16s(params ushort[] values)
	{
		using var bytes = new MemoryStream();
		foreach (ushort value in values)
		{
			bytes.WriteByte((byte)(value >> 8));
			bytes.WriteByte((byte)value);
		}
		return bytes.ToArray();
	}

	private static void WriteUInt32BE(Stream stream, uint value)
	{
		Span<byte> bytes = [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
		stream.Write(bytes);
	}
}
