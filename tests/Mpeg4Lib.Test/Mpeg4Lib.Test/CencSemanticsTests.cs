using AAXClean;
using AAXClean.FrameFilters;
using AAXClean.FrameFilters.Audio;
using Mpeg4Lib.Boxes;
using System.Text;

namespace Mpeg4Lib.Test;

[TestClass]
public class CencSemanticsTests
{
	private static readonly byte[] CencCiphertext =
		Convert.FromHexString("66E94BD4EF8A2C3B884CFA59CA342B2E");

	[TestMethod]
	[DataRow(8)]
	[DataRow(16)]
	public void PerformFiltering_ProtectedPerSampleIv_DecryptsWithoutMutatingIv(int ivSize)
	{
		using var tenc = MakeTenc(isProtected: true, perSampleIvSize: checked((byte)ivSize));
		using var filter = new DashFilter(new byte[16], tenc);
		byte[] iv = new byte[ivSize];
		var frame = MakeFrame((byte[])CencCiphertext.Clone(), iv);

		filter.PerformFiltering(frame);

		CollectionAssert.AreEqual(new byte[16], frame.FrameData.ToArray());
		CollectionAssert.AreEqual(new byte[ivSize], iv);
	}

	[TestMethod]
	[DataRow(8)]
	[DataRow(16)]
	public void PerformFiltering_ProtectedConstantIv_DecryptsEverySample(int ivSize)
	{
		using var tenc = MakeTenc(
			isProtected: true,
			perSampleIvSize: 0,
			constantIv: new byte[ivSize]);
		using var filter = new DashFilter(new byte[16], tenc);
		var first = MakeFrame((byte[])CencCiphertext.Clone());
		var second = MakeFrame((byte[])CencCiphertext.Clone());

		filter.PerformFiltering(first);
		filter.PerformFiltering(second);

		CollectionAssert.AreEqual(new byte[16], first.FrameData.ToArray());
		CollectionAssert.AreEqual(new byte[16], second.FrameData.ToArray());
		CollectionAssert.AreEqual(new byte[ivSize], tenc.DefaultConstantIv);
	}

	[TestMethod]
	public void PerformFiltering_ProtectedTencWithoutSampleIv_FailsClosed()
	{
		using var tenc = MakeTenc(isProtected: true, perSampleIvSize: 8);
		using var filter = new DashFilter(new byte[16], tenc);
		var frame = MakeFrame((byte[])CencCiphertext.Clone());

		Assert.ThrowsExactly<InvalidDataException>(() => filter.PerformFiltering(frame));
	}

	[TestMethod]
	public void PerformFiltering_PerSampleIvDoesNotMatchTenc_FailsClosed()
	{
		using var tenc = MakeTenc(isProtected: true, perSampleIvSize: 8);
		using var filter = new DashFilter(new byte[16], tenc);
		var frame = MakeFrame((byte[])CencCiphertext.Clone(), new byte[16]);

		Assert.ThrowsExactly<InvalidDataException>(() => filter.PerformFiltering(frame));
	}

	[TestMethod]
	public void Constructor_ProtectedTencWithInvalidConstantIv_FailsClosed()
	{
		using var tenc = MakeTenc(
			isProtected: true,
			perSampleIvSize: 0,
			constantIv: new byte[4]);

		Assert.ThrowsExactly<InvalidDataException>(() => new DashFilter(new byte[16], tenc));
	}

	[TestMethod]
	[DataRow(1, 0)]
	[DataRow(0, 1)]
	public void Constructor_CencPatternEncryption_FailsClosed(int cryptByteBlock, int skipByteBlock)
	{
		using var tenc = MakeTenc(
			isProtected: true,
			perSampleIvSize: 16,
			version: 1,
			cryptByteBlock: checked((byte)cryptByteBlock),
			skipByteBlock: checked((byte)skipByteBlock));

		Assert.ThrowsExactly<InvalidDataException>(() => new DashFilter(new byte[16], tenc));
	}

	[TestMethod]
	public void Constructor_ProtectedTencWithoutKey_FailsClosed()
	{
		using var tenc = MakeTenc(isProtected: true, perSampleIvSize: 8);

		Assert.ThrowsExactly<InvalidOperationException>(() => new DashFilter(key: null, tenc));
	}

	[TestMethod]
	public void PerformFiltering_ClearTenc_DoesNotDecryptEvenWhenSampleCarriesIv()
	{
		using var tenc = MakeTenc(isProtected: false, perSampleIvSize: 0);
		using var filter = new DashFilter(new byte[16], tenc);
		byte[] expected = (byte[])CencCiphertext.Clone();
		var frame = MakeFrame((byte[])expected.Clone(), new byte[16]);

		filter.PerformFiltering(frame);

		CollectionAssert.AreEqual(expected, frame.FrameData.ToArray());
	}

	[TestMethod]
	public void PerformFiltering_SampleIvWithoutTenc_FailsClosed()
	{
		using var filter = new DashFilter(key: null, trackEncryption: null);
		var frame = MakeFrame((byte[])CencCiphertext.Clone(), new byte[8]);

		Assert.ThrowsExactly<InvalidDataException>(() => filter.PerformFiltering(frame));
	}

	[TestMethod]
	public void DashFile_ClearTenc_AllowsFilterWithoutKey()
	{
		byte[] source = CreateDashSource(isProtected: false);
		using var dash = new DashFile(new MemoryStream(source));
		using FrameTransformBase<FrameEntry, FrameEntry> filter = dash.GetAudioFrameFilter();
		var frame = MakeFrame([0, 0]);

		Assert.AreSame(frame, filter.PerformFiltering(frame));
	}

	[TestMethod]
	public void DashFile_CencSampleEntryWithoutTenc_FailsClosed()
	{
		byte[] source = CreateDashSource(includeTenc: false);

		Assert.ThrowsExactly<InvalidDataException>(
			() => new DashFile(new MemoryStream(source)));
	}

	[TestMethod]
	[DataRow(true, false)]
	[DataRow(false, true)]
	public void DashFile_InconsistentProtectionSignaling_FailsClosed(
		bool protectedSampleEntry,
		bool includeSinf)
	{
		byte[] source = CreateDashSource(
			protectedSampleEntry: protectedSampleEntry,
			includeSinf: includeSinf);

		Assert.ThrowsExactly<InvalidDataException>(
			() => new DashFile(new MemoryStream(source)));
	}

	[TestMethod]
	[DataRow("sgpd")]
	[DataRow("sbgp")]
	public void DashFile_CencSampleGroupOverride_FailsClosed(string boxType)
	{
		byte[] source = CreateDashSource(sampleGroupBox: boxType);

		Assert.ThrowsExactly<NotSupportedException>(
			() => new DashFile(new MemoryStream(source)));
	}

	[TestMethod]
	public void Mp4aWriter_RemovesProtectionMetadataOnlyFromOutputClone()
	{
		byte[] source = CreateDashSource();
		using var dash = new DashFile(new MemoryStream(source));

		Assert.AreEqual("enca", dash.AudioSampleEntry.Header.Type);
		Assert.IsNotNull(dash.AudioSampleEntry.GetChild<SinfBox>());
		Assert.HasCount(1, dash.Moov.GetChildren<PsshBox>());

		using var output = new MemoryStream();
		using var writer = new Mp4aWriter(output, dash.Ftyp, dash.Moov);
		AudioSampleEntry outputEntry = writer.Moov.AudioTrack.Mdia.Minf.Stbl.Stsd.AudioSampleEntry!;

		Assert.AreEqual("ac-4", outputEntry.Header.Type);
		Assert.IsNull(outputEntry.GetChild<SinfBox>());
		Assert.IsEmpty(writer.Moov.GetChildren<PsshBox>());
		Assert.AreEqual("enca", dash.AudioSampleEntry.Header.Type);
		Assert.IsNotNull(dash.AudioSampleEntry.GetChild<SinfBox>());
		Assert.HasCount(1, dash.Moov.GetChildren<PsshBox>());

		writer.AddFrame([0, 0], newChunk: true, frameDelta: 1000);
		writer.Close();
	}

	[TestMethod]
	public void DashFile_SaveInPlace_FailsBeforeChangingWritableSource()
	{
		byte[] source = CreateDashSource();
		using var stream = new MemoryStream();
		stream.Write(source);
		stream.Position = 0;
		using var dash = new DashFile(stream);

		Assert.ThrowsExactly<NotSupportedException>(() => dash.SaveAsync());
		CollectionAssert.AreEqual(source, stream.ToArray());
	}

	private static FrameEntry MakeFrame(byte[] data, byte[]? iv = null)
		=> new()
		{
			SamplesInFrame = 1024,
			FrameData = data,
			ExtraData = iv
		};

	private static TencBox MakeTenc(
		bool isProtected,
		byte perSampleIvSize,
		byte[]? constantIv = null,
		byte version = 0,
		byte cryptByteBlock = 0,
		byte skipByteBlock = 0)
	{
		using var box = new MemoryStream(TencBoxBytes(
			isProtected,
			perSampleIvSize,
			constantIv,
			version,
			cryptByteBlock,
			skipByteBlock));
		return BoxFactory.CreateBox<TencBox>(box, parent: null);
	}

	private static byte[] CreateDashSource(
		bool includeTenc = true,
		bool isProtected = true,
		string? sampleGroupBox = null,
		bool protectedSampleEntry = true,
		bool includeSinf = true)
	{
		const uint timescale = 1000;
		const uint duration = 1000;
		byte[] ftyp = Box(
			"ftyp",
			Encoding.ASCII.GetBytes("iso5"),
			UInt32s(0),
			Encoding.ASCII.GetBytes("dash"));
		byte[] moof = Box("moof");
		byte[] mdat = Box("mdat", [0, 0]);

		byte[] mvhd = Box(
			"mvhd",
			UInt32s(0, 0, 0, timescale, duration, 0x0001_0000),
			UInt16s(0x0100, 0),
			new byte[8],
			new byte[36],
			new byte[24],
			UInt32s(2));
		byte[] tkhd = Box(
			"tkhd",
			UInt32s(0, 0, 0, 1, 0, duration),
			new byte[8],
			UInt16s(0, 0, 0x0100, 0),
			new byte[36],
			UInt32s(0, 0));
		byte[] mdhd = Box("mdhd", UInt32s(0, 0, 0, timescale, duration, 0));
		byte[] hdlr = Box(
			"hdlr",
			UInt32s(0, 0),
			Encoding.ASCII.GetBytes("soun"),
			new byte[12]);

		byte[] schi = includeTenc
			? Box(
				"schi",
				TencBoxBytes(
					isProtected,
					perSampleIvSize: isProtected ? (byte)8 : (byte)0))
			: Box("schi");
		byte[] sinf = Box(
			"sinf",
			Box("frma", Encoding.ASCII.GetBytes("ac-4")),
			Box("schm", UInt32s(0, (uint)SchmBox.SchemeType.Cenc, 0x0001_0000)),
			schi);
		byte[][] sampleEntryPayloads =
		[
			new byte[6],
			UInt16s(1),
			new byte[8],
			UInt16s(2, 16, 0, 0, checked((ushort)timescale), 0),
			Box("dac4", [0])
		];
		if (includeSinf)
			sampleEntryPayloads = [.. sampleEntryPayloads, sinf];

		byte[] sampleEntry = Box(
			protectedSampleEntry ? "enca" : "ac-4",
			sampleEntryPayloads);
		byte[] stsd = Box("stsd", UInt32s(0, 1), sampleEntry);
		byte[] stts = Box("stts", UInt32s(0, 1, 1, duration));
		byte[] stsc = Box("stsc", UInt32s(0, 1, 1, 1, 1));
		byte[] stsz = Box("stsz", UInt32s(0, 0, 1, 2));
		byte[] stco = Box("stco", UInt32s(0, 1, checked((uint)(ftyp.Length + moof.Length + 8))));
		byte[]? sampleGroup = sampleGroupBox is null
			? null
			: Box(
				sampleGroupBox,
				UInt32s(0),
				Encoding.ASCII.GetBytes("seig"),
				UInt32s(0));
		byte[] stbl = sampleGroup is null
			? Box("stbl", stsd, stts, stsc, stsz, stco)
			: Box("stbl", stsd, stts, stsc, stsz, stco, sampleGroup);
		byte[] minf = Box("minf", stbl);
		byte[] mdia = Box("mdia", mdhd, hdlr, minf);
		byte[] trak = Box("trak", tkhd, mdia);
		byte[] mvex = Box("mvex", Box("mehd", UInt32s(0, duration)));
		byte[] pssh = Box("pssh", UInt32s(0), new byte[16], UInt32s(0));
		byte[] moov = Box("moov", mvhd, trak, mvex, pssh);

		return [.. ftyp, .. moof, .. mdat, .. moov];
	}

	private static byte[] TencBoxBytes(
		bool isProtected,
		byte perSampleIvSize,
		byte[]? constantIv = null,
		byte version = 0,
		byte cryptByteBlock = 0,
		byte skipByteBlock = 0)
	{
		using var payload = new MemoryStream();
		payload.Write([version, 0, 0, 0]);
		payload.WriteByte(0);
		payload.WriteByte(version == 0 ? (byte)0 : (byte)((cryptByteBlock << 4) | skipByteBlock));
		payload.WriteByte(isProtected ? (byte)1 : (byte)0);
		payload.WriteByte(perSampleIvSize);
		payload.Write(new byte[16]);
		if (isProtected && perSampleIvSize == 0)
		{
			constantIv ??= [];
			payload.WriteByte(checked((byte)constantIv.Length));
			payload.Write(constantIv);
		}

		return Box("tenc", payload.ToArray());
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
			WriteUInt16BE(bytes, value);
		return bytes.ToArray();
	}

	private static void WriteUInt32BE(Stream stream, uint value)
	{
		Span<byte> bytes =
		[
			(byte)(value >> 24),
			(byte)(value >> 16),
			(byte)(value >> 8),
			(byte)value
		];
		stream.Write(bytes);
	}

	private static void WriteUInt16BE(Stream stream, ushort value)
	{
		Span<byte> bytes = [(byte)(value >> 8), (byte)value];
		stream.Write(bytes);
	}
}
