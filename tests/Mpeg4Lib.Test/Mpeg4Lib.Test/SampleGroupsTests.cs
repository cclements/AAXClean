using Mpeg4Lib.Boxes;
using System.Text;

namespace Mpeg4Lib.Test;

[TestClass]
public class SampleGroupsTests
{
	[TestMethod]
	[DataRow("sgpd")]
	[DataRow("sbgp")]
	public void ContainsCencSampleGroup_SeigGroupingType_IsDetected(string boxType)
	{
		using IBox box = ParseBox(MakeBox(
			boxType,
			UInt32s(0),
			Encoding.ASCII.GetBytes("seig"),
			UInt32s(0)));

		Assert.IsTrue(SampleGroups.ContainsCencSampleGroup(box));
	}

	[TestMethod]
	[DataRow("sgpd")]
	[DataRow("sbgp")]
	public void ContainsCencSampleGroup_NonSeigGroupingType_IsIgnored(string boxType)
	{
		using IBox box = ParseBox(MakeBox(
			boxType,
			UInt32s(0),
			Encoding.ASCII.GetBytes("roll"),
			new byte[2]));

		Assert.IsFalse(SampleGroups.ContainsCencSampleGroup(box));
	}

	[TestMethod]
	[DataRow("sgpd")]
	[DataRow("sbgp")]
	public void ContainsCencSampleGroup_TypedSampleGroupBox_FailsClosed(string boxType)
	{
		using var box = new TypedSampleGroupBox(boxType);

		Assert.IsTrue(SampleGroups.ContainsCencSampleGroup(box));
	}

	[TestMethod]
	public void ContainsCencSampleGroup_NestedSeigGroup_IsDetectedThroughChildren()
	{
		byte[] seig = MakeBox(
			"sgpd",
			UInt32s(0),
			Encoding.ASCII.GetBytes("seig"),
			UInt32s(0));
		using IBox parent = ParseBox(MakeBox("stbl", seig));

		Assert.IsTrue(SampleGroups.ContainsCencSampleGroup(parent));
	}

	//No typed sgpd/sbgp parser exists today; this stand-in proves the detector fails
	//closed if one is ever introduced without updating the seig detection.
	private sealed class TypedSampleGroupBox(string type)
		: Box(new BoxHeader(8, type), parent: null)
	{
		protected override void Render(Stream file) { }
	}

	private static IBox ParseBox(byte[] rendered)
	{
		using var stream = new MemoryStream(rendered);
		return BoxFactory.CreateBox(stream, parent: null);
	}

	private static byte[] MakeBox(string type, params byte[][] payloads)
	{
		using var box = new MemoryStream();
		uint size = checked((uint)(8 + payloads.Sum(payload => payload.Length)));
		Span<byte> header =
		[
			(byte)(size >> 24),
			(byte)(size >> 16),
			(byte)(size >> 8),
			(byte)size
		];
		box.Write(header);
		box.Write(Encoding.ASCII.GetBytes(type));
		foreach (byte[] payload in payloads)
			box.Write(payload);
		return box.ToArray();
	}

	private static byte[] UInt32s(params uint[] values)
	{
		using var bytes = new MemoryStream();
		foreach (uint value in values)
		{
			Span<byte> span =
			[
				(byte)(value >> 24),
				(byte)(value >> 16),
				(byte)(value >> 8),
				(byte)value
			];
			bytes.Write(span);
		}

		return bytes.ToArray();
	}
}
