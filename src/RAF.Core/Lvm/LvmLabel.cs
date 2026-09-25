using System.Buffers.Binary;
using System.Text;

namespace RAF.Core.Lvm;

/// <summary>
/// כונן שהוא חלק ממאגר לוגי של לינוקס (LVM2): התווית באחד מארבעת הסקטורים הראשונים,
/// ואחריה אזור ניהול עם תיאור המאגר — טקסט קריא שמפרט את הכוננים, האזורים, ואיפה
/// כל קטע של כל אזור יושב. אותו תיאור נשמר בכל כונן במאגר.
///
/// אזור הניהול הוא מעגלי: כל שינוי כותב עותק חדש אחרי הקודם, והכותרת שלו מצביעה על
/// העותק העדכני. לכן גם אזור שנמחק מהמאגר נשאר לפעמים בעותק ישן — זה עוד לא מנוצל כאן.
/// </summary>
internal sealed class LvmLabel
{
    /// <summary>מזהה הכונן במאגר (32 תווים, בלי מקפים).</summary>
    internal string PvId { get; init; } = "";
    /// <summary>התיאור העדכני של המאגר, או null — לא נקרא.</summary>
    internal string? Metadata { get; init; }

    private static readonly byte[] MdaMagic = Encoding.ASCII.GetBytes(" LVM2 x[5A%r0N*>");

    internal static bool IsLabel(ReadOnlySpan<byte> sector)
        => sector.Length >= 32 && sector[..8].SequenceEqual("LABELONE"u8) && sector.Slice(24, 8).SequenceEqual("LVM2 001"u8);

    internal static LvmLabel? Read(Func<long, int, byte[]?> read, long size)
    {
        if (read(0, 2048) is not { Length: 2048 } head) return null;
        for (int s = 0; s < 4; s++)
        {
            var sector = head.AsSpan(s * 512, 512);
            if (!IsLabel(sector)) continue;
            int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(sector[20..]);
            if (offset < 32 || offset > 400) return null;
            return Parse(read, size, sector[offset..].ToArray());
        }
        return null;
    }

    private static LvmLabel? Parse(Func<long, int, byte[]?> read, long size, byte[] pv)
    {
        string id = Encoding.ASCII.GetString(pv, 0, 32);
        // רשימת אזורי הנתונים ואחריה רשימת אזורי הניהול, כל אחת מסתיימת בזוג אפסים.
        int at = 40;
        while (at + 16 <= pv.Length && BinaryPrimitives.ReadUInt64LittleEndian(pv.AsSpan(at)) != 0) at += 16;
        at += 16;
        string? metadata = null;
        while (metadata is null && at + 16 <= pv.Length)
        {
            long mdaOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(pv.AsSpan(at));
            long mdaSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(pv.AsSpan(at + 8));
            if (mdaOffset == 0) break;
            metadata = Text(read, mdaOffset, mdaSize, size);
            at += 16;
        }
        return new LvmLabel { PvId = id, Metadata = metadata };
    }

    /// <summary>התיאור העדכני מאזור ניהול: הכותרת מצביעה עליו; אם הוא גולש מעבר לסוף — ממשיך מתחילת האזור.</summary>
    private static string? Text(Func<long, int, byte[]?> read, long mda, long mdaSize, long deviceSize)
    {
        if (mda <= 0 || mda + 512 > deviceSize || mdaSize < 1024) return null;
        if (read(mda, 512) is not { Length: 512 } header || !header.AsSpan(4, 16).SequenceEqual(MdaMagic)) return null;

        long offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(40));
        long length = (long)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(48));
        if (offset < 512 || offset >= mdaSize || length <= 0 || length > 16 * 1024 * 1024) return null;

        var bytes = new byte[length];
        long first = Math.Min(length, mdaSize - offset);
        if (read(mda + offset, (int)first) is not { } a || a.Length != first) return null;
        a.CopyTo(bytes, 0);
        if (first < length)
        {
            if (read(mda + 512, (int)(length - first)) is not { } b || b.Length != length - first) return null;
            b.CopyTo(bytes, first);
        }
        int end = Array.IndexOf(bytes, (byte)0);
        return Encoding.UTF8.GetString(bytes, 0, end < 0 ? bytes.Length : end);
    }
}
