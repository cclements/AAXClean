using AAXClean.FrameFilters.Audio;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpeg4Lib.Boxes;
using System.Text;

namespace AAXClean.Test;

[TestClass]
public class Mp4aWriterDac4Tests
{
	[TestMethod]
	public void AacOutput_ReplacesSourceDac4WithEsds()
	{
		byte[] aacAudioSpecificConfig = [0x11, 0x90]; // AAC-LC, 48 kHz, stereo
		using MoovBox sourceMoov = CreateAc4Moov();
		AudioSampleEntry sourceEntry = sourceMoov.AudioTrack.Mdia.Minf.Stbl.Stsd.AudioSampleEntry!;

		Assert.AreEqual("ac-4", sourceEntry.Header.Type);
		Assert.IsNotNull(sourceEntry.Dac4);
		Assert.IsNull(sourceEntry.Esds);

		using var output = new MemoryStream();
		using (var writer = new Mp4aWriter(output, FtypBox.Create("M4A ", 0), sourceMoov, aacAudioSpecificConfig))
		{
			writer.AddFrame([0x01, 0x02], newChunk: true, frameDelta: 1024);
		}

		using var converted = new AAXClean.Mp4File(new MemoryStream(output.ToArray()));
		AudioSampleEntry outputEntry = converted.AudioSampleEntry;
		var outputConfig = outputEntry.Esds?.ES_Descriptor.DecoderConfig.AudioSpecificConfig;

		Assert.AreEqual("mp4a", outputEntry.Header.Type);
		Assert.IsNull(outputEntry.Dac4, "AAC output must not retain the source AC-4 configuration.");
		Assert.IsNotNull(outputConfig, "AAC output must carry an elementary-stream descriptor.");
		Assert.HasCount(1, outputEntry.GetChildren<EsdsBox>());
		Assert.AreEqual(2, outputConfig.AudioObjectType);
		Assert.AreEqual(48_000, outputConfig.SamplingFrequency);
		Assert.AreEqual(2, outputConfig.ChannelConfiguration);
		CollectionAssert.AreEqual(aacAudioSpecificConfig, outputConfig.AscBlob);
		Assert.AreEqual((ushort)48_000, outputEntry.SampleRate);
		Assert.AreEqual((ushort)2, outputEntry.ChannelCount);
	}

	private static MoovBox CreateAc4Moov()
	{
		const uint timescale = 48_000;
		const uint frameDuration = 1024;

		byte[] mvhd = Box("mvhd",
			UInt32s(0, 0, 0, timescale, frameDuration, 0x0001_0000),
			UInt16s(0x0100, 0), new byte[8], new byte[36], new byte[24], UInt32s(2));
		byte[] tkhd = Box("tkhd",
			UInt32s(0, 0, 0, 1, 0, frameDuration), new byte[8],
			UInt16s(0, 0, 0x0100, 0), new byte[36], UInt32s(0, 0));
		byte[] mdhd = Box("mdhd", UInt32s(0, 0, 0, timescale, frameDuration, 0));
		byte[] hdlr = Box("hdlr", UInt32s(0, 0), Encoding.ASCII.GetBytes("soun"), new byte[12]);

		byte[] sampleEntry = Box("ac-4",
			new byte[6], UInt16s(1), new byte[8],
			UInt16s(2, 16, 0, 0, checked((ushort)timescale), 0),
			Box("dac4", [0]));
		byte[] stsd = Box("stsd", UInt32s(0, 1), sampleEntry);
		byte[] stts = Box("stts", UInt32s(0, 1, 1, frameDuration));
		byte[] stsc = Box("stsc", UInt32s(0, 1, 1, 1, 1));
		byte[] stsz = Box("stsz", UInt32s(0, 0, 1, 2));
		byte[] stco = Box("stco", UInt32s(0, 1, 0));
		byte[] stbl = Box("stbl", stsd, stts, stsc, stsz, stco);
		byte[] minf = Box("minf", stbl);
		byte[] mdia = Box("mdia", mdhd, hdlr, minf);
		byte[] trak = Box("trak", tkhd, mdia);

		return BoxFactory.CreateBox<MoovBox>(new MemoryStream(Box("moov", mvhd, trak)), parent: null);
	}

	private static byte[] Box(string type, params byte[][] payloads)
	{
		using var stream = new MemoryStream();
		WriteUInt32(stream, checked((uint)(8 + payloads.Sum(payload => payload.Length))));
		stream.Write(Encoding.ASCII.GetBytes(type));
		foreach (byte[] payload in payloads)
			stream.Write(payload);
		return stream.ToArray();
	}

	private static byte[] UInt32s(params uint[] values)
	{
		using var stream = new MemoryStream();
		foreach (uint value in values)
			WriteUInt32(stream, value);
		return stream.ToArray();
	}

	private static byte[] UInt16s(params ushort[] values)
	{
		using var stream = new MemoryStream();
		foreach (ushort value in values)
		{
			stream.WriteByte((byte)(value >> 8));
			stream.WriteByte((byte)value);
		}
		return stream.ToArray();
	}

	private static void WriteUInt32(Stream stream, uint value)
	{
		Span<byte> bytes = [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
		stream.Write(bytes);
	}
}
