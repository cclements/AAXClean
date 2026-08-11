using AAXClean;
using AAXClean.FrameFilters;
using AAXClean.FrameFilters.Audio;
using Mpeg4Lib;
using Mpeg4Lib.Chunks;

namespace Mpeg4Lib.Test;

[TestClass]
public class MultipartBoundarySafetyTests
{
	[TestMethod]
	public void Public_constructor_preserves_the_four_parameter_clr_signature()
	{
		var constructor = typeof(MultipartFilterBase<FrameEntry, NewSplitCallback>).GetConstructor(
			[typeof(ChapterInfo), typeof(SampleRate), typeof(bool), typeof(long)]);

		Assert.IsNotNull(constructor);
		var mediaOffset = constructor.GetParameters()[3];
		Assert.IsTrue(mediaOffset.HasDefaultValue);
		Assert.AreEqual(0L, mediaOffset.DefaultValue);
	}

	[TestMethod]
	public async Task PositionedEmptyPlaceholders_DoNotAdvancePastChaptersBeforeAudioArrives()
	{
		ChapterInfo chapters = TwoThousandSampleChapters();
		using RecordingFilter filter = new(chapters);

		await filter.AddInputAsync(Frame(start: 0, samples: 0, frameData: Memory<byte>.Empty));
		await filter.AddInputAsync(Frame(start: 1024, samples: 0, frameData: Memory<byte>.Empty));
		await filter.AddInputAsync(Frame(start: 0, samples: 1001));
		await filter.AddInputAsync(Frame(start: 1001, samples: 999));
		await filter.CompleteAsync();

		CollectionAssert.AreEqual(new[] { "one", "two" }, filter.OpenedParts);
		Assert.AreEqual(1001u, filter.Writes.Single(write => write.part == "one").samples);
	}

	[TestMethod]
	public async Task Zero_duration_compressed_access_unit_with_payload_is_not_discarded()
	{
		ChapterInfo chapters = TwoThousandSampleChapters();
		using RecordingFilter filter = new(chapters);

		await filter.AddInputAsync(Frame(start: 0, samples: 1));
		await filter.AddInputAsync(Frame(start: 1, samples: 0, new byte[] { 0x5a }));
		await filter.CompleteAsync();

		Assert.HasCount(2, filter.Writes);
		Assert.AreEqual(0u, filter.Writes[1].samples);
	}

	[TestMethod]
	public async Task DefaultCompressedRouting_KeepsWholeFramesAtChapterBoundaries()
	{
		ChapterInfo chapters = TwoThousandSampleChapters();
		using RecordingFilter filter = new(chapters);

		await filter.AddInputAsync(Frame(start: 0, samples: 1001));
		await filter.AddInputAsync(Frame(start: 1001, samples: 999));
		await filter.CompleteAsync();

		CollectionAssert.AreEqual(new[] { "one", "two" }, filter.OpenedParts);
		CollectionAssert.AreEqual(
			new uint[] { 1001, 999 },
			filter.Writes.Select(write => write.samples).ToArray());
	}

	private static ChapterInfo TwoThousandSampleChapters()
	{
		ChapterInfo chapters = new();
		chapters.AddChapter("one", TimeSpan.FromTicks(1_250_000));
		chapters.AddChapter("two", TimeSpan.FromTicks(1_250_000));
		return chapters;
	}

	private static readonly ChunkEntry Chunk = new()
	{
		TrackId = 1,
		ChunkIndex = 0,
		ChunkOffset = 0,
		FirstSample = 0,
		ChunkSize = 1,
		FrameSizes = [1],
		FrameDurations = [1],
	};

	private static FrameEntry Frame(long start, uint samples, Memory<byte>? frameData = null)
		=> new()
		{
			Chunk = Chunk,
			StartSample = start,
			SamplesInFrame = samples,
			FrameData = frameData ?? (samples == 0 ? Memory<byte>.Empty : new byte[1]),
		};

	private sealed class RecordingFilter(ChapterInfo chapters)
		// Deliberately retain the historically valid fourth-argument default literal. This
		// guards source compatibility separately from the CLR-signature assertion above.
		: MultipartFilterBase<FrameEntry, NewSplitCallback>(
			chapters, SampleRate.Hz_8000, inputStereo: false, default)
	{
		private string? currentPart;
		protected override int InputBufferSize => 10;
		public List<string> OpenedParts { get; } = [];
		public List<(string part, long? start, uint samples)> Writes { get; } = [];

		protected override void CloseCurrentWriter() { }

		protected override void CreateNewWriter(NewSplitCallback callback)
		{
			currentPart = callback.Chapter.Title;
			OpenedParts.Add(currentPart);
		}

		protected override void WriteFrameToFile(FrameEntry audioFrame, bool newChunk)
		{
			if (currentPart is not null)
				Writes.Add((currentPart, audioFrame.StartSample, audioFrame.SamplesInFrame));
		}
	}
}
