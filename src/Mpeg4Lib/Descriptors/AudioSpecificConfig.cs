using Mpeg4Lib.Util;
using System;
using System.IO;

namespace Mpeg4Lib.Descriptors;

public interface IASC
{
	int AudioObjectType { get; set; }
	int SamplingFrequency { get; set; }
	int ChannelConfiguration { get; set; }

	//GASpecificConfig in ISO/IEC 14496-3 Subpart 4 4.4.1 (pp 487)
	bool FrameLengthFlag { get; set; }
	bool DependsOnCoreCoder { get; set; }
}

public enum AudioExtensionSignaling
{
	None,
	Explicit,
	SyncExtension,
}

public abstract class AudioObjectSpecificConfig
{
	internal abstract void Render(BitWriter writer);
}

//GASpecificConfig in ISO/IEC 14496-3 Subpart 4 4.4.1.
public sealed class GASpecificConfig : AudioObjectSpecificConfig
{
	private readonly BitReader source;
	private readonly int remainderPosition;

	internal int? EndPosition { get; }

	internal GASpecificConfig(BitReader reader, int audioObjectType, int channelConfiguration)
	{
		source = reader;
		FrameLengthFlag = reader.ReadBool();
		DependsOnCoreCoder = reader.ReadBool();
		remainderPosition = reader.Position;
		EndPosition = FindEndPosition(reader, audioObjectType, channelConfiguration);
	}

	public bool FrameLengthFlag { get; set; }
	public bool DependsOnCoreCoder { get; set; }

	internal override void Render(BitWriter writer)
	{
		writer.Write(FrameLengthFlag ? 1u : 0, 1);
		writer.Write(DependsOnCoreCoder ? 1u : 0, 1);

		int restorePosition = source.Position;
		source.Position = remainderPosition;
		source.CopyTo(writer);
		source.Position = restorePosition;
	}

	private int? FindEndPosition(BitReader reader, int audioObjectType, int channelConfiguration)
	{
		int restorePosition = reader.Position;
		try
		{
			if (DependsOnCoreCoder)
				reader.Read(14); //coreCoderDelay

			bool extensionFlag = reader.ReadBool();

			//A program_config_element has variable-length syntax. Preserve it, but do not
			//guess where a following sync extension starts.
			if (channelConfiguration == 0)
				return null;

			if (audioObjectType is 6 or 20)
				reader.Read(3); //layerNr

			if (extensionFlag)
			{
				if (audioObjectType == 22)
				{
					reader.Read(5); //numOfSubFrame
					reader.Read(11); //layer_length
				}

				if (audioObjectType is 17 or 19 or 20 or 23)
				{
					reader.Read(1); //aacSectionDataResilienceFlag
					reader.Read(1); //aacScalefactorDataResilienceFlag
					reader.Read(1); //aacSpectralDataResilienceFlag
				}

				reader.Read(1); //extensionFlag3
			}

			return reader.Position;
		}
		catch (InvalidOperationException)
		{
			//The legacy surface only required the first two GA bits. Preserve a short or
			//otherwise unbounded payload, but do not claim that its extension was parsed.
			return null;
		}
		finally
		{
			reader.Position = restorePosition;
		}
	}
}

//USAC's UsacConfig has different syntax from GASpecificConfig. Mpeg4Lib does not
//decode that codec-specific payload; it retains every bit verbatim.
public sealed class UsacConfig : AudioObjectSpecificConfig
{
	private readonly BitReader source;
	private readonly int startPosition;

	internal UsacConfig(BitReader reader)
	{
		source = reader;
		startPosition = reader.Position;
		reader.Position = reader.Length;
	}

	internal override void Render(BitWriter writer)
	{
		int restorePosition = source.Position;
		source.Position = startPosition;
		source.CopyTo(writer);
		source.Position = restorePosition;
	}
}

//ISO/IEC 14496-3 (MPEG-4 Systems) Section 1.6 (pp 52).
//Supports the existing GA object types and USAC, plus explicit SBR/PS wrappers
//whose nested object type resolves to one of those supported configurations.
public class AudioSpecificConfig : BaseDescriptor, IASC
{
	private static readonly int[] ASC_SampleRates =
		[96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

	public static readonly int MinSampleRate = ASC_SampleRates[^1];
	public static readonly int MaxSampleRate = ASC_SampleRates[0];

	private static readonly byte[] SupportedSpecificConfigObjectTypes = [1, 2, 3, 4, 6, 7, 17, 19, 20, 21, 22, 23, 42];
	private const byte AOT_ESCAPE = 31;
	private const int AOT_SBR = 5;
	private const int AOT_PS = 29;
	private const int AOT_USAC = 42;
	private const uint SYNC_EXTENSION_TYPE = 0x2b7;
	private const uint PS_SYNC_EXTENSION_TYPE = 0x548;

	private int parsedSamplingFrequency;
	private int parsedSamplingFrequencyIndex;
	private int? explicitSignaledAudioObjectType;

	public override int InternalSize => base.InternalSize + AscBlob.Length;

	public byte[] AscBlob
	{
		get => GetAscBlob();
		set => LoadAscBlob(value);
	}

	public AudioObjectSpecificConfig SpecificConfig { get; private set; } = null!;
	public AudioExtensionSignaling ExtensionSignaling { get; private set; }
	public int SignaledAudioObjectType
		=> explicitSignaledAudioObjectType ?? AudioObjectType;
	public int SamplingFrequencyIndex
		=> GetRenderedFrequencyIndex(SamplingFrequency, parsedSamplingFrequency, parsedSamplingFrequencyIndex);
	public int? ExtensionAudioObjectType { get; private set; }
	public int? ExtensionSamplingFrequency { get; private set; }
	public int? ExtensionSamplingFrequencyIndex { get; private set; }
	public int? ExtensionChannelConfiguration { get; private set; }
	public bool? SbrPresentFlag { get; private set; }
	public bool? PsPresentFlag { get; private set; }

	public AudioSpecificConfig(Stream file, DescriptorHeader header) : base(file, header)
	{
		LoadAscBlob(file.ReadBlock(Header.TotalBoxSize - Header.HeaderSize));
	}

	private AudioSpecificConfig() : base(5)
	{
		//AAC-LC, 44.1kHz, 2 channels. Use an arbitrary valid configuration
		//to initialize a descriptor that callers will update before rendering.
		LoadAscBlob([0x13, 0x90]);
	}

	public static AudioSpecificConfig CreateEmpty()
		=> new();

	// Preserve the original public return type so already-compiled consumers keep
	// resolving the same CLR member signature. The concrete object remains an
	// AudioSpecificConfig for callers that need the richer model.
	public static IASC Parse(byte[] ascBlob)
	{
		var asc = new AudioSpecificConfig();
		asc.LoadAscBlob(ascBlob);
		return asc;
	}

	private void LoadAscBlob(byte[] ascBlob)
	{
		ArgumentNullException.ThrowIfNull(ascBlob);
		if (ascBlob.Length == 0)
			throw new InvalidDataException("AudioSpecificConfig is empty.");

		var bitReader = new BitReader((byte[])ascBlob.Clone());
		ResetExtensionState();

		int signaledAudioObjectType = ReadAudioObjectType(bitReader);
		(parsedSamplingFrequencyIndex, SamplingFrequency) = ReadFrequency(bitReader);
		parsedSamplingFrequency = SamplingFrequency;
		ChannelConfiguration = (int)bitReader.Read(4);

		if (signaledAudioObjectType is AOT_SBR or AOT_PS)
		{
			explicitSignaledAudioObjectType = signaledAudioObjectType;
			ExtensionSignaling = AudioExtensionSignaling.Explicit;
			ExtensionAudioObjectType = AOT_SBR;
			SbrPresentFlag = true;
			PsPresentFlag = signaledAudioObjectType == AOT_PS ? true : null;

			(int extensionIndex, int extensionFrequency) = ReadFrequency(bitReader);
			ExtensionSamplingFrequencyIndex = extensionIndex;
			ExtensionSamplingFrequency = extensionFrequency;
			AudioObjectType = ReadAudioObjectType(bitReader);

			if (AudioObjectType == 22)
				ExtensionChannelConfiguration = (int)bitReader.Read(4);
		}
		else
		{
			AudioObjectType = signaledAudioObjectType;
		}

		if (Array.IndexOf(SupportedSpecificConfigObjectTypes, (byte)AudioObjectType) < 0)
			throw new NotSupportedException($"{nameof(AudioObjectType)} of {AudioObjectType} is unsupported");

		if (AudioObjectType == AOT_USAC)
		{
			SpecificConfig = new UsacConfig(bitReader);
		}
		else
		{
			var gaSpecificConfig = new GASpecificConfig(bitReader, AudioObjectType, ChannelConfiguration);
			SpecificConfig = gaSpecificConfig;

			if (ExtensionSignaling == AudioExtensionSignaling.None
				&& gaSpecificConfig.EndPosition is int extensionPosition)
			{
				TryLoadSyncExtension(bitReader, extensionPosition);
			}
		}
	}

	private void ResetExtensionState()
	{
		explicitSignaledAudioObjectType = null;
		ExtensionSignaling = AudioExtensionSignaling.None;
		ExtensionAudioObjectType = null;
		ExtensionSamplingFrequency = null;
		ExtensionSamplingFrequencyIndex = null;
		ExtensionChannelConfiguration = null;
		SbrPresentFlag = null;
		PsPresentFlag = null;
	}

	private void TryLoadSyncExtension(BitReader bitReader, int extensionPosition)
	{
		int restorePosition = bitReader.Position;
		try
		{
			bitReader.Position = extensionPosition;
			if (bitReader.Length - bitReader.Position < 16
				|| bitReader.Read(11) != SYNC_EXTENSION_TYPE)
				return;

			ExtensionSignaling = AudioExtensionSignaling.SyncExtension;
			ExtensionAudioObjectType = ReadAudioObjectType(bitReader);

			if (ExtensionAudioObjectType is AOT_SBR or 22)
			{
				SbrPresentFlag = bitReader.ReadBool();
				if (SbrPresentFlag == true)
				{
					(int extensionIndex, int extensionFrequency) = ReadFrequency(bitReader);
					ExtensionSamplingFrequencyIndex = extensionIndex;
					ExtensionSamplingFrequency = extensionFrequency;
				}

				if (ExtensionAudioObjectType == 22)
					ExtensionChannelConfiguration = (int)bitReader.Read(4);
			}

			if (ExtensionAudioObjectType == AOT_SBR && bitReader.Length - bitReader.Position >= 12)
			{
				int psExtensionPosition = bitReader.Position;
				if (bitReader.Read(11) == PS_SYNC_EXTENSION_TYPE)
					PsPresentFlag = bitReader.ReadBool();
				else
					bitReader.Position = psExtensionPosition;
			}
		}
		catch (InvalidOperationException ex)
		{
			throw new InvalidDataException("AudioSpecificConfig contains a truncated sync extension.", ex);
		}
		finally
		{
			bitReader.Position = restorePosition;
		}
	}

	public override void Render(Stream file)
	{
		file.Write(AscBlob);
	}

	private byte[] GetAscBlob()
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(AudioObjectType, 0, nameof(AudioObjectType));
		ArgumentOutOfRangeException.ThrowIfGreaterThan(AudioObjectType, 32 + 63, nameof(AudioObjectType));
		ArgumentOutOfRangeException.ThrowIfLessThan(ChannelConfiguration, 0, nameof(ChannelConfiguration));
		ArgumentOutOfRangeException.ThrowIfGreaterThan(ChannelConfiguration, 7, nameof(ChannelConfiguration));

		var writer = new BitWriter();
		if (ExtensionSignaling == AudioExtensionSignaling.Explicit)
		{
			WriteAudioObjectType(writer, explicitSignaledAudioObjectType
				?? throw new InvalidOperationException("Explicit extension signaling has no outer audio object type."));
			WriteFrequency(writer, SamplingFrequency, SamplingFrequencyIndex);
			writer.Write((uint)ChannelConfiguration, 4);
			WriteFrequency(
				writer,
				ExtensionSamplingFrequency
					?? throw new InvalidOperationException("Explicit extension signaling has no extension sampling frequency."),
				ExtensionSamplingFrequencyIndex
					?? throw new InvalidOperationException("Explicit extension signaling has no extension sampling-frequency index."));
			WriteAudioObjectType(writer, AudioObjectType);

			if (AudioObjectType == 22)
				writer.Write((uint)(ExtensionChannelConfiguration ?? 0), 4);
		}
		else
		{
			WriteAudioObjectType(writer, AudioObjectType);
			WriteFrequency(writer, SamplingFrequency, SamplingFrequencyIndex);
			writer.Write((uint)ChannelConfiguration, 4);
		}

		if (AudioObjectType == AOT_USAC && SpecificConfig is not UsacConfig)
			throw new InvalidOperationException("USAC requires a UsacConfig payload.");
		if (AudioObjectType != AOT_USAC && SpecificConfig is not GASpecificConfig)
			throw new InvalidOperationException($"Audio object type {AudioObjectType} requires a GASpecificConfig payload.");

		SpecificConfig.Render(writer);
		return writer.ToByteArray();
	}

	private static int ReadAudioObjectType(BitReader reader)
	{
		int audioObjectType = (int)reader.Read(5);
		return audioObjectType == AOT_ESCAPE
			? (int)reader.Read(6) + 32
			: audioObjectType;
	}

	private static void WriteAudioObjectType(BitWriter writer, int audioObjectType)
	{
		if (audioObjectType < AOT_ESCAPE)
		{
			writer.Write((uint)audioObjectType, 5);
		}
		else
		{
			writer.Write(AOT_ESCAPE, 5);
			writer.Write((uint)audioObjectType - 32, 6);
		}
	}

	private static (int Index, int Frequency) ReadFrequency(BitReader reader)
	{
		int samplingFrequencyIndex = (int)reader.Read(4);
		if (samplingFrequencyIndex <= 12)
			return (samplingFrequencyIndex, ASC_SampleRates[samplingFrequencyIndex]);
		if (samplingFrequencyIndex == 15)
			return (samplingFrequencyIndex, (int)reader.Read(24));

		throw new NotSupportedException($"Sampling frequency index of {samplingFrequencyIndex} is reserved.");
	}

	private static int GetRenderedFrequencyIndex(int frequency, int parsedFrequency, int parsedIndex)
	{
		if (frequency == parsedFrequency)
			return parsedIndex;

		int predefinedIndex = Array.IndexOf(ASC_SampleRates, frequency);
		return predefinedIndex >= 0 ? predefinedIndex : 15;
	}

	private static void WriteFrequency(BitWriter writer, int frequency, int index)
	{
		if (index is < 0 or > 15 or 13 or 14)
			throw new ArgumentOutOfRangeException(nameof(index), index, "Sampling-frequency index must be defined or explicit (15).");

		writer.Write((uint)index, 4);
		if (index == 15)
		{
			ArgumentOutOfRangeException.ThrowIfLessThan(frequency, 1, nameof(frequency));
			ArgumentOutOfRangeException.ThrowIfGreaterThan(frequency, 0x00ff_ffff, nameof(frequency));
			writer.Write((uint)frequency, 24);
		}
	}

	public int AudioObjectType { get; set; }
	public int SamplingFrequency { get; set; }
	public int ChannelConfiguration { get; set; }

	public bool FrameLengthFlag
	{
		get => SpecificConfig is GASpecificConfig gaSpecificConfig && gaSpecificConfig.FrameLengthFlag;
		set
		{
			//Kept as a no-op for non-GA object types to preserve the legacy IASC surface
			//without treating bits from UsacConfig as GA flags.
			if (SpecificConfig is GASpecificConfig gaSpecificConfig)
				gaSpecificConfig.FrameLengthFlag = value;
		}
	}

	public bool DependsOnCoreCoder
	{
		get => SpecificConfig is GASpecificConfig gaSpecificConfig && gaSpecificConfig.DependsOnCoreCoder;
		set
		{
			if (SpecificConfig is GASpecificConfig gaSpecificConfig)
				gaSpecificConfig.DependsOnCoreCoder = value;
		}
	}
}
