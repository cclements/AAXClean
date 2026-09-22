using AAXClean;
using AAXClean.FrameFilters;
using AAXClean.FrameFilters.Audio;
using Mpeg4Lib.Boxes;
using Mpeg4Lib.Chunks;

namespace Mpeg4Lib.Test;

[TestClass]
public class SyncAdmissionTests
{
    [TestMethod]
    [DataRow("FFF", "")]
    [DataRow("???", "")]
    [DataRow("F?F", "")]
    [DataRow("T?F", "1")]
    [DataRow("FTF", "2")]
    [DataRow("TTT", "all")]
    public void Writer_preserves_confirmed_sync_membership_including_zero(string flags, string expected)
    {
        using Mp4File source = Source();
        using MemoryStream output = new();
        using (var writer = new Mp4aWriter(output, source.Ftyp, source.Moov))
            for (int index = 0; index < flags.Length; index++)
                writer.AddFrame([(byte)(index + 1), 0], index == 0, 1000,
                    flags[index] == '?' ? null : flags[index] == 'T');
        using Mp4File result = new(new MemoryStream(output.ToArray()));
        var stss = result.Moov.AudioTrack.Mdia.Minf.Stbl.Stss;
        if (expected == "all") Assert.IsNull(stss);
        else
        {
            Assert.IsNotNull(stss, "An absent table declares every sample independently decodable.");
            CollectionAssert.AreEqual(expected.Length == 0 ? Array.Empty<uint>() : expected.Split(',').Select(uint.Parse).ToArray(), stss.SampleNumbers);
        }
        var actual = new ChunkEntryList(result.Moov.AudioTrack).SelectMany(c => c.SyncFlags!).ToArray();
        CollectionAssert.AreEqual(flags.Select(c => c == 'T').ToArray(), actual);
    }

    [TestMethod]
    public void Legacy_writer_overload_retains_its_all_independent_contract()
    {
        using Mp4File source = Source();
        using MemoryStream output = new();
        using (var writer = new Mp4aWriter(output, source.Ftyp, source.Moov))
            writer.AddFrame([1, 0], true, 1000);
        using Mp4File result = new(new MemoryStream(output.ToArray()));
        Assert.IsNull(result.Moov.AudioTrack.Mdia.Minf.Stbl.Stss);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Usac_without_any_prior_independent_frame_rejects_trim_or_split(bool split)
    {
        using Mp4File source = Source(usac: true);
        ChapterInfo chapters = new(TimeSpan.FromMilliseconds(1500));
        chapters.AddChapter("part", TimeSpan.FromSeconds(1));
        using MemoryStream output = new();
        int callbacks = 0;
        await AssertSyncFailure(async () =>
        {
            if (split)
                await source.ConvertToMultiMp4aAsync(chapters, callback => { callbacks++; callback.OutputFile = new MemoryStream(); });
            else await source.ConvertToMp4aAsync(output, chapters);
        });
        Assert.AreEqual(0, callbacks, "A split output must not be created before its entry point is validated.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(null)]
    public async Task Split_without_confirmed_entry_point_rejects_before_output_callback(bool? sync)
    {
        using Mp4File source = Source();
        ChapterInfo chapters = new(); chapters.AddChapter("part", TimeSpan.FromSeconds(1));
        int callbacks = 0;
        using var filter = new LosslessMultipartFilter(chapters, source.Ftyp, source.Moov,
            callback => { callbacks++; callback.OutputFile = new MemoryStream(); });
        await AssertSyncFailure(async () =>
        {
            await filter.AddInputAsync(Frame(0, sync));
            await filter.CompleteAsync();
        });
        Assert.AreEqual(0, callbacks);
    }

    [TestMethod]
    public async Task Unknown_dependent_run_can_reuse_a_confirmed_prior_sync()
    {
        using Mp4File source = Source();
        ChapterInfo chapters = new(TimeSpan.FromMilliseconds(1500)); chapters.AddChapter("part", TimeSpan.FromSeconds(1));
        List<MemoryStream> outputs = [];
        using var filter = new LosslessMultipartFilter(chapters, source.Ftyp, source.Moov,
            callback => { var output = new MemoryStream(); outputs.Add(output); callback.OutputFile = output; });
        await filter.AddInputAsync(Frame(0, true));
        await filter.AddInputAsync(Frame(1000, null));
        await filter.AddInputAsync(Frame(2000, false));
        await filter.CompleteAsync();
        Assert.HasCount(1, outputs);
        using Mp4File result = new(new MemoryStream(outputs[0].ToArray()));
        Assert.AreEqual(1500L, result.PresentationStartSample);
        Assert.AreEqual(1000L, result.PresentedDurationSamples);
        CollectionAssert.AreEqual(new uint[] { 1 }, result.Moov.AudioTrack.Mdia.Minf.Stbl.Stss!.SampleNumbers);
    }

    [TestMethod]
    public async Task Filter_flush_failure_preserves_original_exception_after_input_completion()
    {
        using var filter = new FlushFailureFilter();
        await filter.AddInputAsync(1);
        var actual = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => filter.CompleteAsync());
        Assert.AreSame(filter.Error, actual);
    }

    private sealed class FlushFailureFilter : FrameFinalBase<int>
    {
        public readonly InvalidDataException Error = new("fixture flush failure");
        protected override int InputBufferSize => 1;
        protected override Task PerformFilteringAsync(int input) => Task.CompletedTask;
        protected override Task FlushAsync() => Task.FromException(Error);
    }

    private static async Task AssertSyncFailure(Func<Task> action)
    {
        Exception error = await Assert.ThrowsAsync<Exception>(action);
        // Filter channels and Mp4Operation preserve their established public
        // wrappers; validate the actual causal failure through those wrappers.
        while (true)
        {
            if (error is AggregateException aggregate)
            {
                var errors = aggregate.Flatten().InnerExceptions;
                Assert.HasCount(1, errors, aggregate.ToString());
                error = errors[0];
            }
            else if (error is System.Threading.Channels.ChannelClosedException && error.InnerException is not null)
                error = error.InnerException;
            else break;
        }
        Assert.IsInstanceOfType<InvalidDataException>(error, error.ToString());
        StringAssert.Contains(error.Message, "no confirmed preceding sync frame");
    }

    private static FrameEntry Frame(long start, bool? sync) => new()
    {
        Chunk = new ChunkEntry { TrackId = 1, ChunkIndex = 0, ChunkOffset = 0, FirstSample = 0,
            ChunkSize = 6, FrameSizes = [2, 2, 2], FrameDurations = [1000, 1000, 1000] },
        StartSample = start, SamplesInFrame = 1000, FrameData = new byte[] { (byte)(start / 1000 + 1), 0 }, IsSyncSample = sync
    };

    private static Mp4File Source(bool usac = false)
    {
        var source = new Mp4File(new MemoryStream(PresentationWindowContractTests.CreateAc4Source(1000, 1000, 1000, [1, 2, 3, 0x84])));
        if (usac)
        {
            var entry = source.AudioSampleEntry;
            entry.Children.Remove(entry.Dac4!); entry.Header.ChangeAtomName("mp4a");
            EsdsBox.CreateEmpty(entry).ES_Descriptor.DecoderConfig.AudioSpecificConfig.AscBlob = Convert.FromHexString("F9464800");
        }
        return source;
    }
}
