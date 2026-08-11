using AAXClean.Chunks;
using AAXClean.FrameFilters;
using Mpeg4Lib.Chunks;

namespace Mpeg4Lib.Test;

[TestClass]
public class ChunkReaderFailureTests
{
	[TestMethod]
	public async Task ProcessingFailure_IsNotMaskedAsCancellationDuringFilterCleanup()
	{
		byte[] metadataBytes = PresentationSyncSafetyTests.CreateAc4Source(
			movieTimescale: 1000,
			mediaTimescale: 1000,
			frameDelta: 1000,
			samples: [1]);
		using var metadata = new AAXClean.Mp4File(new MemoryStream(metadataBytes));
		using var input = new MemoryStream([0]);
		using var cancellationSource = new CancellationTokenSource();
		var filter = new CancellationBlockingFilter();
		var reader = new FailingChunkReader(input, metadata.Moov.AudioTrack.Tkhd.TrackID);
		reader.AddTrack(metadata.Moov.AudioTrack, filter);

		var exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(
			() => reader.RunAsync(cancellationSource));

		Assert.AreEqual("root processing failure", exception.Message);
	}

	private sealed class FailingChunkReader(Stream input, uint trackId)
		: ChunkReader(input, TimeSpan.Zero, TimeSpan.FromSeconds(1))
	{
		protected override IEnumerable<ChunkEntry> EnumerateChunks()
		{
			yield return new ChunkEntry
			{
				TrackId = trackId,
				ChunkIndex = 0,
				ChunkOffset = 0,
				FirstSample = 0,
				ChunkSize = 1,
				FrameSizes = [1],
				FrameDurations = [1]
			};

			throw new InvalidDataException("root processing failure");
		}
	}

	private sealed class CancellationBlockingFilter : FrameFilterBase<FrameEntry>
	{
		private CancellationToken cancellationToken;
		protected override int InputBufferSize => 1;

		public override void SetCancellationToken(CancellationToken token)
		{
			base.SetCancellationToken(token);
			cancellationToken = token;
		}

		protected override Task FlushAsync() => Task.CompletedTask;

		protected override Task HandleInputDataAsync(FrameEntry input)
			=> Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
	}
}
