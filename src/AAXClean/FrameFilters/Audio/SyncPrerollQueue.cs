using System.Collections.Generic;

namespace AAXClean.FrameFilters.Audio
{
	/// <summary>
	/// The frames since (and including) the most recent sync frame, oldest first, with
	/// each frame's exact media start position — so output can begin at an independently
	/// decodable entry point at or before a requested media position. A sync frame resets
	/// the queue, so all frames required to decode forward from that entry point are retained;
	/// no arbitrary cap may evict the only usable sync frame. For all-sync codecs the queue
	/// still holds exactly one frame.
	/// </summary>
	public sealed class SyncPrerollQueue
	{
		private readonly Queue<(FrameEntry frame, long start)> queue = new();

		public IReadOnlyCollection<(FrameEntry frame, long start)> Frames => queue;

		/// <summary>Record a processed frame. A sync frame restarts the preroll at itself.</summary>
		public void Push(FrameEntry frame, long startSample, bool isSync)
		{
			if (isSync)
				queue.Clear();
			queue.Enqueue((frame, startSample));
		}
	}
}
