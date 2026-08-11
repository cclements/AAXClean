using AAXClean;
using Mpeg4Lib.Descriptors;

namespace Mpeg4Lib.Test;

[TestClass]
public class AudioSpecificConfigTests
{
	private static readonly int[] DefinedSampleRates =
		[96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

	private static AudioSpecificConfig Parse(string hex)
	{
		var asc = AudioSpecificConfig.CreateEmpty();
		asc.AscBlob = Convert.FromHexString(hex);
		return asc;
	}

	[TestMethod]
	public void Aot42_KnownAaxWitness_DispatchesToUsacAndRoundTripsByteForByte()
	{
		byte[] source = Convert.FromHexString("F9464800");

		var asc = Parse("F9464800");

		Assert.AreEqual(42, asc.AudioObjectType);
		Assert.AreEqual(3, asc.SamplingFrequencyIndex);
		Assert.AreEqual(48000, asc.SamplingFrequency);
		Assert.AreEqual(2, asc.ChannelConfiguration);
		Assert.IsInstanceOfType<UsacConfig>(asc.SpecificConfig);

		AaxFile.ApplyAndroidCoverArtWorkaround(asc);

		CollectionAssert.AreEqual(source, asc.AscBlob);
	}

	[TestMethod]
	public void Aot42_LegacyGaFlagSurface_IsSafeAndDoesNotMutateUsacConfig()
	{
		byte[] source = Convert.FromHexString("F9464800");
		IASC asc = AudioSpecificConfig.Parse(source);

		Assert.IsFalse(asc.FrameLengthFlag);
		Assert.IsFalse(asc.DependsOnCoreCoder);

		asc.FrameLengthFlag = true;
		asc.DependsOnCoreCoder = true;

		CollectionAssert.AreEqual(source, ((AudioSpecificConfig)asc).AscBlob);
	}

	[TestMethod]
	public void AndroidCoverArtWorkaround_StillClearsGaFlag()
	{
		var asc = Parse("1392");
		Assert.IsInstanceOfType<GASpecificConfig>(asc.SpecificConfig);
		Assert.IsTrue(asc.DependsOnCoreCoder);

		AaxFile.ApplyAndroidCoverArtWorkaround(asc);

		Assert.IsFalse(asc.DependsOnCoreCoder);
		CollectionAssert.AreEqual(Convert.FromHexString("1390"), asc.AscBlob);
	}

	[TestMethod]
	public void Aot42_AllDefinedSamplingFrequencyIndices_RoundTripByteForByte()
	{
		byte[] witness = Convert.FromHexString("F9464800");

		for (int index = 0; index < DefinedSampleRates.Length; index++)
		{
			byte[] source = (byte[])witness.Clone();
			//For escaped AOT 42, samplingFrequencyIndex occupies bits 4..1 of byte 1.
			source[1] = (byte)((source[1] & 0xe1) | (index << 1));

			var asc = AudioSpecificConfig.CreateEmpty();
			asc.AscBlob = source;

			Assert.AreEqual(index, asc.SamplingFrequencyIndex, $"index {index}");
			Assert.AreEqual(DefinedSampleRates[index], asc.SamplingFrequency, $"index {index}");
			Assert.IsInstanceOfType<UsacConfig>(asc.SpecificConfig, $"index {index}");
			CollectionAssert.AreEqual(source, asc.AscBlob, $"index {index}");
		}
	}

	[TestMethod]
	public void ExplicitHeAacV1_ParsesCoreAndExtensionAndRoundTripsByteForByte()
	{
		byte[] source = Convert.FromHexString("2B920800");

		var asc = Parse("2B920800");

		Assert.AreEqual(5, asc.SignaledAudioObjectType);
		Assert.AreEqual(2, asc.AudioObjectType);
		Assert.AreEqual(AudioExtensionSignaling.Explicit, asc.ExtensionSignaling);
		Assert.AreEqual(5, asc.ExtensionAudioObjectType);
		Assert.IsTrue(asc.SbrPresentFlag.GetValueOrDefault());
		Assert.IsNull(asc.PsPresentFlag);
		Assert.AreEqual(7, asc.SamplingFrequencyIndex);
		Assert.AreEqual(22050, asc.SamplingFrequency);
		Assert.AreEqual(4, asc.ExtensionSamplingFrequencyIndex);
		Assert.AreEqual(44100, asc.ExtensionSamplingFrequency);
		Assert.AreEqual(2, asc.ChannelConfiguration);
		Assert.IsInstanceOfType<GASpecificConfig>(asc.SpecificConfig);
		CollectionAssert.AreEqual(source, asc.AscBlob);
	}

	[TestMethod]
	public void ExplicitHeAacV2_ParsesParametricStereoAndRoundTripsByteForByte()
	{
		byte[] source = Convert.FromHexString("EB8A0800");

		var asc = Parse("EB8A0800");

		Assert.AreEqual(29, asc.SignaledAudioObjectType);
		Assert.AreEqual(2, asc.AudioObjectType);
		Assert.AreEqual(AudioExtensionSignaling.Explicit, asc.ExtensionSignaling);
		Assert.AreEqual(5, asc.ExtensionAudioObjectType);
		Assert.IsTrue(asc.SbrPresentFlag.GetValueOrDefault());
		Assert.IsTrue(asc.PsPresentFlag.GetValueOrDefault());
		Assert.AreEqual(1, asc.ChannelConfiguration);
		Assert.IsInstanceOfType<GASpecificConfig>(asc.SpecificConfig);
		CollectionAssert.AreEqual(source, asc.AscBlob);
	}

	[TestMethod]
	public void ExplicitHeAac_UnsupportedNestedCodecFailsWithItsActualObjectType()
	{
		var exception = Assert.ThrowsExactly<NotSupportedException>(() => Parse("2B9220"));

		StringAssert.Contains(exception.Message, "AudioObjectType of 8");
	}

	[TestMethod]
	public void ExplicitSamplingFrequency_ParsesIndex15AndRoundTripsByteForByte()
	{
		byte[] source = Convert.FromHexString("178061A810");

		var asc = Parse("178061A810");

		Assert.AreEqual(2, asc.AudioObjectType);
		Assert.AreEqual(15, asc.SamplingFrequencyIndex);
		Assert.AreEqual(50000, asc.SamplingFrequency);
		Assert.IsInstanceOfType<GASpecificConfig>(asc.SpecificConfig);
		CollectionAssert.AreEqual(source, asc.AscBlob);
	}

	[TestMethod]
	public void ExplicitHeAacFrequency_ParsesIndex15AndRoundTripsByteForByte()
	{
		byte[] source = Convert.FromHexString("2B9780562A8800");

		var asc = Parse("2B9780562A8800");

		Assert.AreEqual(2, asc.AudioObjectType);
		Assert.AreEqual(15, asc.ExtensionSamplingFrequencyIndex);
		Assert.AreEqual(44117, asc.ExtensionSamplingFrequency);
		Assert.IsInstanceOfType<GASpecificConfig>(asc.SpecificConfig);
		CollectionAssert.AreEqual(source, asc.AscBlob);
	}

	[TestMethod]
	public void SyncExtension_ParsesSbrAndPsAndRoundTripsByteForByte()
	{
		byte[] source = Convert.FromHexString("139056E5A54880");

		var asc = Parse("139056E5A54880");

		Assert.AreEqual(2, asc.SignaledAudioObjectType);
		Assert.AreEqual(2, asc.AudioObjectType);
		Assert.AreEqual(AudioExtensionSignaling.SyncExtension, asc.ExtensionSignaling);
		Assert.AreEqual(5, asc.ExtensionAudioObjectType);
		Assert.IsTrue(asc.SbrPresentFlag.GetValueOrDefault());
		Assert.IsTrue(asc.PsPresentFlag.GetValueOrDefault());
		Assert.AreEqual(4, asc.ExtensionSamplingFrequencyIndex);
		Assert.AreEqual(44100, asc.ExtensionSamplingFrequency);
		Assert.IsInstanceOfType<GASpecificConfig>(asc.SpecificConfig);
		CollectionAssert.AreEqual(source, asc.AscBlob);
	}
}
