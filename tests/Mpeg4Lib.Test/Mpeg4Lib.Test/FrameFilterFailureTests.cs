using AAXClean.FrameFilters;

namespace Mpeg4Lib.Test;

[TestClass]
public class FrameFilterFailureTests
{
	private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

	[TestMethod]
	public async Task BackpressuredProducerAndLaterCallsRetainWorkerFailure()
	{
		var failure = new IOException("worker failed");
		using var filter = new GatedSink(failure);
		await filter.AddInputAsync("first");
		await filter.Entered.Task.WaitAsync(Limit);
		await filter.AddInputAsync("queued one");
		await filter.AddInputAsync("queued two");
		var producer = filter.AddInputAsync("blocked");
		Assert.IsFalse(producer.IsCompleted);
		filter.Release.SetResult();
		Assert.AreSame(failure, await Catch(producer));
		Assert.IsTrue(filter.Exited);
		Assert.AreSame(failure, await Catch(filter.AddInputAsync("after failure")));
		Assert.AreSame(failure, await Catch(filter.CompleteAsync()));
		Assert.AreSame(failure, await Catch(filter.CompleteAsync()));
		Assert.AreEqual(0, filter.FlushCount);
	}

	[TestMethod]
	public async Task CompletionOfPartialBufferRetainsWorkerFailure()
	{
		var failure = new InvalidDataException("partial-buffer failure");
		using var filter = new GatedSink(failure, bufferSize: 2);
		await filter.AddInputAsync("first");
		await filter.AddInputAsync("second");
		await filter.Entered.Task.WaitAsync(Limit);
		await filter.AddInputAsync("partial");
		filter.Release.SetResult();
		await filter.Exiting.Task.WaitAsync(Limit);
		Assert.AreSame(failure, await Catch(filter.CompleteAsync()));
		Assert.AreSame(failure, await Catch(filter.CompleteAsync()));
		Assert.IsTrue(filter.Exited);
	}

	[TestMethod]
	public async Task FailureWhileCompletionIsWaitingRetainsOriginalException()
	{
		var failure = new IOException("late failure");
		using var filter = new GatedSink(failure);
		await filter.AddInputAsync("first");
		await filter.Entered.Task.WaitAsync(Limit);
		await filter.AddInputAsync("second");
		await filter.AddInputAsync("third");
		var completion = filter.CompleteAsync();
		Assert.IsFalse(completion.IsCompleted);
		filter.Release.SetResult();
		Assert.AreSame(failure, await Catch(completion));
		Assert.AreSame(failure, await Catch(filter.CompleteAsync()));
	}

	[TestMethod]
	public async Task SuccessfulCompletionDrainsPartialBufferAndFlushesOnlyOnce()
	{
		using var filter = new GatedSink(null, bufferSize: 2);
		filter.Release.SetResult();
		await filter.AddInputAsync("one");
		await filter.AddInputAsync("two");
		await filter.AddInputAsync("three");
		await filter.CompleteAsync().WaitAsync(Limit);
		await filter.CompleteAsync().WaitAsync(Limit);
		CollectionAssert.AreEqual(new[] { "one", "two", "three" }, filter.Received);
		Assert.AreEqual(1, filter.FlushCount);
		await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => filter.AddInputAsync("too late"));
	}

	[TestMethod]
	public async Task UpstreamFailureStillWaitsForLinkedWorkerToFinish()
	{
		var failure = new IOException("upstream failed");
		using var sink = new GatedSink(null);
		using var transform = new FailingTransform(failure);
		transform.LinkTo(sink);
		await transform.AddInputAsync("forward");
		await sink.Entered.Task.WaitAsync(Limit);
		await transform.AddInputAsync("fail");
		await transform.Failing.Task.WaitAsync(Limit);
		var completion = transform.CompleteAsync();
		try
		{
			await Task.WhenAny(completion, sink.Completing.Task).WaitAsync(Limit);
			Assert.IsTrue(sink.Completing.Task.IsCompleted, "Linked completion must run even after the upstream worker fails.");
			Assert.IsFalse(completion.IsCompleted, "Completion cannot return while a linked worker still uses its resources.");
		}
		finally { sink.Release.TrySetResult(); }
		Assert.AreSame(failure, await Catch(completion));
		Assert.IsTrue(sink.Exited);
		Assert.AreEqual(1, sink.FlushCount);
		Assert.AreSame(failure, await Catch(transform.CompleteAsync()));
	}

	[TestMethod]
	public async Task IndependentUpstreamAndLinkedFlushFailuresAreBothPreserved()
	{
		var first = new IOException("upstream failed");
		var second = new InvalidDataException("linked flush failed");
		using var sink = new GatedSink(null, flushFailure: second);
		sink.Release.SetResult();
		using var transform = new FailingTransform(first);
		transform.LinkTo(sink);
		await transform.AddInputAsync("forward");
		await sink.Entered.Task.WaitAsync(Limit);
		await transform.AddInputAsync("fail");
		await transform.Failing.Task.WaitAsync(Limit);
		var observed = await Catch(transform.CompleteAsync());
		Assert.IsInstanceOfType<AggregateException>(observed);
		var failures = ((AggregateException)observed).InnerExceptions;
		Assert.HasCount(2, failures);
		Assert.AreSame(first, failures[0]);
		Assert.AreSame(second, failures[1]);
		Assert.AreEqual(1, sink.FlushCount);
	}

	[TestMethod]
	public async Task LinkedFailureIsNotDuplicatedDuringChainCompletion()
	{
		var failure = new IOException("linked failed");
		using var sink = new GatedSink(failure);
		using var transform = new FailingTransform(null);
		transform.LinkTo(sink);
		await transform.AddInputAsync("first");
		await sink.Entered.Task.WaitAsync(Limit);
		sink.Release.SetResult();
		await sink.Exiting.Task.WaitAsync(Limit);
		// Enough inputs to observe backpressure or an already completed worker.
		Exception? producerFailure = null;
		for (int i = 0; i < 12 && producerFailure is null; i++)
		{
			try { await transform.AddInputAsync("more").WaitAsync(Limit); }
			catch (Exception ex) { producerFailure = ex; }
		}
		Assert.AreSame(failure, producerFailure);
		Assert.AreSame(failure, await Catch(transform.CompleteAsync()));
		Assert.IsTrue(sink.Exited);
	}

	[TestMethod]
	public async Task CancellationBeforeInputDoesNotProcessOrFlush()
	{
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();
		using var filter = new GatedSink(null);
		filter.SetCancellationToken(cancellation.Token);
		await filter.AddInputAsync("cancelled");
		Assert.IsInstanceOfType<OperationCanceledException>(await Catch(filter.CompleteAsync()));
		Assert.HasCount(0, filter.Received);
		Assert.AreEqual(0, filter.FlushCount);
	}

	[TestMethod]
	public async Task CancellationWhileBackpressuredStillJoinsInFlightWorker()
	{
		using var cancellation = new CancellationTokenSource();
		using var filter = new GatedSink(null);
		filter.SetCancellationToken(cancellation.Token);
		await filter.AddInputAsync("first");
		await filter.Entered.Task.WaitAsync(Limit);
		await filter.AddInputAsync("second");
		await filter.AddInputAsync("third");
		var producer = filter.AddInputAsync("blocked");
		cancellation.Cancel();
		Assert.IsInstanceOfType<OperationCanceledException>(await Catch(producer));
		var completion = filter.CompleteAsync();
		Assert.IsFalse(completion.IsCompleted);
		filter.Release.SetResult();
		Assert.IsInstanceOfType<OperationCanceledException>(await Catch(completion));
		Assert.IsTrue(filter.Exited);
		Assert.AreEqual(0, filter.FlushCount);
	}

	[TestMethod]
	[DataRow(true)]
	[DataRow(false)]
	public async Task RealFailureWinsOverCancellationFromAnotherFilter(bool upstreamCancelled)
	{
		var failure = new IOException("processing failed");
		var cancellation = new OperationCanceledException("cleanup cancellation");
		using var sink = new GatedSink(null, flushFailure: upstreamCancelled ? failure : cancellation);
		sink.Release.SetResult();
		using var transform = new FailingTransform(upstreamCancelled ? cancellation : failure);
		transform.LinkTo(sink);
		await transform.AddInputAsync("forward");
		await sink.Entered.Task.WaitAsync(Limit);
		await transform.AddInputAsync("fail");
		await transform.Failing.Task.WaitAsync(Limit);
		Assert.AreSame(failure, await Catch(transform.CompleteAsync()));
		Assert.AreEqual(1, sink.FlushCount);
	}

	private static async Task<Exception> Catch(Task task)
	{
		try { await task.WaitAsync(Limit); }
		catch (Exception ex) { return ex; }
		Assert.Fail("Expected the original worker failure.");
		throw new InvalidOperationException();
	}

	private sealed class FailingTransform(Exception? failure) : FrameTransformBase<string, string>
	{
		protected override int InputBufferSize => 1;
		public TaskCompletionSource Failing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public override string PerformFiltering(string input)
		{
			if (input == "fail" && failure is not null)
			{
				Failing.TrySetResult();
				throw failure;
			}
			return input;
		}
	}

	private sealed class GatedSink(Exception? failure, int bufferSize = 1, Exception? flushFailure = null)
		: FrameFinalBase<string>
	{
		protected override int InputBufferSize => bufferSize;
		public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Exiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Completing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public List<string> Received { get; } = [];
		public bool Exited { get; private set; }
		public int FlushCount { get; private set; }
		protected override async Task PerformFilteringAsync(string input)
		{
			Entered.TrySetResult();
			try
			{
				await Release.Task;
				if (failure is not null) throw failure;
				Received.Add(input);
			}
			finally { Exited = true; Exiting.TrySetResult(); }
		}
		protected override async Task CompleteInternalAsync()
		{
			Completing.TrySetResult();
			await base.CompleteInternalAsync();
		}
		protected override Task FlushAsync()
		{
			FlushCount++;
			return flushFailure is null ? Task.CompletedTask : Task.FromException(flushFailure);
		}
	}
}
