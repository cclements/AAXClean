using Mpeg4Lib.Descriptors;

namespace Mpeg4Lib.Test;

[TestClass]
public class AudioSpecificConfigUsacTests
{
	[TestMethod]
	public void Aot42_AndroidGaFlagWorkaround_DoesNotMutateUsacPayload()
	{
		byte[] source = Convert.FromHexString("F9464800");
		var asc = AudioSpecificConfig.CreateEmpty();
		asc.AscBlob = source;

		Assert.AreEqual(42, asc.AudioObjectType);
		Assert.AreEqual(48_000, asc.SamplingFrequency);
		Assert.AreEqual(2, asc.ChannelConfiguration);

		// AaxFile clears this GA-only flag as an Android cover-art workaround.
		asc.DependsOnCoreCoder = false;

		CollectionAssert.AreEqual(source, asc.AscBlob);
	}

	[TestMethod]
	public void Aot42_LegacyGaFlags_AreHarmlessThroughBothPublicParseSurfaces()
	{
		byte[] source = Convert.FromHexString("F9464800");
		var descriptor = AudioSpecificConfig.CreateEmpty();
		descriptor.AscBlob = source;
		IASC parsed = AudioSpecificConfig.Parse(source);

		AssertFlagsAreHarmless(descriptor);
		AssertFlagsAreHarmless(parsed);
		CollectionAssert.AreEqual(source, descriptor.AscBlob);
	}

	[TestMethod]
	public void GaSpecificConfig_DependsOnCoreCoder_RemainsMutable()
	{
		var asc = AudioSpecificConfig.CreateEmpty();
		asc.AscBlob = Convert.FromHexString("1392");
		IASC parsed = AudioSpecificConfig.Parse(Convert.FromHexString("1392"));

		Assert.IsTrue(asc.DependsOnCoreCoder);
		Assert.IsTrue(parsed.DependsOnCoreCoder);

		asc.DependsOnCoreCoder = false;
		parsed.DependsOnCoreCoder = false;

		Assert.IsFalse(asc.DependsOnCoreCoder);
		Assert.IsFalse(parsed.DependsOnCoreCoder);
		CollectionAssert.AreEqual(Convert.FromHexString("1390"), asc.AscBlob);
	}

	private static void AssertFlagsAreHarmless(IASC asc)
	{
		Assert.IsFalse(asc.FrameLengthFlag);
		Assert.IsFalse(asc.DependsOnCoreCoder);

		asc.FrameLengthFlag = true;
		asc.DependsOnCoreCoder = true;

		Assert.IsFalse(asc.FrameLengthFlag);
		Assert.IsFalse(asc.DependsOnCoreCoder);
	}
}
