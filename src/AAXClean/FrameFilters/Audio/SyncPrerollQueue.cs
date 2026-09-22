using System.Collections.Generic;

namespace AAXClean.FrameFilters.Audio
{
	/// <summary>
	/// Frames from the most recent sync run, optionally retaining its predecessor for
	/// decoder overlap, oldest first with exact media positions. Dependent frames are never evicted
	/// by an arbitrary cap that could discard the only usable decode entry point.
	/// </summary>
	internal sealed class SyncPrerollQueue
	{
		private readonly Queue<(FrameEntry frame, long start)> queue = new();
		private int previousRunCount;

		public IReadOnlyCollection<(FrameEntry frame, long start)> Frames => queue;
		public bool HasSyncFrame { get; private set; }

		public void Push(FrameEntry frame, long startSample, bool isSync, bool retainPreviousSyncRun = false)
		{
			if (isSync)
			{
				if (retainPreviousSyncRun && HasSyncFrame)
				{
					// AAC-LC also needs overlap state before its nominal sync frame.
					// Keep the preceding complete sync run, not an arbitrary frame cap.
					for (int i = 0; i < previousRunCount; i++) queue.Dequeue();
					previousRunCount = queue.Count;
				}
				else
				{
					queue.Clear();
					previousRunCount = 0;
				}
				HasSyncFrame = true;
			}
			queue.Enqueue((frame, startSample));
		}
	}
}
