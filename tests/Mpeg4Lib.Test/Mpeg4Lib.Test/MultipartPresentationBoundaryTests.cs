using AAXClean;
using AAXClean.FrameFilters;
using AAXClean.FrameFilters.Audio;
using Mpeg4Lib.Chunks;

namespace Mpeg4Lib.Test;

[TestClass]
public class MultipartPresentationBoundaryTests
{
	[TestMethod]
	public async Task PositionedEmptyPlaceholders_DoNotAdvanceMultipartState()
	{
		using RecordingFilter filter = new(TwoChapters());

		await filter.AddInputAsync(Frame(start: 0, samples: 0, data: Memory<byte>.Empty));
		await filter.AddInputAsync(Frame(start: 1024, samples: 0, data: Memory<byte>.Empty));
		await filter.AddInputAsync(Frame(start: 0, samples: 1001));
		await filter.AddInputAsync(Frame(start: 1001, samples: 999));
		await filter.CompleteAsync();

		CollectionAssert.AreEqual(new[] { "one", "two" }, filter.OpenedParts);
		Assert.AreEqual(1001u, filter.Writes.Single(write => write.part == "one").samples);
	}

	[TestMethod]
	public async Task ZeroDurationCompressedPayload_IsNotTreatedAsAPlaceholder()
	{
		using RecordingFilter filter = new(TwoChapters());

		await filter.AddInputAsync(Frame(start: 0, samples: 1));
		await filter.AddInputAsync(Frame(start: 1, samples: 0, data: new byte[] { 0x5a }));
		await filter.CompleteAsync();

		Assert.HasCount(2, filter.Writes);
		Assert.AreEqual(0u, filter.Writes[1].samples);
	}

	[TestMethod]
	public async Task DefaultCompressedRouting_KeepsWholeFramesAtChapterBoundaries()
	{
		using RecordingFilter filter = new(TwoChapters());

		await filter.AddInputAsync(Frame(start: 0, samples: 1001));
		await filter.AddInputAsync(Frame(start: 1001, samples: 999));
		await filter.CompleteAsync();

		CollectionAssert.AreEqual(
			new uint[] { 1001, 999 },
			filter.Writes.Select(write => write.samples).ToArray());
	}

	[TestMethod]
	public async Task FrameAlignedCompressedBoundary_OpensTheFollowingPart()
	{
		using RecordingFilter filter = new(TwoChapters());

		await filter.AddInputAsync(Frame(start: 0, samples: 1000));
		await filter.AddInputAsync(Frame(start: 1000, samples: 1000));
		await filter.CompleteAsync();

		CollectionAssert.AreEqual(new[] { "one", "two" }, filter.OpenedParts);
		CollectionAssert.AreEqual(
			new[] { "one", "two" },
			filter.Writes.Select(write => write.part).ToArray());
	}

	[TestMethod]
	public async Task OptInSplittableRouting_DividesAtTheExactHalfOpenBoundary()
	{
		using RecordingFilter filter = new(TwoChapters(), splitFrames: true);

		await filter.AddInputAsync(Frame(start: 0, samples: 2000, data: new byte[2000]));
		await filter.CompleteAsync();

		CollectionAssert.AreEqual(new[] { "one", "two" }, filter.OpenedParts);
		CollectionAssert.AreEqual(
			new uint[] { 1000, 1000 },
			filter.Writes.Select(write => write.samples).ToArray());
		CollectionAssert.AreEqual(
			new long?[] { 0, 1000 },
			filter.Writes.Select(write => write.start).ToArray());
	}

	private static ChapterInfo TwoChapters()
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

	private static FrameEntry Frame(long start, uint samples, Memory<byte>? data = null)
		=> new()
		{
			Chunk = Chunk,
			StartSample = start,
			SamplesInFrame = samples,
			FrameData = data ?? (samples == 0 ? Memory<byte>.Empty : new byte[samples]),
		};

	private sealed class RecordingFilter : MultipartFilterBase<FrameEntry, NewSplitCallback>
	{
		private readonly bool splitFrames;
		private string? currentPart;

		public RecordingFilter(ChapterInfo chapters, bool splitFrames = false)
			: base(
				chapters,
				SampleRate.Hz_8000,
				inputStereo: false,
				time => (long)Math.Round(time.TotalSeconds * 8000),
				PresentationTimeMappingKind.Exact)
			=> this.splitFrames = splitFrames;

		protected override int InputBufferSize => 10;
		protected override bool SplitFramesAtPartBoundaries => splitFrames;
		public List<string> OpenedParts { get; } = [];
		public List<(string part, long? start, uint samples)> Writes { get; } = [];

		protected override (FrameEntry first, FrameEntry second) SplitFrame(
			FrameEntry input,
			uint firstPartSamples)
		{
			long? secondStart = input.StartSample is long start
				? checked(start + firstPartSamples)
				: null;
			return (
				Copy(input, input.StartSample, firstPartSamples, input.FrameData[..(int)firstPartSamples]),
				Copy(input, secondStart, input.SamplesInFrame - firstPartSamples,
					input.FrameData[(int)firstPartSamples..]));
		}

		private static FrameEntry Copy(
			FrameEntry input,
			long? start,
			uint samples,
			Memory<byte> data)
			=> new()
			{
				Chunk = input.Chunk,
				StartSample = start,
				SamplesInFrame = samples,
				FrameData = data,
				IsSyncSample = input.IsSyncSample,
			};

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
