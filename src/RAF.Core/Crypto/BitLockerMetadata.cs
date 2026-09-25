using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace RAF.Core.Crypto;

/// <summary>
/// אזור הניהול של BitLocker: שלושה עותקים (לא מוצפנים) שבהם שמורים המפתחות,
/// כל אחד נעול ב"מגן" אחר — מפתח השחזור, סיסמה, שבב TPM במחשב. אחד מהם
/// פותחים עם מה שהמשתמש יודע, ומקבלים את מפתח הכונן שמצפין כל סקטור.
///
/// המבנה: כותרת המחיצה מפנה לשלושת העותקים; בכל עותק כותרת, ואחריה רשימת
/// ערכים — חלקם מכילים רשימה פנימית משלהם.
/// </summary>
internal sealed class BitLockerMetadata
{
    /// <summary>מזהה BitLocker שבכותרת המחיצה (4967D63B-2E29-4AD8-8399-F6A339E3D001).</summary>
    private static readonly Guid Identifier = new("4967d63b-2e29-4ad8-8399-f6a339e3d001");

    /// <summary>גודל כל עותק של אזור הניהול — מה שהקורא מציג כאפסים.</summary>
    internal const int BlockSize = 64 * 1024;

    /// <summary>שיטת ההצפנה של הכונן.</summary>
    internal ushort Method { get; private init; }

    /// <summary>הגודל שכבר הוצפן. מעבר לו — הצפנה שלא הסתיימה, והתוכן גלוי.</summary>
    internal long EncryptedSize { get; private init; }

    /// <summary>היכן שמורה תחילת המחיצה המקורית (הסקטורים שהכותרת של BitLocker תפסה), ובאיזה גודל.</summary>
    internal long HeaderOffset { get; private init; }
    internal long HeaderSize { get; private init; }

    /// <summary>היסטים של שלושת העותקים, יחסית לתחילת המחיצה.</summary>
    internal long[] BlockOffsets { get; private init; } = Array.Empty<long>();

    internal int SectorSize { get; private init; }

    /// <summary>המגנים שהוגדרו לכונן, בסדר שבו הם שמורים.</summary>
    internal List<Protector> Protectors { get; } = new();

    /// <summary>מפתח הכונן, נעול במפתח הביניים.</summary>
    private Entry? _fvek;

    internal enum ProtectorKind
    {
        ClearKey = 0x0000,        // ההגנה הושהתה: המפתח שמור גלוי
        Tpm = 0x0100,
        StartupKey = 0x0200,       // קובץ BEK בדיסק-און-קי
        TpmAndPin = 0x0500,
        RecoveryPassword = 0x0800, // 48 ספרות
        Password = 0x2000,
    }

    internal sealed record Protector(ProtectorKind Kind, Guid Id, List<Entry> Properties);

    /// <summary>ערך באזור הניהול: סוג, סוג התוכן ותוכן.</summary>
    internal sealed record Entry(ushort Type, ushort ValueType, byte[] Data)
    {
        /// <summary>ערכים פנימיים, מההיסט הנתון בתוך התוכן.</summary>
        internal List<Entry> Nested(int at) => ParseEntries(Data, at, Data.Length);
    }

    /// <summary>
    /// זיהוי: כותרת של BitLocker (Windows 7 ומעלה), או של BitLocker To Go על כונן
    /// FAT — שם הכותרת היא מחיצת FAT32 קטנה עם קורא, והמזהה בהיסט אחר.
    /// </summary>
    internal static long[]? BlockOffsetsOf(ReadOnlySpan<byte> header)
    {
        if (header.Length < 512) return null;

        int at = header.Slice(3, 8).SequenceEqual("-FVE-FS-"u8) ? 0xA0
               : header.Slice(3, 8).SequenceEqual("MSWIN4.1"u8) ? 0x1A8
               : -1;
        if (at < 0 || new Guid(header.Slice(at, 16)) != Identifier) return null;

        return new[]
        {
            BinaryPrimitives.ReadInt64LittleEndian(header[(at + 16)..]),
            BinaryPrimitives.ReadInt64LittleEndian(header[(at + 24)..]),
            BinaryPrimitives.ReadInt64LittleEndian(header[(at + 32)..]),
        };
    }

    internal static bool IsBitLocker(ReadOnlySpan<byte> header) => BlockOffsetsOf(header) is not null;

    /// <summary>
    /// קריאת אזור הניהול מהמחיצה. העותק הראשון שנקרא בשלמותו מנצח — אם הראשון
    /// ניזוק, השני והשלישי נמצאים במקומות אחרים במחיצה בדיוק בשביל זה.
    /// </summary>
    internal static BitLockerMetadata Read(Func<long, int, byte[]> read, long volumeSize)
    {
        byte[] header = read(0, 512);
        var offsets = BlockOffsetsOf(header)
            ?? throw new InvalidOperationException(L.T("המחיצה אינה מוצפנת ב-BitLocker."));

        int sectorSize = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(11));
        if (sectorSize is not (512 or 1024 or 2048 or 4096)) sectorSize = 512;

        foreach (long offset in offsets)
        {
            if (offset <= 0 || offset + BlockSize > volumeSize) continue;
            try
            {
                var metadata = Parse(read(offset, BlockSize), offsets, sectorSize);
                if (metadata is not null) return metadata;
            }
            catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
            {
                // עותק פגום — ממשיכים לבא.
            }
        }

        throw new InvalidOperationException(
            L.T("אזור הניהול של ההצפנה ניזוק בכל שלושת העותקים שלו, ולכן אי אפשר לפתוח את הכונן גם עם המפתח הנכון."));
    }

    private static BitLockerMetadata? Parse(byte[] block, long[] offsets, int sectorSize)
    {
        if (!block.AsSpan(0, 8).SequenceEqual("-FVE-FS-"u8)) return null;

        int version = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(10));
        if (version != 2)
            throw new InvalidOperationException(
                L.T("הכונן הוצפן ב-Windows Vista, בגרסה ישנה של BitLocker שהתוכנה אינה קוראת. פתחו אותו ב-Windows, ואז אפשר לסרוק אותו כאן."));

        const int meta = 0x40;
        int size = BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(meta));
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(meta + 8));
        if (headerSize != 0x30 || size < headerSize || meta + size > block.Length) return null;

        var entries = ParseEntries(block, meta + headerSize, meta + size);

        long headerOffset = BinaryPrimitives.ReadInt64LittleEndian(block.AsSpan(0x38));
        long headerLength = (long)BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(0x1C)) * sectorSize;

        // ערך "מיקום וגודל" של תחילת המחיצה המקורית — מדויק יותר משדה הכותרת.
        if (entries.FirstOrDefault(e => e.Type == 0x000F && e.ValueType == 0x000F) is { Data.Length: >= 24 } location)
        {
            headerOffset = BinaryPrimitives.ReadInt64LittleEndian(location.Data.AsSpan(8));
            headerLength = BinaryPrimitives.ReadInt64LittleEndian(location.Data.AsSpan(16));
        }

        var metadata = new BitLockerMetadata
        {
            Method = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(meta + 0x24)),
            EncryptedSize = BinaryPrimitives.ReadInt64LittleEndian(block.AsSpan(0x10)),
            HeaderOffset = headerOffset,
            HeaderSize = headerLength,
            BlockOffsets = offsets,
            SectorSize = sectorSize,
            _fvek = entries.FirstOrDefault(e => e.Type == 0x0003 && e.ValueType == 0x0005),
        };

        foreach (var e in entries.Where(e => e.Type == 0x0002 && e.ValueType == 0x0008 && e.Data.Length >= 0x24))
        {
            var kind = (ProtectorKind)BinaryPrimitives.ReadUInt16LittleEndian(e.Data.AsSpan(0x22));
            metadata.Protectors.Add(new Protector(kind, new Guid(e.Data.AsSpan(8, 16)), e.Nested(0x24)));
        }

        return metadata._fvek is null ? null : metadata;
    }

    /// <summary>רשימת ערכים: לכל אחד גודל, סוג, סוג תוכן וגרסה, ואז התוכן.</summary>
    private static List<Entry> ParseEntries(byte[] data, int from, int to)
    {
        var list = new List<Entry>();
        int at = from;
        while (at + 8 <= to)
        {
            int size = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at));
            if (size < 8 || at + size > to) break;
            list.Add(new Entry(
                BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 2)),
                BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 4)),
                data.AsSpan(at, size).ToArray()));
            at += size;
        }
        return list;
    }

    // ------------------------------------------------------------ פתיחה

    /// <summary>המגן הושהה (Suspend) — אפשר לפתוח בלי סיסמה ובלי מפתח.</summary>
    internal bool HasClearKey => Protectors.Any(p => p.Kind == ProtectorKind.ClearKey);

    /// <summary>
    /// פתיחה עם מה שהמשתמש הקליד: מפתח שחזור (48 ספרות) או סיסמה. ריק — רק מגן
    /// גלוי. מחזיר null כשהמפתח אינו נכון לאף מגן.
    /// </summary>
    internal BitLockerCipher? Unlock(string secret)
    {
        foreach (var protector in Protectors.Where(p => p.Kind == ProtectorKind.ClearKey))
            if (protector.Properties.FirstOrDefault(e => e.ValueType == 0x0001) is { } clear &&
                OpenFvek(DecryptKey(protector, KeyOf(clear))) is { } cipher)
                return cipher;

        if (string.IsNullOrWhiteSpace(secret)) return null;

        byte[]? passwordHash = null;
        ProtectorKind kind = ProtectorKind.Password;
        if (RecoveryKey(secret) is { } recovery)
        {
            passwordHash = SHA256.HashData(recovery);
            kind = ProtectorKind.RecoveryPassword;
        }

        // 48 ספרות שהן גם סיסמה חוקית: קודם כמפתח שחזור, ואם לא — כסיסמה.
        foreach (var attempt in passwordHash is null ? new[] { ProtectorKind.Password } : new[] { kind, ProtectorKind.Password })
        {
            byte[] hash = attempt == ProtectorKind.RecoveryPassword
                ? passwordHash!
                : SHA256.HashData(SHA256.HashData(Encoding.Unicode.GetBytes(secret)));

            foreach (var protector in Protectors.Where(p => p.Kind == attempt))
            {
                var stretch = protector.Properties.FirstOrDefault(e => e.ValueType == 0x0003 && e.Data.Length >= 0x1C);
                if (stretch is null) continue;
                byte[] key = Stretch(hash, stretch.Data.AsSpan(0x0C, 16));
                if (OpenFvek(DecryptKey(protector, key)) is { } cipher) return cipher;
            }
        }

        return null;
    }

    /// <summary>מפתח הביניים (VMK), כשהוא נפתח במפתח הנתון. null — המפתח שגוי.</summary>
    private static byte[]? DecryptKey(Protector protector, byte[]? key)
    {
        if (key is null) return null;
        foreach (var locked in protector.Properties.Where(e => e.ValueType == 0x0005))
            if (Decrypt(locked, key) is { } plain && KeyOf(plain) is { Length: 32 } vmk)
                return vmk;
        return null;
    }

    private BitLockerCipher? OpenFvek(byte[]? vmk)
    {
        if (vmk is null || _fvek is null || Decrypt(_fvek, vmk) is not { } plain) return null;
        if (plain.Length < 12) return null;
        ushort method = BinaryPrimitives.ReadUInt16LittleEndian(plain.AsSpan(8));
        return BitLockerCipher.Create(method == 0 ? Method : method, KeyOf(plain) ?? Array.Empty<byte>(), SectorSize);
    }

    /// <summary>
    /// פענוח ערך נעול (AES-CCM): 12 בתים של ערך חד-פעמי, 16 של חותמת, ואז התוכן.
    /// חותמת שאינה תואמת — המפתח שגוי; כך לעולם לא "נפתח" כונן בסיסמה לא נכונה.
    /// </summary>
    private static byte[]? Decrypt(Entry entry, byte[] key)
    {
        var data = entry.Data;
        if (data.Length < 0x24 || key.Length is not (16 or 24 or 32)) return null;

        byte[] plain = new byte[data.Length - 0x24];
        try
        {
            using var ccm = new AesCcm(key);
            ccm.Decrypt(data.AsSpan(8, 12), data.AsSpan(0x24), data.AsSpan(0x14, 16), plain);
            return plain;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>ערך מסוג "מפתח": שיטה ב-8, והמפתח מ-12 עד סוף הערך.</summary>
    private static byte[]? KeyOf(Entry entry) => KeyOf(entry.Data);

    private static byte[]? KeyOf(byte[] data)
    {
        if (data.Length < 12) return null;
        int size = BinaryPrimitives.ReadUInt16LittleEndian(data);
        ushort valueType = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4));
        if (valueType != 0x0001 || size < 12 || size > data.Length) return null;
        return data.AsSpan(12, size - 12).ToArray();
    }

    /// <summary>
    /// מפתח שחזור: 8 קבוצות של 6 ספרות, כל אחת מתחלקת ב-11 והמנה קטנה מ-65536.
    /// כל מנה היא שני בתים של המפתח. מקפים ורווחים מותרים בכל מקום.
    /// </summary>
    internal static byte[]? RecoveryKey(string text)
    {
        var digits = new StringBuilder();
        foreach (char c in text)
        {
            if (char.IsAsciiDigit(c)) digits.Append(c);
            else if (c is not ('-' or ' ' or ' ' or '\t')) return null;
        }
        if (digits.Length != 48) return null;

        byte[] key = new byte[16];
        for (int i = 0; i < 8; i++)
        {
            int group = int.Parse(digits.ToString(i * 6, 6));
            if (group % 11 != 0 || group / 11 > 0xFFFF) return null;
            BinaryPrimitives.WriteUInt16LittleEndian(key.AsSpan(i * 2), (ushort)(group / 11));
        }
        return key;
    }

    /// <summary>
    /// "מתיחת" הסיסמה: מיליון סבבים של SHA-256 על הגיבוב הקודם, הגיבוב של הסיסמה,
    /// המלח ומונה. זה מה שהופך ניחוש סיסמאות לאיטי — וזה לוקח כשנייה.
    /// </summary>
    internal static byte[] Stretch(ReadOnlySpan<byte> passwordHash, ReadOnlySpan<byte> salt)
    {
        Span<byte> state = stackalloc byte[88];
        state.Clear();
        passwordHash.CopyTo(state[32..]);
        salt.CopyTo(state[64..]);

        Span<byte> next = stackalloc byte[32];
        for (ulong count = 0; count < 0x100000; count++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(state[80..], count);
            SHA256.HashData(state, next);
            next.CopyTo(state);
        }
        return state[..32].ToArray();
    }

    internal static string Describe(ProtectorKind kind) => kind switch
    {
        ProtectorKind.RecoveryPassword => L.T("מפתח שחזור"),
        ProtectorKind.Password => L.T("סיסמה"),
        ProtectorKind.Tpm => L.T("שבב האבטחה של המחשב"),
        ProtectorKind.TpmAndPin => L.T("שבב האבטחה של המחשב וקוד"),
        ProtectorKind.StartupKey => L.T("קובץ מפתח בדיסק-און-קי"),
        ProtectorKind.ClearKey => L.T("ההגנה מושהית"),
        _ => L.T("אחר"),
    };
}
