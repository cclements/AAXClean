using Mpeg4Lib.Boxes;
using Mpeg4Lib.Util;
using System;
using System.IO;

namespace AAXClean.FrameFilters.Audio;

internal class DashFilter : AacValidateFilter
{
	protected override int InputBufferSize => 1000;
	public byte[]? Key { get; }
	private AesCtr? AesCtr { get; }
	private bool HasTrackEncryption { get; }
	private bool IsProtected { get; }
	private byte PerSampleIvSize { get; }
	private byte[]? ConstantIv { get; }

	public DashFilter(byte[]? key, TencBox? trackEncryption, bool audioIsUsac = false)
		: base(audioIsUsac)
	{
		Key = key;
		HasTrackEncryption = trackEncryption is not null;
		if (trackEncryption is not null
			&& (trackEncryption.DefaultCryptByteBlock != 0
				|| trackEncryption.DefaultSkipByteBlock != 0))
		{
			throw new InvalidDataException(
				"The cenc scheme does not support nonzero crypt/skip pattern fields.");
		}

		IsProtected = trackEncryption?.DefaultIsProtected == true;
		if (!IsProtected)
			return;

		if (key is null)
			throw new InvalidOperationException("A protected CENC track requires a decryption key.");

		PerSampleIvSize = trackEncryption!.DefaultPerSampleIvSize;
		if (PerSampleIvSize == 0)
		{
			if (trackEncryption.DefaultConstantIv.Length is not (8 or AesCtr.AES_BLOCK_SIZE))
				throw new InvalidDataException(
					"A protected CENC track without per-sample IVs requires an 8- or 16-byte constant IV.");
			ConstantIv = (byte[])trackEncryption.DefaultConstantIv.Clone();
		}
		else if (PerSampleIvSize is not (8 or AesCtr.AES_BLOCK_SIZE))
		{
			throw new InvalidDataException(
				$"Unsupported CENC per-sample IV size {PerSampleIvSize}; expected 8 or {AesCtr.AES_BLOCK_SIZE} bytes.");
		}

		AesCtr = new AesCtr(key);
	}

	public override FrameEntry PerformFiltering(FrameEntry input)
	{
		if (!HasTrackEncryption && input.ExtraData is byte[])
			throw new InvalidDataException(
				"A CENC sample IV was present without track encryption metadata.");

		if (IsProtected)
		{
			if (AesCtr is null)
				throw new InvalidOperationException(
					"A protected CENC track requires an initialized AES-CTR decryptor.");

			byte[] iv;
			if (ConstantIv is not null)
			{
				iv = ConstantIv;
			}
			else if (input.ExtraData is byte[] sampleIv)
			{
				if (sampleIv.Length != PerSampleIvSize)
					throw new InvalidDataException(
						$"CENC sample IV is {sampleIv.Length} bytes, but tenc requires {PerSampleIvSize} bytes.");
				iv = sampleIv;
			}
			else
			{
				throw new InvalidDataException(
					"A protected CENC sample has no usable per-sample or constant IV.");
			}

			var frameData = input.FrameData.Span;
			AesCtr.Decrypt(iv, frameData, frameData);
		}
		return base.PerformFiltering(input);
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing && !Disposed)
			AesCtr?.Dispose();
		base.Dispose(disposing);
	}
}
