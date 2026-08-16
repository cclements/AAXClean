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
			//ISO/IEC 23001-7 pairs constant IVs (per_sample_IV_size == 0) with the
			//cbcs pattern scheme. Under cenc's AES-CTR mode one shared IV would reuse
			//the whole keystream for every sample, so a conformant cenc encryptor
			//cannot produce this shape; reject it instead of decrypting every sample
			//with the same counter block.
			throw new InvalidDataException(
				"A protected CENC track must carry per-sample IVs; a constant IV is a cbcs-scheme feature.");
		}

		if (PerSampleIvSize is not (8 or AesCtr.AES_BLOCK_SIZE))
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
			if (input.ExtraData is byte[] sampleIv)
			{
				if (sampleIv.Length != PerSampleIvSize)
					throw new InvalidDataException(
						$"CENC sample IV is {sampleIv.Length} bytes, but tenc requires {PerSampleIvSize} bytes.");
				iv = sampleIv;
			}
			else
			{
				throw new InvalidDataException(
					"A protected CENC sample has no per-sample IV.");
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
