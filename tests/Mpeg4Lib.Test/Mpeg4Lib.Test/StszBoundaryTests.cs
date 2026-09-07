using Mpeg4Lib.Boxes;
using System.Buffers.Binary;

namespace Mpeg4Lib.Test;

[TestClass]
public class StszBoundaryTests
{
	[TestMethod]
	public void FixedSizeTrack_UsesLongTotalsAndHasNoPerSampleRenderPayload()
	{
		byte[] source = Bytes(65536, 65536);
		using StszBox box = Parse(source);
		Assert.AreEqual(4_294_967_296L, box.TotalSize);
		Assert.AreEqual(20L, box.RenderSize);
		Assert.AreEqual(65536, box.GetSizeAtIndex(65535));
		using MemoryStream output = new();
		box.Save(output);
		CollectionAssert.AreEqual(source, output.ToArray());
	}

	[TestMethod]
	public void WideSamples_RepeatedSerializationPreservesModelAndBytes()
	{
		byte[] source = Bytes(0, 3, 65535, 65536, 0x01234567);
		using StszBox box = Parse(source);
		for (int pass = 0; pass < 3; pass++)
		{
			using MemoryStream output = new();
			box.Save(output);
			CollectionAssert.AreEqual(source, output.ToArray());
			Assert.AreEqual(65536, box.GetSizeAtIndex(1), "Rendering must not endian-swap the live sample model.");
			Assert.AreEqual(65535L + 65536 + 0x01234567, box.TotalSize);
		}
	}

	[TestMethod]
	public void EmptyVariableTable_HasZeroExtentAndMaximum()
	{
		using StszBox box = Parse(Bytes(0, 0));
		Assert.AreEqual(0, box.MaxSize);
		Assert.AreEqual(0L, box.TotalSize);
		Assert.AreEqual(20L, box.RenderSize);
	}

	[TestMethod]
	public void TableCannotReadEntriesFromFollowingBoxOrTruncatedStream()
	{
		byte[] source = Bytes(0, 1, 123);
		BinaryPrimitives.WriteUInt32BigEndian(source, 20);
		Assert.ThrowsExactly<InvalidDataException>(() => Parse(source));
		byte[] truncated = Bytes(0, 1, 123)[..^1];
		Assert.ThrowsExactly<InvalidDataException>(() => Parse(truncated));
		Assert.ThrowsExactly<InvalidDataException>(() => Parse(Bytes(0, 1024)));
	}

	[TestMethod]
	public void SizesOutsideManagedBufferRangeAreExplicitlyUnsupported()
	{
		Assert.ThrowsExactly<NotSupportedException>(() => Parse(Bytes(0x80000000, 1)));
		Assert.ThrowsExactly<NotSupportedException>(() => Parse(Bytes(0, 1, 0x80000000)));
	}

	[TestMethod]
	public void ChunkBufferBoundsRejectAggregateOverflowAndOutOfRangeFrames()
	{
		using StszBox box = Parse(Bytes(int.MaxValue, 2));
		IStszBox table = box;
		Assert.ThrowsExactly<OverflowException>(() => table.GetFrameSizes(0, 2));
		Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => table.GetFrameSizes(1, 2));
		Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => table.GetFrameSizes(uint.MaxValue, 1));
		(int[] sizes, int total) = table.GetFrameSizes(2, 0);
		Assert.IsEmpty(sizes);
		Assert.AreEqual(0, total);
	}

	internal static StszBox Parse(byte[] source) => BoxFactory.CreateBox<StszBox>(new MemoryStream(source), null);

	internal static byte[] Bytes(uint sampleSize, uint count, params uint[] sizes)
	{
		byte[] result = new byte[20 + sizes.Length * 4];
		BinaryPrimitives.WriteUInt32BigEndian(result, (uint)result.Length);
		"stsz"u8.CopyTo(result.AsSpan(4));
		BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(12), sampleSize);
		BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(16), count);
		for (int i = 0; i < sizes.Length; i++)
			BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(20 + i * 4), sizes[i]);
		return result;
	}
}
