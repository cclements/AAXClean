using Mpeg4Lib.Util;
using System;
using System.IO;

namespace Mpeg4Lib.Boxes;

public class PsshBox : FullBox
{
	public override long RenderSize => Header.HeaderSize + sizeof(uint) + 16
		+ (Version == 1 ? sizeof(uint) + 16L * KIDs.Length : 0)
		+ sizeof(uint) + InitData.Length;
	public Guid ProtectionSystemId { get; }
	public Guid[] KIDs { get; }
	public byte[] InitData { get; }
	public byte[] ExtraData => Array.Empty<byte>();

	public PsshBox(Stream file, BoxHeader header, IBox? parent) : base(file, header, parent)
	{
		long fullBoxHeaderSize = (long)header.HeaderSize + sizeof(uint);
		if (header.TotalBoxSize < fullBoxHeaderSize)
			throw new InvalidDataException(
				$"pssh size {header.TotalBoxSize} is smaller than its {fullBoxHeaderSize}-byte header.");

		long remaining = header.TotalBoxSize - fullBoxHeaderSize;
		if (Version is not (0 or 1))
			throw new InvalidDataException($"Unsupported pssh version {Version}.");

		EnsureRemaining(remaining, 16, nameof(ProtectionSystemId));
		ProtectionSystemId = new Guid(file.ReadBlock(16), bigEndian: true);
		remaining -= 16;

		if (Version == 1)
		{
			EnsureRemaining(remaining, sizeof(uint), "KID_count");
			uint kidCount = file.ReadUInt32BE();
			remaining -= sizeof(uint);

			if (kidCount > int.MaxValue)
				throw new InvalidDataException($"pssh KID_count {kidCount} exceeds the supported array size.");

			long kidBytes = 16L * kidCount;
			EnsureRemaining(remaining, kidBytes + sizeof(uint), "KIDs and DataSize");
			KIDs = new Guid[(int)kidCount];
			for (int i = 0; i < KIDs.Length; i++)
				KIDs[i] = new Guid(file.ReadBlock(16), bigEndian: true);
			remaining -= kidBytes;
		}
		else
		{
			KIDs = [];
		}

		EnsureRemaining(remaining, sizeof(uint), "DataSize");
		uint initDataSize = file.ReadUInt32BE();
		remaining -= sizeof(uint);
		if (remaining != initDataSize)
			throw new InvalidDataException(
				$"pssh DataSize is {initDataSize} bytes, but {remaining} bytes remain in the box.");
		if (initDataSize > int.MaxValue)
			throw new InvalidDataException($"pssh DataSize {initDataSize} exceeds the supported array size.");

		InitData = file.ReadBlock((int)initDataSize);
	}

	protected override void Render(Stream file)
	{
		base.Render(file);
		file.Write(ProtectionSystemId.ToByteArray(bigEndian: true));
		if (Version == 1)
		{
			file.WriteUInt32BE((uint)KIDs.Length);
			foreach (Guid kid in KIDs)
				file.Write(kid.ToByteArray(bigEndian: true));
		}
		file.WriteUInt32BE((uint)InitData.Length);
		file.Write(InitData);
	}

	private static void EnsureRemaining(long remaining, long required, string field)
	{
		if (required < 0 || remaining < required)
			throw new InvalidDataException(
				$"pssh {field} requires {required} bytes, but only {remaining} bytes remain in the box.");
	}
}
