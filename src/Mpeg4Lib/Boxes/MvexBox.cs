using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Mpeg4Lib.Boxes;

public class MvexBox : Box
{
	public IEnumerable<TrexBox> TrackExtends => GetChildren<TrexBox>();

	public MvexBox(Stream file, BoxHeader header, IBox? parent) : base(header, parent)
	{
		LoadChildren(file);
	}

	public TrexBox GetTrackExtends(uint trackId)
		=> TrackExtends.SingleOrDefault(t => t.TrackID == trackId)
		?? throw new InvalidDataException($"The {nameof(MvexBox)} doesn't contain a {nameof(TrexBox)} for track ID {trackId}.");

	protected override void Render(Stream file)
	{
		return;
	}
}
