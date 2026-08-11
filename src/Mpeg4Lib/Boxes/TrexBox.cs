using Mpeg4Lib.Util;
using System.IO;

namespace Mpeg4Lib.Boxes;

/// <summary>Track-fragment defaults from an <c>mvex</c> box.</summary>
public class TrexBox : FullBox
{
	public override long RenderSize => base.RenderSize + 20;

	public uint TrackID { get; }
	public uint DefaultSampleDescriptionIndex { get; }
	public uint DefaultSampleDuration { get; }
	public uint DefaultSampleSize { get; }
	public uint DefaultSampleFlags { get; }

	public TrexBox(Stream file, BoxHeader header, IBox? parent) : base(file, header, parent)
	{
		TrackID = file.ReadUInt32BE();
		DefaultSampleDescriptionIndex = file.ReadUInt32BE();
		DefaultSampleDuration = file.ReadUInt32BE();
		DefaultSampleSize = file.ReadUInt32BE();
		DefaultSampleFlags = file.ReadUInt32BE();
	}

	protected override void Render(Stream file)
	{
		base.Render(file);
		file.WriteUInt32BE(TrackID);
		file.WriteUInt32BE(DefaultSampleDescriptionIndex);
		file.WriteUInt32BE(DefaultSampleDuration);
		file.WriteUInt32BE(DefaultSampleSize);
		file.WriteUInt32BE(DefaultSampleFlags);
	}
}
