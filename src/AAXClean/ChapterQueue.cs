using AAXClean.FrameFilters;
using Mpeg4Lib;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;

namespace AAXClean
{
	public class ChapterEntry
	{
		public Memory<byte> FrameData { get; init; }
		public uint SamplesInFrame { get; init; }
		public string Title { get; }
		public ChapterEntry(string title)
		{
			ArgumentNullException.ThrowIfNull(title, nameof(title));
			Title = title;
		}
	}

	/// <summary>
	/// Chapters to be written to an mp4 file.
	/// </summary>
	public class ChapterQueue
	{
		private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
		private static readonly Encoding Utf16BigEndian = new UnicodeEncoding(true, false, true);
		private static readonly Encoding Utf16LittleEndian = new UnicodeEncoding(false, false, true);
		private int subtractNext = 0;
		private readonly double SampleScaleFactor;
		private readonly SampleRate OutputSampleRate;
		private readonly object lockObj = new();
		private readonly Queue<ChapterEntry> chapterEntries = new();
		private TimeSpan userChapterDuration;
		private long userChapterSamples;

		public ChapterQueue(SampleRate inputRate, SampleRate outputRate)
		{
			OutputSampleRate = outputRate;
			SampleScaleFactor = (double)outputRate / (double)inputRate;
		}

		public bool TryGetNextChapter([NotNullWhen(true)] out ChapterEntry? chapterEntry)
		{
			lock (lockObj)
			{
				if (chapterEntries.Count > 0)
				{
					chapterEntry = chapterEntries.Dequeue();
					return true;
				}
			}

			chapterEntry = null;
			return false;
		}

		public void AddRange(IEnumerable<Chapter> chapters)
		{
			foreach (var ch in chapters)
				Add(ch);
		}

		/// <summary>
		/// Add a user-defined chapter
		/// </summary>
		public void Add(Chapter chapter)
		{
			byte[] frameData = new byte[chapter.RenderSize];

			using var ms = new MemoryStream(frameData);
			chapter.WriteChapter(ms);
			lock (lockObj)
			{
				userChapterDuration += chapter.Duration;
				long chapterEndSample = (long)Math.Round(
					userChapterDuration.TotalSeconds * (int)OutputSampleRate);
				uint sampleDelta = checked((uint)(chapterEndSample - userChapterSamples));
				userChapterSamples = chapterEndSample;

				chapterEntries.Enqueue(new ChapterEntry(chapter.Title)
				{
					FrameData = frameData,
					SamplesInFrame = sampleDelta
				});
			}
		}

		/// <summary>
		/// Add a chapter read directly from the timed text track.
		/// </summary>
		public void Add(FrameEntry entry)
		{
			ReadOnlySpan<byte> frameData = entry.FrameData.Span;
			if (frameData.Length < sizeof(ushort))
				throw new InvalidDataException("The chapter sample is missing its two-byte text length.");
			int size = BinaryPrimitives.ReadUInt16BigEndian(frameData);
			if (size > frameData.Length - sizeof(ushort))
				throw new InvalidDataException("The chapter text length exceeds its sample payload.");
			ReadOnlySpan<byte> text = frameData.Slice(sizeof(ushort), size);
			string title;
			try
			{
				// QuickTime chapter text is UTF-8, or UTF-16 identified by its BOM.
				// Preserve the original sample and extensions for lossless remux.
				title = text.Length >= 2 && text[0] == 0xfe && text[1] == 0xff
					? Utf16BigEndian.GetString(text[2..])
					: text.Length >= 2 && text[0] == 0xff && text[1] == 0xfe
						? Utf16LittleEndian.GetString(text[2..])
						: Utf8.GetString(text);
			}
			catch (DecoderFallbackException ex)
			{
				throw new InvalidDataException("The chapter sample contains invalid Unicode text.", ex);
			}

			//Takes care of 'negative' sample deltas in malformed Stts entries (e.g. Broken Angels)
			var sif = (int)entry.SamplesInFrame;

			lock (lockObj)
			{
				chapterEntries.Enqueue(new ChapterEntry(title)
				{
					FrameData = entry.FrameData,
					SamplesInFrame = (uint)(Math.Max(0, sif + subtractNext) * SampleScaleFactor)
				});
			}

			subtractNext = sif < 0 ? sif : 0;
		}
	}
}
