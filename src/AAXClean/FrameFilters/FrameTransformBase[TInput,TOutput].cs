using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace AAXClean.FrameFilters
{
	public abstract class FrameTransformBase<TInput, TOutput> : FrameFilterBase<TInput>
	{
		private FrameFilterBase<TOutput>? Linked;

		public override void SetCancellationToken(CancellationToken cancellationToken)
		{
			base.SetCancellationToken(cancellationToken);
			Linked?.SetCancellationToken(cancellationToken);
		}

		public void LinkTo(FrameFilterBase<TOutput> nextFilter) => Linked = nextFilter;
		public abstract TOutput PerformFiltering(TInput input);
		protected virtual TOutput? PerformFinalFiltering() => default;

		protected sealed override async Task FlushAsync()
		{
			if (PerformFinalFiltering() is TOutput filteredData && Linked is not null)
				await Linked.AddInputAsync(filteredData);
		}

		protected sealed override async Task HandleInputDataAsync(TInput input)
		{
			TOutput filteredData = PerformFiltering(input);
			if (Linked is null)
#if DEBUG
				//Allow unlinked for testing purposes
				return;
#else
				throw new System.InvalidOperationException($"A FrameTransformBase<TInput, TOutput> must be linked to a FrameFilterBase<TOutput>");
#endif
			await Linked.AddInputAsync(filteredData);
		}

		protected sealed override async Task CompleteInternalAsync()
		{
			ExceptionDispatchInfo? failure = null;
			try { await base.CompleteInternalAsync(); }
			catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }

			// A failed transform can leave its downstream worker running with queued
			// input. Always join the entire chain before its resources are disposed.
			try { await (Linked?.CompleteAsync() ?? Task.CompletedTask); }
			catch (Exception ex) when (failure is not null)
			{
				if (!ReferenceEquals(ex, failure.SourceException) && ex is not OperationCanceledException)
				{
					if (failure.SourceException is OperationCanceledException)
						throw;
					throw new AggregateException("Audio filtering and linked completion both failed.",
						failure.SourceException, ex);
				}
			}
			failure?.Throw();
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing && !Disposed)
				Linked?.Dispose();
			base.Dispose(disposing);
		}
	}
}
