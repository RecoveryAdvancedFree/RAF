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
    private static bool IsTreePage(byte type) => type is 0x02 or 0x05 or 0x0A or 0x0D;

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
        if (!IsTreePage(head[100])) return ("כותרת מסד הנתונים פגומה, וגם הדף הראשון שלו אינו במבנה הצפוי. " +
                                            "לא ניתן לשחזר את הכותרת בוודאות.", null);

        int inferred = pageSize > 0 ? pageSize : InferPageSize(read, size);
        if (inferred == 0)
            return ("כותרת מסד הנתונים פגומה: גודל הדף — הנתון שקובע איך המסד נקרא — אבד, " +
                    "ואי אפשר לגלות אותו מתוך המסד. לא ניתן לתקן בוודאות.", null);

        byte[] patch = new byte[8];
        BinaryPrimitives.WriteUInt16BigEndian(patch, (ushort)(inferred == 65536 ? 1 : inferred));
        patch[2] = head[18] is 1 or 2 ? head[18] : (byte)1;
        patch[3] = head[19] is 1 or 2 ? head[19] : (byte)1;
        patch[4] = head[20];
        patch[5] = 64; patch[6] = 32; patch[7] = 32;

        string what = pageSize == 0
            ? $"גודל הדף — הנתון שקובע איך המסד נקרא — אבד. לפי מבנה המסד עצמו הוא {inferred:N0} בתים"
            : "כמה שדות קבועים בכותרת נפגעו";
        return ($"כותרת מסד הנתונים פגומה: {what}. אפשר לשחזר את הכותרת, והנתונים עצמם לא ישתנו.", patch);
    }

    /// <summary>
    /// גודל הדף שבו החלק הגדול ביותר של גבולות הדפים נופל על תחילת עץ. 0 — אין גודל משכנע.
    /// דפים שאינם עצים (רשימת דפים פנויים, המשך של רשומה ארוכה) קיימים גם במסד תקין,
    /// ולכן לא נדרשת התאמה מלאה — אבל גודל קטן מדי נופל גם באמצע דפים, והיחס שלו נמוך.
    /// </summary>
    private static int InferPageSize(Func<long, int, byte[]> read, long size)
    {
        int best = 0;
        double bestRatio = 0;

        for (int candidate = 512; candidate <= 65536; candidate <<= 1)
        {
            if (candidate >= size) break;

            long pages = Math.Min(size / candidate, 64);
            int hits = 0, samples = 0;
            for (long page = 1; page < pages; page++)
            {
                byte[] b = read(page * candidate, 1);
                if (b.Length < 1) break;
                samples++;
                if (IsTreePage(b[0])) hits++;
            }
            if (samples == 0) continue;

            // בשוויון — הגודל הקטן יותר: גודל כפול מהאמיתי פוגע רק בכל דף שני, וגם
            // שם כולם עצים. גודל קטן מהאמיתי, לעומתו, נופל באמצע דפים ומקבל יחס נמוך.
            double ratio = hits / (double)samples;
            if (ratio > bestRatio) { best = candidate; bestRatio = ratio; }
        }

        return bestRatio >= 0.3 ? best : 0;
    }
}
