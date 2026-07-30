using System.IO;

namespace Mpeg4Lib.Boxes;

//ISO/IEC 14496-12 § 8.6.5 Edit Box: container for the edit list.
public class EdtsBox : Box
{
	public static EdtsBox CreateBlank(TrakBox parent)
	{
		BoxHeader header = new BoxHeader(8, "edts");
		EdtsBox edtsBox = new EdtsBox(header, parent);

		//Conventionally edts appears between tkhd and mdia.
		int tkhdIndex = parent.Children.IndexOf(parent.Tkhd);
		parent.Children.Insert(tkhdIndex + 1, edtsBox);
		return edtsBox;
	}

	private EdtsBox(BoxHeader header, IBox? parent) : base(header, parent) { }

	public EdtsBox(Stream file, BoxHeader header, IBox? parent) : base(header, parent)
	{
		LoadChildren(file);
	}

	public ElstBox? Elst => GetChild<ElstBox>();

	protected override void Render(Stream file)
	{
		return;
	}
}
