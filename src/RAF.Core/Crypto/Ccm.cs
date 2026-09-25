using System.Security.Cryptography;

namespace RAF.Core.Crypto;

/// <summary>
/// פענוח AES-CCM (RFC 3610) על גבי AES רגיל — במערכות שבהן ‎.NET לא מספקת AesCcm (במק).
/// CCM הוא מונה (CTR) להצפנה, ו-CBC-MAC לחותמת; שניהם צריכים רק הצפנת בלוק בודד.
/// </summary>
internal static class Ccm
{
    /// <summary>
    /// פענוח ובדיקת חותמת (בלי נתונים נלווים). false — החותמת אינה תואמת: המפתח שגוי.
    /// </summary>
    public static bool Decrypt(byte[] key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag, Span<byte> plaintext)
    {
        if (AesCcm.IsSupported)
        {
            try
            {
                using var native = new AesCcm(key);
                native.Decrypt(nonce, ciphertext, tag, plaintext);
                return true;
            }
            catch (CryptographicException) { return false; }
        }
        return DecryptManaged(key, nonce, ciphertext, tag, plaintext);
    }

    internal static bool DecryptManaged(byte[] key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag, Span<byte> plaintext)
    {
        int l = 15 - nonce.Length;                         // אורך מונה הבלוקים, בבתים
        if (l is < 2 or > 8 || tag.Length is < 4 or > 16 || tag.Length % 2 == 1) return false;
        using var aes = Aes.Create();
        aes.Key = key;

        Span<byte> counter = stackalloc byte[16], stream = stackalloc byte[16];
        counter[0] = (byte)(l - 1);
        nonce.CopyTo(counter[1..]);

        void Block(Span<byte> c, long i)
        {
            for (int k = 0; k < l; k++) c[15 - k] = (byte)(i >> (8 * k));
        }

        // תוכן: XOR עם הצפנת המונה, מבלוק 1.
        for (int at = 0, i = 1; at < ciphertext.Length; at += 16, i++)
        {
            Block(counter, i);
            aes.EncryptEcb(counter, stream, PaddingMode.None);
            int n = Math.Min(16, ciphertext.Length - at);
            for (int k = 0; k < n; k++) plaintext[at + k] = (byte)(ciphertext[at + k] ^ stream[k]);
        }

        // חותמת: CBC-MAC על B0 ועל התוכן המפוענח, ואז XOR עם הצפנת מונה 0.
        Span<byte> mac = stackalloc byte[16], block = stackalloc byte[16];
        block.Clear();
        block[0] = (byte)(((tag.Length - 2) / 2) << 3 | (l - 1));
        nonce.CopyTo(block[1..]);
        for (int k = 0; k < l; k++) block[15 - k] = (byte)((long)ciphertext.Length >> (8 * k));
        aes.EncryptEcb(block, mac, PaddingMode.None);
        for (int at = 0; at < plaintext.Length; at += 16)
        {
            int n = Math.Min(16, plaintext.Length - at);
            for (int k = 0; k < 16; k++) block[k] = (byte)(mac[k] ^ (k < n ? plaintext[at + k] : 0));
            aes.EncryptEcb(block, mac, PaddingMode.None);
        }
        Block(counter, 0);
        aes.EncryptEcb(counter, stream, PaddingMode.None);
        for (int k = 0; k < tag.Length; k++) mac[k] ^= stream[k];

        if (CryptographicOperations.FixedTimeEquals(mac[..tag.Length], tag)) return true;
        plaintext.Clear();
        return false;
    }
}
