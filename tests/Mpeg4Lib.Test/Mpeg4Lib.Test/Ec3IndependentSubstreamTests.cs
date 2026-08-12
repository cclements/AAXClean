using Mpeg4Lib.Boxes;
using Mpeg4Lib.Boxes.EC3SpecificBox;
using Mpeg4Lib.Util;

namespace Mpeg4Lib.Test;

[TestClass]
public class Ec3IndependentSubstreamTests
{
	private const uint ChannelLocationMask =
		(1u << (int)ChannelLocation.Lc_Rc_Pair) |
		(1u << (int)ChannelLocation.Cs) |
		(1u << (int)ChannelLocation.LFE2);

	[TestMethod]
	public void Channel_location_enum_retains_public_numeric_abi()
	{
		Assert.AreEqual(typeof(short), Enum.GetUnderlyingType(typeof(ChannelLocation)));
		CollectionAssert.AreEqual(
			Enumerable.Range(0, 9).Select(value => (short)value).ToArray(),
			Enum.GetValues<ChannelLocation>().Select(value => (short)value).ToArray());
		Assert.IsFalse(typeof(ChannelLocation).IsDefined(typeof(FlagsAttribute), inherit: false));
	}

	[TestMethod]
	public void Dependent_substream_count_and_raw_channel_field_are_preserved()
	{
		var substream = ParseIndependentSubstream();

		Assert.AreEqual((byte)1, substream.num_dep_sub);
		Assert.AreEqual((ChannelLocation)ChannelLocationMask, substream.chan_loc);
	}

	[TestMethod]
	public void Dec3_metadata_counts_dependent_channel_locations()
	{
		var writer = new BitWriter();
		writer.Write(256, 13); // data_rate: 256 kbps
		writer.Write(0, 3);   // one independent substream
		WriteIndependentSubstream(writer);

		byte[] payload = writer.ToByteArray();
		using var file = new MemoryStream();
		WriteUInt32BigEndian(file, (uint)(8 + payload.Length));
		file.Write("dec3"u8);
		file.Write(payload);
		file.Position = 0;

		var dec3 = BoxFactory.CreateBox<Dec3Box>(file, parent: null);

		Assert.AreEqual(10, dec3.NumberOfChannels,
			"5.1 plus the Lc/Rc pair, Cs, and LFE2 dependent locations must report ten channels.");
	}

	private static Ec3IndependentSubstream ParseIndependentSubstream()
	{
		var writer = new BitWriter();
		WriteIndependentSubstream(writer);
		return new Ec3IndependentSubstream(new BitReader(writer.ToByteArray()));
	}

	private static void WriteIndependentSubstream(BitWriter writer)
	{
		writer.Write(0, 2);   // fscod: 48 kHz
		writer.Write(16, 5);  // bsid: E-AC-3
		writer.Write(0, 1);   // reserved
		writer.Write(0, 1);   // asvc: main audio service
		writer.Write(0, 3);   // bsmod
		writer.Write(7, 3);   // acmod: L C R Ls Rs (five channels)
		writer.Write(1, 1);   // lfeon (one channel)
		writer.Write(0, 3);   // reserved
		writer.Write(1, 4);   // num_dep_sub
		writer.Write(ChannelLocationMask, 9);
	}

	private static void WriteUInt32BigEndian(Stream stream, uint value)
	{
		Span<byte> bytes =
		[
			(byte)(value >> 24),
			(byte)(value >> 16),
			(byte)(value >> 8),
			(byte)value,
		];
		stream.Write(bytes);
	}
}
