using AAXClean;
using Mpeg4Lib;

namespace Mpeg4Lib.Test;

[TestClass]
public class ChapterQueueTests
{
	[TestMethod]
	public void ReconstructionDurations_CumulativeRoundingPreservesStartsAndPresentedTail()
	{
		const int sampleRate = 44100;
		const long presentedSamples = 765243638;
		int[] chapterDurationsMs =
		[
			16924,
			1059283,
			2921161,
			2026467,
			1889693,
			2192485,
			2475426,
			1193586,
			1563562,
			1809000,
		];

		ChapterInfo chapters = new();
		for (int i = 0; i < chapterDurationsMs.Length; i++)
			chapters.AddChapter($"Chapter {i + 1}", TimeSpan.FromMilliseconds(chapterDurationsMs[i]));

		TimeSpan presentedDuration = TimeSpan.FromSeconds(presentedSamples / (double)sampleRate);
		chapters.AddChapter("End Credits", presentedDuration - chapters.EndOffset);

		ChapterQueue queue = new(SampleRate.Hz_44100, SampleRate.Hz_44100);
		queue.AddRange(chapters);

		List<ChapterEntry> entries = [];
		while (queue.TryGetNextChapter(out ChapterEntry? entry))
			entries.Add(entry);

		Assert.HasCount(chapters.Count, entries);
		Assert.AreEqual(
			presentedSamples,
			entries.Sum(entry => (long)entry.SamplesInFrame),
			"Independently truncating the observed chapter durations loses four samples.");

		long actualStart = 0;
		for (int i = 0; i < chapters.Count; i++)
		{
			Chapter chapter = chapters.Chapters[i];
			long expectedStart = (long)Math.Round(
				(chapter.StartOffset - chapters.StartOffset).TotalSeconds * sampleRate);

			Assert.AreEqual(chapter.Title, entries[i].Title);
			Assert.AreEqual(expectedStart, actualStart, $"Chapter {i + 1} start");
			actualStart += entries[i].SamplesInFrame;
		}
	}
}
