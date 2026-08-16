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
}
