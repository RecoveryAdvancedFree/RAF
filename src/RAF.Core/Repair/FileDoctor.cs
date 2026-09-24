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

    /// <summary>טבלת המיקומים של מסמך PDF חסרה או שבורה — היא תיבנה מחדש מהמסמך עצמו.</summary>
    PdfStructureDamaged,

    /// <summary>נתוני התמונה מפסיקים להתפענח באמצע — נקטעה, נדרסה או שולבה בקובץ אחר.</summary>
    ImageDamaged,

    /// <summary>בתוך תמונה פגומה שמורה תמונה מוקטנת שלמה — היא תישמר כקובץ נפרד.</summary>
    PreviewAvailable,

    /// <summary>לסרטון חסר האינדקס (moov) — ההקלטה נקטעה. נבנה מחדש בעזרת סרטון תקין מאותו מכשיר.</summary>
    VideoIndexMissing,

    /// <summary>מסמך פגום שהטקסט שבו שרד — הוא יישמר כקובץ טקסט פשוט, כמוצא אחרון.</summary>
    TextRecoverable,
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

    /// <summary>התיקון דורש סרטון תקין מאותו מכשיר — ראו Mp4Rebuilder.</summary>
    public bool NeedsReferenceVideo => Issues.Any(i => i.Kind == FileIssueKind.VideoIndexMissing);

    // ---- נתונים פנימיים לשלב התיקון ----
    internal FileSignature? Format { get; init; }
    internal long? CorrectLength { get; init; }

    /// <summary>התמונה המוקטנת השלמה שבתוך תמונה פגומה — מה שיישמר בתיקון.</summary>
    internal JpegPreviews.Preview? Preview { get; init; }

    /// <summary>הטקסט שחולץ ממסמך פגום — מה שיישמר בתיקון כקובץ טקסט.</summary>
    internal string? Text { get; init; }
}

/// <summary>תוצאת תיקון קובץ.</summary>
public sealed class FileRepairResult
{
    public bool Succeeded { get; init; }
    public string? OutputPath { get; init; }
    public List<string> Applied { get; init; } = new();

    /// <summary>התמונה המוקטנת שנשמרה מתוך תמונה פגומה, כשנשמרה.</summary>
    public string? PreviewPath { get; init; }

    /// <summary>קובץ הטקסט שנשמר מתוך מסמך פגום, כשנשמר.</summary>
    public string? TextPath { get; init; }
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
                    $"תחילת הקובץ נפגעה: חתימת הפתיחה של {expected.Name} — הבתים שמזהים את סוג הקובץ — " +
                    "חלקית או מאופסת. זה סימן לנזק, ולא לסוג קובץ אחר, ולכן ניתן לשחזר אותה.", true));
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
                        "גם הבית שאחרי חתימת ה-JPEG (סמן המקטע הראשון) נפגע. המידע שאחריו שרד, " +
                        "ולכן ניתן לשחזר אותו במדויק.", true)
                    : new FileIssue(FileIssueKind.HeaderDamaged,
                        "גם הבית שאחרי חתימת ה-JPEG (סמן המקטע הראשון) נפגע, וגם המידע שממנו " +
                        "ניתן היה לשחזר אותו אבד. לא ניתן לשחזר אותו בוודאות, והתמונה עלולה שלא להיפתח.", false));
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

        // ---------------------------------------------- מסמך PDF
        // ב-PDF, "%%EOF" בסוף אינו מוכיח דבר: מה שפותח את המסמך הוא טבלת המיקומים
        // שלפניו. טבלה שבורה נבנית מחדש מהאובייקטים — ראו PdfRebuilder.
        if (format is not null && format.Extensions[0] == "pdf" && size <= MaxArchive)
        {
            byte[] all = File.ReadAllBytes(path);
            if (detected is null) RestoreHeader(all, format);
            var pdf = PdfRebuilder.Analyze(all);

            if (!pdf.XrefValid)
            {
                archiveDamaged = true;
                issues.Add(new FileIssue(FileIssueKind.PdfStructureDamaged,
                    pdf.XrefProblem + (!pdf.CanRebuild
                        ? " לא נמצאו במסמך החלק הראשי שלו ולא רשימת העמודים, ולכן אי אפשר לבנות את הטבלה מחדש."
                        : pdf.CatalogMissing
                            ? " גם החלק הראשי של המסמך אבד, אבל רשימת העמודים שרדה: ייבנו טבלה וחלק ראשי חדשים " +
                              $"מתוך {pdf.Objects.Count:N0} החלקים שנמצאו. העמודים יוצגו; תוכן עניינים וסימניות עלולים לחסור."
                            : $" נמצאו {pdf.Objects.Count:N0} חלקים שלמים במסמך, ואפשר לבנות ממנו טבלה חדשה."),
                    pdf.CanRebuild));
            }
        }

        // ---------------------------------------------- מסמך Office ישן (OLE)
        // בפורמט הזה אין חתימת סיום ואין שדה אורך — אבל טבלת ההקצאה (FAT) מגלה
        // עד איזה סקטור הקובץ משתמש. קובץ קצר מזה נקטע; טבלה שאופסה — נדרסה.
        if (format is not null && format.Extensions[0] == "doc" && detected is not null
            && OleDamage(volume, size) is { } oleProblem)
        {
            issues.Add(new FileIssue(FileIssueKind.Truncated, oleProblem, false));
        }

        // ---------------------------------------------- סרטון בלי אינדקס
        // הקלטה שנקטעה: התמונות והקול בקובץ, אבל האינדקס שנכתב רק בסוף חסר.
        // בדיקת האורך הרגילה הייתה מדווחת כאן "חסרים נתונים" — וזה לא מה שחסר.
        if (format is not null && format.Structure == "mp4" && detected is not null && Mp4Rebuilder.Inspect(path) is { } missing)
        {
            archiveDamaged = true;
            issues.Add(new FileIssue(FileIssueKind.VideoIndexMissing,
                "הסרטון לא נסגר כראוי: חסר בו האינדקס — החלק שאומר לנגן היכן כל תמונה וכל קטע קול. " +
                "זה קורה כשההקלטה נקטעת (סוללה שנגמרה, כרטיס שנשלף, מכשיר שנתקע). " +
                // הגודל מבודד משמאל לימין — אחרת "11.7 MB" מוצג הפוך בתוך משפט בעברית.
                $"התמונות והקול עצמם נמצאים בקובץ (⁦{Size(missing.DataEnd - missing.DataStart)}⁩). " +
                "אפשר לבנות אינדקס חדש בעזרת סרטון תקין אחד שצולם באותו מכשיר ובאותן הגדרות.", false));
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
                // ב-TIFF וב-RAW האורך הוא סוף הנתון הרחוק שהתגיות מכירות. מצלמות שומרות
                // לפעמים נתונים שתגית רגילה אינה מצביעה אליהם — קיצור היה הורס את התמונה.
                // לכן בהם אורך קצר מהקובץ אינו "נתונים עודפים".
                if (declared < size && format.Structure == "tif")
                {
                }
                // אחרי סוף ה-JPEG טלפונים ומצלמות כותבים חלקים של הקובץ עצמו — "הסרה" שלהם
                // הייתה מוחקת מידע אמיתי. ראו KnownJpegTrailer.
                else if (declared < size && format.Structure == "jpg" && KnownJpegTrailer(volume, declared, size))
                {
                }
                else if (declared < size)
                {
                    correctLength = declared;
                    issues.Add(new FileIssue(FileIssueKind.TrailingData,
                        $"מבנה הקובץ מצהיר על {declared:N0} בתים, אך הקובץ מכיל {size:N0}. " +
                        $"{size - declared:N0} הבתים העודפים אינם חלק מהקובץ וניתן להסיר אותם.", true));
                }
                // MKV שנקטע אינו דורש תיקון: נבדק ב-Edge, ב-ffmpeg (VLC) ובמנוע של Windows —
                // שלושתם מנגנים אותו כמו שהוא עד המקום שבו נקטע. סימון האורך כ"לא ידוע"
                // (כמו במשיב) לא שינה דבר באף אחד מהם, ולכן אינו מוצע כתיקון.
                else if (declared > size && format.Extensions[0] == "mkv")
                {
                    issues.Add(new FileIssue(FileIssueKind.Truncated,
                        $"הסרטון נקטע: הוא מצהיר על {declared:N0} בתים, ויש בו {size:N0} — כ-{size * 100.0 / declared:N0}% ממנו. " +
                        "החלק שנשאר מתנגן כמו שהוא — ב-VLC, ב-Edge ובנגן של Windows — עד המקום שבו נקטע. " +
                        "החלק החסר אינו נמצא בקובץ, ולכן אין מה לתקן בו.", false));
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
                        $"סוף הקובץ חסר (חתימת הסיום של {format.Name}) — ככל הנראה הקובץ נקטע. " +
                        "השלמת החתימה מאפשרת לרוב התוכנות לפתוח את החלק הקיים.", true));
                }
                // PDF מסתיים לרוב ב-"%%EOF" ושורה חדשה, והתקן מתיר זאת. בלי ההבחנה הזו
                // כמעט כל PDF תקין דווח כבעל "בתים עודפים".
                else if (footerEnd < size &&
                         !volume.IsZeroRange(footerEnd, size, allowWhitespace: format.Extensions[0] == "pdf"))
                {
                    correctLength = footerEnd;
                    issues.Add(new FileIssue(FileIssueKind.TrailingData,
                        $"אחרי סוף הקובץ יש {size - footerEnd:N0} בתים עודפים " +
                        "שאינם חלק ממנו. ניתן להסיר אותם.", true));
                }
            }
        }

        // ---------------------------------------------- תמונת JPEG: פענוח עד הסוף
        // חתימה, סיום ואורך תקינים אינם מוכיחים שהתמונה שלמה: קובץ שחציו נדרס
        // עובר את כולם. המפענח עובר על כל התמונה ומראה היכן היא נשברת.
        JpegPreviews.Preview? preview = null;
        if (format is not null && format.Extensions[0] == "jpg" && size <= format.MaxSize)
        {
            byte[] all = File.ReadAllBytes(path);
            if (detected is null) RestoreHeader(all, format);

            var check = JpegDecoder.Check(JpegBytes.Of(all));
            if (check.Verdict == JpegVerdict.Corrupt)
                issues.Add(new FileIssue(FileIssueKind.ImageDamaged,
                    $"התמונה פגומה: רק כ-{check.Fraction:P0} ממנה מתפענח, ומשם והלאה הנתונים אינם של התמונה — " +
                    "הקובץ נקטע, נדרס, או שחלקו נלקח מקובץ אחר. את החלק החסר אי אפשר להשלים.", false));

            // התמונה המוקטנת שבתוכה שורדת לעיתים קרובות — היא בתחילת הקובץ.
            bool damaged = issues.Any(i => i.Kind is FileIssueKind.ImageDamaged or FileIssueKind.Truncated
                                                   or FileIssueKind.FooterMissing
                                           || (i.Kind == FileIssueKind.HeaderDamaged && !i.Fixable));
            if (damaged && JpegPreviews.Best(all) is { } best)
            {
                preview = best;
                issues.Add(new FileIssue(FileIssueKind.PreviewAvailable,
                    $"בתוך הקובץ שמורה תמונה מוקטנת שלמה, בגודל {best.Width}×{best.Height}. " +
                    "אפשר לשמור אותה כקובץ נפרד — גם אם התמונה עצמה לא תיפתח, היא תישאר.", true));
            }
        }

        // ---------------------------------------------- מסמך פגום: הטקסט שבו
        // מוצא אחרון: גם אם המסמך לא ייפתח — לא לפני התיקון ולא אחריו — המילים נשמרות.
        string? text = null;
        string? textKind = TextExtractor.KindOf(extension)
                           ?? (format is not null ? TextExtractor.KindOf(format.Extensions[0]) : null);
        bool documentDamaged = issues.Any(i => i.Kind is not (FileIssueKind.ExtensionMismatch
                                                   or FileIssueKind.TrailingData or FileIssueKind.FooterMissing));
        if (textKind is not null && documentDamaged && size <= MaxArchive)
        {
            byte[] all = File.ReadAllBytes(path);
            if (detected is null && format is not null) RestoreHeader(all, format);
            text = TextExtractor.Extract(all, textKind == "office" ? "zip" : textKind);
            if (text is not null)
                issues.Add(new FileIssue(FileIssueKind.TextRecoverable,
                    $"הטקסט של המסמך שרד — כ-{TextExtractor.Words(text):N0} מילים. אפשר לשמור אותו כקובץ טקסט פשוט: " +
                    "העיצוב, התמונות והטבלאות לא יישמרו, אבל התוכן כן — גם אם המסמך עצמו לא ייפתח.", true));
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
            Preview = preview,
            Text = text,
        };
    }

    /// <summary>
    /// בדיקת מבנה OLE: טבלאות ההקצאה שהכותרת מצביעה עליהן קיימות ואינן מאופסות,
    /// והקובץ ארוך לפחות כמו הסקטור האחרון שבשימוש. null — לא נמצאה בעיה.
    /// </summary>
    private static string? OleDamage(StreamVolume volume, long size)
    {
        byte[] header = volume.ReadAt(0, 512);
        if (header.Length < 512) return "הקובץ קצר מכותרת המסמך עצמה — כמעט כולו חסר.";
        int shift = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x1E));
        if (shift is not (9 or 12)) return null;
        int sector = 1 << shift;
        long SectorOffset(uint id) => (id + 1L) * sector;

        uint fatSectors = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x2C));
        long lastUsed = -1;
        for (int i = 0; i < Math.Min(109u, fatSectors); i++)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x4C + i * 4));
            if (id >= 0xFFFFFFFA) continue;
            if (SectorOffset(id) + sector > size)
                return "המסמך נקטע: חלק מטבלת ההקצאה שלו — המפה שאומרת היכן כל חלק של המסמך — נמצא מעבר לסוף הקובץ. " +
                       "Word לא יפתח אותו.";

            byte[] fat = volume.ReadAt(SectorOffset(id), sector);
            if (fat.All(b => b == 0))
                return "טבלת ההקצאה של המסמך — המפה שאומרת היכן כל חלק שלו — אופסה או נדרסה. Word לא יפתח אותו.";

            for (int k = 0; k < sector / 4; k++)
            {
                uint entry = BinaryPrimitives.ReadUInt32LittleEndian(fat.AsSpan(k * 4));
                if (entry != 0xFFFFFFFF) lastUsed = Math.Max(lastUsed, (long)i * (sector / 4) + k);
            }
        }

        return lastUsed >= 0 && SectorOffset((uint)lastUsed) + sector > size + sector - 1
            ? $"המסמך נקטע: הוא אמור להיות באורך {(lastUsed + 2) * sector:N0} בתים לפחות, ויש בו {size:N0}. Word לא יפתח אותו."
            : null;
    }

    /// <summary>
    /// נתונים אחרי סוף ה-JPEG שהם חלק מהקובץ, ולא זבל:
    /// תמונה נוספת — תצוגה מקדימה גדולה (MPF) שמצלמות כותבות אחרי התמונה;
    /// סרטון קצר ("תמונה נעה" של Google ו-Samsung) — MP4 שמתחיל ב-ftyp;
    /// ובלוק המידע של טלפוני Samsung, שמסתיים ב-"SEFT" (זמן הצילום, פרטי הטלפון).
    /// </summary>
    private static bool KnownJpegTrailer(StreamVolume volume, long end, long size)
    {
        byte[] start = volume.ReadAt(end, 12);
        if (start is [0xFF, 0xD8, 0xFF, ..]) return true;
        if (start.Length >= 8 && start.AsSpan(4, 4).SequenceEqual("ftyp"u8)) return true;
        return volume.ReadAt(size - 4, 4).AsSpan().SequenceEqual("SEFT"u8);
    }

    private static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        _ => $"{bytes / 1024.0:0} KB",
    };

    /// <summary>
    /// בניית אינדקס לסרטון שחסר בו, לעותק חדש בתיקיית היעד — בעזרת סרטון תקין מאותו
    /// מכשיר. העותק נבדק שוב אחרי הכתיבה, כמו כל תיקון.
    /// </summary>
    public static FileRepairResult RepairVideo(string path, string referencePath, string outputFolder,
        IProgress<double>? progress = null, CancellationToken token = default)
    {
        Directory.CreateDirectory(outputFolder);
        string extension = System.IO.Path.GetExtension(path).TrimStart('.');
        string output = UniquePath(outputFolder, $"{System.IO.Path.GetFileNameWithoutExtension(path)} (תוקן)",
            extension.Length > 0 ? extension : "mp4");

        if (string.Equals(System.IO.Path.GetFullPath(output), System.IO.Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            return new FileRepairResult { Message = "נתיב היעד זהה לקובץ המקורי. התיקון בוטל." };

        VideoRebuildResult rebuilt;
        try
        {
            rebuilt = Mp4Rebuilder.Rebuild(path, referencePath, output, progress, token);
        }
        catch
        {
            try { File.Delete(output); } catch { }
            throw;
        }

        if (!rebuilt.Succeeded)
        {
            try { File.Delete(output); } catch { }
            return new FileRepairResult { Message = rebuilt.Message };
        }

        var after = Diagnose(output);
        return new FileRepairResult
        {
            Succeeded = after.IsHealthy,
            OutputPath = output,
            Applied = new List<string> { rebuilt.Message },
            After = after,
            Message = after.IsHealthy
                ? "הסרטון קיבל אינדקס חדש ונבדק מחדש — הוא אמור להיפתח ולהתנגן."
                : "האינדקס נכתב, אך הבדיקה החוזרת מצאה בעיות: " + string.Join(" ", after.Issues.Select(i => i.Description)),
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
                    applied.Add($"שוחזרה תחילת הקובץ (חתימת הפתיחה של {format.Name})");
                    restoreHeader = true;
                    break;

                case FileIssueKind.TrailingData when diagnosis.CorrectLength is > 0:
                    bodyLength = diagnosis.CorrectLength.Value;
                    applied.Add($"הוסרו נתונים עודפים — הקובץ קוצר ל-{bodyLength:N0} בתים");
                    break;

                case FileIssueKind.FooterMissing when format?.Footer is { Length: > 0 }:
                    footer = format.Footer;
                    applied.Add($"הושלם סוף הקובץ (חתימת הסיום של {format.Name})");
                    break;

                case FileIssueKind.ExtensionMismatch:
                    applied.Add($"הסיומת תוקנה ל-.{diagnosis.SuggestedExtension}");
                    break;
            }
        }

        // ארכיון ומסמך PDF נבנים מחדש בזיכרון — האבחון בודק אותם רק עד MaxArchive.
        bool rebuildArchive = diagnosis.Issues.Any(i => i.Fixable &&
            i.Kind is FileIssueKind.ArchiveDirectoryDamaged or FileIssueKind.ArchiveEntriesDamaged);
        bool rebuildPdf = diagnosis.Issues.Any(i => i.Fixable && i.Kind == FileIssueKind.PdfStructureDamaged);

        // --- כתיבת העותק המתוקן ---
        string extension = diagnosis.SuggestedExtension ?? System.IO.Path.GetExtension(path).TrimStart('.');
        string stem = System.IO.Path.GetFileNameWithoutExtension(path);

        Directory.CreateDirectory(outputFolder);

        // התמונה המוקטנת נשמרת לצד התיקון — ובתמונה שאין בה דבר אחר לתקן, במקומו.
        string? previewPath = null;
        if (diagnosis.Preview is { } preview && diagnosis.Issues.Any(i => i.Fixable && i.Kind == FileIssueKind.PreviewAvailable))
        {
            previewPath = UniquePath(outputFolder, $"{stem} (תמונה מוקטנת)", "jpg");
            File.WriteAllBytes(previewPath, preview.Data);
            applied.Add($"נשמרה התמונה המוקטנת שבתוך הקובץ ({preview.Width}×{preview.Height}) כקובץ נפרד");

            if (!diagnosis.Issues.Any(i => i.Fixable && i.Kind is not (FileIssueKind.PreviewAvailable or FileIssueKind.TextRecoverable)))
            {
                var previewCheck = Diagnose(previewPath);
                return new FileRepairResult
                {
                    Succeeded = previewCheck.IsHealthy,
                    OutputPath = previewPath,
                    PreviewPath = previewPath,
                    Applied = applied,
                    After = previewCheck,
                    Message = "התמונה עצמה פגומה, ואת החלק החסר בה אי אפשר להשלים. " +
                              $"התמונה המוקטנת שבתוכה ({preview.Width}×{preview.Height}) נשמרה כקובץ נפרד ונבדקה — היא שלמה.",
                };
            }
        }

        // הטקסט של מסמך פגום נשמר לצד התיקון — ובמסמך שאין בו דבר אחר לתקן, במקומו.
        string? textPath = null;
        if (diagnosis.Text is { } text && diagnosis.Issues.Any(i => i.Fixable && i.Kind == FileIssueKind.TextRecoverable))
        {
            textPath = UniquePath(outputFolder, $"{stem} (טקסט)", "txt");
            File.WriteAllText(textPath, text, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            applied.Add($"נשמר הטקסט של המסמך (כ-{TextExtractor.Words(text):N0} מילים) בקובץ טקסט: {System.IO.Path.GetFileName(textPath)}");

            if (!diagnosis.Issues.Any(i => i.Fixable && i.Kind is not (FileIssueKind.TextRecoverable or FileIssueKind.PreviewAvailable)))
                return new FileRepairResult
                {
                    Succeeded = true,
                    OutputPath = textPath,
                    TextPath = textPath,
                    Applied = applied,
                    Message = "המסמך עצמו פגום, ואי אפשר לתקן אותו כך שייפתח. הטקסט שבו נשמר כקובץ טקסט פשוט — " +
                              "אפשר לפתוח אותו בפנקס הרשימות או להעתיק ממנו לוורד.",
                };
        }

        string output = UniquePath(outputFolder, $"{stem} (תוקן)", extension);

        // הגנה: לעולם לא לדרוס את הקובץ המקורי.
        if (string.Equals(System.IO.Path.GetFullPath(output), System.IO.Path.GetFullPath(path),
                StringComparison.OrdinalIgnoreCase))
            return new FileRepairResult { Message = "נתיב היעד זהה לקובץ המקורי. התיקון בוטל." };

        try
        {
            if (rebuildArchive)
                applied.Add(WriteRebuiltArchive(path, output, format, restoreHeader));
            else if (rebuildPdf)
                applied.Add(WriteRebuiltPdf(path, output, format, restoreHeader));
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

        // התמונה המוקטנת כבר נשמרה; בעותק היא מופיעה שוב כ"ניתן לשמור", וזה אינו כישלון התיקון.
        bool fixedAll = after.Issues.All(i => !i.Fixable || i.Kind is FileIssueKind.PreviewAvailable or FileIssueKind.TextRecoverable);

        return new FileRepairResult
        {
            Succeeded = fixedAll,
            OutputPath = output,
            PreviewPath = previewPath,
            TextPath = textPath,
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
    /// <summary>מסמך PDF עם טבלת מיקומים חדשה, מהנתונים כפי שהם אחרי שחזור החתימה.</summary>
    private static string WriteRebuiltPdf(string path, string output, FileSignature? format, bool restoreHeader)
    {
        byte[] data = File.ReadAllBytes(path);
        if (restoreHeader && format is not null) RestoreHeader(data, format);

        var pdf = PdfRebuilder.Analyze(data);
        using (var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write))
            target.Write(PdfRebuilder.Rebuild(data, pdf));

        return $"נבנתה טבלת מיקומים חדשה ל-{pdf.Objects.Count:N0} חלקי המסמך";
    }

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

        /// <summary>האם הטווח ריק: אפסים, ואם allowWhitespace — גם רווחים ושורות חדשות.</summary>
        internal bool IsZeroRange(long from, long to, bool allowWhitespace = false)
        {
            byte[] chunk = new byte[64 * 1024];

            for (long at = from; at < to; at += chunk.Length)
            {
                int want = (int)Math.Min(chunk.Length, to - at);
                int read = ReadRaw(at, chunk.AsSpan(0, want));
                for (int i = 0; i < read; i++)
                    if (chunk[i] != 0 && !(allowWhitespace && chunk[i] is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t'))
                        return false;
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
