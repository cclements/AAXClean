using Mpeg4Lib.Util;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Mpeg4Lib.Boxes;

//ISO/IEC 14496-12 § 8.6.2 Sync Sample Box.
//When absent, every sample is a sync sample. USAC (xHE-AAC) audio has only sparse
//independently decodable frames, so ISO/IEC 23003-3 § H.1 requires this box to
//enumerate them; without it, seeking demuxers (notably Apple's) start decoding at
//frames that are not valid entry points.
public class StssBox : FullBox
{
	public override long RenderSize => base.RenderSize + 4 + SampleNumbers.Count * 4;

	//1-based sample numbers of the sync samples, in increasing order.
	public List<uint> SampleNumbers { get; } = new List<uint>();

	public static StssBox CreateBlank(IBox parent)
	{
		int size = 4 + 12 /* empty Box size*/;
		BoxHeader header = new BoxHeader((uint)size, "stss");

		StssBox stssBox = new StssBox([0, 0, 0, 0], header, parent);

		parent.Children.Add(stssBox);
		return stssBox;
	}

	private StssBox(byte[] versionFlags, BoxHeader header, IBox? parent)
		: base(versionFlags, header, parent) { }

	public StssBox(Stream file, BoxHeader header, IBox? parent)
		: base(file, header, parent)
	{
		uint entryCount = file.ReadUInt32BE();
		Debug.Assert(entryCount <= int.MaxValue);
		SampleNumbers = new List<uint>((int)entryCount);
		CollectionsMarshal.SetCount(SampleNumbers, (int)entryCount);
		Span<uint> sampleNumbers = CollectionsMarshal.AsSpan(SampleNumbers);

		file.ReadExactly(MemoryMarshal.AsBytes(sampleNumbers));
		if (BitConverter.IsLittleEndian)
		{
			BinaryPrimitives.ReverseEndianness(sampleNumbers, sampleNumbers);
		}
	}

	protected override void Render(Stream file)
	{
		base.Render(file);
		file.WriteUInt32BE((uint)SampleNumbers.Count);
		foreach (uint sampleNumber in SampleNumbers)
			file.WriteUInt32BE(sampleNumber);
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing && !Disposed)
			SampleNumbers.Clear();
		base.Dispose(disposing);
	}
}
