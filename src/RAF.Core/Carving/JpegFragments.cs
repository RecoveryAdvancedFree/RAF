using System.Diagnostics;

namespace RAF.Core.Carving;

/// <summary>JPEG שחובר משני מקטעים: split בתים מההתחלה, ואחריהם — gap בתים הלאה.</summary>
internal readonly record struct JpegFragmentPair(long Split, long Gap, long Length);

/// <summary>
/// חיבור JPEG שהיה מפוצל לשני מקטעים (bifragment gap carving).
///
/// מערכת קבצים שמקצה קובץ בשני חלקים משאירה ביניהם "פער" — אשכולות של קבצים
/// אחרים. בחילוץ לפי חתימה הקובץ נקרא ברצף, ולכן מהפער והלאה התמונה משובשת.
/// המפענח מזהה היכן בערך זה קרה; כאן מחפשים את שני הנעלמים — היכן בדיוק נגמר
/// המקטע הראשון, וכמה רחוק מתחיל השני.
///
/// כל מועמד נבדק בהמשך פענוח מנקודת השמירה שלפני החיבור, וחייב להגיע לכל
/// יחידות התמונה ולסימן הסיום בדיוק — מבחן שנתונים זרים כמעט לא עוברים.
/// גבולות המקטעים הם בגבולות סקטור, כי כך מערכות קבצים מקצות.
/// </summary>
internal static class JpegFragments
{
    /// <summary>עד כמה לפני נקודת השגיאה עשוי להיות החיבור האמיתי.</summary>
    internal const int SplitWindow = 4096;

    /// <param name="data">הבתים מתחילת הקובץ על הדיסק והלאה — כולל הפער והמקטע השני.</param>
    /// <param name="check">תוצאת הפענוח הרציף, שמצאה שגיאה.</param>
    /// <param name="decoder">המפענח שהגיע לשגיאה, עם נקודות השמירה שלו.</param>
    internal static JpegFragmentPair? Find(
        byte[] data, JpegCheck check, JpegDecoder decoder, int alignment, TimeSpan budget)
    {
        if (check.Verdict != JpegVerdict.Corrupt || decoder.Checkpoints is not { Count: > 0 } points)
            return null;

        long error = check.Offset;
        long lowest = Math.Max(decoder.ScanStart, error - SplitWindow);
        long highest = error / alignment * alignment;

        var splits = new List<(long Split, JpegDecoder.Checkpoint From)>();
        for (long split = highest; split > lowest; split -= alignment)
        {
            // נקודת השמירה האחרונה שכל הבתים שלה לפני החיבור.
            int i = points.FindLastIndex(p => p.Pos <= split);
            if (i >= 0) splits.Add((split, points[i]));
        }
        if (splits.Count == 0) return null;

        var clock = Stopwatch.StartNew();

        // פער קטן קודם: בכונן לא מקוטע במיוחד רוב הפערים קצרים.
        for (long gap = alignment; ; gap += alignment)
        {
            bool anyInRange = false;
            foreach (var (split, from) in splits)
            {
                if (split + gap >= data.Length) continue;
                anyInRange = true;

                var result = decoder.ResumeFrom(from, JpegBytes.Spliced(data, split, gap));
                if (result.Verdict == JpegVerdict.Complete)
                    return new JpegFragmentPair(split, gap, result.Offset);
            }

            if (!anyInRange || clock.Elapsed > budget) return null;
        }
    }
}
