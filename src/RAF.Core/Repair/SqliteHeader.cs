using System.Buffers.Binary;

namespace RAF.Core.Repair;

/// <summary>
/// כותרת מסד נתונים SQLite — 100 הבתים הראשונים, שבלעדיהם המסד לא נפתח.
///
/// אחרי החתימה באים גודל הדף ועוד כמה שדות שערכם כמעט תמיד קבוע. כשהם
/// נפגעים, גודל הדף ניתן לגילוי מהמסד עצמו: כל דף שייך לעץ, ובתחילת כל
/// דף כזה יש בית שמציין את סוגו. גודל הדף הנכון הוא זה שבו הבתים בגבולות
/// הדפים הם סוגי עצים חוקיים.
/// </summary>
internal static class SqliteHeader
{
    /// <summary>סוגי הדפים בעץ: פנימי ועלה, של טבלה ושל אינדקס.</summary>
    internal static bool IsTreePage(byte type) => type is 0x02 or 0x05 or 0x0A or 0x0D;

    /// <summary>גודל הדף כפי שהוא כתוב, או 0 כשהערך אינו חוקי.</summary>
    private static int DeclaredPageSize(ReadOnlySpan<byte> head)
    {
        int raw = BinaryPrimitives.ReadUInt16BigEndian(head[16..]);
        int size = raw == 1 ? 65536 : raw;
        return size is >= 512 and <= 65536 && (size & (size - 1)) == 0 ? size : 0;
    }

    /// <summary>
    /// בדיקת השדות שאחרי החתימה. null — הם תקינים. אחרת: תיאור הנזק, והבתים
    /// המתוקנים לכתיבה בהיסט 16 (null כשגודל הדף לא נמצא ואי אפשר לתקן).
    /// </summary>
    internal static (string Problem, byte[]? Patch)? Check(ReadOnlySpan<byte> head, Func<long, int, byte[]> read, long size)
    {
        if (head.Length < 101) return null;

        int pageSize = DeclaredPageSize(head);
        bool versions = head[18] is 1 or 2 && head[19] is 1 or 2;
        bool fractions = head[21] == 64 && head[22] == 32 && head[23] == 32;
        if (pageSize > 0 && versions && fractions) return null;

        // הדף הראשון הוא תמיד שורש רשימת הטבלאות — אם גם הוא לא עץ, זה לא מסד שאפשר להציל כך.
        if (!IsTreePage(head[100])) return (L.T("כותרת מסד הנתונים פגומה, וגם הדף הראשון שלו אינו במבנה הצפוי. " +
                                            "לא ניתן לשחזר את הכותרת בוודאות."), null);

        int inferred = pageSize > 0 ? pageSize : InferPageSize(read, size);
        if (inferred == 0)
            return (L.T("כותרת מסד הנתונים פגומה: גודל הדף — הנתון שקובע איך המסד נקרא — אבד, " +
                    "ואי אפשר לגלות אותו מתוך המסד. לא ניתן לתקן בוודאות."), null);

        byte[] patch = new byte[8];
        BinaryPrimitives.WriteUInt16BigEndian(patch, (ushort)(inferred == 65536 ? 1 : inferred));
        patch[2] = head[18] is 1 or 2 ? head[18] : (byte)1;
        patch[3] = head[19] is 1 or 2 ? head[19] : (byte)1;
        patch[4] = head[20];
        patch[5] = 64; patch[6] = 32; patch[7] = 32;

        string what = pageSize == 0
            ? L.T("גודל הדף — הנתון שקובע איך המסד נקרא — אבד. לפי מבנה המסד עצמו הוא {0} בתים", inferred.ToString("N0"))
            : L.T("כמה שדות קבועים בכותרת נפגעו");
        return (L.T("כותרת מסד הנתונים פגומה: {0}. אפשר לשחזר את הכותרת, והנתונים עצמם לא ישתנו.", what), patch);
    }

    /// <summary>
    /// גודל הדף שבו הכי הרבה דפים הם עצים תקינים. 0 — אין גודל משכנע.
    ///
    /// לא מספיק שהבית הראשון של הדף יהיה סוג של עץ: בקובץ קטן, גודל כפול מהאמיתי נופל רק
    /// על חלק מהדפים ועלול לקבל יחס גבוה יותר במקרה. לכן נבדק כל דף עד הסוף — מספר התאים
    /// וכל המצביעים לתאים. תאים נכתבים מסוף הדף, ולכן בגודל קטן מהאמיתי המצביעים חורגים
    /// ממנו; בגודל גדול ממנו נבדקת רק חלק מהדפים. הגודל האמיתי הוא זה שהכי הרבה דפים תקינים בו.
    /// </summary>
    internal static int InferPageSize(Func<long, int, byte[]> read, long size)
    {
        int best = 0, bestCount = 0;
        for (int candidate = 512; candidate <= 65536; candidate <<= 1)
        {
            if (candidate >= size) break;
            long pages = size / candidate;
            long step = Math.Max(1, pages / 2000);
            int count = 0;
            for (long page = 1; page < pages; page += step)
                if (SaneTreePage(read(page * candidate, candidate), candidate)) count++;
            if (count > bestCount) { best = candidate; bestCount = count; }
        }
        return bestCount >= 1 ? best : 0;
    }

    private static bool SaneTreePage(byte[] page, int size)
    {
        if (page.Length < 12 || !IsTreePage(page[0])) return false;
        int cells = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(3));
        int pointers = page[0] is 0x02 or 0x05 ? 12 : 8;
        if (pointers + 2 * cells > page.Length) return false;
        for (int i = 0; i < cells; i++)
        {
            int p = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(pointers + 2 * i));
            if (p < pointers + 2 * cells || p >= size) return false;
        }
        // דף פנימי: המצביע לדף הימני אינו אפס. עלה ריק: אזור התוכן מתחיל בסוף הדף.
        if (pointers == 12 && BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(8)) == 0) return false;
        return cells > 0 || BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(5)) is 0 || BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(5)) == size;
    }
}
