using System.Buffers.Binary;
using RAF.Core.Carving;
using RAF.Core.FileSystems;
using RAF.Core.Signatures;

namespace RAF.Core.Repair;

/// <summary>סוג הבעיה שנמצאה בקובץ.</summary>
public enum FileIssueKind
{
    /// <summary>הקובץ ריק או מכיל אפסים בלבד — אין מה לתקן.</summary>
    Empty,

    /// <summary>חתימת הפתיחה נפגעה, אך שאר הקובץ נראה תקין.</summary>
    HeaderDamaged,

    /// <summary>התוכן אינו תואם לסיומת, ולא ניתן לזהות אותו בוודאות.</summary>
    Unrecognized,

    /// <summary>התוכן תקין אך הסיומת שגויה.</summary>
    ExtensionMismatch,

    /// <summary>אחרי סוף הקובץ האמיתי יש נתונים עודפים.</summary>
    TrailingData,

    /// <summary>הקובץ קצר ממה שמבנהו מצהיר — חלק מהנתונים חסר.</summary>
    Truncated,

    /// <summary>חתימת הסיום של הפורמט חסרה.</summary>
    FooterMissing,

    /// <summary>תוכן העניינים של ארכיון ZIP (ומסמך Office) חסר או פגום.</summary>
    ArchiveDirectoryDamaged,

    /// <summary>קבצים פנימיים בארכיון פגומים — הם יושמטו מהעותק המתוקן.</summary>
    ArchiveEntriesDamaged,

    /// <summary>חלק שמסמך Office אינו נפתח בלעדיו חסר או פגום.</summary>
    ArchivePartMissing,
}

/// <summary>בעיה אחת בקובץ, עם הסבר ועם ציון האם ניתן לתקן אותה.</summary>
public sealed record FileIssue(FileIssueKind Kind, string Description, bool Fixable);

/// <summary>תוצאת אבחון קובץ.</summary>
public sealed class FileDiagnosis
{
    public string Path { get; init; } = "";
    public long Size { get; init; }

    /// <summary>הפורמט שזוהה בפועל מתוך התוכן.</summary>
    public string? DetectedFormat { get; init; }

    /// <summary>הפורמט שהסיומת מרמזת עליו.</summary>
    public string? ExpectedFormat { get; init; }

    /// <summary>הסיומת הנכונה, כשהיא שונה מהקיימת.</summary>
    public string? SuggestedExtension { get; init; }

    public List<FileIssue> Issues { get; init; } = new();

    public bool IsHealthy => Issues.Count == 0;
    public bool CanRepair => Issues.Any(i => i.Fixable);

    // ---- נתונים פנימיים לשלב התיקון ----
    internal FileSignature? Format { get; init; }
    internal long? CorrectLength { get; init; }
}

/// <summary>תוצאת תיקון קובץ.</summary>
public sealed class FileRepairResult
{
    public bool Succeeded { get; init; }
    public string? OutputPath { get; init; }
    public List<string> Applied { get; init; } = new();
    public string Message { get; init; } = "";

    /// <summary>אבחון העותק המתוקן — ההוכחה שהתיקון עבד.</summary>
    public FileDiagnosis? After { get; init; }
}

/// <summary>
/// אבחון ותיקון קבצים פגומים לפי זיהוי HEX.
///
/// האבחון משווה בין שלושה מקורות מידע: חתימת הפתיחה בפועל, הסיומת,
/// והאורך שמבנה הקובץ מצהיר עליו. כל פער ביניהם הוא בעיה שניתן לזהות,
/// וחלקן ניתנות לתיקון מכני: שחזור חתימה שנמחקה, הסרת זבל אחרי סוף
/// הקובץ, השלמת חתימת סיום ותיקון סיומת.
///
/// הקובץ המקורי לעולם אינו משתנה. התיקון נכתב לעותק חדש, ואחריו
/// מתבצע אבחון חוזר של העותק כדי להוכיח שהתיקון אכן הצליח.
/// </summary>
public static class FileDoctor
{
    private const int HeadBytes = 4096;

    // ================================================================ אבחון

    public static FileDiagnosis Diagnose(string path)
    {
        using var volume = StreamVolume.Open(path);
        long size = volume.Length;
        string extension = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

        var issues = new List<FileIssue>();

        if (size == 0 || volume.IsAllZeros())
        {
            issues.Add(new FileIssue(FileIssueKind.Empty,
                "הקובץ ריק או מכיל אפסים בלבד. אין בו תוכן לתקן.", false));

            return new FileDiagnosis { Path = path, Size = size, Issues = issues };
        }

        byte[] head = volume.ReadAt(0, HeadBytes);
        var detected = FileSignatures.Identify(head);
        var expected = FileSignatures.ForExtension(extension).FirstOrDefault();

        FileSignature? format = detected;
        string? suggestedExtension = null;

        // ---------------------------------------------- חתימת הפתיחה
        if (detected is null && expected is not null)
        {
            if (LooksLikeDamagedHeader(head, expected))
            {
                format = expected;
                issues.Add(new FileIssue(FileIssueKind.HeaderDamaged,
                    $"חתימת הפתיחה של {expected.Name} נפגעה. הבתים הראשונים של הקובץ " +
                    "אינם תואמים לפורמט, אך הם חלקיים או מאופסים — סימן לנזק ולא לסוג קובץ אחר. " +
                    "ניתן לשחזר את החתימה.", true));
            }
            else
            {
                issues.Add(new FileIssue(FileIssueKind.Unrecognized,
                    $"הסיומת מציינת {expected.Name}, אך תחילת הקובץ אינה דומה לפורמט הזה ואינה " +
                    "מזוהה כפורמט אחר. ייתכן שהתוכן נדרס. לא ניתן לתקן בביטחון.", false));
            }
        }
        else if (detected is not null && !detected.MatchesExtension(extension))
        {
            suggestedExtension = detected.Extensions[0];
            issues.Add(new FileIssue(FileIssueKind.ExtensionMismatch,
                $"התוכן הוא {detected.Name}, אך הסיומת היא .{(extension.Length > 0 ? extension : "(ללא)")}. " +
                $"הסיומת הנכונה היא .{suggestedExtension}.", true));
        }

        // ---------------------------------------------- סמן JPEG
        // חתימת JPEG היא שלושה בתים, אך קובץ תקין דורש גם סמן מקטע אחריה.
        // בלי הבדיקה הזו, קובץ שהחתימה שלו שוחזרה אך הסמן לא — היה מדווח
        // כתקין, ולא היה נפתח.
        if (format is not null && format.Extensions[0] == "jpg" && head.Length > 3)
        {
            byte[] check = detected is null ? WithHeader(head, format) : head;

            if (!IsJpegMarker(check[3]))
            {
                byte? inferred = InferJpegMarker(check);

                issues.Add(inferred is not null
                    ? new FileIssue(FileIssueKind.HeaderDamaged,
                        "סמן המקטע הראשון של ה-JPEG נפגע. מזהה המקטע שאחריו שרד, " +
                        "ולכן ניתן לשחזר את הסמן במדויק.", true)
                    : new FileIssue(FileIssueKind.HeaderDamaged,
                        "סמן המקטע הראשון של ה-JPEG נפגע, וגם המידע שממנו ניתן היה לשחזר " +
                        "אותו אבד. לא ניתן לשחזר את הסמן בוודאות, והתמונה עלולה שלא להיפתח.", false));
            }
        }

        // ---------------------------------------------- ארכיון ZIP / Office
        // ב-ZIP "חתימת סיום" היא תוכן עניינים שלם. השלמת ארבעה בתים לא הייתה
        // פותחת אותו — לכן ארכיון פגום נבדק לעומק, קובץ פנימי אחר קובץ פנימי.
        bool archiveDamaged = false;
        if (format is not null && format.Extensions[0] == "zip" && size <= MaxArchive)
        {
            byte[] all = File.ReadAllBytes(path);
            if (detected is null) RestoreHeader(all, format);
            archiveDamaged = DiagnoseArchive(all, extension, issues);
        }

        // ---------------------------------------------- אורך וחתימת סיום
        long? correctLength = null;

        if (format is not null && !archiveDamaged)
        {
            // בקובץ שחתימתו נפגעה, קריאת המבנה צריכה לראות את החתימה התקינה.
            byte[] effectiveHead = head;
            if (detected is null) effectiveHead = WithHeader(head, format);

            long declared = FileLength.ReadDeclaredLength(
                format, effectiveHead, volume, 0, format.MaxSize);

            if (declared > 0)
            {
                if (declared < size)
                {
                    correctLength = declared;
                    issues.Add(new FileIssue(FileIssueKind.TrailingData,
                        $"מבנה הקובץ מצהיר על {declared:N0} בתים, אך הקובץ מכיל {size:N0}. " +
                        $"{size - declared:N0} הבתים העודפים אינם חלק מהקובץ וניתן להסיר אותם.", true));
                }
                else if (declared > size)
                {
                    issues.Add(new FileIssue(FileIssueKind.Truncated,
                        $"מבנה הקובץ מצהיר על {declared:N0} בתים, אך רק {size:N0} קיימים. " +
                        $"{declared - size:N0} בתים חסרים ואינם ניתנים לשחזור מתוך הקובץ עצמו. " +
                        "ייתכן שהקובץ ייפתח חלקית.", false));
                }
            }
            else if (format.Footer is { Length: > 0 })
            {
                long footerEnd = volume.FindLast(format.Footer);

                if (footerEnd <= 0)
                {
                    issues.Add(new FileIssue(FileIssueKind.FooterMissing,
                        $"חתימת הסיום של {format.Name} חסרה — ככל הנראה הקובץ נקטע. " +
                        "השלמת החתימה מאפשרת לרוב התוכנות לפתוח את החלק הקיים.", true));
                }
                else if (footerEnd < size && !volume.IsZeroRange(footerEnd, size))
                {
                    correctLength = footerEnd;
                    issues.Add(new FileIssue(FileIssueKind.TrailingData,
                        $"אחרי חתימת הסיום של הקובץ יש {size - footerEnd:N0} בתים עודפים " +
                        "שאינם חלק ממנו. ניתן להסיר אותם.", true));
                }
            }
        }

        return new FileDiagnosis
        {
            Path = path,
            Size = size,
            DetectedFormat = detected?.Name,
            ExpectedFormat = expected?.Name,
            SuggestedExtension = suggestedExtension,
            Issues = issues,
            Format = format,
            CorrectLength = correctLength,
        };
    }

    /// <summary>ארכיון גדול מזה אינו נבדק לעומק — הוא נקרא כולו לזיכרון.</summary>
    private const long MaxArchive = 512L * 1024 * 1024;

    /// <summary>סיומות Office שבלי [Content_Types].xml התוכנה מסרבת לפתוח.</summary>
    private static readonly string[] OpenXml = { "docx", "xlsx", "pptx" };

    /// <summary>
    /// אבחון ארכיון: האם תוכן העניינים שלם, ואילו קבצים פנימיים פגומים.
    /// מחזיר true כשנמצאה בעיה — ואז בדיקת האורך הכללית אינה רלוונטית.
    /// </summary>
    private static bool DiagnoseArchive(byte[] data, string extension, List<FileIssue> issues)
    {
        var a = ZipRebuilder.Analyze(data);

        if (a.Unsupported)
        {
            issues.Add(new FileIssue(FileIssueKind.Unrecognized, a.UnsupportedReason!, false));
            return true;
        }

        var damaged = a.Damaged.ToList();
        if (a.DirectoryIntact && damaged.Count == 0) return false;

        int intact = a.Intact.Count();
        if (intact == 0)
        {
            issues.Add(new FileIssue(FileIssueKind.ArchiveDirectoryDamaged,
                "הארכיון פגום, ולא נמצא בו אף קובץ פנימי שלם שאפשר להציל.", false));
            return true;
        }

        if (!a.DirectoryIntact)
            issues.Add(new FileIssue(FileIssueKind.ArchiveDirectoryDamaged,
                $"תוכן העניינים שבסוף הקובץ חסר או פגום, ולכן התוכנה שיצרה את הקובץ לא תפתח אותו. " +
                $"{intact:N0} קבצים פנימיים שלמים נמצאו בגוף הקובץ ונבדקו בסכום ביקורת — " +
                "אפשר לבנות מהם תוכן עניינים חדש.", true));

        if (damaged.Count > 0)
            issues.Add(new FileIssue(FileIssueKind.ArchiveEntriesDamaged,
                $"{damaged.Count:N0} קבצים פנימיים פגומים ויושמטו מהעותק המתוקן: " +
                string.Join(", ", damaged.Take(5).Select(e => $"{e.Name} ({e.Problem})")) +
                (damaged.Count > 5 ? " ועוד." : "."), true));

        // מסמך Office שחלק החובה שלו אבד לא ייפתח גם אחרי הבנייה מחדש — עדיף לומר זאת.
        if (OpenXml.Contains(extension) &&
            !a.Intact.Any(e => e.Name.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase)))
            issues.Add(new FileIssue(FileIssueKind.ArchivePartMissing,
                "החלק [Content_Types].xml של המסמך חסר או פגום. בלעדיו Office לא יפתח את הקובץ גם " +
                "אחרי התיקון — אבל התוכן (למשל word/document.xml) יישאר נגיש בפתיחה כ-ZIP.", false));

        return true;
    }

    /// <summary>
    /// האם תחילת הקובץ נראית כחתימה שנפגעה, ולא כסוג קובץ אחר.
    /// שני סימנים: רוב בתי החתימה עדיין תואמים, או שכל האזור מאופס.
    /// </summary>
    private static bool LooksLikeDamagedHeader(byte[] head, FileSignature expected)
    {
        int start = expected.HeaderOffset;
        if (head.Length < start + expected.Header.Length) return false;

        int defined = 0, matching = 0, zeros = 0;

        for (int i = 0; i < expected.Header.Length; i++)
        {
            byte? want = expected.Header[i];
            if (want is null) continue;

            defined++;
            byte actual = head[start + i];
            if (actual == want.Value) matching++;
            if (actual == 0) zeros++;
        }

        if (defined == 0) return false;

        // כל בית שתואם או מאופס עקבי עם נזק: אזור שאופס חלקית משאיר
        // בדיוק את התבנית הזו. בית שאינו כזה מעיד על תוכן אחר.
        bool consistent = true;
        for (int i = 0; i < expected.Header.Length; i++)
        {
            byte? want = expected.Header[i];
            if (want is null) continue;

            byte actual = head[start + i];
            if (actual != want.Value && actual != 0) consistent = false;
        }

        // חתימה של שני בתים בלבד (כמו BM או MZ) קצרה מכדי להסיק ממנה דבר
        // מהתאמה חלקית; נדרש שלפחות אחד מהם מאופס.
        if (defined <= 2) return consistent && zeros > 0;

        return consistent || matching * 2 >= defined;
    }

    /// <summary>
    /// ב-JPEG, הבית שאחרי החתימה הוא סמן המקטע הראשון, והוא משתנה בין
    /// קבצים — ולכן אינו חלק מהחתימה. כשהוא נמחק יחד איתה, ניתן לשחזר
    /// אותו רק אם מזהה המקטע שאחריו שרד.
    /// </summary>
    private static byte? InferJpegMarker(ReadOnlySpan<byte> data)
    {
        if (data.Length < 11) return null;

        if (data[6..11].SequenceEqual("JFIF\0"u8)) return 0xE0;
        if (data[6..11].SequenceEqual("Exif\0"u8)) return 0xE1;

        return null;
    }

    /// <summary>סמן מקטע JPEG חוקי: מ-0xC0 עד 0xFE.</summary>
    private static bool IsJpegMarker(byte value) => value is >= 0xC0 and <= 0xFE;

    // ================================================================ תיקון

    /// <summary>
    /// תיקון קובץ לתוך עותק חדש בתיקיית היעד. הקובץ המקורי אינו משתנה.
    /// </summary>
    public static FileRepairResult Repair(string path, string outputFolder)
    {
        var diagnosis = Diagnose(path);

        if (diagnosis.IsHealthy)
            return new FileRepairResult { Succeeded = true, Message = "הקובץ תקין ואינו זקוק לתיקון." };

        if (!diagnosis.CanRepair)
            return new FileRepairResult
            {
                Message = "הבעיות שנמצאו אינן ניתנות לתיקון מכני: " +
                          string.Join(" ", diagnosis.Issues.Select(i => i.Description)),
            };

        // התיקונים נוגעים רק בתחילת הקובץ ובסופו, ולכן הקובץ אינו נקרא כולו
        // לזיכרון: סרטון של כמה ג'יגה-בתים מועתק בזרימה, והתיקונים מוחלים בדרך.
        var applied = new List<string>();
        var format = diagnosis.Format;

        bool restoreHeader = false;
        long bodyLength = diagnosis.Size;
        byte[] footer = [];

        foreach (var issue in diagnosis.Issues.Where(i => i.Fixable))
        {
            switch (issue.Kind)
            {
                // חתימה וסמן נפגעים לרוב יחד; השחזור מטפל בשניהם בפעם אחת.
                case FileIssueKind.HeaderDamaged when format is not null && !restoreHeader:
                    applied.Add($"שוחזרה חתימת הפתיחה של {format.Name}");
                    restoreHeader = true;
                    break;

                case FileIssueKind.TrailingData when diagnosis.CorrectLength is > 0:
                    bodyLength = diagnosis.CorrectLength.Value;
                    applied.Add($"הוסרו נתונים עודפים — הקובץ קוצר ל-{bodyLength:N0} בתים");
                    break;

                case FileIssueKind.FooterMissing when format?.Footer is { Length: > 0 }:
                    footer = format.Footer;
                    applied.Add($"הושלמה חתימת הסיום של {format.Name}");
                    break;

                case FileIssueKind.ExtensionMismatch:
                    applied.Add($"הסיומת תוקנה ל-.{diagnosis.SuggestedExtension}");
                    break;
            }
        }

        // ארכיון נבנה מחדש בזיכרון — האבחון בודק ארכיונים רק עד MaxArchive.
        bool rebuildArchive = diagnosis.Issues.Any(i => i.Fixable &&
            i.Kind is FileIssueKind.ArchiveDirectoryDamaged or FileIssueKind.ArchiveEntriesDamaged);

        // --- כתיבת העותק המתוקן ---
        string extension = diagnosis.SuggestedExtension ?? System.IO.Path.GetExtension(path).TrimStart('.');
        string stem = System.IO.Path.GetFileNameWithoutExtension(path);

        Directory.CreateDirectory(outputFolder);
        string output = UniquePath(outputFolder, $"{stem} (תוקן)", extension);

        // הגנה: לעולם לא לדרוס את הקובץ המקורי.
        if (string.Equals(System.IO.Path.GetFullPath(output), System.IO.Path.GetFullPath(path),
                StringComparison.OrdinalIgnoreCase))
            return new FileRepairResult { Message = "נתיב היעד זהה לקובץ המקורי. התיקון בוטל." };

        try
        {
            if (rebuildArchive)
                applied.Add(WriteRebuiltArchive(path, output, format, restoreHeader));
            else
                WriteStreamed(path, output, format, restoreHeader, bodyLength, footer);
        }
        catch
        {
            // עותק חלקי הוא הטעיה — עדיף שלא יהיה עותק כלל.
            try { File.Delete(output); } catch { }
            throw;
        }

        // --- אבחון חוזר: ההוכחה שהתיקון עבד ---
        var after = Diagnose(output);
        bool fixedAll = after.Issues.All(i => !i.Fixable);

        return new FileRepairResult
        {
            Succeeded = fixedAll,
            OutputPath = output,
            Applied = applied,
            After = after,
            Message = fixedAll
                ? after.IsHealthy
                    ? "הקובץ תוקן ונבדק מחדש — לא נמצאו בו בעיות."
                    : "כל מה שניתן לתקן תוקן. נותרו בעיות שאינן ניתנות לתיקון מכני: " +
                      string.Join(" ", after.Issues.Select(i => i.Description))
                : "התיקון נכתב, אך הבדיקה החוזרת עדיין מוצאת בעיות הניתנות לתיקון.",
        };
    }

    /// <summary>
    /// בתים מתחילת הקובץ שהתיקון עשוי לשנות: החתימה, סמן ה-JPEG ומזהה המקטע
    /// שאחריו, ושדות הגודל של BMP ו-RIFF. כל השאר מועתק כמו שהוא.
    /// </summary>
    private const int PatchedHead = 64;

    /// <summary>
    /// כתיבת העותק בזרימה: תחילת הקובץ עם החתימה המשוחזרת, גוף הקובץ עד
    /// האורך הנכון, וחתימת הסיום כשהיא חסרה. עובד בכל גודל קובץ.
    /// </summary>
    private static void WriteStreamed(string path, string output, FileSignature? format,
        bool restoreHeader, long bodyLength, byte[] footer)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.SequentialScan);
        using var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1024 * 1024, FileOptions.SequentialScan);

        byte[] head = new byte[(int)Math.Min(PatchedHead, bodyLength)];
        source.ReadExactly(head);

        if (restoreHeader && format is not null)
        {
            RestoreHeader(head, format);
            FixSizeFields(head, format, bodyLength + footer.Length);
        }

        target.Write(head);

        byte[] buffer = new byte[1024 * 1024];
        for (long left = bodyLength - head.Length; left > 0;)
        {
            int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
            if (read <= 0) throw new EndOfStreamException("הקובץ המקורי התקצר בזמן התיקון.");
            target.Write(buffer, 0, read);
            left -= read;
        }

        target.Write(footer);
    }

    /// <summary>בניית ארכיון מחדש, מהנתונים כפי שהם אחרי שחזור החתימה.</summary>
    private static string WriteRebuiltArchive(string path, string output, FileSignature? format,
        bool restoreHeader)
    {
        byte[] data = File.ReadAllBytes(path);
        if (restoreHeader && format is not null) RestoreHeader(data, format);

        var archive = ZipRebuilder.Analyze(data);
        using (var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write))
            target.Write(ZipRebuilder.Rebuild(data, archive));

        int dropped = archive.Damaged.Count();
        return $"נבנה תוכן עניינים חדש מ-{archive.Intact.Count():N0} קבצים פנימיים שלמים" +
               (dropped > 0 ? $"; {dropped:N0} קבצים פגומים הושמטו" : "");
    }

    /// <summary>כתיבת בתי החתימה המוגדרים. בתים חופשיים בחתימה אינם משתנים.</summary>
    private static void RestoreHeader(byte[] data, FileSignature format)
    {
        for (int i = 0; i < format.Header.Length; i++)
        {
            int at = format.HeaderOffset + i;
            if (at >= data.Length) break;

            byte? value = format.Header[i];
            if (value is not null) data[at] = value.Value;
        }

        // סמן ה-JPEG משוחזר רק כשיש עדות ודאית לערכו.
        if (format.Extensions[0] == "jpg" && data.Length > 3 && !IsJpegMarker(data[3]))
        {
            byte? marker = InferJpegMarker(data);
            if (marker is not null) data[3] = marker.Value;
        }
    }

    /// <summary>
    /// בפורמטים שבהם שדה הגודל צמוד לחתימה, שדה שאופס יחד איתה
    /// משוחזר לפי גודל העותק המתוקן.
    /// </summary>
    private static void FixSizeFields(byte[] head, FileSignature format, long totalLength)
    {
        string ext = format.Extensions[0];

        if (ext == "bmp" && head.Length >= 6 &&
            BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(2)) == 0)
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(2), (uint)totalLength);

        if (ext is "wav" or "avi" or "webp" && head.Length >= 8 &&
            BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4)) == 0)
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(4), (uint)(totalLength - 8));
    }

    /// <summary>ראש הקובץ כשהחתימה התקינה במקומה, לצורך קריאת מבנה.</summary>
    private static byte[] WithHeader(byte[] head, FileSignature format)
    {
        byte[] copy = (byte[])head.Clone();
        RestoreHeader(copy, format);
        return copy;
    }

    private static string UniquePath(string folder, string stem, string extension)
    {
        string suffix = extension.Length > 0 ? "." + extension : "";
        string candidate = System.IO.Path.Combine(folder, stem + suffix);

        for (int i = 2; File.Exists(candidate); i++)
            candidate = System.IO.Path.Combine(folder, $"{stem} {i}{suffix}");

        return candidate;
    }

    // ================================================================ גישה לקובץ

    /// <summary>
    /// קובץ על הדיסק בתור מחיצה גולמית, כדי שאותו מנגנון קביעת אורך
    /// שמשמש את הסריקה המתקדמת ישמש גם כאן — ללא קוד כפול.
    /// </summary>
    private sealed class StreamVolume : IClusterVolume
    {
        private readonly FileStream _stream;

        private StreamVolume(FileStream stream) => _stream = stream;

        internal static StreamVolume Open(string path)
            => new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));

        internal long Length => _stream.Length;

        public int BytesPerCluster => 512;
        public long ClusterToOffset(long cluster) => cluster * BytesPerCluster;
        public bool? IsClusterAllocated(long cluster) => null;

        public int ReadRaw(long offset, Span<byte> destination)
        {
            if (offset < 0 || offset >= _stream.Length) return 0;

            _stream.Position = offset;
            int total = 0;

            while (total < destination.Length)
            {
                int read = _stream.Read(destination[total..]);
                if (read <= 0) break;
                total += read;
            }

            return total;
        }

        internal byte[] ReadAt(long offset, int count)
        {
            byte[] buffer = new byte[(int)Math.Max(0, Math.Min(count, _stream.Length - offset))];
            int read = ReadRaw(offset, buffer);
            return read == buffer.Length ? buffer : buffer.AsSpan(0, read).ToArray();
        }

        /// <summary>דגימה של הקובץ כולו: האם הוא מאופס לגמרי.</summary>
        internal bool IsAllZeros()
        {
            byte[] chunk = new byte[64 * 1024];
            long step = Math.Max(chunk.Length, _stream.Length / 32);

            for (long at = 0; at < _stream.Length; at += step)
            {
                int read = ReadRaw(at, chunk);
                for (int i = 0; i < read; i++)
                    if (chunk[i] != 0) return false;
            }

            return true;
        }

        internal bool IsZeroRange(long from, long to)
        {
            byte[] chunk = new byte[64 * 1024];

            for (long at = from; at < to; at += chunk.Length)
            {
                int want = (int)Math.Min(chunk.Length, to - at);
                int read = ReadRaw(at, chunk.AsSpan(0, want));
                for (int i = 0; i < read; i++)
                    if (chunk[i] != 0) return false;
            }

            return true;
        }

        /// <summary>
        /// ההיסט שאחרי המופע האחרון של רצף בתים, או אפס אם אינו קיים.
        /// החיפוש מתחיל מסוף הקובץ, כי חתימת הסיום צפויה שם.
        /// </summary>
        internal long FindLast(byte[] needle)
        {
            const int window = 1024 * 1024;
            long end = _stream.Length;

            while (end > 0)
            {
                long start = Math.Max(0, end - window);
                int length = (int)(end - start);
                byte[] block = ReadAt(start, length + needle.Length - 1);

                for (int i = Math.Min(block.Length - needle.Length, length - 1); i >= 0; i--)
                {
                    bool match = true;
                    for (int j = 0; j < needle.Length && match; j++)
                        match = block[i + j] == needle[j];

                    if (match) return start + i + needle.Length;
                }

                end = start;
            }

            return 0;
        }

        public void Dispose() => _stream.Dispose();
    }
}
