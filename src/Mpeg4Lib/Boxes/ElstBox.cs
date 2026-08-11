using Mpeg4Lib.Util;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace Mpeg4Lib.Boxes;

//ISO/IEC 14496-12 § 8.6.6 Edit List Box. Maps the track's media timeline onto the
//presentation timeline. Used here to trim playback of a split part to the exact
//chapter window: the part's media may begin before the chapter start (at the nearest
//preceding sync sample, so decoders have a valid entry point) and end after the
//chapter end (at a frame boundary); the single edit entry presents exactly the
//chapter's samples.
public class ElstBox : FullBox
{
	public override long RenderSize => base.RenderSize + 4 + Entries.Count * (Version == 1 ? 20 : 12);

	public List<EditEntry> Entries { get; } = new List<EditEntry>();

	/// <summary>
	/// The single non-empty rate-1 edit — the only edit-list form this library currently
	/// writes and honors. Every other form throws so a remux cannot silently discard a
	/// presentation mapping it does not understand.
	/// </summary>
	public EditEntry? SingleEdit
	{
		get
		{
			if (Entries.Count == 1)
			{
				EditEntry entry = Entries[0];
				if (entry.MediaTime >= 0
					&& entry.MediaRateInteger == 1
					&& entry.MediaRateFraction == 0)
					return entry;
			}

			throw new NotSupportedException(
				"This edit list cannot be represented as the supported single non-empty rate-1 presentation window.");
		}
	}

	/// <summary>
	/// Convert a non-negative duration between timescales with exact rational arithmetic,
	/// rounding to nearest with exact half-way values rounded up.
	/// </summary>
	public static ulong ScaleDuration(ulong duration, uint fromTimescale, uint toTimescale)
	{
		ArgumentOutOfRangeException.ThrowIfZero(fromTimescale);
		ArgumentOutOfRangeException.ThrowIfZero(toTimescale);

		BigInteger numerator = (BigInteger)duration * toTimescale;
		BigInteger quotient = BigInteger.DivRem(numerator, fromTimescale, out BigInteger remainder);
		if (remainder * 2 >= fromTimescale)
			quotient++;

		return quotient <= ulong.MaxValue
			? (ulong)quotient
			: throw new OverflowException("The scaled edit-list duration exceeds UInt64.MaxValue.");
	}

	public static ElstBox CreateBlank(IBox parent)
	{
		int size = 4 + 12 /* empty FullBox size*/;
		BoxHeader header = new BoxHeader((uint)size, "elst");

		ElstBox elstBox = new ElstBox([0, 0, 0, 0], header, parent);

		parent.Children.Add(elstBox);
		return elstBox;
	}

	private ElstBox(byte[] versionFlags, BoxHeader header, IBox? parent)
		: base(versionFlags, header, parent) { }

	public ElstBox(Stream file, BoxHeader header, IBox? parent)
		: base(file, header, parent)
	{
		uint entryCount = file.ReadUInt32BE();
		Entries = new List<EditEntry>((int)entryCount);

		for (uint i = 0; i < entryCount; i++)
		{
			ulong segmentDuration = Version == 1 ? file.ReadUInt64BE() : file.ReadUInt32BE();
			long mediaTime = Version == 1 ? file.ReadInt64BE() : file.ReadInt32BE();
			short rateInteger = file.ReadInt16BE();
			short rateFraction = file.ReadInt16BE();
			Entries.Add(new EditEntry(segmentDuration, mediaTime, rateInteger, rateFraction));
		}
	}

	/// <summary>Use the 64-bit entry layout when any entry's values overflow 32 bits.</summary>
	public void UpdateVersion()
	{
		foreach (EditEntry entry in Entries)
		{
			if (entry.SegmentDuration > uint.MaxValue || entry.MediaTime > int.MaxValue)
			{
				Version = 1;
				return;
			}
		}
		Version = 0;
	}

	protected override void Render(Stream file)
	{
		base.Render(file);
		file.WriteUInt32BE((uint)Entries.Count);
		foreach (EditEntry entry in Entries)
		{
			if (Version == 1)
			{
				file.WriteUInt64BE(entry.SegmentDuration);
				file.WriteInt64BE(entry.MediaTime);
			}
			else
			{
				file.WriteUInt32BE((uint)entry.SegmentDuration);
				file.WriteInt32BE((int)entry.MediaTime);
			}
			file.WriteInt16BE(entry.MediaRateInteger);
			file.WriteInt16BE(entry.MediaRateFraction);
		}
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing && !Disposed)
			Entries.Clear();
		base.Dispose(disposing);
	}

	/// <param name="SegmentDuration">Presentation duration of this edit, in movie (mvhd) timescale units.</param>
	/// <param name="MediaTime">Start of this edit within the media, in media (mdhd) timescale units; -1 for an empty edit.</param>
	public readonly record struct EditEntry(ulong SegmentDuration, long MediaTime, short MediaRateInteger = 1, short MediaRateFraction = 0);
}
