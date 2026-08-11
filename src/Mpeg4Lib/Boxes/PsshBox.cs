using Mpeg4Lib.Util;
using System;
using System.IO;

namespace Mpeg4Lib.Boxes;

public class PsshBox : FullBox
{
	public override long RenderSize => Header.HeaderSize + sizeof(uint) + 16
		+ (Version == 1 ? sizeof(uint) + 16L * KIDs.Length : 0)
		+ sizeof(uint) + InitData.Length;
	public Guid SystemID { get; }
	public Guid ProtectionSystemId => SystemID;
	public Guid[] KIDs { get; }
	public byte[] InitData { get; }
	public byte[] ExtraData => Array.Empty<byte>();

	public PsshBox(Stream file, BoxHeader header, IBox? parent) : base(file, header, parent)
	{
		long boxEnd = header.FilePosition + header.TotalBoxSize;
		if (Version is not (0 or 1))
			throw new InvalidDataException($"Unsupported pssh version {Version}.");

		EnsureRemaining(file, boxEnd, 16, nameof(SystemID));
		SystemID = new Guid(file.ReadBlock(16), bigEndian: true);

		if (Version == 1)
		{
			EnsureRemaining(file, boxEnd, sizeof(uint), "KID_count");
			uint kidCount = file.ReadUInt32BE();
			long kidBytes = 16L * kidCount;
			EnsureRemaining(file, boxEnd, kidBytes + sizeof(uint), "KIDs and DataSize");
			if (kidCount > int.MaxValue)
				throw new InvalidDataException($"pssh KID_count {kidCount} exceeds the supported array size.");

			KIDs = new Guid[(int)kidCount];
			for (int i = 0; i < KIDs.Length; i++)
				KIDs[i] = new Guid(file.ReadBlock(16), bigEndian: true);
		}
		else
		{
			KIDs = [];
		}

		EnsureRemaining(file, boxEnd, sizeof(uint), "DataSize");
		uint initDataSize = file.ReadUInt32BE();
		long remaining = boxEnd - file.Position;
		if (remaining != initDataSize)
			throw new InvalidDataException($"pssh DataSize is {initDataSize} bytes, but {remaining} bytes remain in the box.");
		if (initDataSize > int.MaxValue)
			throw new InvalidDataException($"pssh DataSize {initDataSize} exceeds the supported array size.");

		InitData = file.ReadBlock((int)initDataSize);
	}

	protected override void Render(Stream file)
	{
		base.Render(file);
		file.Write(SystemID.ToByteArray(bigEndian: true));
		if (Version == 1)
		{
			file.WriteUInt32BE((uint)KIDs.Length);
			foreach (var kid in KIDs)
				file.Write(kid.ToByteArray(bigEndian: true));
		}
		file.WriteUInt32BE((uint)InitData.Length);
		file.Write(InitData);
	}

	private static void EnsureRemaining(Stream file, long boxEnd, long required, string field)
	{
		long remaining = boxEnd - file.Position;
		if (required < 0 || remaining < required)
			throw new InvalidDataException($"pssh {field} requires {required} bytes, but only {remaining} bytes remain in the box.");
	}
}
