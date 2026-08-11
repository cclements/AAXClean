using Mpeg4Lib.Boxes.EC3SpecificBox;
using Mpeg4Lib.Util;

namespace Mpeg4Lib.Test;

[TestClass]
public class Ec3IndependentSubstreamTests
{
	[TestMethod]
	public void Dependent_substream_channel_locations_are_preserved_and_counted()
	{
		CollectionAssert.AreEqual(
			Enumerable.Range(0, 9).Select(value => (short)value).ToArray(),
			Enum.GetValues<ChannelLocation>().Select(value => (short)value).ToArray(),
			"The public enum's legacy ordinal values are a compatibility contract, not raw chan_loc masks.");
		Assert.IsFalse(typeof(ChannelLocation).IsDefined(typeof(FlagsAttribute), inherit: false));

		var writer = new BitWriter();
		writer.Write(0, 2);   // fscod: 48 kHz
		writer.Write(16, 5);  // bsid: E-AC-3
		writer.Write(0, 1);   // reserved
		writer.Write(1, 1);   // asvc
		writer.Write(0, 3);   // bsmod
		writer.Write(7, 3);   // acmod: L C R Ls Rs (5 channels)
		writer.Write(1, 1);   // lfeon (one channel)
		writer.Write(0, 3);   // reserved
		writer.Write(1, 4);   // num_dep_sub
		writer.Write(0b101000001, 9); // Lc/Rc pair, Cs, LFE2 (four channels)

		var substream = new Ec3IndependentSubstream(new BitReader(writer.ToByteArray()));

		Assert.AreEqual((byte)1, substream.num_dep_sub);
		Assert.AreEqual((ChannelLocation)0b101000001, substream.chan_loc,
			"The parsed field must retain every raw chan_loc bit.");
		Assert.IsTrue(substream.HasChannelLocation(ChannelLocation.Lc_Rc_Pair));
		Assert.IsTrue(substream.HasChannelLocation(ChannelLocation.Cs));
		Assert.IsTrue(substream.HasChannelLocation(ChannelLocation.LFE2));
		Assert.IsFalse(substream.HasChannelLocation(ChannelLocation.Lrs_Rrs_Pair));
		Assert.AreEqual(10, substream.ChannelCount());
	}
}
