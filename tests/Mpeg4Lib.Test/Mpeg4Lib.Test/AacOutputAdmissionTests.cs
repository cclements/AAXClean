using AAXClean;
using AAXClean.FrameFilters.Audio;
using Mpeg4Lib.Boxes;

namespace Mpeg4Lib.Test;

[TestClass]
public class AacOutputAdmissionTests
{
    [TestMethod]
    [DataRow(0, 2)] // 96 kHz would narrow to 30,464 in the 16.16 field.
    [DataRow(1, 2)] // 88.2 kHz would narrow to 22,664.
    [DataRow(4, 0)] // Program-config-element layout is not an explicit channel count.
    [DataRow(4, 3)]
    [DataRow(4, 7)]
    public void Unsupported_output_format_fails_before_touching_the_stream(int rateIndex, int channels)
    {
        using Mp4File source = Source();
        using MemoryStream output = SeededOutput();
        byte[] original = output.ToArray();
        Assert.ThrowsExactly<NotSupportedException>(() => new Mp4aWriter(output, source.Ftyp, source.Moov, Asc(rateIndex, channels)));
        Assert.AreEqual(2L, output.Position);
        CollectionAssert.AreEqual(original, output.ToArray());
        Assert.AreEqual(16000u, source.Moov.AudioTrack.Mdia.Mdhd.Timescale);
    }

    [TestMethod]
    public void Null_config_fails_before_touching_the_stream()
    {
        using Mp4File source = Source();
        using MemoryStream output = SeededOutput();
        byte[] original = output.ToArray();
        Assert.ThrowsExactly<ArgumentNullException>(() => new Mp4aWriter(output, source.Ftyp, source.Moov, null!));
        Assert.AreEqual(2L, output.Position);
        CollectionAssert.AreEqual(original, output.ToArray());
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public void Truncated_config_fails_before_touching_the_stream(int length)
    {
        using Mp4File source = Source();
        using MemoryStream output = SeededOutput();
        byte[] original = output.ToArray();
        Assert.ThrowsExactly<InvalidDataException>(() => new Mp4aWriter(output, source.Ftyp, source.Moov, Asc(4, 2)[..length]));
        Assert.AreEqual(2L, output.Position);
        CollectionAssert.AreEqual(original, output.ToArray());
    }

    [TestMethod]
    [DataRow(2, 64000, 1)]
    [DataRow(4, 44100, 2)]
    [DataRow(12, 7350, 1)]
    public void Representable_mono_stereo_config_keeps_exact_metadata(int index, int rate, int channels)
    {
        using Mp4File source = Source();
        using MemoryStream output = new();
        using (var writer = new Mp4aWriter(output, source.Ftyp, source.Moov, Asc(index, channels)))
            writer.AddFrame([1], true, 1024);
        using Mp4File reopened = new(new MemoryStream(output.ToArray()));
        AudioSampleEntry entry = reopened.AudioSampleEntry;
        Assert.AreEqual((ushort)rate, entry.SampleRate);
        Assert.AreEqual((ushort)channels, entry.ChannelCount);
        Assert.AreEqual(rate, entry.Esds!.ES_Descriptor.DecoderConfig.AudioSpecificConfig.SamplingFrequency);
        Assert.AreEqual((uint)rate, reopened.Moov.AudioTrack.Mdia.Mdhd.Timescale);
        Assert.AreEqual((uint)rate, reopened.Moov.Mvhd.Timescale);
    }

    private static byte[] Asc(int rateIndex, int channels) => [(byte)((2 << 3) | (rateIndex >> 1)), (byte)(((rateIndex & 1) << 7) | (channels << 3))];
    private static Mp4File Source() => new(new MemoryStream(PresentationWindowContractTests.CreateAc4Source(600, 16000, 1024, [1])));
    private static MemoryStream SeededOutput()
    {
        MemoryStream result = new();
        result.Write([9, 8, 7, 6, 5]);
        result.Position = 2;
        return result;
    }
}
