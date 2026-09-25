using RAF.Core.Carving;

namespace RAF.Core.Repair;

/// <summary>
/// בניית תחילת קובץ RAW של מצלמה (CR2, NEF, ARW, DNG ושאר הקבצים שבנויים כמו TIFF)
/// שנדרסה, בעזרת קובץ תקין מאותה מצלמה ובאותן הגדרות.
///
/// קובץ כזה בנוי מרשימות תגיות (IFD) שמצביעות לחלקים שלו: התצוגה המקדימה, התמונה
/// המוקטנת ונתוני החיישן עצמם. הרשימה הראשונה נמצאת בתחילת הקובץ, ולכן היא הראשונה
/// שנהרסת — ובלעדיה אף תוכנה לא מוצאת את הנתונים, אף שכולם בקובץ.
///
/// מצלמה כותבת את אותן רשימות, באותו סדר, בכל קובץ. מה שמשתנה מתמונה לתמונה הוא
/// האורכים (תצוגה מקדימה ונתונים דחוסים) — ולכן גם המיקומים של מה שאחריהם. מכאן
/// הבנייה: תחילת הקובץ נלקחת מהדוגמה רק עד הנקודה שממנה המבנה של הקובץ עצמו שרד,
/// וכל מצביע בחלק שהועתק מתוקן למקום שבו החלק נמצא בפועל בקובץ הפגום: רשימות
/// שורדות מזוהות לפי התגיות שלהן, תמונות JPEG לפי החתימה, והשאר — לפי מיקומן
/// ביחס לחלקים שכבר נמצאו. התוצאה נבדקת שוב במלואה: אותו מבנה כמו בדוגמה, וכל תמונה
/// שבה שלמה. הקובץ המקורי אינו משתנה.
/// </summary>
public static class TiffTransplant
{
    /// <summary>קובץ גדול מזה אינו נבנה — הוא נקרא כולו לזיכרון.</summary>
    public const long MaxSize = 512L * 1024 * 1024;

    /// <summary>קובצי RAW שבנויים כמו TIFF רגיל (בלי חתימה משלהם, מלבד CR2).</summary>
    public static readonly IReadOnlySet<string> RawExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "cr2", "nef", "nrw", "arw", "srf", "sr2", "dng", "pef", "srw", "erf", "3fr", "kdc", "dcr", "mef", "mos", "iiq",
    };

    private sealed class Entry
    {
        public int Pos, Tag, Type, Count, ValuePos, Size;
    }

    private sealed class Ifd
    {
        public string Path = "";
        public int Offset, End, NextPos;
        public long Next;
        public List<Entry> Entries = new();
    }

    private sealed class Block
    {
        public string Key = "";
        public Ifd Owner = null!;
        public Entry OffsetEntry = null!, LengthEntry = null!;
        public int Index;
        public long Offset, Length;
    }

    private sealed class Tiff
    {
        public bool Little;
        public List<Ifd> Ifds = new();
        public List<Block> Blocks = new();
    }

    /// <summary>רשימות שמצביעות לרשימות אחרות: SubIFDs, EXIF, GPS, Interop.</summary>
    private static readonly int[] PointerTags = { 0x14A, 0x8769, 0x8825, 0xA005 };

    /// <summary>זוגות של מיקום ואורך: רצועות, אריחים ותמונת JPEG.</summary>
    private static readonly (int Offset, int Length)[] BlockTags = { (0x111, 0x117), (0x144, 0x145), (0x201, 0x202) };

    // ================================================================ אבחון

    /// <summary>
    /// האם זה קובץ RAW שתחילתו נהרסה: הרשימה הראשונה אינה קריאה (גם כשרק החתימה נפגעה
    /// היא נבדקת — במקרה כזה התיקון הרגיל של החתימה מספיק), ובקובץ יש נתונים — תמונת
    /// JPEG אחת לפחות, כמו התצוגה המקדימה שכל מצלמה כותבת.
    /// </summary>
    internal static bool LostHeader(byte[] d)
        => d.Length >= 64 && !FirstIfdReadable(d, d.Length) && d.AsSpan().IndexOf(new byte[] { 0xFF, 0xD8, 0xFF }) > 0;

    /// <summary>
    /// בדיקה מהירה, מתחילת הקובץ בלבד: הרשימה הראשונה קריאה. כך קובץ תקין לא נקרא כולו.
    /// </summary>
    internal static bool FirstIfdReadable(byte[] head, long size)
    {
        foreach (bool little in new[] { true, false })
        {
            long first = U32(head, 4, little);
            if (first >= 8 && ReadIfd(head, first, little, "0", size) is not null) return true;
        }
        return false;
    }

    /// <summary>בדיקת קובץ הדוגמה: null — מתאים; אחרת, מה הבעיה, במילים פשוטות.</summary>
    public static string? DescribeReference(string path)
    {
        if (new FileInfo(path).Length > MaxSize) return L.T("קובץ הדוגמה גדול מדי.");
        byte[] d = File.ReadAllBytes(path);
        var t = Parse(d);
        if (t is null || t.Blocks.Count == 0 || !Plausible(d, t))
            return L.T("קובץ הדוגמה אינו קובץ RAW תקין. בחרו קובץ RAW תקין שצולם באותה מצלמה.");
        return null;
    }

    // ================================================================ בנייה

    public static PhotoRebuildResult Rebuild(string brokenPath, string referencePath, string outputPath)
    {
        if (string.Equals(Path.GetFullPath(brokenPath), Path.GetFullPath(referencePath), StringComparison.OrdinalIgnoreCase))
            return new PhotoRebuildResult { Message = L.T("קובץ הדוגמה הוא הקובץ הפגום עצמו. בחרו קובץ תקין אחר מאותה מצלמה.") };
        if (DescribeReference(referencePath) is { } problem)
            return new PhotoRebuildResult { Message = problem };
        if (new FileInfo(brokenPath).Length > MaxSize)
            return new PhotoRebuildResult { Message = L.T("הקובץ גדול מדי לבנייה מחדש.") };

        byte[] broken = File.ReadAllBytes(brokenPath);
        byte[] donor = File.ReadAllBytes(referencePath);
        var dt = Parse(donor)!;

        // תחילת האזור של הנתונים: עד שם — רשימות ופרטי צילום בלבד.
        int headerEnd = (int)dt.Blocks.Min(b => b.Offset);

        // היכן כל רשימה של הדוגמה נמצאת בקובץ הפגום — אם שרדה. לפי סדר המיקום: כל רשימה
        // מחופשת בהזזה של הקודמת לה (מה שזז, זז יחד), ורק אחריה — הסדר נשמר, וכך גם שתי
        // רשימות עם אותן תגיות (כמו שתי תצוגות מקדימות) לא מתחלפות.
        var found = new Dictionary<Ifd, int?>();
        int shift = 0, last = 0;
        foreach (var ifd in dt.Ifds.OrderBy(i => i.Offset))
        {
            found[ifd] = Find(broken, ifd, ifd.Offset + shift, last, dt.Little);
            if (found[ifd] is { } at) { last = at; shift = at - ifd.Offset; }
        }

        var cuts = dt.Ifds.Select(i => i.Offset).Where(o => o <= headerEnd).Append(headerEnd).Distinct().Order();
        foreach (int cut in cuts)
        {
            // כל מה שאחרי נקודת החיתוך — מהקובץ עצמו, ולכן כל רשימה שם חייבת לשרוד בו.
            if (dt.Ifds.Any(i => i.Offset >= cut && (found[i] is not { } at || at < cut))) continue;
            if (cut > broken.Length) break;

            var output = Assemble(broken, donor, dt, found, cut);
            if (output is null || !Valid(output, donor, dt)) continue;

            File.WriteAllBytes(outputPath, output);
            var applied = new List<string>
            {
                L.T("מקובץ הדוגמה נלקחו {0} הבתים הראשונים: רשימות התגיות שנהרסו. מיקומי התצוגה המקדימה ונתוני החיישן " +
                    "ואורכיהם תוקנו לפי הקובץ עצמו, והשאר נשאר של הקובץ עצמו.", cut.ToString("N0")),
            };
            if (dt.Ifds.Any(i => i.Offset < cut && i.Path.Contains("8769")))
                applied.Add(L.T("פרטי הצילום (תאריך, חשיפה, איזון לבן) נלקחו מקובץ הדוגמה, כי אלה של הקובץ נדרסו. " +
                    "אם הצבעים נראים שונים, כוונו את איזון הלבן בתוכנת העריכה. אם הקובץ לא נפתח, קובץ הדוגמה " +
                    "צולם כנראה בהגדרת דחיסה אחרת (למשל בניקון: דחיסה ללא אובדן מול דחיסה רגילה) — נסו קובץ דוגמה אחר."));
            return new PhotoRebuildResult
            {
                Written = true,
                Complete = true,
                Fraction = 1,
                Applied = applied,
                Message = L.T("תחילת הקובץ נבנתה מחדש, וכל החלקים שלו נמצאו במקומם."),
            };
        }

        return new PhotoRebuildResult
        {
            Message = L.T("קובץ הדוגמה אינו מתאים לקובץ הפגום: המבנה שלו שונה, או שחלקים מהקובץ הפגום לא נמצאו. " +
                "בחרו קובץ שצולם באותה מצלמה ובאותן הגדרות (סוג ה-RAW, גודלו ועומק הצבע). אם יש כמה, נסו קובץ אחר."),
        };
    }

    /// <summary>
    /// הקובץ החדש: הקובץ הפגום, ובתחילתו — עד cut — הבתים של הדוגמה, עם מצביעים מתוקנים.
    /// אם חלק של הקובץ עצמו מתחיל לפני cut (הכותרת של הדוגמה ארוכה במעט), ההעתקה נעצרת
    /// לפניו, ומה שלא נכנס — רשימות וערכים — נכתב בסוף הקובץ: ב-TIFF רשימה יכולה להיות
    /// בכל מקום, כל עוד המצביעים אליה נכונים. null — חלק שהחלק המועתק מצביע אליו לא נמצא.
    /// </summary>
    private static byte[]? Assemble(byte[] broken, byte[] donor, Tiff dt, Dictionary<Ifd, int?> found, int cut)
    {
        bool little = dt.Little;
        var copied = dt.Ifds.Where(i => i.Offset < cut).ToList();
        // הרשימה הראשונה נשארת במקומה; חלק של הקובץ עצמו לא יכול להתחיל לפני סופה.
        long floor = copied.Count > 0 ? copied.Min(i => i.End) : 8;

        // --- חלקים שמיקומם בקובץ הפגום ידוע: הרשימות שלו, והחלקים שהרשימות שלו מצביעות אליהם.
        var anchors = new List<(long DonorStart, long DonorEnd, long Start, long End)> { (0, cut, 0, cut) };
        foreach (var ifd in dt.Ifds.Where(i => i.Offset >= cut))
            anchors.Add((ifd.Offset, ifd.End, found[ifd]!.Value, found[ifd]!.Value + ifd.End - ifd.Offset));

        foreach (var b in dt.Blocks.Where(b => b.Owner.Offset >= cut))
        {
            // אותה רשימה, במקום שבו נמצאה בקובץ הפגום.
            int shift = found[b.Owner]!.Value - b.Owner.Offset;
            long offset = OwnValue(broken, b.OffsetEntry, b.Index, shift, little);
            long length = OwnValue(broken, b.LengthEntry, b.Index, shift, little);
            if (offset < floor || length < 0) return null;
            anchors.Add((b.Offset, b.Offset + b.Length, offset, offset + length));
        }

        // --- החלקים שרק הרשימות שהועתקו מצביעות אליהם: קודם תמונות JPEG, לפי החתימה,
        // ואחר כך השאר — ביחס לחלקים שכבר נמצאו לפניהם ואחריהם.
        var moved = dt.Blocks.Where(b => b.Owner.Offset < cut).OrderBy(b => b.Offset).ToList();
        var placed = new Dictionary<Block, (long Offset, long Length)>();

        foreach (var b in moved.Where(b => IsJpeg(donor, b.Offset)))
        {
            var a = Before(anchors, b.Offset);
            long expected = a.End + (b.Offset - a.DonorEnd);
            long at = FindJpeg(broken, donor, b.Offset, a.DonorStart == 0 ? floor : a.End, expected);
            if (at < floor) return null;
            long end = JpegEnd(broken, at);
            if (end < 0) return null;
            placed[b] = (at, end - at);
            anchors.Add((b.Offset, b.Offset + b.Length, at, end));
        }

        foreach (var b in moved.Where(b => !IsJpeg(donor, b.Offset)))
        {
            var a = Before(anchors, b.Offset);
            var after = anchors.Where(x => x.DonorStart >= b.Offset + b.Length).OrderBy(x => x.DonorStart).FirstOrDefault();
            long at, length = b.Length;
            if (a.DonorStart == 0 && after != default)
            {
                // לפניו רק החלק שהועתק מהדוגמה — שאורכו של הדוגמה, לא של הקובץ. המיקום נקבע
                // לפי החלק שאחריו, שנמצא בקובץ עצמו.
                at = after.Start - (after.DonorStart - (b.Offset + b.Length)) - b.Length;
            }
            else
            {
                at = Align(a.End, a.End + (b.Offset - a.DonorEnd), b.Offset);
                if (after != default && after.Start > at) length = Math.Min(length, after.Start - at);
            }
            if (at < floor || at + length > broken.Length) return null;
            placed[b] = (at, length);
            anchors.Add((b.Offset, b.Offset + b.Length, at, at + length));
        }

        // --- ההעתקה: עד cut, או עד החלק הראשון של הקובץ עצמו אם הוא מתחיל לפני כן.
        long copyEnd = anchors.Skip(1).Select(x => x.Start).Where(x => x < cut).DefaultIfEmpty(cut).Min();

        // מה שלא נכנס עובר לסוף הקובץ: רשימות שחורגות מ-copyEnd, וערכים (שאינם בתוך הרשימה)
        // שנמצאים בין copyEnd ל-cut. ערך שמתחיל לפני copyEnd ונגמר אחריו — עובר כולו.
        long tail = (broken.Length + 3) / 4 * 4;
        var moves = new Dictionary<long, long>();          // מיקום בדוגמה ← מיקום בקובץ החדש
        var extra = new List<(long Donor, int Length)>();
        void Move(long donorPos, int length)
        {
            if (moves.ContainsKey(donorPos)) return;
            moves[donorPos] = tail;
            extra.Add((donorPos, length));
            tail += (length + 3) / 4 * 4;
        }
        foreach (var ifd in copied)
        {
            if (ifd.End > copyEnd) Move(ifd.Offset, ifd.End - ifd.Offset);
            foreach (var e in ifd.Entries.Where(e => e.Size > 4 && e.ValuePos < cut && e.ValuePos + e.Size > copyEnd))
                Move(e.ValuePos, e.Size);
        }

        byte[] output = new byte[tail];
        broken.CopyTo(output, 0);
        Array.Copy(donor, output, copyEnd);
        foreach (var (from, length) in extra) Array.Copy(donor, from, output, moves[from], length);

        long IfdAt(Ifd i) => i.Offset >= cut ? found[i]!.Value : moves.TryGetValue(i.Offset, out long m) ? m : i.Offset;
        Ifd? ByOffset(long offset) => dt.Ifds.FirstOrDefault(i => i.Offset == offset);

        // מיקום הערך של תגית ברשימה שהועתקה, בקובץ החדש.
        long ValueAt(Ifd owner, Entry e)
            => e.Size <= 4 ? IfdAt(owner) + (e.Pos - owner.Offset) + 8
             : moves.TryGetValue(e.ValuePos, out long m) ? m : e.ValuePos;

        bool Put(Ifd owner, Entry e, int k, long value)
        {
            long at = ValueAt(owner, e) + (e.Type is 3 or 8 ? 2 : 4) * k;
            // כתיבה רק בחלק שהועתק או שהועבר — אחרת היא הייתה נופלת על נתונים של הקובץ עצמו.
            if (at >= copyEnd && at < broken.Length) return false;
            if (e.Type is 3 or 8)
            {
                if (value > ushort.MaxValue) return false;
                Put16(output, at, (int)value, little);
            }
            else Put32(output, at, value, little);
            return true;
        }

        // --- מצביעים: לרשימות (מהכותרת, מהרשימות שהועתקו ומ"הרשימה הבאה") ולערכים שהועברו.
        if (ByOffset(U32(donor, 4, little)) is { } first) Put32(output, 4, IfdAt(first), little);
        foreach (var ifd in copied)
        {
            long at = IfdAt(ifd);
            foreach (var e in ifd.Entries)
            {
                if (e.Size > 4 && moves.TryGetValue(e.ValuePos, out long m))
                    Put32(output, at + (e.Pos - ifd.Offset) + 8, m, little);
                if (PointerTags.Contains(e.Tag))
                    for (int k = 0; k < e.Count; k++)
                        if (ByOffset(Value(donor, e, k, little)) is { } child && !Put(ifd, e, k, IfdAt(child))) return null;
            }
            if (ifd.Next != 0 && ByOffset(ifd.Next) is { } next) Put32(output, at + (ifd.NextPos - ifd.Offset), IfdAt(next), little);
        }

        foreach (var (b, (offset, length)) in placed)
            if (!Put(b.Owner, b.OffsetEntry, b.Index, offset) || !Put(b.Owner, b.LengthEntry, b.Index, length)) return null;
        return output;
    }

    /// <summary>החלק הידוע שנגמר הכי קרוב לפני מיקום נתון בדוגמה.</summary>
    private static (long DonorStart, long DonorEnd, long Start, long End) Before(
        List<(long DonorStart, long DonorEnd, long Start, long End)> anchors, long donorOffset)
        => anchors.Where(a => a.DonorEnd <= donorOffset).MaxBy(a => a.DonorEnd);

    /// <summary>
    /// מיקום של חלק בלי חתימה: אחרי סוף החלק הקודם, ביישור של 4 בתים כשגם בדוגמה הוא
    /// מיושר כך — המרווח שמצלמות משאירות הוא ריפוד ליישור, ולא אורך קבוע.
    /// </summary>
    private static long Align(long previousEnd, long expected, long donorOffset)
    {
        if (donorOffset % 4 != 0) return expected;
        long first = (previousEnd + 3) / 4 * 4;
        return first + Math.Max(0, (long)Math.Round((expected - first) / 4.0)) * 4;
    }

    // ================================================================ בדיקה

    /// <summary>
    /// הקובץ החדש תקין: אותו מבנה בדיוק כמו בדוגמה (אותן רשימות, אותן תגיות), כל תמונת
    /// JPEG שבו מתחילה ונגמרת במקום שהרשימות אומרות, ובשדות הטקסט אין תווים אקראיים.
    /// </summary>
    private static bool Valid(byte[] output, byte[] donor, Tiff dt)
    {
        var t = Parse(output);
        if (t is null || t.Ifds.Count != dt.Ifds.Count || t.Blocks.Count != dt.Blocks.Count) return false;
        for (int i = 0; i < t.Ifds.Count; i++)
            if (t.Ifds[i].Path != dt.Ifds[i].Path || !SameTags(t.Ifds[i], dt.Ifds[i])) return false;

        // אותן מידות ואותו קידוד כמו בדוגמה: קובץ RAW בגודל אחר (למשל mRAW מול RAW) בנוי
        // מאותן רשימות בדיוק — ורק כאן נראה ההבדל.
        int[] shape = { 0x100, 0x101, 0x102, 0x103 };
        for (int i = 0; i < t.Ifds.Count; i++)
            foreach (int tag in shape)
            {
                var a = t.Ifds[i].Entries.FirstOrDefault(e => e.Tag == tag);
                var b = dt.Ifds[i].Entries.FirstOrDefault(e => e.Tag == tag);
                if (a is null || b is null) continue;
                for (int k = 0; k < Math.Min(a.Count, 4); k++)
                    if (Value(output, a, k, t.Little) != Value(donor, b, k, dt.Little)) return false;
            }

        var donorBlocks = dt.Blocks.ToDictionary(b => b.Key);
        foreach (var b in t.Blocks)
        {
            if (!donorBlocks.TryGetValue(b.Key, out var d)) return false;
            if (IsJpeg(donor, d.Offset))
            {
                long end = JpegEnd(output, b.Offset);
                if (end < 0 || end > b.Offset + b.Length || end < b.Offset + b.Length - 1024) return false;
                if (!JpegFrame(output, b.Offset).SequenceEqual(JpegFrame(donor, d.Offset))) return false;
            }
        }
        return Plausible(output, t);
    }

    /// <summary>שדות טקסט בלי תווי בקרה — בתים אקראיים שנשארו מהנזק היו מכילים אותם.</summary>
    private static bool Plausible(byte[] d, Tiff t)
    {
        foreach (var e in t.Ifds.SelectMany(i => i.Entries).Where(e => e.Type == 2))
            for (int k = 0; k < e.Size; k++)
            {
                byte c = d[e.ValuePos + k];
                if (c is not 0 and < 0x20 and not (9 or 10 or 13)) return false;
            }
        return true;
    }

    private static bool SameTags(Ifd a, Ifd b)
        => a.Entries.Count == b.Entries.Count
           && a.Entries.Zip(b.Entries).All(p => p.First.Tag == p.Second.Tag && p.First.Type == p.Second.Type);

    // ================================================================ חיפוש בקובץ הפגום

    /// <summary>
    /// היכן רשימה של הדוגמה נמצאת בקובץ הפגום: הקרובה ביותר למקום הצפוי עם אותן תגיות —
    /// בקבצים שבהם פרטי הצילום משנים אורך (למשל NEF), מה שאחריהם זז.
    /// </summary>
    private static int? Find(byte[] broken, Ifd ifd, long expected, int after, bool little)
    {
        const int Window = 4 * 1024 * 1024;
        bool Matches(long at)
        {
            if (at < 8 || at <= after || at + (ifd.End - ifd.Offset) > broken.Length) return false;
            if (U16(broken, at, little) != ifd.Entries.Count) return false;
            for (int k = 0; k < ifd.Entries.Count; k++)
            {
                var entry = ifd.Entries[k];
                long e = at + 2 + 12 * k;
                if (U16(broken, e, little) != entry.Tag || U16(broken, e + 2, little) != entry.Type) return false;
                // גם מספר הערכים — פרט לטקסט ולנתונים חופשיים, שאורכם משתנה מתמונה לתמונה.
                if (entry.Type is not (2 or 7) && U32(broken, e + 4, little) != entry.Count) return false;
            }
            return true;
        }

        foreach (long start in new[] { Math.Max(expected, after + 2), Math.Max(ifd.Offset, after + 2) })
            for (int step = 0; step <= Window; step += 2)
            {
                if (Matches(start + step)) return (int)(start + step);
                if (step > 0 && Matches(start - step)) return (int)(start - step);
            }
        return null;
    }

    /// <summary>
    /// תמונת JPEG בקובץ הפגום: אותם ארבעה בתים ראשונים כמו בדוגמה, אחרי החלק הקודם,
    /// הקרובה ביותר למקום הצפוי. -1 — לא נמצאה.
    /// </summary>
    private static long FindJpeg(byte[] broken, byte[] donor, long donorOffset, long from, long expected)
    {
        byte[] signature = donor.AsSpan((int)donorOffset, 4).ToArray();
        long best = -1;
        int at = (int)Math.Max(0, from);
        while (at < broken.Length)
        {
            int hit = broken.AsSpan(at).IndexOf(signature);
            if (hit < 0) break;
            long p = at + hit;
            if (JpegEnd(broken, p) > 0)
            {
                if (best < 0 || Math.Abs(p - expected) < Math.Abs(best - expected)) best = p;
                if (p > expected) break;                 // מכאן והלאה רק מתרחקים
            }
            at = (int)p + 1;
        }
        return best;
    }

    private static bool IsJpeg(byte[] d, long offset)
        => offset + 4 <= d.Length && d[offset] == 0xFF && d[offset + 1] == 0xD8 && d[offset + 2] == 0xFF;

    /// <summary>
    /// סוף תמונת JPEG (אחרי FF D9): מעבר על המקטעים, ובנתונים הדחוסים — עד הסימן הבא.
    /// מתאים גם ל-JPEG ללא אובדן שבו נשמרים נתוני החיישן. -1 — המבנה שבור.
    /// </summary>
    private static long JpegEnd(byte[] d, long start)
    {
        if (!IsJpeg(d, start)) return -1;
        long at = start + 2;
        while (at + 2 <= d.Length)
        {
            if (d[at] != 0xFF) return -1;
            int marker = d[at + 1];
            if (marker == 0xFF) { at++; continue; }
            if (marker == 0xD9) return at + 2;              // גם בסוף הקובץ ממש — נתוני החיישן ב-CR2 נגמרים שם
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7)) { at += 2; continue; }
            if (marker is 0x00 or 0xD8 || at + 4 > d.Length) return -1;
            long end = at + 2 + (d[at + 2] << 8 | d[at + 3]);
            if (end > d.Length || end < at + 4) return -1;
            at = end;
            if (marker != 0xDA) continue;

            // נתונים דחוסים: FF מלווה ב-00 (בית רגיל) או בסימן התחלה מחדש.
            while (at + 1 < d.Length)
            {
                int hit = d.AsSpan((int)at).IndexOf((byte)0xFF);
                if (hit < 0) return -1;
                at += hit;
                if (at + 1 >= d.Length) return -1;
                int next = d[at + 1];
                if (next == 0x00 || next is >= 0xD0 and <= 0xD7 || next == 0xFF) { at += next == 0xFF ? 1 : 2; continue; }
                break;
            }
        }
        return -1;
    }

    /// <summary>סוג הקידוד, הדיוק, המידות ומספר הרכיבים של תמונת JPEG (מקטע SOF).</summary>
    private static byte[] JpegFrame(byte[] d, long start)
    {
        long at = start + 2;
        while (at + 10 <= d.Length && d[at] == 0xFF)
        {
            int marker = d[at + 1];
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
                return [(byte)marker, .. d.AsSpan((int)at + 4, 6)];
            if (marker == 0xDA) break;
            at += 2 + (d[at + 2] << 8 | d[at + 3]);
        }
        return [];
    }

    // ================================================================ קריאת המבנה

    /// <summary>כל הרשימות והחלקים, בקריאה מחמירה: כל רשימה, תגית וחלק חייבים להיות תקינים.</summary>
    private static Tiff? Parse(byte[] d)
    {
        if (d.Length < 16) return null;
        bool little;
        if (d[0] == 'I' && d[1] == 'I') little = true;
        else if (d[0] == 'M' && d[1] == 'M') little = false;
        else return null;
        if (U16(d, 2, little) != 42) return null;

        var t = new Tiff { Little = little };
        var seen = new HashSet<long>();

        bool Walk(long offset, string path)
        {
            if (!seen.Add(offset) || t.Ifds.Count >= 64) return false;
            var ifd = ReadIfd(d, offset, little, path);
            if (ifd is null) return false;
            t.Ifds.Add(ifd);

            foreach (var (offsetTag, lengthTag) in BlockTags)
            {
                var o = ifd.Entries.FirstOrDefault(e => e.Tag == offsetTag);
                var l = ifd.Entries.FirstOrDefault(e => e.Tag == lengthTag);
                if (o is null || l is null) continue;
                if (o.Count != l.Count) return false;
                for (int k = 0; k < o.Count; k++)
                {
                    long at = Value(d, o, k, little), length = Value(d, l, k, little);
                    if (length == 0) continue;
                    if (at < 8 || at + length > d.Length) return false;
                    t.Blocks.Add(new Block
                    {
                        Key = $"{path}:{offsetTag:x}:{k}", Owner = ifd, OffsetEntry = o, LengthEntry = l, Index = k, Offset = at, Length = length,
                    });
                }
            }

            foreach (var e in ifd.Entries.Where(e => PointerTags.Contains(e.Tag)))
                for (int k = 0; k < e.Count; k++)
                    if (!Walk(Value(d, e, k, little), $"{path}/{e.Tag:x}.{k}")) return false;

            return ifd.Next == 0 || Walk(ifd.Next, path + ">");
        }

        return Walk(U32(d, 4, little), "0") ? t : null;
    }

    /// <param name="size">גודל הקובץ — כשנקראה רק תחילתו; הערכים עצמם אינם נקראים כאן.</param>
    private static Ifd? ReadIfd(byte[] d, long offset, bool little, string path, long size = -1)
    {
        long limit = size < 0 ? d.Length : size;
        if (offset < 8 || offset + 2 > d.Length) return null;
        int count = U16(d, offset, little);
        long end = offset + 2 + 12L * count + 4;
        if (count is 0 or > 1000 || end > d.Length) return null;

        var ifd = new Ifd { Path = path, Offset = (int)offset, End = (int)end, NextPos = (int)(end - 4) };
        ifd.Next = U32(d, ifd.NextPos, little);
        for (int k = 0; k < count; k++)
        {
            int pos = (int)offset + 2 + 12 * k;
            int type = U16(d, pos + 2, little);
            long n = U32(d, pos + 4, little);
            int unit = type switch
            {
                1 or 2 or 6 or 7 => 1,
                3 or 8 => 2,
                4 or 9 or 11 or 13 => 4,
                5 or 10 or 12 => 8,
                _ => 0,
            };
            if (unit == 0 || n > 64 * 1024 * 1024) return null;
            long bytes = n * unit;
            long valuePos = bytes <= 4 ? pos + 8 : U32(d, pos + 8, little);
            if (valuePos + bytes > limit) return null;
            ifd.Entries.Add(new Entry
            {
                Pos = pos, Tag = U16(d, pos, little), Type = type, Count = (int)n, ValuePos = (int)valuePos, Size = (int)bytes,
            });
        }
        return ifd;
    }

    private static long Value(byte[] d, Entry e, int k, bool little) => e.Type switch
    {
        3 or 8 => U16(d, e.ValuePos + 2 * k, little),
        4 or 9 or 13 => U32(d, e.ValuePos + 4 * k, little),
        _ => -1,
    };

    /// <summary>ערך של תגית ברשימה של הקובץ הפגום, שנמצאת shift בתים מהמקום שלה בדוגמה.</summary>
    private static long OwnValue(byte[] broken, Entry e, int k, int shift, bool little)
    {
        long valuePos = e.Size <= 4 ? e.Pos + shift + 8 : U32(broken, e.Pos + shift + 8, little);
        long at = valuePos + (e.Type is 3 or 8 ? 2 : 4) * k;
        if (at < 0 || at + 4 > broken.Length) return -1;
        return e.Type is 3 or 8 ? U16(broken, at, little) : U32(broken, at, little);
    }

    private static int U16(byte[] d, long p, bool little)
        => p < 0 || p + 2 > d.Length ? -1 : little ? d[p] | d[p + 1] << 8 : d[p] << 8 | d[p + 1];

    private static long U32(byte[] d, long p, bool little)
        => p < 0 || p + 4 > d.Length ? -1
         : little ? (uint)(d[p] | d[p + 1] << 8 | d[p + 2] << 16 | d[p + 3] << 24)
                  : (uint)(d[p] << 24 | d[p + 1] << 16 | d[p + 2] << 8 | d[p + 3]);

    private static void Put16(byte[] d, long p, int v, bool little)
    {
        if (little) { d[p] = (byte)v; d[p + 1] = (byte)(v >> 8); }
        else { d[p] = (byte)(v >> 8); d[p + 1] = (byte)v; }
    }

    private static void Put32(byte[] d, long p, long v, bool little)
    {
        for (int i = 0; i < 4; i++)
            d[p + (little ? i : 3 - i)] = (byte)(v >> (8 * i));
    }
}
