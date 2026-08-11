using Mpeg4Lib.Boxes;

namespace Mpeg4Lib.Test;

[TestClass]
public class PsshBoxTests
{
	private static readonly Guid CommonSystemId = Guid.Parse("1077efec-c0b2-4d02-ace3-3c1e52e2fb4b");

	[TestMethod]
	public void PublicSurface_PreservesProtectionSystemIdWithoutRedundantAlias()
	{
		Assert.IsNotNull(typeof(PsshBox).GetProperty(nameof(PsshBox.ProtectionSystemId)));
		Assert.IsNull(typeof(PsshBox).GetProperty("SystemID"));
	}

	[TestMethod]
	public void Parse_Version0_PreservesProtectionSystemIdAndRoundTrips()
	{
		byte[] data = [0x10, 0x20, 0x30];
		byte[] source = MakeBox("pssh",
			[0, 0, 0, 0],
			CommonSystemId.ToByteArray(bigEndian: true),
			UInt32BE((uint)data.Length),
			data);

		var pssh = BoxFactory.CreateBox<PsshBox>(new MemoryStream(source), parent: null);

		Assert.AreEqual(CommonSystemId, pssh.ProtectionSystemId);
		Assert.IsEmpty(pssh.KIDs);
		CollectionAssert.AreEqual(data, pssh.InitData);
		Assert.IsEmpty(pssh.ExtraData);

		using var rendered = new MemoryStream();
		pssh.Save(rendered);
		CollectionAssert.AreEqual(source, rendered.ToArray());
	}

	[TestMethod]
	public void Parse_W3cVersion1_SeparatesKidsFromInitDataAndRoundTrips()
	{
		byte[] source = Convert.FromHexString(
			"000000447073736801000000" +
			"1077EFECC0B24D02ACE33C1E52E2FB4B" +
			"00000002" +
			"30313233343536373839303132333435" +
			"4142434445464748494A4B4C4D4E4F50" +
			"00000000");

		var pssh = BoxFactory.CreateBox<PsshBox>(new MemoryStream(source), parent: null);

		Assert.AreEqual((byte)1, pssh.Version);
		Assert.AreEqual(CommonSystemId, pssh.ProtectionSystemId);
		CollectionAssert.AreEqual(
			new[]
			{
				new Guid(System.Text.Encoding.ASCII.GetBytes("0123456789012345"), bigEndian: true),
				new Guid(System.Text.Encoding.ASCII.GetBytes("ABCDEFGHIJKLMNOP"), bigEndian: true)
			},
			pssh.KIDs);
		Assert.IsEmpty(pssh.InitData);

		using var rendered = new MemoryStream();
		pssh.Save(rendered);
		CollectionAssert.AreEqual(source, rendered.ToArray());
	}

	[TestMethod]
	public void Parse_RejectsPayloadBeyondDeclaredDataSize()
	{
		byte[] source = MakeBox("pssh",
			[0, 0, 0, 0],
			CommonSystemId.ToByteArray(bigEndian: true),
			UInt32BE(1),
			[0xAA, 0xBB]);

		Assert.ThrowsExactly<InvalidDataException>(
			() => BoxFactory.CreateBox<PsshBox>(new MemoryStream(source), parent: null));
	}

	[TestMethod]
	public void Parse_RejectsKidCountBeyondBoxBounds()
	{
		byte[] source = MakeBox("pssh",
			[1, 0, 0, 0],
			CommonSystemId.ToByteArray(bigEndian: true),
			UInt32BE(1),
			UInt32BE(0));

		Assert.ThrowsExactly<InvalidDataException>(
			() => BoxFactory.CreateBox<PsshBox>(new MemoryStream(source), parent: null));
	}

	[TestMethod]
	public void Parse_RejectsUnsupportedVersion()
	{
		byte[] source = MakeBox("pssh",
			[2, 0, 0, 0],
			CommonSystemId.ToByteArray(bigEndian: true),
			UInt32BE(0));

		Assert.ThrowsExactly<InvalidDataException>(
			() => BoxFactory.CreateBox<PsshBox>(new MemoryStream(source), parent: null));
	}

	[TestMethod]
	public void Parse_ExtendedSize_RendersWithItsSixteenByteHeader()
	{
		using var source = new MemoryStream();
		WriteUInt32BE(source, 1);
		source.Write("pssh"u8);
		WriteUInt64BE(source, 40);
		source.Write([0, 0, 0, 0]);
		source.Write(CommonSystemId.ToByteArray(bigEndian: true));
		WriteUInt32BE(source, 0);
		byte[] sourceBytes = source.ToArray();

		source.Position = 0;
		var header = new BoxHeader(source);
		var pssh = new PsshBox(source, header, parent: null);

		Assert.AreEqual(40L, pssh.RenderSize);
		using var rendered = new MemoryStream();
		pssh.Save(rendered);
		CollectionAssert.AreEqual(sourceBytes, rendered.ToArray());
	}

	private static byte[] MakeBox(string type, params byte[][] payloads)
	{
		int payloadSize = payloads.Sum(payload => payload.Length);
		using var stream = new MemoryStream();
		WriteUInt32BE(stream, (uint)(8 + payloadSize));
		stream.Write(System.Text.Encoding.ASCII.GetBytes(type));
		foreach (byte[] payload in payloads)
			stream.Write(payload);
		return stream.ToArray();
	}

	private static byte[] UInt32BE(uint value)
	{
		using var stream = new MemoryStream();
		WriteUInt32BE(stream, value);
		return stream.ToArray();
	}

	private static void WriteUInt32BE(Stream stream, uint value)
	{
		Span<byte> bytes = [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
		stream.Write(bytes);
	}

	private static void WriteUInt64BE(Stream stream, ulong value)
	{
		Span<byte> bytes =
		[
			(byte)(value >> 56), (byte)(value >> 48), (byte)(value >> 40), (byte)(value >> 32),
			(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value
		];
		stream.Write(bytes);
	}
}
