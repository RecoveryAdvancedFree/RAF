using System.Buffers.Binary;
using System.Text;

namespace RAF.Core.Imaging;

/// <summary>
/// הכותרת של כונן VHD בגודל קבוע — 512 בתים בסוף הקובץ, אחרי הנתונים עצמם.
///
/// זה כל ההבדל בין תמונה גולמית לכונן וירטואלי ש-Windows יודע לחבר בלחיצה כפולה:
/// הנתונים זהים בית אחר בית, ולכן אותו מנגנון העתקה (שלושת המעברים, המשך מנקודה)
/// עובד כמו שהוא, והתוכנה עצמה פותחת את הקובץ כמו כל תמונה.
/// </summary>
internal static class VhdFooter
{
    /// <summary>הגודל המרבי של VHD (מגבלת הפורמט).</summary>
    internal const long MaxSize = 2040L * 1024 * 1024 * 1024;

    internal const int Length = 512;

    internal static bool IsVhdPath(string path) => path.EndsWith(".vhd", StringComparison.OrdinalIgnoreCase);

    internal static byte[] Build(long size, Guid id, DateTime createdUtc)
    {
        byte[] f = new byte[Length];
        Encoding.ASCII.GetBytes("conectix").CopyTo(f, 0);
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(8), 2);                   // תכונות: שמור
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(12), 0x00010000);         // גרסת הפורמט
        BinaryPrimitives.WriteUInt64BigEndian(f.AsSpan(16), ulong.MaxValue);     // כונן קבוע: אין טבלאות
        uint seconds = (uint)Math.Max(0, (createdUtc - new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds);
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(24), seconds);
        Encoding.ASCII.GetBytes("RAF ").CopyTo(f, 28);                          // התוכנה שיצרה
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(32), 0x00010000);
        Encoding.ASCII.GetBytes("Wi2k").CopyTo(f, 36);                          // מערכת ההפעלה: Windows
        BinaryPrimitives.WriteInt64BigEndian(f.AsSpan(40), size);                // הגודל המקורי
        BinaryPrimitives.WriteInt64BigEndian(f.AsSpan(48), size);                // הגודל הנוכחי

        var (cylinders, heads, sectors) = Geometry(size);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(56), cylinders);
        f[58] = heads;
        f[59] = sectors;

        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(60), 2);                  // סוג: גודל קבוע
        id.ToByteArray().CopyTo(f, 68);

        // סכום הביקורת: המשלים של סכום כל הבתים, כששדה הסכום עצמו אפס.
        uint sum = 0;
        foreach (byte b in f) sum += b;
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(64), ~sum);
        return f;
    }

    /// <summary>גאומטריה מדומה (צילינדרים, ראשים, סקטורים) — לפי האלגוריתם שבמפרט של VHD.</summary>
    internal static (ushort Cylinders, byte Heads, byte Sectors) Geometry(long size)
    {
        long total = Math.Min(size / 512, 65535L * 16 * 255);
        long spt, heads, cth;

        if (total >= 65535L * 16 * 63)
        {
            spt = 255; heads = 16; cth = total / spt;
        }
        else
        {
            spt = 17; cth = total / spt;
            heads = Math.Max(4, (cth + 1023) / 1024);
            if (cth >= heads * 1024 || heads > 16) { spt = 31; heads = 16; cth = total / spt; }
            if (cth >= heads * 1024) { spt = 63; heads = 16; cth = total / spt; }
        }

        return ((ushort)(cth / heads), (byte)heads, (byte)spt);
    }
}
