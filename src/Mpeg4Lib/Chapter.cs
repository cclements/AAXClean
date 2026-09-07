using Mpeg4Lib.Util;
using System;
using System.IO;
using System.Text;

namespace Mpeg4Lib;

public record Chapter
{
	private static readonly Encoding TitleEncoding = new UTF8Encoding(false, true);
	public string Title { get; }
	public TimeSpan StartOffset { get; }
	public TimeSpan Duration { get; }
	public TimeSpan EndOffset { get; }
	public int RenderSize => 2 + GetEncodedTitleLength() + encd.Length;
	public Chapter(string title, TimeSpan start, TimeSpan duration)
	{
		ArgumentNullException.ThrowIfNull(title, nameof(title));
		try
		{
			_ = TitleEncoding.GetByteCount(title);
		}
		catch (EncoderFallbackException ex)
		{
			throw new ArgumentException("A chapter title must contain valid Unicode text.", nameof(title), ex);
		}
		Title = title;
		StartOffset = start;
		Duration = duration;
		EndOffset = StartOffset + Duration;
	}

	public void WriteChapter(Stream output)
	{
		// A valid title read from UTF-16 may be too large to serialize as UTF-8.
		// Validate before allocating/writing, without restricting the readable model.
		_ = GetEncodedTitleLength();
		byte[] title = TitleEncoding.GetBytes(Title);

		output.WriteUInt16BE(checked((ushort)title.Length));
		output.Write(title);
		output.Write(encd);
	}

	private int GetEncodedTitleLength()
	{
		int length = TitleEncoding.GetByteCount(Title);
		if (length > ushort.MaxValue)
			throw new InvalidOperationException("A serialized chapter title cannot exceed 65,535 UTF-8 bytes.");
		return length;
	}
	public override string ToString()
	{
		return $"{Title} {{{StartOffset} - {EndOffset}}}";
	}

	//This is constant for UTF-8 text
	//https://github.com/FFmpeg/FFmpeg/blob/master/libavformat/movenc.c
	private static readonly byte[] encd = [0, 0, 0, 0xc, (byte)'e', (byte)'n', (byte)'c', (byte)'d', 0, 0, 1, 0];
}
