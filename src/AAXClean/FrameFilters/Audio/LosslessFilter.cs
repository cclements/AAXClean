using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace AAXClean.FrameFilters.Audio
{
	internal class LosslessFilter : FrameFinalBase<FrameEntry>
	{
		public bool Closed { get; private set; }
		protected override int InputBufferSize => 1000;

		private long lastChunkIndex = -1;
		public readonly Mp4aWriter Mp4aWriter;
		private readonly ChapterQueue ChapterQueue;
		private readonly long windowStart;
		private readonly long windowEnd;
		private readonly bool trimming;
		private readonly SyncPrerollQueue preroll = new();
		private bool insideWindow;
		private long currentSample;

		public LosslessFilter(Stream outputStream, Mp4File mp4Audio, ChapterQueue chapterQueue)
			: this(outputStream, mp4Audio, chapterQueue, 0, long.MaxValue) { }

		public LosslessFilter(Stream outputStream, Mp4File mp4Audio, ChapterQueue chapterQueue,
			long windowStartSample, long windowEndSample)
		{
			Mp4aWriter = new Mp4aWriter(outputStream, mp4Audio.Ftyp, mp4Audio.Moov);
			ChapterQueue = chapterQueue;

			long mediaDuration = checked((long)mp4Audio.Moov.AudioTrack.Mdia.Mdhd.Duration);
			windowStart = windowStartSample;
			windowEnd = Math.Min(windowEndSample, mediaDuration);
			trimming = windowStart > 0 || windowEnd < mediaDuration;
			insideWindow = !trimming;
		}

		protected override Task FlushAsync()
		{
			//Write any remaining chapters
			while (ChapterQueue.TryGetNextChapter(out var chapterEntry))
				Mp4aWriter.WriteChapter(chapterEntry);

			CloseWriter();
			return Task.CompletedTask;
		}

		protected override Task PerformFilteringAsync(FrameEntry input)
		{
			if (!trimming)
			{
				WriteFrame(input);
				return Task.CompletedTask;
			}

			currentSample = input.StartSample ?? currentSample;

			if (!insideWindow)
			{
				if (input.Chunk is not null && currentSample + input.SamplesInFrame <= windowStart)
				{
					preroll.Push(input, currentSample, input.IsSyncSample ?? true);
					currentSample += input.SamplesInFrame;
					return Task.CompletedTask;
				}

				insideWindow = true;
				//A corrected bitstream sync on the overlapping frame supersedes any
				//older metadata-derived preroll and is the nearest valid entry point.
				var frames = input.IsSyncSample ?? true
					? new List<(FrameEntry frame, long start)>()
					: new List<(FrameEntry frame, long start)>(preroll.Frames);
				frames.Add((input, currentSample));
				Mp4aWriter.SetEditList(
					mediaTime: Math.Max(0, windowStart - frames[0].start),
					presentedSamples: windowEnd - windowStart);
				foreach ((FrameEntry frame, long _) in frames)
					WriteFrame(frame);
				currentSample += input.SamplesInFrame;
				return Task.CompletedTask;
			}

			if (input.Chunk is not null && currentSample >= windowEnd)
			{
				currentSample += input.SamplesInFrame;
				return Task.CompletedTask;
			}

			WriteFrame(input);
			currentSample += input.SamplesInFrame;
			return Task.CompletedTask;
		}

		private void WriteFrame(FrameEntry input)
		{
			var chunkIndex = input.Chunk?.ChunkIndex ?? lastChunkIndex;
			bool newChunk = chunkIndex > lastChunkIndex;

			//Write chapters as soon as they're available.
			while (ChapterQueue.TryGetNextChapter(out var chapterEntry))
			{
				Mp4aWriter.WriteChapter(chapterEntry);
				newChunk = true;
			}

			Mp4aWriter.AddFrame(input.FrameData.Span, newChunk, input.SamplesInFrame, input.IsSyncSample);
			lastChunkIndex = chunkIndex;
		}

		private void CloseWriter()
		{
			if (Closed) return;
			Mp4aWriter.Close();
			Closed = true;
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing && !Disposed)
			{
				CloseWriter();
				Mp4aWriter?.Dispose();
			}
			base.Dispose(disposing);
		}
	}
}
