using AAXClean.FrameFilters;
using AAXClean.FrameFilters.Audio;
using Mpeg4Lib.Boxes;

namespace Mpeg4Lib.Test;

[TestClass]
public class DashFilterTests
{
	private static TencBox MakeTenc(
		byte perSampleIvSize,
		byte[]? constantIv = null,
		byte version = 0,
		byte cryptByteBlock = 0,
		byte skipByteBlock = 0)
	{
		using var payload = new MemoryStream();
		payload.Write([version, 0, 0, 0]); //version + flags
		payload.WriteByte(0); //reserved
		payload.WriteByte(version == 0 ? (byte)0 : (byte)((cryptByteBlock << 4) | skipByteBlock));
		payload.WriteByte(1); //default_isProtected
		payload.WriteByte(perSampleIvSize);
		payload.Write(new byte[16]); //default_KID
		if (perSampleIvSize == 0)
		{
			constantIv ??= [];
			payload.WriteByte((byte)constantIv.Length);
			payload.Write(constantIv);
		}

		using var box = new MemoryStream();
		WriteUInt32BE(box, (uint)(8 + payload.Length));
		box.Write("tenc"u8);
		payload.Position = 0;
		payload.CopyTo(box);
		box.Position = 0;
		return BoxFactory.CreateBox<TencBox>(box, parent: null);
	}

	private static void WriteUInt32BE(Stream stream, uint value)
	{
		Span<byte> bytes = [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
		stream.Write(bytes);
	}

	private static FrameEntry MakeFrame(byte[] data, byte[]? iv = null)
		=> new()
		{
			SamplesInFrame = 1024,
			FrameData = data,
			ExtraData = iv
		};

	[TestMethod]
	public void PerformFiltering_ProtectedTencWithoutSampleIv_FailsClosed()
	{
		using var filter = new DashFilter(new byte[16], MakeTenc(perSampleIvSize: 8));
		var frame = MakeFrame(new byte[16]);

		Assert.ThrowsExactly<InvalidDataException>(() => filter.PerformFiltering(frame));
	}

	[TestMethod]
	[DataRow(8)]
	[DataRow(16)]
	public void PerformFiltering_ProtectedConstantIv_DecryptsEverySample(int ivSize)
	{
		byte[] ciphertext = Convert.FromHexString("66E94BD4EF8A2C3B884CFA59CA342B2E");
		var tenc = MakeTenc(perSampleIvSize: 0, constantIv: new byte[ivSize]);
		using var filter = new DashFilter(new byte[16], tenc);

		var first = MakeFrame((byte[])ciphertext.Clone());
		var second = MakeFrame((byte[])ciphertext.Clone());
		filter.PerformFiltering(first);
		filter.PerformFiltering(second);

		CollectionAssert.AreEqual(new byte[16], first.FrameData.ToArray());
		CollectionAssert.AreEqual(new byte[16], second.FrameData.ToArray());
		CollectionAssert.AreEqual(new byte[ivSize], tenc.DefaultConstantIv);
	}

	[TestMethod]
	public void Constructor_ProtectedTencWithInvalidConstantIv_FailsClosed()
	{
		var tenc = MakeTenc(perSampleIvSize: 0, constantIv: new byte[4]);

		Assert.ThrowsExactly<InvalidDataException>(() => new DashFilter(new byte[16], tenc));
	}

	[TestMethod]
	[DataRow(1, 0)]
	[DataRow(0, 1)]
	public void Constructor_CencPatternEncryption_FailsClosed(int cryptByteBlock, int skipByteBlock)
	{
		var tenc = MakeTenc(
			perSampleIvSize: 16,
			version: 1,
			cryptByteBlock: checked((byte)cryptByteBlock),
			skipByteBlock: checked((byte)skipByteBlock));

		Assert.ThrowsExactly<InvalidDataException>(() => new DashFilter(new byte[16], tenc));
	}

	[TestMethod]
	public void PerformFiltering_PerSampleIvDoesNotMatchTenc_FailsClosed()
	{
		using var filter = new DashFilter(new byte[16], MakeTenc(perSampleIvSize: 8));
		var frame = MakeFrame(new byte[16], iv: new byte[16]);

		Assert.ThrowsExactly<InvalidDataException>(() => filter.PerformFiltering(frame));
	}

	[TestMethod]
	public void DashFile_CencSampleEntryWithoutTenc_FailsBeforeProtectionMetadataIsRemoved()
	{
		byte[] source = PresentationSyncSafetyTests.CreateAc4Source(
			movieTimescale: 1000,
			mediaTimescale: 1000,
			frameDelta: 1000,
			samples: [1],
			malformedCenc: true);

		Assert.ThrowsExactly<InvalidDataException>(() => new AAXClean.DashFile(new MemoryStream(source)));
	}
}
