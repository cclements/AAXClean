using AAXClean;
using AAXClean.Chunks;
using AAXClean.FrameFilters;
using Mpeg4Lib.Boxes;
using AAXClean.FrameFilters.Audio;
using Mpeg4Lib.Chunks;

namespace Mpeg4Lib.Test;

[TestClass]
public class AacPrerollTests
{
    [TestMethod]
    [DataRow(false, 0L, 1024u)]
    [DataRow(false, 1500L, 1024u)]
    [DataRow(true, 0L, 1024u)]
    [DataRow(true, 1500L, 1024u)]
    [DataRow(false, 0L, 960u)]
    [DataRow(false, 1500L, 960u)]
    [DataRow(true, 0L, 960u)]
    [DataRow(true, 1500L, 960u)]
    public async Task Trim_and_each_split_retain_a_prior_aac_access_unit(bool split, long start, uint frameDuration)
    {
        using Mp4File source = Source(frameDuration: frameDuration);
        ChapterInfo chapters = new(TimeSpan.FromTicks(start * 625));
        chapters.AddChapter("first", TimeSpan.FromTicks(1500 * 625));
        chapters.AddChapter("second", TimeSpan.FromTicks(1500 * 625));
        List<MemoryStream> outputs = [];
        if (split)
            await source.ConvertToMultiMp4aAsync(chapters, callback => { var output = new MemoryStream(); outputs.Add(output); callback.OutputFile = output; });
        else
        {
            var output = new MemoryStream(); outputs.Add(output);
            await source.ConvertToMp4aAsync(output, chapters);
        }
        Assert.HasCount(split ? 2 : 1, outputs);
        for (int part = 0; part < outputs.Count; part++)
        {
            using Mp4File result = new(new MemoryStream(outputs[part].ToArray()));
            Assert.IsGreaterThanOrEqualTo((long)frameDuration, result.PresentationStartSample,
                "AAC-LC needs a prior access unit to establish decoder overlap state.");
            Assert.AreEqual(split ? 1500L : 3000L, result.PresentedDurationSamples);
            var first = new ChunkEntryList(result.Moov.AudioTrack).First();
            result.InputStream.Position = first.ChunkOffset;
            int firstSourceFrame = result.InputStream.ReadByte();
            long sourceWindow = 2048 + start + (split ? part * 1500 : 0);
            Assert.AreEqual(sourceWindow, firstSourceFrame * (long)frameDuration + result.PresentationStartSample);
        }
    }

    [TestMethod]
    public async Task Original_media_start_does_not_require_nonexistent_preroll()
    {
        using Mp4File source = Source(editStart: 0);
        using var output = new MemoryStream();
        await source.ConvertToMp4aAsync(output);
        using Mp4File result = new(new MemoryStream(output.ToArray()));
        Assert.AreEqual(0L, result.PresentationStartSample);
        Assert.AreEqual(source.PresentedDurationSamples, result.PresentedDurationSamples);
    }

    [TestMethod]
    public async Task Fragmented_aac_retains_a_packet_from_the_previous_indexed_segment()
    {
        using var dash = new DashFile(new MemoryStream(DashPresentationDurationTests.CreateTwoSegmentDash(16000, 1024)));
        var entry = dash.AudioSampleEntry;
        entry.Children.Remove(entry.Dac4!);
        entry.Header.ChangeAtomName("mp4a");
        EsdsBox.CreateEmpty(entry).ES_Descriptor.DecoderConfig.AudioSpecificConfig.AscBlob = [0x14, 0x08];
        using var filter = new RecordingFilter();
        var reader = new DashChunkReader(dash, dash.InputStream, TimeSpan.FromMilliseconds(192), TimeSpan.FromMilliseconds(224));
        reader.AddTrack(dash.Moov.AudioTrack, filter);
        await reader.RunAsync(new CancellationTokenSource());
        CollectionAssert.AreEqual(new long?[] { 2048, 3072 }, filter.Starts);
    }

    [TestMethod]
    [DataRow(1920L, 960L)]
    [DataRow(2944L, 1920L)]
    public void Flat_reader_preroll_crosses_sample_duration_runs(long requested, long expected)
    {
        using Mp4File source = Source(editStart: 0, mixedDurations: true);
        using var filter = new RecordingFilter();
        var reader = new InspectingReader(source.InputStream, TimeSpan.FromTicks(requested * 625));
        reader.AddTrack(source.Moov.AudioTrack, filter);
        Assert.AreEqual(expected, reader.DispatchStart);
    }

    private sealed class InspectingReader(Stream stream, TimeSpan start) : ChunkReader(stream, start, TimeSpan.FromSeconds(1))
    {
        public long DispatchStart => TrackEntries.Values.Single().DispatchStartSample;
    }

    private sealed class RecordingFilter : FrameFinalBase<FrameEntry>
    {
        public List<long?> Starts = [];
        protected override int InputBufferSize => 1;
        protected override Task FlushAsync() => Task.CompletedTask;
        protected override Task PerformFilteringAsync(FrameEntry input) { Starts.Add(input.StartSample); return Task.CompletedTask; }
    }

    private static Mp4File Source(long editStart = 2048, uint frameDuration = 1024, bool mixedDurations = false)
    {
        using var seed = new Mp4File(new MemoryStream(PresentationWindowContractTests.CreateAc4Source(16000, 16000, 16000, [1, 2, 3, 4])));
        var bytes = new MemoryStream();
        using (var writer = new Mp4aWriter(bytes, seed.Ftyp, seed.Moov, frameDuration == 960 ? [0x14, 0x0c] : [0x14, 0x08]))
        {
            // Deliberately separate chunks expose reader dispatch errors as well as sink errors.
            // Structural sample identifiers; these are not decodable AAC access units.
            for (byte frame = 0; frame < 16; frame++) writer.AddFrame([frame, 0], true, mixedDurations && frame < 2 ? 960u : frameDuration);
            writer.SetEditList(editStart, 8000);
            writer.Close();
        }
        bytes.Position = 0;
        return new Mp4File(bytes);
    }
}
