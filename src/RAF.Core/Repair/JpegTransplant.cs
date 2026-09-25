using System.Buffers.Binary;
using System.Text;
using RAF.Core.Carving;

namespace RAF.Core.Repair;

/// <summary>תוצאת בנייה מחדש של תחילת תמונה.</summary>
public sealed class PhotoRebuildResult
{
    /// <summary>נכתב קובץ — גם כשרק חלק מהתמונה מתפענח.</summary>
    public bool Written { get; init; }

    /// <summary>כל התמונה מתפענחת עד הסוף.</summary>
    public bool Complete { get; init; }

    public double Fraction { get; init; }
    public string Message { get; init; } = "";
    public List<string> Applied { get; init; } = new();
}

/// <summary>
/// בניית תחילת JPEG שנדרסה, בעזרת תמונה תקינה מאותה מצלמה ובאותן הגדרות — מה שנעשה
/// ידנית בעורך הקסדצימלי: לוקחים את תחילת הקובץ מתמונה תקינה ומדביקים על הפגומה.
///
/// בתחילת JPEG יש מה שאינו כתוב בנתונים עצמם: טבלאות הדחיסה (DQT), טבלאות הפענוח
/// (DHT), המידות (SOF) ומרווח ההתחלה מחדש (DRI). מצלמה כותבת אותם כמעט זהים בכל
/// תמונה. הנתונים הדחוסים מתחילים אחרי סימן הסריקה (SOS) — ובתמונת מצלמה הוא נמצא
/// עשרות קילובייטים מתחילת הקובץ, אחרי פרטי הצילום והתמונה המוקטנת, ולכן שורד
/// לעיתים קרובות כשתחילת הקובץ נדרסה.
///
/// מה ששרד בתמונה עצמה נשמר, ורק מה שחסר נלקח מתמונת הדוגמה. כל צירוף אפשרי נבדק
/// בפענוח מלא (JpegDecoder), והנבחר הוא זה שכל התמונה מתפענחת בו — כך גם מידות שגויות
/// או מרווח התחלה מחדש שגוי מתגלים, ולא רק נתונים פגומים.
/// הקובץ המקורי אינו משתנה.
/// </summary>
public static class JpegTransplant
{
    /// <summary>תמונה גדולה מזה אינה נבנית — היא נקראת כולה לזיכרון.</summary>
    public const long MaxSize = 256L * 1024 * 1024;

    /// <summary>מקטע בכותרת: הסימן, והיכן הוא מתחיל (בית ה-FF) ונגמר.</summary>
    private readonly record struct Segment(int Marker, int Start, int End);

    // ================================================================ אבחון

    /// <summary>
    /// האם זו תמונה שתחילתה נהרסה והנתונים שלה שרדו: הפענוח נכשל לפני יחידת התמונה
    /// הראשונה, ואחרי מקום הכישלון יש סימן סריקה של התמונה הראשית (ראו MainScan).
    /// מחזיר את אורך הנתונים ששרדו, או null.
    /// </summary>
    internal static long? SurvivingData(byte[] data, long errorOffset)
        => MainScan(data, errorOffset) is { } scan ? scan.RegionEnd - scan.DataStart : null;

    /// <summary>סימן סריקה, תחילת הנתונים שאחריו, ועד היכן הם יכולים להגיע (הסימן הבא או סוף הקובץ).</summary>
    private readonly record struct ScanRegion(int Sos, int DataStart, int RegionEnd);

    /// <summary>
    /// סימן הסריקה של התמונה הראשית: זה שאחריו האזור הגדול ביותר. בקובץ יש לעיתים
    /// גם התמונה המוקטנת (בתוך פרטי הצילום) ותמונות נוספות שמצלמות מצרפות בסוף —
    /// הנתונים שלהן קטנים בהרבה. אם הסימן של התמונה הראשית עצמו נדרס, האזור הגדול
    /// ביותר מתחיל בתמונה המוקטנת ונמשך על פני נתוני התמונה הראשית; תמונה כזו
    /// מתפענחת כולה בזכות הכותרת שלה, ונגמרת הרבה לפני סוף האזור — ואז אין כאן
    /// מה לבנות.
    /// </summary>
    private static ScanRegion? MainScan(byte[] d, long after)
    {
        var all = ScanCandidates(d).ToList();
        ScanRegion? best = null;
        for (int i = 0; i < all.Count; i++)
        {
            if (all[i].DataStart <= after) continue;
            var region = new ScanRegion(all[i].Sos, all[i].DataStart, i + 1 < all.Count ? all[i + 1].Sos : d.Length);
            if (best is null || region.RegionEnd - region.DataStart > best.Value.RegionEnd - best.Value.DataStart) best = region;
        }
        if (best is not { } main) return null;

        // תמונה שלמה בפני עצמה: כותרת שלמה מיד אחרי FF D8, שמתפענחת ונגמרת לפני סוף האזור.
        var chain = OwnChain(d, main.Sos);
        int soi = chain[0].Start - 2;
        if (soi > 0 && d[soi] == 0xFF && d[soi + 1] == 0xD8)
        {
            var check = JpegDecoder.Check(JpegBytes.Of(d.AsSpan(soi, main.RegionEnd - soi).ToArray()));
            if (check.Verdict == JpegVerdict.Complete && soi + check.Offset < main.DataStart + 0.9 * (main.RegionEnd - main.DataStart))
                return null;
        }
        return main;
    }

    /// <summary>בדיקת תמונת הדוגמה: null — מתאימה; אחרת, מה הבעיה, במילים פשוטות.</summary>
    public static string? DescribeReference(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxSize) return L.T("תמונת הדוגמה גדולה מדי.");

        byte[] data = File.ReadAllBytes(path);
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
            return L.T("הקובץ שנבחר אינו תמונת JPEG.");

        var check = JpegDecoder.Check(JpegBytes.Of(data));
        return check.Verdict switch
        {
            JpegVerdict.Complete => null,
            JpegVerdict.Unsupported => L.T("תמונת הדוגמה שמורה בשיטה שאינה נתמכת (למשל JPEG מדורג, שנוצר בעריכה). " +
                                          "בחרו תמונה כפי שיצאה מהמצלמה."),
            _ => L.T("תמונת הדוגמה עצמה פגומה. בחרו תמונה תקינה."),
        };
    }

    // ================================================================ בנייה

    public static PhotoRebuildResult Rebuild(string brokenPath, string referencePath, string outputPath)
    {
        if (string.Equals(Path.GetFullPath(brokenPath), Path.GetFullPath(referencePath), StringComparison.OrdinalIgnoreCase))
            return new PhotoRebuildResult { Message = L.T("תמונת הדוגמה היא התמונה הפגומה עצמה. בחרו תמונה תקינה אחרת מאותה מצלמה.") };

        if (DescribeReference(referencePath) is { } problem)
            return new PhotoRebuildResult { Message = problem };

        if (new FileInfo(brokenPath).Length > MaxSize)
            return new PhotoRebuildResult { Message = L.T("התמונה גדולה מדי לבנייה מחדש.") };

        byte[] broken = File.ReadAllBytes(brokenPath);
        byte[] donor = File.ReadAllBytes(referencePath);
        var donorHeader = DonorSegments(donor);
        if (donorHeader is null)
            return new PhotoRebuildResult { Message = L.T("לא ניתן לקרוא את תחילת תמונת הדוגמה.") };

        Attempt? best = null;
        foreach (var attempt in Attempts(broken, donor, donorHeader))
        {
            var check = JpegDecoder.Check(JpegBytes.Of(attempt.Bytes));
            var scored = attempt with { Check = check };
            if (best is null || Better(scored, best)) best = scored;
            if (check.Verdict == JpegVerdict.Complete) break;    // הצירופים מסודרים מהמועדף — הראשון השלם נבחר
        }

        if (best is null)
            return new PhotoRebuildResult { Message = L.T("לא נמצאו בתמונה הפגומה הנתונים הדחוסים שלה — הם נדרסו יחד עם תחילת הקובץ.") };

        var result = best.Check;
        if (result.McusDecoded == 0 || Tier(best) == 0)
            return new PhotoRebuildResult
            {
                Message = L.T("תמונת הדוגמה אינה מתאימה לתמונה הפגומה: הנתונים של התמונה אינם מתפענחים עם ההגדרות שלה. " +
                    "בחרו תמונה שצולמה באותה מצלמה ובאותן הגדרות (גודל התמונה ואיכותה)."),
            };

        byte[] output = best.Bytes;
        if (result.Verdict == JpegVerdict.Complete && result.Offset < output.Length)
            output = output.AsSpan(0, (int)result.Offset).ToArray();       // נתונים עודפים אחרי סוף התמונה
        File.WriteAllBytes(outputPath, output);

        var applied = new List<string> { best.Describe() };
        if (!best.OwnExif)
            applied.Add(L.T("פרטי הצילום (תאריך, מצלמה, כיוון) אבדו עם תחילת הקובץ. אם התמונה מוצגת שוכבת, סובבו אותה."));

        bool complete = result.Verdict == JpegVerdict.Complete;
        return new PhotoRebuildResult
        {
            Written = true,
            Complete = complete,
            Fraction = result.Fraction,
            Applied = applied,
            Message = complete
                ? L.T("תחילת התמונה נבנתה מחדש, וכל התמונה מתפענחת.")
                : L.T("תחילת התמונה נבנתה מחדש, אבל רק כ-{0} ממנה מתפענח — משם והלאה הנתונים פגומים.", result.Fraction.ToString("P0")),
        };
    }

    /// <summary>צירוף אחד של כותרת ונתונים, ומה נלקח בו מתמונת הדוגמה.</summary>
    private sealed record Attempt(byte[] Bytes, List<string> Taken, bool OwnExif, JpegCheck Check = default)
    {
        public string Describe() => Taken.Count == 0
            ? L.T("הכותרת נבנתה מחדש מהחלקים ששרדו בתמונה עצמה.")
            : L.T("מתמונת הדוגמה נלקחו: {0}. השאר נלקח מהתמונה עצמה.", string.Join(", ", Taken));
    }

    private static bool Better(Attempt a, Attempt b)
    {
        int ta = Tier(a), tb = Tier(b);
        if (ta != tb) return ta > tb;
        return a.Check.McusDecoded > b.Check.McusDecoded;
    }

    /// <summary>
    /// 2 — כל התמונה מתפענחת. 1 — נשברת באמצע: הנתונים עצמם פגומים. 0 — הנתונים
    /// תקינים עד סופם אבל מספר היחידות אינו תואם: הם נגמרו לפני שהתמונה הושלמה, או
    /// שהתמונה הושלמה והם ממשיכים. זה סימן לגודל תמונה שונה בדוגמה, ולא לנזק.
    /// </summary>
    private static int Tier(Attempt a)
    {
        var c = a.Check;
        if (c.Verdict == JpegVerdict.Complete) return 2;
        if (c.McusTotal > 0 && c.McusDecoded >= c.McusTotal) return 0;
        for (long i = Math.Max(0, c.Offset - 16); i < Math.Min(a.Bytes.Length - 1, c.Offset + 16); i++)
            if (a.Bytes[i] == 0xFF && a.Bytes[i + 1] == 0xD9) return 0;
        return 1;
    }

    /// <summary>
    /// כל הצירופים לבדיקה, מהמועדף: לסימן הסריקה של התמונה הראשית — קודם מה ששרד בה
    /// בתוספת מה שחסר מהדוגמה, ואחר כך כל הכותרת של הדוגמה. כשהמידות נלקחות
    /// מהדוגמה, נבדקות גם הפוכות — טלפונים שומרים תמונה לאורך כך.
    /// </summary>
    private static IEnumerable<Attempt> Attempts(byte[] broken, byte[] donor, List<Segment> donorHeader)
    {
        var donorSof = donorHeader.FirstOrDefault(s => s.Marker is 0xC0 or 0xC1);
        var donorDri = donorHeader.FirstOrDefault(s => s.Marker == 0xDD);
        var donorApps = donorHeader.Where(s => IsNeutralApp(donor, s)).ToList();

        if (MainScan(broken, -1) is { } main)
        {
            var (sos, dataStart, _) = main;
            var chain = OwnChain(broken, sos);

            var own = chain.Take(chain.Count - 1).ToList();          // בלי ה-SOS עצמו
            var ownQuant = TableIds(broken, own, 0xDB);
            var ownHuff = TableIds(broken, own, 0xC4);
            bool ownSof = own.Any(s => s.Marker is 0xC0 or 0xC1);
            bool ownDri = own.Any(s => s.Marker == 0xDD);
            bool ownExif = own.Any(s => s.Marker == 0xE1);
            var ownMarkers = own.Select(s => s.Marker).ToHashSet();

            var needQuant = donorHeader.Where(s => s.Marker == 0xDB && !TableIds(donor, new[] { s }, 0xDB).IsSubsetOf(ownQuant)).ToList();
            var needHuff = donorHeader.Where(s => s.Marker == 0xC4 && !TableIds(donor, new[] { s }, 0xC4).IsSubsetOf(ownHuff)).ToList();
            var needApps = donorApps.Where(s => !ownMarkers.Contains(s.Marker)).ToList();

            var leading = own.TakeWhile(s => s.Marker is >= 0xE0 and <= 0xEF).ToList();
            var rest = own.Skip(leading.Count).ToList();

            var taken = new List<string>();
            if (needQuant.Count > 0) taken.Add(L.T("טבלאות הדחיסה"));
            if (needHuff.Count > 0) taken.Add(L.T("טבלאות הפענוח"));
            if (!ownSof) taken.Add(L.T("מידות התמונה"));

            foreach (bool swap in ownSof ? new[] { false } : new[] { false, true })
            {
                foreach (bool withDri in ownDri || donorDri.End == 0 ? new[] { false } : new[] { true, false })
                {
                    using var h = new MemoryStream();
                    h.Write([0xFF, 0xD8]);
                    foreach (var s in leading) Copy(h, broken, s);
                    foreach (var s in needApps) Copy(h, donor, s);
                    foreach (var s in needQuant) Copy(h, donor, s);
                    if (!ownSof) CopySof(h, donor, donorSof, swap);
                    foreach (var s in needHuff) Copy(h, donor, s);
                    if (withDri) Copy(h, donor, donorDri);
                    foreach (var s in rest) Copy(h, broken, s);
                    Copy(h, broken, chain[^1]);

                    var t = new List<string>(taken);
                    if (withDri) t.Add(L.T("מרווח ההתחלה מחדש"));
                    yield return new Attempt(Join(h, broken, dataStart), t, ownExif);
                }
            }

            // כל הכותרת של הדוגמה — כשמה ששרד בתמונה עצמה אינו מתאים (למשל נפגם בלי שנראה).
            {
                foreach (bool swap in new[] { false, true })
                {
                    using var h = new MemoryStream();
                    h.Write([0xFF, 0xD8]);
                    foreach (var s in donorHeader)
                    {
                        if (s.Marker is >= 0xE0 and <= 0xEF && !IsNeutralApp(donor, s)) continue;
                        if (s.Marker is 0xC0 or 0xC1) CopySof(h, donor, s, swap);
                        else Copy(h, donor, s);
                    }
                    yield return new Attempt(Join(h, broken, dataStart),
                        new List<string> { L.T("כל הכותרת (טבלאות, מידות והגדרות)") }, false);
                }
            }
        }
    }

    /// <summary>
    /// מקטעי תוספת שאינם מתארים את התמונה המסוימת ולכן מותר להעתיק מהדוגמה: JFIF,
    /// פרופיל צבע, ו-Adobe (שקובע איך לפרש את הצבעים). פרטי הצילום (APP1) ואינדקס
    /// התמונות הנוספות (MPF, שמצביע למיקומים בקובץ הדוגמה) — לא.
    /// </summary>
    private static bool IsNeutralApp(byte[] d, Segment s)
    {
        var body = d.AsSpan(s.Start + 4, s.End - s.Start - 4);
        return s.Marker switch
        {
            0xE0 => body.StartsWith("JFIF\0"u8),
            0xE2 => body.StartsWith("ICC_PROFILE\0"u8),
            0xEE => body.StartsWith("Adobe"u8),
            _ => false,
        };
    }

    private static byte[] Join(MemoryStream header, byte[] broken, int dataStart)
    {
        byte[] result = new byte[header.Length + broken.Length - dataStart];
        header.GetBuffer().AsSpan(0, (int)header.Length).CopyTo(result);
        broken.AsSpan(dataStart).CopyTo(result.AsSpan((int)header.Length));
        return result;
    }

    private static void Copy(Stream s, byte[] d, Segment seg) => s.Write(d, seg.Start, seg.End - seg.Start);

    private static void CopySof(Stream s, byte[] d, Segment seg, bool swap)
    {
        byte[] sof = d.AsSpan(seg.Start, seg.End - seg.Start).ToArray();
        if (swap)
        {
            // גובה ברוחב 5-6, רוחב ב-7-8 (אחרי FF Cx, אורך, ודיוק).
            (sof[5], sof[6], sof[7], sof[8]) = (sof[7], sof[8], sof[5], sof[6]);
        }
        s.Write(sof);
    }

    // ================================================================ מבנה

    /// <summary>
    /// סימני סריקה בסיסיים (baseline) בקובץ: FF DA, אורך שתואם למספר הרכיבים, וסיום
    /// "0, 63, 0" — שילוב שכמעט לא מופיע במקרה. בנתונים הדחוסים עצמם FF תמיד מלווה
    /// ב-00 או בסימן התחלה מחדש, ולכן המועמדים הם רק בכותרות.
    /// </summary>
    private static IEnumerable<(int Sos, int DataStart)> ScanCandidates(byte[] d)
    {
        for (int i = 0; i + 14 <= d.Length; i++)
        {
            if (d[i] != 0xFF || d[i + 1] != 0xDA) continue;
            int length = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i + 2));
            int count = d[i + 4];
            if (count is < 1 or > 4 || length != 6 + 2 * count || i + 2 + length >= d.Length) continue;

            bool ok = true;
            for (int c = 0; c < count && ok; c++)
            {
                int tables = d[i + 6 + 2 * c];
                ok = (tables >> 4) <= 3 && (tables & 15) <= 3;
            }
            int tail = i + 5 + 2 * count;
            if (ok && d[tail] == 0 && d[tail + 1] == 63 && d[tail + 2] == 0)
                yield return (i, i + 2 + length);
        }
    }

    /// <summary>
    /// המקטעים ששרדו לפני סימן הסריקה: השרשרת הארוכה ביותר של מקטעים תקינים שמסתיימת
    /// בדיוק בו. כל מקטע נבדק לפי המבנה שלו, כדי שבתים אקראיים לא ייחשבו כותרת.
    /// כולל את ה-SOS עצמו, כאחרון.
    /// </summary>
    private static List<Segment> OwnChain(byte[] d, int sos)
    {
        int sosEnd = sos + 2 + BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(sos + 2));
        for (int q = 0; q < sos; q++)
        {
            if (d[q] != 0xFF || !IsHeaderMarker(d[q + 1])) continue;
            var chain = new List<Segment>();
            int at = q;
            while (at < sos)
            {
                if (at + 4 > d.Length || d[at] != 0xFF || !IsHeaderMarker(d[at + 1])) break;
                int end = at + 2 + BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(at + 2));
                if (end > sos || !ValidSegment(d, d[at + 1], at + 4, end)) break;
                chain.Add(new Segment(d[at + 1], at, end));
                at = end;
            }
            if (at == sos)
            {
                chain.Add(new Segment(0xDA, sos, sosEnd));
                return chain;
            }
        }
        return new List<Segment> { new(0xDA, sos, sosEnd) };
    }

    private static bool IsHeaderMarker(int m) => m is 0xC0 or 0xC1 or 0xC4 or 0xDB or 0xDD or 0xFE or (>= 0xE0 and <= 0xEF);

    private static bool ValidSegment(byte[] d, int marker, int body, int end)
    {
        if (end < body) return false;
        switch (marker)
        {
            case 0xDB:
                while (body < end)
                {
                    int pq = d[body] >> 4, tq = d[body] & 15;
                    if (pq > 1 || tq > 3) return false;
                    body += 1 + 64 * (pq + 1);
                }
                return body == end;

            case 0xC4:
                while (body < end)
                {
                    int tc = d[body] >> 4, th = d[body] & 15;
                    if (tc > 1 || th > 3 || body + 17 > end) return false;
                    int total = 0;
                    for (int k = 1; k <= 16; k++) total += d[body + k];
                    if (total is 0 or > 256) return false;
                    body += 17 + total;
                }
                return body == end;

            case 0xC0 or 0xC1:
            {
                if (end - body < 6 || d[body] != 8) return false;
                int height = d[body + 1] << 8 | d[body + 2], width = d[body + 3] << 8 | d[body + 4], count = d[body + 5];
                return height > 0 && width > 0 && count is 1 or 3 or 4 && end - body == 6 + 3 * count;
            }

            case 0xDD:
                return end - body == 2;

            default:
                return true;      // APPn, COM — תוכן חופשי
        }
    }

    /// <summary>מספרי הטבלאות שמקטעי DQT או DHT מגדירים (לפענוח — סוג ומספר).</summary>
    private static HashSet<int> TableIds(byte[] d, IEnumerable<Segment> segments, int marker)
    {
        var ids = new HashSet<int>();
        foreach (var s in segments.Where(s => s.Marker == marker))
        {
            int at = s.Start + 4;
            while (at < s.End)
            {
                ids.Add(d[at]);
                at += marker == 0xDB
                    ? 1 + 64 * ((d[at] >> 4) + 1)
                    : 17 + Enumerable.Range(1, 16).Sum(k => d[at + k]);
            }
        }
        return ids;
    }

    /// <summary>מקטעי הכותרת של תמונה תקינה, מההתחלה ועד ה-SOS הראשון (כולל).</summary>
    private static List<Segment>? DonorSegments(byte[] d)
    {
        var list = new List<Segment>();
        int at = 2;
        while (at + 4 <= d.Length)
        {
            if (d[at] != 0xFF) return null;
            while (at + 1 < d.Length && d[at + 1] == 0xFF) at++;
            int marker = d[at + 1];
            int end = at + 2 + BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(at + 2));
            if (end > d.Length) return null;
            list.Add(new Segment(marker, at, end));
            if (marker == 0xDA) return list;
            at = end;
        }
        return null;
    }
}
