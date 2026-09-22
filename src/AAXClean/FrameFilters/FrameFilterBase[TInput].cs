using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace AAXClean.FrameFilters
{
	public abstract class FrameFilterBase<TInput> : IDisposable
	{
		private record BufferEntry(int NumEntries, TInput[] Entries);
		protected abstract int InputBufferSize { get; }

		private CancellationToken CancellationToken;
		private Task? filterLoop;
		private Task? completion;
		private TInput[] buffer;
		private int bufferPosition = 0;
		private readonly Channel<BufferEntry> filterChannel;

		public FrameFilterBase()
		{
			filterChannel = Channel.CreateBounded<BufferEntry>(new BoundedChannelOptions(2) { SingleReader = true, SingleWriter = true });
			buffer = new TInput[InputBufferSize];
		}

		public virtual void SetCancellationToken(CancellationToken cancellationToken) => CancellationToken = cancellationToken;
		protected abstract Task FlushAsync();
		protected abstract Task HandleInputDataAsync(TInput input);

		public virtual async Task AddInputAsync(TInput input)
		{
			// A failed producer may be called again by cleanup or a buffered upstream
			// filter. Observe the worker before touching a possibly full input buffer.
			if (completion is not null)
			{
				await completion;
				throw new InvalidOperationException("The filter has already completed.");
			}
			if (filterLoop?.IsCompleted is true)
				await filterLoop;
			filterLoop ??= Task.Run(Encoder, CancellationToken);

			if (CancellationToken.IsCancellationRequested)
				return;

			buffer[bufferPosition++] = input;

			if (bufferPosition == InputBufferSize)
			{
				try
				{
					await filterChannel.Writer.WriteAsync(new BufferEntry(bufferPosition, buffer), CancellationToken);
					bufferPosition = 0;
					buffer = new TInput[InputBufferSize];
				}
				catch (ChannelClosedException)
				{
					// The channel is transport; retain the worker's actual exception and
					// ensure it has stopped before the caller begins resource cleanup.
					await filterLoop;
					throw;
				}
			}
		}

		private async Task Encoder()
		{
			try
			{
				while (await filterChannel.Reader.WaitToReadAsync(CancellationToken))
				{
					await foreach (var messages in filterChannel.Reader.ReadAllAsync(CancellationToken))
					{
						for (int i = 0; i < messages.NumEntries; i++)
							await HandleInputDataAsync(messages.Entries[i]);
					}
				}
				await FlushAsync();
			}
			catch (Exception ex)
			{
				// Completion may already have closed input before this filter or its
				// flush fails. Preserve the real failure instead of throwing a second
				// ChannelClosedException while trying to notify producers.
				filterChannel.Writer.TryComplete(ex);
				throw;
			}
		}

		protected virtual async Task CompleteInternalAsync()
		{
			try
			{
				if (bufferPosition > 0)
					await filterChannel.Writer.WriteAsync(new BufferEntry(bufferPosition, buffer), CancellationToken);
			}
			catch (OperationCanceledException) { }
			catch (ChannelClosedException) { } // The worker below owns the failure.
			finally { filterChannel.Writer.TryComplete(); }

			if (filterLoop is not null)
				await filterLoop;
		}

		public Task CompleteAsync() => completion ??= CompleteInternalAsync();

		#region IDisposable
		protected bool Disposed { get; private set; }
		public void Dispose()
		{
			Dispose(disposing: true);
			GC.SuppressFinalize(this);
		}

		~FrameFilterBase()
		{
			Dispose(disposing: false);
		}

		protected virtual void Dispose(bool disposing)
		{
			if (disposing && !Disposed)
			{
				if (filterLoop?.IsCompleted is false)
					filterChannel.Writer.TryComplete();
			}
			Disposed = true;
		}
		#endregion
	}
}
