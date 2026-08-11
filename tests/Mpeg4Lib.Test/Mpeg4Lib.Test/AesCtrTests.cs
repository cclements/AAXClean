using Mpeg4Lib.Util;

namespace Mpeg4Lib.Test;

[TestClass]
public class AesCtrTests
{
	[TestMethod]
	public void Decrypt_EightByteCencIv_ZeroExtendsWithoutMutatingIv()
	{
		byte[] key = new byte[16];
		byte[] iv = new byte[8];
		byte[] ciphertext = Convert.FromHexString(
			"66E94BD4EF8A2C3B884CFA59CA342B2E" +
			"58E2FCCEFA7E3061367F1D57A4E7455A");
		byte[] plaintext = new byte[ciphertext.Length];

		using var aes = new AesCtr(key);
		aes.Decrypt(iv, ciphertext, plaintext);

		CollectionAssert.AreEqual(new byte[32], plaintext);
		CollectionAssert.AreEqual(new byte[8], iv);
	}

	[TestMethod]
	public void Decrypt_SixteenByteNistVector_RemainsSupportedWithoutMutatingIv()
	{
		byte[] key = Convert.FromHexString("2B7E151628AED2A6ABF7158809CF4F3C");
		byte[] iv = Convert.FromHexString("F0F1F2F3F4F5F6F7F8F9FAFBFCFDFEFF");
		byte[] originalIv = (byte[])iv.Clone();
		byte[] ciphertext = Convert.FromHexString(
			"874D6191B620E3261BEF6864990DB6CE" +
			"9806F66B7970FDFF8617187BB9FFFDFF");
		byte[] expected = Convert.FromHexString(
			"6BC1BEE22E409F96E93D7E117393172A" +
			"AE2D8A571E03AC9C9EB76FAC45AF8E51");
		byte[] plaintext = new byte[ciphertext.Length];

		using var aes = new AesCtr(key);
		aes.Decrypt(iv, ciphertext, plaintext);

		CollectionAssert.AreEqual(expected, plaintext);
		CollectionAssert.AreEqual(originalIv, iv);
	}
}
