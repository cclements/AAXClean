using AAXClean;
using AAXClean.FrameFilters;
using AAXClean.FrameFilters.Audio;
using Mpeg4Lib.Boxes;
using Mpeg4Lib.Chunks;
using System.Text;

namespace Mpeg4Lib.Test;

[TestClass]
public class PresentationWindowContractTests
{
	[TestMethod]
	public async Task SparseStssAc4_TrimStartsAtPrecedingSourceSyncAndKeepsExactWindow()
	{
		byte[] sourceBytes = CreateAc4Source(
			movieTimescale: 600,
			mediaTimescale: 1000,
			frameDelta: 1000,
			samples: [1, 2, 3, 4, 5, 6, 7, 8],
			syncSamples: [1, 4, 7]);
		using var source = new AAXClean.Mp4File(new MemoryStream(sourceBytes));
		using var output = new MemoryStream();

		await source.ConvertToMp4aAsync(output, TrimWindow());

		using var converted = new AAXClean.Mp4File(new MemoryStream(output.ToArray()));
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

		ElstBox.EditEntry edit = converted.Moov.AudioTrack.Edts!.Elst!.Entries.Single();
		Assert.AreEqual(2500L, edit.MediaTime);
		Assert.AreEqual(720ul, edit.SegmentDuration);

		TrakBox textTrack = converted.Moov.TextTrack!;
		ElstBox.EditEntry textEdit = textTrack.Edts!.Elst!.SingleEdit!.Value;
		Assert.AreEqual(0L, textEdit.MediaTime);
		Assert.AreEqual(720ul, textEdit.SegmentDuration);
		Assert.AreEqual(1200ul, textTrack.Mdia.Mdhd.Duration);
		Assert.AreEqual(720ul, textTrack.Tkhd.Duration);
	}

	[TestMethod]
	public async Task SparseStssAc4_SplitStartsAtPrecedingSourceSyncAndKeepsExactWindow()
	{
		byte[] sourceBytes = CreateAc4Source(
			movieTimescale: 600,
			mediaTimescale: 1000,
			frameDelta: 1000,
			samples: [1, 2, 3, 4, 5, 6, 7, 8],
			syncSamples: [1, 4, 7]);
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
	public async Task FrameAlignedSplit_StartsAtTheBoundarySyncWithoutOlderPreroll()
	{
		byte[] sourceBytes = CreateAc4Source(
			movieTimescale: 1000,
			mediaTimescale: 1000,
			frameDelta: 1000,
			samples: [1, 2, 3, 4],
			syncSamples: [1, 3]);
		using var source = new AAXClean.Mp4File(new MemoryStream(sourceBytes));
		ChapterInfo chapters = new();
		chapters.AddChapter("one", TimeSpan.FromSeconds(2));
		chapters.AddChapter("two", TimeSpan.FromSeconds(2));
		var parts = new List<MemoryStream>();

		await source.ConvertToMultiMp4aAsync(chapters, callback =>
		{
			var part = new MemoryStream();
			parts.Add(part);
			callback.OutputFile = part;
		});

		Assert.HasCount(2, parts);
		using var second = new AAXClean.Mp4File(new MemoryStream(parts[1].ToArray()));
		ChunkEntry firstChunk = new ChunkEntryList(second.Moov.AudioTrack).First();
		second.InputStream.Position = firstChunk.ChunkOffset;
		byte[] samples = new byte[firstChunk.FrameSizes.Length];
		for (int i = 0; i < samples.Length; i++)
		{
			samples[i] = (byte)second.InputStream.ReadByte();
			Assert.AreEqual(0, second.InputStream.ReadByte());
		}

		CollectionAssert.AreEqual(new byte[] { 3, 4 }, samples);
		Assert.AreEqual(0L, second.Moov.AudioTrack.Edts!.Elst!.SingleEdit!.Value.MediaTime);
	}

	[TestMethod]
	public async Task SingleFileTrim_UsesCorrectedBoundarySyncWithoutOlderPreroll()
	{
		byte[] sourceBytes = CreateAc4Source(
			movieTimescale: 1000,
			mediaTimescale: 1000,
			frameDelta: 1000,
			samples: [1, 2, 3, 4],
			syncSamples: [1]);
		using var source = new AAXClean.Mp4File(new MemoryStream(sourceBytes));
		using var output = new MemoryStream();
		using var filter = new LosslessFilter(
			output,
			source,
			new ChapterQueue(SampleRate.Hz_8000, SampleRate.Hz_8000),
			windowStartSample: 2000,
			windowEndSample: 4000);
		var chunk = new ChunkEntry
		{
			TrackId = 1,
			ChunkIndex = 0,
			ChunkOffset = 0,
			FirstSample = 0,
			ChunkSize = 8,
			FrameSizes = [2, 2, 2, 2],
			FrameDurations = [1000, 1000, 1000, 1000],
		};

		for (int i = 0; i < 4; i++)
		{
			await filter.AddInputAsync(new FrameEntry
			{
				Chunk = chunk,
				StartSample = i * 1000L,
				SamplesInFrame = 1000,
				FrameData = new byte[] { (byte)(i + 1), 0 },
				IsSyncSample = i is 0 or 2,
			});
		}
		await filter.CompleteAsync();

		using var converted = new AAXClean.Mp4File(new MemoryStream(output.ToArray()));
		ChunkEntry firstChunk = new ChunkEntryList(converted.Moov.AudioTrack).First();
		converted.InputStream.Position = firstChunk.ChunkOffset;
		byte[] samples = new byte[firstChunk.FrameSizes.Length];
		for (int i = 0; i < samples.Length; i++)
		{
			samples[i] = (byte)converted.InputStream.ReadByte();
			Assert.AreEqual(0, converted.InputStream.ReadByte());
		}

		CollectionAssert.AreEqual(new byte[] { 3, 4 }, samples);
		Assert.AreEqual(0L, converted.Moov.AudioTrack.Edts!.Elst!.SingleEdit!.Value.MediaTime);
	}

	[TestMethod]
	public async Task SparseStssWithoutPrecedingSync_FailsInsteadOfStartingOnDependentSample()
	{
		byte[] sourceBytes = CreateAc4Source(
			movieTimescale: 1000,
			mediaTimescale: 1000,
			frameDelta: 1000,
			samples: [1, 2, 3, 4, 5, 6, 7, 8],
			syncSamples: [7]);
		using var source = new AAXClean.Mp4File(new MemoryStream(sourceBytes));
		using var output = new MemoryStream();

		await Assert.ThrowsExactlyAsync<InvalidDataException>(
			async () => await source.ConvertToMp4aAsync(output, TrimWindow()));
	}

	[TestMethod]
	public async Task UnsupportedMultipleEdits_FailBeforeWritingOutput()
	{
		byte[] sourceBytes = CreateAc4Source(
			movieTimescale: 1000,
			mediaTimescale: 1000,
			frameDelta: 1000,
			samples: [1, 2, 3, 4, 5],
			edits:
			[
				new ElstBox.EditEntry(1000, 0),
				new ElstBox.EditEntry(1000, 3000)
			]);
		using var source = new AAXClean.Mp4File(new MemoryStream(sourceBytes));
		using var output = new MemoryStream();

		await Assert.ThrowsExactlyAsync<NotSupportedException>(
			async () => await source.ConvertToMp4aAsync(output));

		Assert.IsEmpty(output.ToArray());
	}

	[TestMethod]
	public async Task OutOfBoundsSingleEdit_FailsBeforeWritingOutput()
	{
		byte[] sourceBytes = CreateAc4Source(
			movieTimescale: 1000,
			mediaTimescale: 1000,
			frameDelta: 1000,
			samples: [1, 2, 3, 4, 5],
			edits: [new ElstBox.EditEntry(2000, 4000)]);
		using var source = new AAXClean.Mp4File(new MemoryStream(sourceBytes));
		using var output = new MemoryStream();

		await Assert.ThrowsExactlyAsync<InvalidDataException>(
			async () => await source.ConvertToMp4aAsync(output));

		Assert.IsEmpty(output.ToArray());
	}

	[TestMethod]
	public async Task SingleEdit_RoundTripsAcrossDifferentTimescalesWithSymmetricRounding()
	{
		byte[] sourceBytes = CreateAc4Source(
			movieTimescale: 3,
			mediaTimescale: 10,
			frameDelta: 1,
			samples: [1, 2, 3, 4, 5, 6, 7, 8, 9, 10],
			edits: [new ElstBox.EditEntry(1, 2)]);
		using var source = new AAXClean.Mp4File(new MemoryStream(sourceBytes));
		using var output = new MemoryStream();

		await source.ConvertToMp4aAsync(output);

		using var converted = new AAXClean.Mp4File(new MemoryStream(output.ToArray()));
		ElstBox.EditEntry edit = converted.Moov.AudioTrack.Edts!.Elst!.SingleEdit!.Value;
		Assert.AreEqual(1ul, edit.SegmentDuration);
		Assert.AreEqual(3L, converted.PresentedDurationSamples);
	}

	[TestMethod]
	public async Task UntrimmedSource_PreservesAllSamplesWithoutAddingAnEditList()
	{
		byte[] sourceBytes = CreateAc4Source(
			movieTimescale: 1000,
			mediaTimescale: 1000,
			frameDelta: 1000,
			samples: [1, 2, 3, 4],
			syncSamples: [1, 3]);
		using var source = new AAXClean.Mp4File(new MemoryStream(sourceBytes));
		using var output = new MemoryStream();

		await source.ConvertToMp4aAsync(output);

		using var converted = new AAXClean.Mp4File(new MemoryStream(output.ToArray()));
		ChunkEntry firstChunk = new ChunkEntryList(converted.Moov.AudioTrack).First();
		converted.InputStream.Position = firstChunk.ChunkOffset;
		byte[] samples = new byte[firstChunk.FrameSizes.Length];
		for (int i = 0; i < samples.Length; i++)
		{
			samples[i] = (byte)converted.InputStream.ReadByte();
			Assert.AreEqual(0, converted.InputStream.ReadByte());
		}

		CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, samples);
		Assert.IsNull(converted.Moov.AudioTrack.Edts);
	}

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

		CollectionAssert.AreEqual(new byte[] { 4, 5, 6, 7 }, samples);
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
		IReadOnlyList<ElstBox.EditEntry>? edits = null)
	{
		uint sampleCount = (uint)samples.Length;
		uint mediaDuration = checked(sampleCount * frameDelta);
		uint movieDuration = edits is null
			? checked((uint)((ulong)mediaDuration * movieTimescale / mediaTimescale))
			: checked((uint)edits.Aggregate(0ul, (sum, edit) => sum + edit.SegmentDuration));

		byte[] ftyp = Box("ftyp", Encoding.ASCII.GetBytes("M4A "), UInt32s(0));
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
		byte[] sampleEntry = Box("ac-4",
			new byte[6], UInt16s(1), new byte[8],
			UInt16s(2, 16, 0, 0, checked((ushort)mediaTimescale), 0), dac4);
		byte[] stsd = Box("stsd", UInt32s(0, 1), sampleEntry);
		byte[] stts = Box("stts", UInt32s(0, 1, sampleCount, frameDelta));
		byte[]? stss = syncSamples is null
			? null
			: Box("stss", UInt32s(0, (uint)syncSamples.Length), UInt32s(syncSamples));
		byte[] stsc = Box("stsc", UInt32s(0, 1, 1, sampleCount, 1));
		byte[] stsz = Box("stsz", UInt32s(0, 0, sampleCount),
			UInt32s(Enumerable.Repeat(2u, samples.Length).ToArray()));
		byte[] stco = Box("stco", UInt32s(0, 1, (uint)(ftyp.Length + 8)));

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

		return [.. ftyp, .. mdat, .. moov];
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
