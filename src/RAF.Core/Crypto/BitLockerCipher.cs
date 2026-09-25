using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace RAF.Core.Crypto;

/// <summary>
/// פענוח סקטורים של BitLocker. כל סקטור מוצפן לחוד, והמקום שלו במחיצה נכנס
/// להצפנה — כך שני סקטורים זהים נראים שונים לגמרי. ארבע שיטות:
///
/// - AES-XTS (Windows 10 ומעלה): המקום הוא מספר הסקטור.
/// - AES-CBC (כונן נייד, מצב "תואם"): וקטור ההתחלה הוא ההיסט בבתים, מוצפן.
/// - AES-CBC עם "מפזר" (Windows 7 ו-Vista): אחרי CBC, שני סבבי ערבוב של
///   מילים בנות 4 בתים, ו-XOR עם מפתח לסקטור.
/// </summary>
internal sealed class BitLockerCipher : IDisposable
{
    private readonly ushort _method;
    private readonly byte[] _key;
    private readonly byte[]? _tweakKey;
    private readonly ThreadLocal<Aes> _aes;
    private readonly ThreadLocal<Aes>? _tweak;

    internal int SectorSize { get; }

    /// <summary>לתצוגה: "AES-XTS 128" וכדומה.</summary>
    internal string Name => _method switch
    {
        0x8000 => "AES-CBC 128 + Elephant",
        0x8001 => "AES-CBC 256 + Elephant",
        0x8002 => "AES-CBC 128",
        0x8003 => "AES-CBC 256",
        0x8004 => "AES-XTS 128",
        0x8005 => "AES-XTS 256",
        _ => "?",
    };

    private bool Xts => _method is 0x8004 or 0x8005;
    private bool Diffuser => _method is 0x8000 or 0x8001;

    private BitLockerCipher(ushort method, byte[] key, byte[]? tweakKey, int sectorSize)
    {
        _method = method;
        _key = key;
        _tweakKey = tweakKey;
        SectorSize = sectorSize;
        _aes = new ThreadLocal<Aes>(() => NewAes(_key), trackAllValues: true);
        if (tweakKey is not null) _tweak = new ThreadLocal<Aes>(() => NewAes(_tweakKey!), trackAllValues: true);
    }

    private static Aes NewAes(byte[] key)
    {
        var aes = Aes.Create();
        aes.Key = key;
        return aes;
    }

    /// <summary>
    /// המפתח שנפתח מאזור הניהול, לפי השיטה. בשיטות עם מפזר מפתח הערבוב
    /// יושב תמיד בהיסט 32, גם כשהמפתח עצמו 16 בתים. null — שיטה לא מוכרת.
    /// </summary>
    internal static BitLockerCipher? Create(ushort method, byte[] key, int sectorSize)
    {
        (int keyLength, int tweakAt, int tweakLength) = method switch
        {
            0x8000 => (16, 32, 16),
            0x8001 => (32, 32, 32),
            0x8002 => (16, 0, 0),
            0x8003 => (32, 0, 0),
            0x8004 => (16, 16, 16),
            0x8005 => (32, 32, 32),
            _ => (0, 0, 0),
        };
        if (keyLength == 0 || key.Length < Math.Max(keyLength, tweakAt + tweakLength)) return null;

        return new BitLockerCipher(method, key[..keyLength],
            tweakLength > 0 ? key[tweakAt..(tweakAt + tweakLength)] : null, sectorSize);
    }

    /// <summary>
    /// פענוח במקום של סקטורים רצופים. <paramref name="offset"/> — ההיסט של הסקטור
    /// הראשון מתחילת המחיצה, כפי שהוצפן (לא בהכרח המקום שממנו נקרא).
    /// </summary>
    internal void Decrypt(Span<byte> data, long offset)
    {
        for (int at = 0; at + SectorSize <= data.Length; at += SectorSize)
            DecryptSector(data.Slice(at, SectorSize), offset + at);
    }

    private void DecryptSector(Span<byte> sector, long offset)
    {
        var aes = _aes.Value!;
        Span<byte> block = stackalloc byte[16];
        block.Clear();

        if (Xts)
        {
            BinaryPrimitives.WriteInt64LittleEndian(block, offset / SectorSize);
            Span<byte> tweak = stackalloc byte[16];
            _tweak!.Value!.EncryptEcb(block, tweak, PaddingMode.None);

            byte[] tweaks = new byte[sector.Length];
            for (int i = 0; i < sector.Length; i += 16)
            {
                tweak.CopyTo(tweaks.AsSpan(i));
                MultiplyByX(tweak);
            }
            Xor(sector, tweaks);
            aes.DecryptEcb(sector, sector, PaddingMode.None);
            Xor(sector, tweaks);
            return;
        }

        BinaryPrimitives.WriteInt64LittleEndian(block, offset);
        Span<byte> iv = stackalloc byte[16];
        aes.EncryptEcb(block, iv, PaddingMode.None);
        aes.DecryptCbc(sector, iv, sector, PaddingMode.None);

        if (!Diffuser) return;

        Span<byte> sectorKey = stackalloc byte[32];
        var tweakAes = _tweak!.Value!;
        tweakAes.EncryptEcb(block, sectorKey[..16], PaddingMode.None);
        block[15] = 0x80;
        tweakAes.EncryptEcb(block, sectorKey[16..], PaddingMode.None);

        var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(sector);
        DiffuserB(words);
        DiffuserA(words);
        for (int i = 0; i < sector.Length; i++) sector[i] ^= sectorKey[i % 32];
    }

    private static readonly int[] RotateA = { 9, 0, 13, 0 };
    private static readonly int[] RotateB = { 0, 10, 0, 25 };

    /// <summary>המפזר A, בכיוון הפענוח: חמישה סבבים.</summary>
    private static void DiffuserA(Span<uint> d)
    {
        int n = d.Length;
        for (int round = 0; round < 5; round++)
            for (int i = 0; i < n; i++)
                d[i] += d[(i + n - 2) % n] ^ BitOperations.RotateLeft(d[(i + n - 5) % n], RotateA[i % 4]);
    }

    /// <summary>המפזר B, בכיוון הפענוח: שלושה סבבים.</summary>
    private static void DiffuserB(Span<uint> d)
    {
        int n = d.Length;
        for (int round = 0; round < 3; round++)
            for (int i = 0; i < n; i++)
                d[i] += d[(i + 2) % n] ^ BitOperations.RotateLeft(d[(i + 5) % n], RotateB[i % 4]);
    }

    /// <summary>הכפלה ב-x בשדה GF(2^128) — ה-tweak של הבלוק הבא ב-XTS.</summary>
    private static void MultiplyByX(Span<byte> t)
    {
        int carry = 0;
        for (int i = 0; i < 16; i++)
        {
            int next = t[i] >> 7;
            t[i] = (byte)((t[i] << 1) | carry);
            carry = next;
        }
        if (carry != 0) t[0] ^= 0x87;
    }

    private static void Xor(Span<byte> target, ReadOnlySpan<byte> with)
    {
        var a = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(target);
        var b = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(with);
        for (int i = 0; i < a.Length; i++) a[i] ^= b[i];
    }

    public void Dispose()
    {
        foreach (var aes in _aes.Values) aes.Dispose();
        _aes.Dispose();
        if (_tweak is not null)
        {
            foreach (var aes in _tweak.Values) aes.Dispose();
            _tweak.Dispose();
        }
    }
}
