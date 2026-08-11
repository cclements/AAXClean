using AAXClean.Chunks;
using AAXClean.FrameFilters;
using Mpeg4Lib.Boxes;
using Mpeg4Lib.Chunks;

namespace Mpeg4Lib.Test;

[TestClass]
public class ChunkReaderFailureTests
{
	[TestMethod]
	public async Task ProcessingFailure_IsNotMaskedByCancellationDuringFilterCleanup()
	{
		var processingFailure = new InvalidDataException("root processing failure");
		using var track = MakeTrack(trackId: 7, timescale: 1_000);
		using var input = new MemoryStream([0]);
		using var cancellationSource = new CancellationTokenSource();
		using var filter = new CancellationBlockingFilter();
		var reader = new FailingChunkReader(input, track.Tkhd.TrackID, processingFailure);
		reader.AddTrack(track, filter);

		var exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(
			() => reader.RunAsync(cancellationSource));

		Assert.AreSame(processingFailure, exception);
	}

	[TestMethod]
	public async Task ProcessingAndCleanupFailures_AreBothPreserved()
	{
		var processingFailure = new InvalidDataException("root processing failure");
		var cleanupFailure = new IOException("independent cleanup failure");
		using var track = MakeTrack(trackId: 7, timescale: 1_000);
		using var input = new MemoryStream([0]);
		using var cancellationSource = new CancellationTokenSource();
		using var filter = new CleanupFailingFilter(cleanupFailure);
		var reader = new FailingChunkReader(input, track.Tkhd.TrackID, processingFailure);
		reader.AddTrack(track, filter);

		var exception = await Assert.ThrowsExactlyAsync<AggregateException>(
			() => reader.RunAsync(cancellationSource));

		Assert.HasCount(2, exception.InnerExceptions);
		Assert.AreSame(processingFailure, exception.InnerExceptions[0]);
		Assert.AreSame(cleanupFailure, exception.InnerExceptions[1]);
	}

	private static TrakBox MakeTrack(uint trackId, uint timescale)
	{
		byte[] tkhd = MakeBox(
			"tkhd",
			UInt32sBE(
				0, // version + flags
				0, 0, trackId, 0, 1, // creation, modification, ID, reserved, duration
				0, 0, // reserved
				0, 0, // layer/group and volume/reserved
				0, 0, 0, 0, 0, 0, 0, 0, 0, // matrix
				0, 0)); // width and height
		byte[] mdhd = MakeBox(
			"mdhd",
			UInt32sBE(
				0, // version + flags
				0, 0, timescale, 1, // creation, modification, timescale, duration
				0)); // language and predefined

		byte[] source = MakeBox("trak", tkhd, MakeBox("mdia", mdhd));
		return BoxFactory.CreateBox<TrakBox>(new MemoryStream(source), parent: null);
	}

	private static byte[] MakeBox(string type, params byte[][] payloads)
	{
		using var stream = new MemoryStream();
		WriteUInt32BE(stream, checked((uint)(8 + payloads.Sum(p => p.Length))));
		stream.Write(System.Text.Encoding.ASCII.GetBytes(type));
		foreach (byte[] payload in payloads)
			stream.Write(payload);
		return stream.ToArray();
	}

	private static byte[] UInt32sBE(params uint[] values)
	{
		using var stream = new MemoryStream();
		foreach (uint value in values)
			WriteUInt32BE(stream, value);
		return stream.ToArray();
	}

	private static void WriteUInt32BE(Stream stream, uint value)
	{
		Span<byte> bytes =
		[
			(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value
		];
		stream.Write(bytes);
	}

	private sealed class FailingChunkReader(Stream input, uint trackId, Exception processingFailure)
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

			throw processingFailure;
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

	private sealed class CleanupFailingFilter(Exception cleanupFailure) : FrameFilterBase<FrameEntry>
	{
		protected override int InputBufferSize => 1;

		protected override Task CompleteInternalAsync() => Task.FromException(cleanupFailure);

		protected override Task FlushAsync() => Task.CompletedTask;

		protected override Task HandleInputDataAsync(FrameEntry input) => Task.CompletedTask;
	}
}
