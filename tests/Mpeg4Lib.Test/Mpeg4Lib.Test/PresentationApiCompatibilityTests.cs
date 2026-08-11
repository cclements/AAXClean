using AAXClean;
using AAXClean.Chunks;
using AAXClean.FrameFilters;
using AAXClean.FrameFilters.Audio;
using Mpeg4Lib.Boxes;

namespace Mpeg4Lib.Test;

[TestClass]
public class PresentationApiCompatibilityTests
{
	[TestMethod]
	public void ChunkReaderContract_PreservesReleasedTwoArgumentAddTrackSignature()
	{
		var methods = typeof(IChunkReader).GetMethods()
			.Where(method => method.Name == nameof(IChunkReader.AddTrack))
			.ToArray();

		Assert.HasCount(1, methods);
		CollectionAssert.AreEqual(
			new[] { typeof(TrakBox), typeof(FrameFilterBase<FrameEntry>) },
			methods[0].GetParameters().Select(parameter => parameter.ParameterType).ToArray());
	}

	[TestMethod]
	public void MultipartContract_PreservesFourArgumentConstructorWithoutDelegateAmbiguity()
	{
		var constructor = typeof(MultipartFilterBase<FrameEntry, NewSplitCallback>).GetConstructor(
			[typeof(ChapterInfo), typeof(SampleRate), typeof(bool), typeof(long)]);

		Assert.IsNotNull(constructor);
		var mediaOffset = constructor.GetParameters()[3];
		Assert.IsTrue(mediaOffset.HasDefaultValue);
		Assert.AreEqual(0L, mediaOffset.DefaultValue);
	}

	[TestMethod]
	public void TwoArgumentAddTrack_DerivesUsacLookbackFromTheTrack()
	{
		byte[] sourceBytes = PresentationWindowContractTests.CreateAc4Source(
			movieTimescale: 1000,
			mediaTimescale: 1000,
			frameDelta: 1000,
			samples: [1, 2, 3, 4, 5, 6, 7, 8]);
		using var source = new AAXClean.Mp4File(new MemoryStream(sourceBytes));
		AudioSampleEntry sampleEntry = source.Moov.AudioTrack.Mdia.Minf.Stbl.Stsd.AudioSampleEntry!;
		if (sampleEntry.Dac4 is Dac4Box dac4)
			sampleEntry.Children.Remove(dac4);
		sampleEntry.Header.ChangeAtomName("mp4a");
		EsdsBox esds = EsdsBox.CreateEmpty(sampleEntry);
		esds.ES_Descriptor.DecoderConfig.AudioSpecificConfig.AudioObjectType = 42;

		using var filter = new DiscardFilter();
		var reader = new InspectableChunkReader(
			new MemoryStream(),
			TimeSpan.FromSeconds(5.5),
			TimeSpan.FromSeconds(6.7));

		(long start, long end) = reader.AddAndInspect(source.Moov.AudioTrack, filter);

		Assert.AreEqual(3500L, start);
		Assert.AreEqual(6700L, end);
	}

	[TestMethod]
	public void SyncPrerollQueue_IsInternalAndRetainsTheUsableSyncRun()
	{
		Assert.IsFalse(typeof(SyncPrerollQueue).IsPublic);

		SyncPrerollQueue queue = new();
		FrameEntry sync = Frame();
		queue.Push(sync, 0, isSync: true);
		for (int i = 1; i <= 5000; i++)
			queue.Push(Frame(), i * 1024L, isSync: false);

		Assert.HasCount(5001, queue.Frames);
		Assert.AreSame(sync, queue.Frames.First().frame);
		Assert.AreEqual(0L, queue.Frames.First().start);
	}

	private static FrameEntry Frame()
		=> new() { SamplesInFrame = 1024, FrameData = new byte[1] };

	private sealed class InspectableChunkReader(
		Stream input,
		TimeSpan start,
		TimeSpan end) : ChunkReader(input, start, end)
	{
		public (long start, long end) AddAndInspect(
			TrakBox track,
			FrameFilterBase<FrameEntry> filter)
		{
			AddTrack(track, filter);
			TrackEntry entry = TrackEntries[track.Tkhd.TrackID];
			return (entry.DispatchStartSample, entry.DispatchEndSample);
		}
	}

	private sealed class DiscardFilter : FrameFinalBase<FrameEntry>
	{
		protected override int InputBufferSize => 1;
		protected override Task FlushAsync() => Task.CompletedTask;
		protected override Task PerformFilteringAsync(FrameEntry input) => Task.CompletedTask;
	}
}
