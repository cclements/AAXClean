using System.Collections.Generic;

namespace AAXClean.FrameFilters.Audio
{
	/// <summary>
	/// Frames since and including the most recent sync frame, oldest first, with exact
	/// media positions. A sync frame resets the queue; dependent frames are never evicted
	/// by an arbitrary cap that could discard the only usable decode entry point.
	/// </summary>
	internal sealed class SyncPrerollQueue
	{
		private readonly Queue<(FrameEntry frame, long start)> queue = new();

		public IReadOnlyCollection<(FrameEntry frame, long start)> Frames => queue;

		public void Push(FrameEntry frame, long startSample, bool isSync)
		{
			if (isSync)
				queue.Clear();
			queue.Enqueue((frame, startSample));
		}
	}
}
