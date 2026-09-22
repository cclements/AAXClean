using AAXClean;
using AAXClean.FrameFilters.Audio;
using Mpeg4Lib.Boxes;

namespace Mpeg4Lib.Test;

[TestClass]
public class EncoderMovieTimescaleTests
{
    [TestMethod]
    [DataRow(1L)]
    [DataRow(128L)]
    [DataRow(1024L)]
    [DataRow(1501L)]
    [DataRow(16001L)]
    [DataRow(4294967297L)]
    public void Reencoded_movie_represents_each_output_sample_exactly(long samples)
    {
        using Mp4File source = new(new MemoryStream(PresentationWindowContractTests.CreateAc4Source(600, 16000, 1024, [1])));
        using MemoryStream output = new();
        using Mp4aWriter writer = new(output, source.Ftyp, source.Moov, [0x12, 0x10]); // 44.1 kHz AAC-LC stereo
        long media = samples + 2048;
        while (media > 0)
        {
            uint duration = (uint)Math.Min(media, uint.MaxValue);
            writer.AddFrame([1], true, duration, true);
            media -= duration;
        }
        writer.SetEditList(2048, samples);
        writer.Close();
        using Mp4File reopened = new(new MemoryStream(output.ToArray()));
        Assert.AreEqual(samples, reopened.PresentedDurationSamples);
        Assert.AreEqual(2048L, reopened.PresentationStartSample);
        Assert.AreEqual(44100u, reopened.Moov.Mvhd.Timescale);
        Assert.AreEqual((ulong)samples, reopened.Moov.Mvhd.Duration);
        Assert.AreEqual((ulong)samples, reopened.Moov.AudioTrack.Tkhd.Duration);
        Assert.AreEqual(600u, source.Moov.Mvhd.Timescale, "Output rescaling must not mutate the source.");
        if (reopened.Moov.TextTrack is { } text)
        {
            Assert.AreEqual(44100u, text.Mdia.Mdhd.Timescale);
            Assert.AreEqual((ulong)samples, text.Mdia.Mdhd.Duration);
            Assert.AreEqual((ulong)samples, text.Tkhd.Duration);
        }
        if (samples > uint.MaxValue)
            Assert.AreEqual(1, (int)reopened.Moov.Mvhd.Version);
    }
}
