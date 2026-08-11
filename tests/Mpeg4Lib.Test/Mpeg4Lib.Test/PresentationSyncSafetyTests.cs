using AAXClean;
using AAXClean.FrameFilters.Audio;
using Mpeg4Lib.Boxes;
using Mpeg4Lib.Chunks;
using System.Text;

namespace Mpeg4Lib.Test;

[TestClass]
public class PresentationSyncSafetyTests
{
	[TestMethod]
	public async Task LeadingEmptyThenMediaEdit_FailsBeforeWritingOutput()
	{
		byte[] sourceBytes = CreateAc4Source(
			movieTimescale: 1000,
			mediaTimescale: 1000,
			frameDelta: 1000,
			samples: [1, 2, 3, 4, 5],
			edits:
			[
				new ElstBox.EditEntry(SegmentDuration: 250, MediaTime: -1),
				new ElstBox.EditEntry(SegmentDuration: 2000, MediaTime: 1000)
			]);
		using var source = new AAXClean.Mp4File(new MemoryStream(sourceBytes));
		using var output = new MemoryStream();

		await Assert.ThrowsExactlyAsync<NotSupportedException>(
			async () => await source.ConvertToMp4aAsync(output));

		Assert.IsEmpty(output.ToArray(), "Unsupported edits must fail before ftyp/mdat output is written.");
	}

	[TestMethod]
	public async Task MultipleMediaEdits_FailBeforeWritingOutput()
	{
		byte[] sourceBytes = CreateAc4Source(
			movieTimescale: 1000,
			mediaTimescale: 1000,
			frameDelta: 1000,
			samples: [1, 2, 3, 4, 5],
			edits:
			[
				new ElstBox.EditEntry(SegmentDuration: 1000, MediaTime: 0),
				new ElstBox.EditEntry(SegmentDuration: 1000, MediaTime: 3000)
			]);
		using var source = new AAXClean.Mp4File(new MemoryStream(sourceBytes));
		using var output = new MemoryStream();

		await Assert.ThrowsExactlyAsync<NotSupportedException>(
			async () => await source.ConvertToMp4aAsync(output));

		Assert.IsEmpty(output.ToArray(), "Unsupported edits must fail before ftyp/mdat output is written.");
	}

	[TestMethod]
	public async Task SingleEdit_RoundTripsAcrossDifferentTimescalesWithSymmetricRounding()
	{
		byte[] sourceBytes = CreateAc4Source(
			movieTimescale: 3,
			mediaTimescale: 10,
			frameDelta: 1,
			samples: [1, 2, 3, 4, 5, 6, 7, 8, 9, 10],
			edits: [new ElstBox.EditEntry(SegmentDuration: 1, MediaTime: 2)]);
		using var source = new AAXClean.Mp4File(new MemoryStream(sourceBytes));
		using var output = new MemoryStream();

		await source.ConvertToMp4aAsync(output);

		using var converted = new AAXClean.Mp4File(new MemoryStream(output.ToArray()));
		ElstBox.EditEntry edit = converted.Moov.AudioTrack.Edts!.Elst!.SingleEdit!.Value;
		Assert.AreEqual(1ul, edit.SegmentDuration,
			"3 media ticks map back to the original 1 movie tick; truncation incorrectly writes zero.");
		Assert.AreEqual(3L, converted.PresentedDurationSamples);
	}

	[TestMethod]
	public async Task SparseStssAc4_TrimStartsAtPrecedingSourceSyncAndKeepsExactWindow()
	{
		byte[] sourceBytes = CreateSparseSyncAc4Source();
		using var source = new AAXClean.Mp4File(new MemoryStream(sourceBytes));
		using var output = new MemoryStream();

		await source.ConvertToMp4aAsync(output, TrimWindow());

		AssertSparseSyncOutput(output.ToArray());
	}

	[TestMethod]
	public async Task SparseStssAc4_SplitStartsAtPrecedingSourceSyncAndKeepsExactWindow()
	{
		byte[] sourceBytes = CreateSparseSyncAc4Source();
		using var source = new AAXClean.Mp4File(new MemoryStream(sourceBytes));
		MemoryStream? part = null;

		await source.ConvertToMultiMp4aAsync(TrimWindow(), callback =>
		{
			part = new MemoryStream();
			callback.OutputFile = part;
		});

		Assert.IsNotNull(part);
		AssertSparseSyncOutput(part.ToArray());
	}

	[TestMethod]
	public void AacTranscodeWriter_RemovesTheSourceAc4Configuration()
	{
		byte[] sourceBytes = CreateAc4Source(
			movieTimescale: 48_000,
			mediaTimescale: 48_000,
			frameDelta: 1024,
			samples: [1]);
		using var source = new AAXClean.Mp4File(new MemoryStream(sourceBytes));
		using var output = new MemoryStream();
		using var writer = new Mp4aWriter(output, source.Ftyp, source.Moov, [0x15, 0x88]);

		AudioSampleEntry entry = writer.Moov.AudioTrack.Mdia.Minf.Stbl.Stsd.AudioSampleEntry!;
		Assert.AreEqual("mp4a", entry.Header.Type);
		Assert.IsNotNull(entry.Esds);
		Assert.IsNull(entry.Dac4);
	}

	private static byte[] CreateSparseSyncAc4Source()
		=> CreateAc4Source(
			movieTimescale: 600,
			mediaTimescale: 1000,
			frameDelta: 1000,
			samples: [1, 2, 3, 4, 5, 6, 7, 8],
			syncSamples: [1, 4, 7]);

	private static ChapterInfo TrimWindow()
	{
		ChapterInfo chapters = new(TimeSpan.FromSeconds(5.5));
		chapters.AddChapter("trim", TimeSpan.FromSeconds(1.2));
		return chapters;
	}

	private static void AssertSparseSyncOutput(byte[] outputBytes)
	{
		using var converted = new AAXClean.Mp4File(new MemoryStream(outputBytes));
		ChunkEntry firstChunk = new ChunkEntryList(converted.Moov.AudioTrack).First();
		converted.InputStream.Position = firstChunk.ChunkOffset;
		byte[] samples = new byte[firstChunk.FrameSizes.Length];
		for (int i = 0; i < samples.Length; i++)
		{
			Assert.AreEqual(2, firstChunk.FrameSizes[i]);
			samples[i] = (byte)converted.InputStream.ReadByte();
			Assert.AreEqual(0, converted.InputStream.ReadByte());
		}

		CollectionAssert.AreEqual(new byte[] { 4, 5, 6, 7 }, samples,
			"The first output sample must be source sample 4, the genuine sync preceding 5.5 s.");
		CollectionAssert.AreEqual(new uint[] { 1, 4 },
			converted.Moov.AudioTrack.Mdia.Minf.Stbl.Stss!.SampleNumbers);

		ElstBox.EditEntry edit = converted.Moov.AudioTrack.Edts!.Elst!.SingleEdit!.Value;
		Assert.AreEqual(2500L, edit.MediaTime);
		Assert.AreEqual(720ul, edit.SegmentDuration);
		Assert.AreEqual(1200L, converted.PresentedDurationSamples);
	}

	internal static byte[] CreateAc4Source(
		uint movieTimescale,
		uint mediaTimescale,
		uint frameDelta,
		byte[] samples,
		uint[]? syncSamples = null,
		IReadOnlyList<ElstBox.EditEntry>? edits = null,
		bool malformedCenc = false)
	{
		uint sampleCount = (uint)samples.Length;
		uint mediaDuration = checked(sampleCount * frameDelta);
		uint movieDuration = edits is null
			? checked((uint)((ulong)mediaDuration * movieTimescale / mediaTimescale))
			: checked((uint)edits.Aggregate(0ul, (sum, edit) => sum + edit.SegmentDuration));

		byte[] ftyp = malformedCenc
			? Box("ftyp", Encoding.ASCII.GetBytes("iso5"), UInt32s(0), Encoding.ASCII.GetBytes("dash"))
			: Box("ftyp", Encoding.ASCII.GetBytes("M4A "), UInt32s(0));
		byte[] mediaPayload = samples.SelectMany(sample => new byte[] { sample, 0 }).ToArray();
		byte[] mdat = Box("mdat", mediaPayload);

		byte[] mvhd = Box("mvhd",
			UInt32s(0, 0, 0, movieTimescale, movieDuration, 0x0001_0000),
			UInt16s(0x0100, 0), new byte[8], new byte[36], new byte[24], UInt32s(2));
		byte[] tkhd = Box("tkhd",
			UInt32s(0, 0, 0, 1, 0, movieDuration), new byte[8],
			UInt16s(0, 0, 0x0100, 0), new byte[36], UInt32s(0, 0));
		byte[] mdhd = Box("mdhd", UInt32s(0, 0, 0, mediaTimescale, mediaDuration, 0));
		byte[] hdlr = Box("hdlr", UInt32s(0, 0), Encoding.ASCII.GetBytes("soun"), new byte[12]);

		byte[] dac4 = Box("dac4", [0]);
		byte[]? sinf = malformedCenc
			? Box("sinf",
				Box("frma", Encoding.ASCII.GetBytes("ac-4")),
				Box("schm", UInt32s(0, (uint)SchmBox.SchemeType.Cenc, 0x0001_0000)),
				Box("schi"))
			: null;
		byte[] sampleEntry = Box(malformedCenc ? "enca" : "ac-4",
			new byte[6], UInt16s(1), new byte[8],
			UInt16s(2, 16, 0, 0, checked((ushort)mediaTimescale), 0), dac4, sinf ?? []);
		byte[] stsd = Box("stsd", UInt32s(0, 1), sampleEntry);
		byte[] stts = Box("stts", UInt32s(0, 1, sampleCount, frameDelta));
		byte[] stsc = Box("stsc", UInt32s(0, 1, 1, sampleCount, 1));
		byte[] stsz = Box("stsz", UInt32s(0, 0, sampleCount),
			UInt32s(Enumerable.Repeat(2u, samples.Length).ToArray()));
		byte[] stco = Box("stco", UInt32s(0, 1, (uint)(ftyp.Length + 8)));
		byte[]? stss = syncSamples is null
			? null
			: Box("stss", UInt32s(0, (uint)syncSamples.Length), UInt32s(syncSamples));

		var stblChildren = new List<byte[]> { stsd, stts };
		if (stss is not null)
			stblChildren.Add(stss);
		stblChildren.AddRange([stsc, stsz, stco]);

		byte[] stbl = Box("stbl", stblChildren.ToArray());
		byte[] minf = Box("minf", stbl);
		byte[] mdia = Box("mdia", mdhd, hdlr, minf);
		byte[]? edts = edits is null ? null : EditBox(edits);
		byte[] trak = edts is null ? Box("trak", tkhd, mdia) : Box("trak", tkhd, edts, mdia);
		byte[] moov = Box("moov", mvhd, trak);

		return malformedCenc
			? [.. ftyp, .. Box("moof"), .. mdat, .. moov]
			: [.. ftyp, .. mdat, .. moov];
	}

	private static byte[] EditBox(IReadOnlyList<ElstBox.EditEntry> edits)
	{
		using var payload = new MemoryStream();
		WriteUInt32BE(payload, 0);
		WriteUInt32BE(payload, (uint)edits.Count);
		foreach (ElstBox.EditEntry edit in edits)
		{
			WriteUInt32BE(payload, checked((uint)edit.SegmentDuration));
			WriteUInt32BE(payload, unchecked((uint)edit.MediaTime));
			WriteUInt16BE(payload, unchecked((ushort)edit.MediaRateInteger));
			WriteUInt16BE(payload, unchecked((ushort)edit.MediaRateFraction));
		}
		return Box("edts", Box("elst", payload.ToArray()));
	}

	private static byte[] Box(string type, params byte[][] payloads)
	{
		using var box = new MemoryStream();
		WriteUInt32BE(box, checked((uint)(8 + payloads.Sum(p => p.Length))));
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
			(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value
		];
		stream.Write(bytes);
	}

	private static void WriteUInt16BE(Stream stream, ushort value)
	{
		Span<byte> bytes = [(byte)(value >> 8), (byte)value];
		stream.Write(bytes);
	}
}
