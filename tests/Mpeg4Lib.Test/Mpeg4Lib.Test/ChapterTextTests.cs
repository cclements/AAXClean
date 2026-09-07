using AAXClean;
using AAXClean.FrameFilters;
using Mpeg4Lib;
using System.Text;

namespace Mpeg4Lib.Test;

[TestClass]
public class ChapterTextTests
{
	[TestMethod]
	[DataRow(0)]
	[DataRow(1)]
	[DataRow(255)]
	[DataRow(256)]
	[DataRow(65535)]
	public void TimedText_UnsignedBigEndianLengthPreservesWholeTitleAndSample(int byteLength)
	{
		string title = new('a', byteLength);
		byte[] sample = [(byte)(byteLength >> 8), (byte)byteLength, .. Encoding.UTF8.GetBytes(title), 0, 0, 0, 12, 101, 110, 99, 100, 0, 0, 1, 0];
		ChapterQueue queue = new(SampleRate.Hz_44100, SampleRate.Hz_44100);
		queue.Add(new FrameEntry { FrameData = sample, SamplesInFrame = 44100 });

		Assert.IsTrue(queue.TryGetNextChapter(out var entry));
		Assert.AreEqual(title, entry.Title);
		Assert.AreEqual(44100u, entry.SamplesInFrame);
		CollectionAssert.AreEqual(sample, entry.FrameData.ToArray(), "Remux must preserve the complete text sample, including its extension.");
	}

	[TestMethod]
	[DataRow("Chapter 一 — café 📚")]
	[DataRow("الفصل الأول — नमस्ते")]
	public void WrittenTitle_UsesUtf8ByteLengthAndRoundTripsThroughTimedTrack(string title)
	{
		Chapter chapter = new(title, TimeSpan.Zero, TimeSpan.FromSeconds(1));
		using MemoryStream output = new();
		chapter.WriteChapter(output);
		byte[] sample = output.ToArray();
		int expectedByteLength = Encoding.UTF8.GetByteCount(title);

		Assert.AreEqual(expectedByteLength, (sample[0] << 8) | sample[1]);
		Assert.HasCount(chapter.RenderSize, sample);
		CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(title), sample[2..(2 + expectedByteLength)]);
		ChapterQueue queue = new(SampleRate.Hz_44100, SampleRate.Hz_44100);
		queue.Add(new FrameEntry { FrameData = sample, SamplesInFrame = 44100 });
		Assert.IsTrue(queue.TryGetNextChapter(out var entry));
		Assert.AreEqual(title, entry.Title);
	}

	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	public void TimedText_Utf16BomIsDecodedWithoutChangingRemuxBytes(bool bigEndian)
	{
		const string title = "第十二章 📚";
		Encoding encoding = new UnicodeEncoding(bigEndian, true, true);
		byte[] text = [.. encoding.GetPreamble(), .. encoding.GetBytes(title)];
		byte[] sample = [(byte)(text.Length >> 8), (byte)text.Length, .. text];
		ChapterQueue queue = new(SampleRate.Hz_44100, SampleRate.Hz_44100);
		queue.Add(new FrameEntry { FrameData = sample, SamplesInFrame = 44100 });
		Assert.IsTrue(queue.TryGetNextChapter(out var entry));
		Assert.AreEqual(title, entry.Title);
		CollectionAssert.AreEqual(sample, entry.FrameData.ToArray());
	}

	[TestMethod]
	public void TimedText_TruncatedOrInvalidEncodingFailsBeforeQueueMutation()
	{
		byte[][] malformed =
		[
			[], [0], [0, 1], [1, 0, 65],
			[0, 1, 0xff], [0, 2, 0xc3, 0x28],
			[0, 3, 0xfe, 0xff, 0x00],
			[0, 4, 0xfe, 0xff, 0xd8, 0x00]
		];
		foreach (byte[] sample in malformed)
		{
			ChapterQueue queue = new(SampleRate.Hz_44100, SampleRate.Hz_44100);
			queue.Add(new FrameEntry { FrameData = new byte[] { 0, 1, 65 }, SamplesInFrame = 44100 });
			Assert.ThrowsExactly<InvalidDataException>(() => queue.Add(new FrameEntry { FrameData = sample, SamplesInFrame = uint.MaxValue }), Convert.ToHexString(sample));
			Assert.IsTrue(queue.TryGetNextChapter(out var preceding));
			Assert.AreEqual("A", preceding.Title);
			Assert.IsFalse(queue.TryGetNextChapter(out _));
			queue.Add(new FrameEntry { FrameData = new byte[] { 0, 1, 66 }, SamplesInFrame = 44100 });
			Assert.IsTrue(queue.TryGetNextChapter(out var following));
			Assert.AreEqual(44100u, following.SamplesInFrame, "Rejected input must not change sample-delta correction state.");
		}
	}

	[TestMethod]
	public void Title_OverlongUtf8AndInvalidUnicodeAreRejectedBeforeWriting()
	{
		foreach (string title in new[] { new string('a', 65536), new string('界', 21846) })
		{
			Chapter oversized = new(title, TimeSpan.Zero, TimeSpan.Zero);
			using MemoryStream existing = new();
			existing.Write(new byte[] { 1, 2, 3 });
			existing.Position = 1;
			Assert.ThrowsExactly<InvalidOperationException>(() => _ = oversized.RenderSize);
			Assert.ThrowsExactly<InvalidOperationException>(() => oversized.WriteChapter(existing));
			Assert.AreEqual(1L, existing.Position);
			CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, existing.ToArray());
		}
		Assert.ThrowsExactly<ArgumentException>(() => new Chapter("\ud800", TimeSpan.Zero, TimeSpan.Zero));
		Chapter boundary = new(new string('界', 21845), TimeSpan.Zero, TimeSpan.Zero);
		using MemoryStream output = new();
		boundary.WriteChapter(output);
		byte[] sample = output.ToArray();
		Assert.AreEqual(0xff, sample[0]);
		Assert.AreEqual(0xff, sample[1]);
		Assert.HasCount(65535 + 2 + 12, sample);
	}

	[TestMethod]
	public void Utf16TitleLargerThanUtf8WriteLimit_RemainsReadableAsChapterInfo()
	{
		string title = new('界', 21846);
		byte[] text = [0xfe, 0xff, .. Encoding.BigEndianUnicode.GetBytes(title)];
		Assert.IsLessThan(65536, text.Length);
		byte[] sample = [(byte)(text.Length >> 8), (byte)text.Length, .. text];
		ChapterQueue queue = new(SampleRate.Hz_44100, SampleRate.Hz_44100);
		queue.Add(new FrameEntry { FrameData = sample, SamplesInFrame = 44100 });
		Assert.IsTrue(queue.TryGetNextChapter(out var entry));
		ChapterInfo chapters = new();
		chapters.AddChapter(entry.Title, TimeSpan.FromSeconds(1));
		Assert.AreEqual(title, chapters.Chapters.Single().Title);
		CollectionAssert.AreEqual(sample, entry.FrameData.ToArray());
	}
}
