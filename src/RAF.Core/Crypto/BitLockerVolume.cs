using RAF.Core.Disks;
using RAF.Core.Model;

namespace RAF.Core.Crypto;

/// <summary>קריאה גולמית מהמחיצה המוצפנת, בהיסט יחסי לתחילתה.</summary>
internal delegate int RawRead(long offset, Span<byte> destination);

/// <summary>
/// מחיצת BitLocker כפי שהיא נראית אחרי פענוח — מה ש-Windows מציג כשהנעילה פתוחה:
///
/// - תחילת המחיצה (שם יושבת עכשיו הכותרת של BitLocker) נקראת מהמקום שאליו
///   BitLocker העביר את הסקטורים המקוריים.
/// - שלושת העותקים של אזור הניהול נקראים כאפסים: הם אינם חלק ממערכת הקבצים.
/// - מעבר לגודל שכבר הוצפן — הצפנה שלא הסתיימה — התוכן גלוי ונקרא כמו שהוא.
/// </summary>
internal sealed class BitLockerVolume
{
    internal BitLockerMetadata Metadata { get; }
    internal BitLockerCipher Cipher { get; }
    internal long Size { get; }

    /// <summary>
    /// ההיסט שבו הוצפנה תחילת המחיצה המקורית: המקום החדש שלה (כך ב-Windows 7 ומעלה),
    /// או המקום המקורי. נקבע בפתיחה — לפי מה שמפענח למערכת קבצים מוכרת.
    /// </summary>
    private readonly bool _headerKeyedToNewPlace;

    /// <summary>הקבצים שבתוך המחיצה, כפי שזוהו בפענוח תחילתה.</summary>
    internal FileSystemIdentifier.Result Inner { get; }

    private BitLockerVolume(BitLockerMetadata metadata, BitLockerCipher cipher, long size, bool keyedToNewPlace,
        FileSystemIdentifier.Result inner)
    {
        Metadata = metadata;
        Cipher = cipher;
        Size = size;
        _headerKeyedToNewPlace = keyedToNewPlace;
        Inner = inner;
    }

    /// <summary>
    /// פתיחה אחרי שהמפתח נמצא. תחילת המחיצה מפוענחת בשתי הדרכים, ומה שמתגלה בה
    /// כמערכת קבצים מוכרת קובע. אף אחת לא — עדיין פותחים (אולי תחילת המחיצה
    /// ניזוקה), והסריקה המתקדמת תעבוד על כל השאר.
    /// </summary>
    internal static BitLockerVolume Open(BitLockerMetadata metadata, BitLockerCipher cipher, long size, RawRead raw)
    {
        FileSystemIdentifier.Result result = new(FileSystemKind.Raw, "");
        foreach (bool keyedToNewPlace in new[] { true, false })
        {
            var volume = new BitLockerVolume(metadata, cipher, size, keyedToNewPlace, default);
            byte[] head = new byte[Math.Max(2048, cipher.SectorSize)];
            if (volume.Read(0, head, raw) < 512) break;

            var found = FileSystemIdentifier.Identify(head);
            if (found.Kind is not (FileSystemKind.Raw or FileSystemKind.Unknown))
                return new BitLockerVolume(metadata, cipher, size, keyedToNewPlace, found);
        }
        return new BitLockerVolume(metadata, cipher, size, true, result);
    }

    /// <summary>קריאת התוכן המפוענח מהיסט יחסי לתחילת המחיצה.</summary>
    internal int Read(long offset, Span<byte> destination, RawRead raw)
    {
        if (offset < 0 || offset >= Size || destination.Length == 0) return 0;
        int total = (int)Math.Min(destination.Length, Size - offset);

        int sector = Cipher.SectorSize;
        long start = offset / sector * sector;
        long end = Math.Min((offset + total + sector - 1) / sector * sector, (Size + sector - 1) / sector * sector);

        byte[] buffer = new byte[end - start];
        int got = raw(start, buffer) / sector * sector;
        if (got <= offset - start) return 0;
        end = start + got;

        // תחילת המחיצה המקורית — מהמקום שאליו הועברה.
        long headerEnd = Math.Min(end, Metadata.HeaderSize);
        if (start < headerEnd && Metadata.HeaderOffset > 0)
        {
            var span = buffer.AsSpan(0, (int)(headerEnd - start));
            if (raw(Metadata.HeaderOffset + start, span) < span.Length) span.Clear();
            else Cipher.Decrypt(span, (_headerKeyedToNewPlace ? Metadata.HeaderOffset : 0) + start);
        }

        // השאר — במקומו. מעבר לגודל שהוצפן התוכן עוד גלוי.
        long from = Math.Max(start, Metadata.HeaderSize);
        long to = Math.Min(end, Metadata.EncryptedSize > 0 ? Metadata.EncryptedSize : end);
        if (to > from)
            Cipher.Decrypt(buffer.AsSpan((int)(from - start), (int)(to - from)), from);

        // אזור הניהול אינו חלק מהקבצים.
        foreach (long block in Metadata.BlockOffsets)
        {
            long a = Math.Max(start, block), b = Math.Min(end, block + BitLockerMetadata.BlockSize);
            if (b > a) buffer.AsSpan((int)(a - start), (int)(b - a)).Clear();
        }

        int usable = (int)Math.Min(total, end - offset);
        buffer.AsSpan((int)(offset - start), usable).CopyTo(destination);
        return usable;
    }
}
