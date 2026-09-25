using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RAF.Core.Carving;
using RAF.Core.Disks;
using RAF.Core.FileSystems;
using RAF.Core.FileSystems.Ntfs;
using RAF.Core.Model;
using RAF.Core.Native;

namespace RAF.Core.Recovery;

/// <summary>הגדרות פעולת השחזור.</summary>
public sealed class RecoveryOptions
{
    /// <summary>תיקיית היעד שאליה ייכתבו הקבצים המשוחזרים.</summary>
    public string TargetFolder { get; init; } = "";

    /// <summary>האם לשחזר את מבנה התיקיות המקורי תחת תיקיית היעד.</summary>
    public bool PreservePaths { get; init; } = true;

    /// <summary>האם לדלג על קבצים שההערכה היא שתוכנם נדרס.</summary>
    public bool SkipUnrecoverable { get; init; } = true;

    /// <summary>תיאור המקור לקובץ ההסבר — המחיצה והכונן שמהם שוחזר.</summary>
    public string Source { get; init; } = "";
}

/// <summary>דיווח התקדמות שוטף של פעולת השחזור.</summary>
public sealed class RecoveryProgress
{
    public string CurrentFile { get; init; } = "";
    public int FilesDone { get; init; }
    public int FilesTotal { get; init; }
    public long BytesWritten { get; init; }
    public TimeSpan Elapsed { get; init; }
    public double? Percent => FilesTotal > 0 ? FilesDone * 100.0 / FilesTotal : null;
}

/// <summary>כישלון בשחזור קובץ יחיד, עם הסיבה בעברית.</summary>
public sealed record RecoveryFailure(string FileName, string Reason);

/// <summary>סיכום פעולת השחזור.</summary>
public sealed class RecoveryReport
{
    public int Succeeded { get; init; }
    public int Skipped { get; init; }
    public long BytesWritten { get; init; }
    public TimeSpan Duration { get; init; }
    public bool Cancelled { get; init; }
    public List<RecoveryFailure> Failures { get; init; } = new();

    /// <summary>קבצים שנכתבו אך ייתכן שתוכנם אינו שלם.</summary>
    public List<string> PartialFiles { get; init; } = new();

    /// <summary>קבצים שתוכנם כבר אינו קיים על הדיסק ולכן לא נכתבו.</summary>
    public int EmptyFiles { get; init; }

    /// <summary>שורה לכל קובץ — גם לאלה שנכשלו או דולגו. נכתבת גם לקובץ הדוח.</summary>
    public List<RecoveryEntry> Entries { get; init; } = new();

    /// <summary>קובץ הדוח (CSV) בתיקיית היעד, או null אם לא נכתב.</summary>
    public string? ReportPath { get; init; }

    /// <summary>התיקייה שאליה הועברו הקבצים ששוחזרו חלקית, אם היו כאלה.</summary>
    public string? PartialFolder { get; init; }

    /// <summary>תמונות מוקטנות שלמות שנשמרו מתוך תמונות שחזרו פגומות.</summary>
    public int PreviewsSaved { get; init; }
}

/// <summary>מה עלה בגורלו של קובץ אחד בשחזור.</summary>
public enum RecoveryStatus { Recovered, Partial, Empty, Failed, Skipped }

/// <summary>שורה בדוח השחזור.</summary>
public sealed record RecoveryEntry(
    string OriginalPath, string? Destination, long Size, RecoveryQuality Quality,
    RecoveryStatus Status, string Reason, string? Sha256, string Location = "");

/// <summary>
/// כתיבת הקבצים המשוחזרים לתיקיית היעד.
/// הכלל המרכזי: לעולם לא לכתוב לדיסק שממנו משחזרים.
/// </summary>
public static class RecoveryWriter
{
    /// <summary>
    /// בדיקה שתיקיית היעד חוקית. זורקת חריגה עם הסבר בעברית אם לא.
    /// נקראת גם מהממשק לפני תחילת השחזור, כדי להזהיר מוקדם.
    /// </summary>
    public static void ValidateTarget(string targetFolder, int sourceDiskNumber)
    {
        if (string.IsNullOrWhiteSpace(targetFolder))
            throw new ArgumentException(L.T("לא נבחרה תיקיית יעד לשחזור."));

        string full;
        try
        {
            full = Path.GetFullPath(targetFolder);
        }
        catch (Exception ex)
        {
            throw new ArgumentException(L.T("נתיב היעד אינו תקין: {0}", ex.Message));
        }

        int targetDisk = DiskEnumerator.GetDiskNumberForPath(full);

        sourceDiskNumber = DiskEnumerator.PhysicalDiskOf(sourceDiskNumber);
        if (sourceDiskNumber < 0)
            throw new InvalidOperationException(
                L.T("לא ניתן לוודא על איזה דיסק יושב הכונן שממנו משחזרים, ולכן גם לא שהיעד אינו עליו. " +
                "ודאו שהכונן עדיין מחובר ופתוח, ונסו שוב."));

        // זהו הכלל החשוב ביותר בשחזור מידע: כתיבה לדיסק המקור
        // דורסת בדיוק את האשכולות שטרם שוחזרו.
        if (targetDisk >= 0 && targetDisk == sourceDiskNumber)
            throw new InvalidOperationException(
                L.T("לא ניתן לשחזר לאותו דיסק שממנו משחזרים. " +
                "כתיבה לדיסק המקור תדרוס את הקבצים שטרם שוחזרו ותמנע את שחזורם. " +
                "בחרו כונן אחר, למשל התקן USB חיצוני."));

        var root = new DriveInfo(Path.GetPathRoot(full)!);
        if (!root.IsReady)
            throw new InvalidOperationException(L.T("כונן היעד אינו זמין."));
    }

    /// <summary>שחזור רשימת קבצים לתיקיית היעד.</summary>
    public static Task<RecoveryReport> RecoverAsync(
        FileSystemKind fileSystem,
        int diskNumber, long partitionOffset, long partitionSize, int sectorSize,
        IReadOnlyList<RecoveredFile> files, RecoveryOptions options,
        IProgress<RecoveryProgress>? progress, CancellationToken token)
        => Task.Run(() => Run(
            fileSystem, diskNumber, partitionOffset, partitionSize, sectorSize,
            files, options, progress, token), token);

    private static RecoveryReport Run(
        FileSystemKind fileSystem,
        int diskNumber, long partitionOffset, long partitionSize, int sectorSize,
        IReadOnlyList<RecoveredFile> files, RecoveryOptions options,
        IProgress<RecoveryProgress>? progress, CancellationToken token)
    {
        ValidateTarget(options.TargetFolder, diskNumber);

        var clock = Stopwatch.StartNew();
        var failures = new List<RecoveryFailure>();
        var partial = new List<string>();
        var entries = new List<RecoveryEntry>();
        var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int succeeded = 0, skipped = 0, done = 0, empty = 0, previews = 0;
        long bytesWritten = 0;
        string partialFolder = Path.Combine(options.TargetFolder, PartialFolderName);

        // המחיצה נפתחת פעם אחת לכל פעולת השחזור, בגישה אקראית:
        // הקבצים מפוזרים על הדיסק ואין יתרון לקריאה רציפה.
        using var reader = VolumeReader.TryOpen(
            diskNumber, partitionOffset, partitionSize, sectorSize, sequential: false);

        if (reader is null)
            throw new IOException(Native.RawDevice.OpenFailure(L.T("דיסק המקור")));

        using var volume = VolumeScanner.Open(reader, fileSystem, sectorSize);
        if (volume is null)
            throw new InvalidDataException(L.T("לא ניתן לקרוא את מבנה המחיצה לצורך השחזור."));

        Directory.CreateDirectory(options.TargetFolder);

        foreach (var file in files)
        {
            if (token.IsCancellationRequested) break;

            done++;
            progress?.Report(new RecoveryProgress
            {
                CurrentFile = file.Name,
                FilesDone = done,
                FilesTotal = files.Count,
                BytesWritten = bytesWritten,
                Elapsed = clock.Elapsed,
            });

            if (file.IsDirectory) { skipped++; continue; }

            string original = string.IsNullOrEmpty(file.Path) ? file.Name : file.Path + "\\" + file.Name;
            void Log(RecoveryStatus status, string reason, string? destination = null, string? sha = null)
                => entries.Add(new RecoveryEntry(original, destination, file.Size, file.Quality, status, reason, sha,
                       Location(file)));

            if (!file.HasContent)
            {
                skipped++;
                string reason = L.T("לא נמצא מידע על מיקום תוכן הקובץ — רשומת המטא-דאטה שלו נדרסה.");
                failures.Add(new RecoveryFailure(file.Name, reason));
                Log(RecoveryStatus.Skipped, reason);
                continue;
            }

            if (options.SkipUnrecoverable && !file.IsWorthRecovering)
            {
                skipped++;
                if (file.Content == ContentCheck.Empty)
                {
                    empty++;
                    Log(RecoveryStatus.Empty, L.T("התוכן נבדק בזמן הסריקה ונמצא ריק — הקובץ לא נכתב."));
                }
                else
                {
                    Log(RecoveryStatus.Skipped, L.T("הקובץ סומן כבלתי ניתן לשחזור."));
                }
                continue;
            }

            string? destination = null;
            try
            {
                destination = ResolveDestination(file, options, usedPaths);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                var (outcome, sha) = WriteFile(volume, file, destination, token);

                // קובץ שכל תוכנו אפסים אינו שחזור אלא אשליה: הגודל נכון,
                // התוכן אינו קיים. עדיף למחוק אותו ולומר זאת מפורשות.
                if (!outcome.SawContent && file.Size > 0)
                {
                    TryDelete(destination);
                    empty++;
                    string reason = L.T("תוכן הקובץ כבר אינו קיים על הדיסק — אזור הנתונים שלו מכיל אפסים בלבד.");
                    failures.Add(new RecoveryFailure(file.Name, reason));
                    Log(RecoveryStatus.Empty, reason);
                    continue;
                }

                bytesWritten += outcome.BytesWritten;

                // קובץ חלקי עובר לתיקייה נפרדת, כדי שאפשר יהיה לדעת לפי המיקום
                // בלבד על אילו קבצים לסמוך — בלי לפתוח את הדוח.
                bool isPartial = outcome.UnreadableBytes > 0 || outcome.BytesWritten < file.Size;
                if (isPartial)
                {
                    destination = MoveUnder(destination, options.TargetFolder, partialFolder);
                    partial.Add(file.Name);
                    Log(RecoveryStatus.Partial,
                        outcome.UnreadableBytes > 0
                            ? L.T("{0} בתים לא נקראו מהדיסק ונכתבו כאפסים.", outcome.UnreadableBytes.ToString("N0"))
                            : L.T("נכתבו {0} מתוך {1} בתים.", outcome.BytesWritten.ToString("N0"), file.Size.ToString("N0")),
                        destination, sha);
                }
                else
                {
                    Log(RecoveryStatus.Recovered, "", destination, sha);
                }

                ApplyTimestamps(destination, file);
                succeeded++;

                // תמונה שחזרה חלקית או פגומה: התמונה המוקטנת שבתוכה נשמרת לצדה.
                if (SavePreviewIfDamaged(file, destination, isPartial) is { } preview)
                {
                    previews++;
                    Log(RecoveryStatus.Recovered,
                        L.T("תמונה מוקטנת שלמה ({0}×{1}) מתוך התמונה הפגומה", preview.Width, preview.Height), preview.Path);
                }
            }
            catch (Exception ex)
            {
                failures.Add(new RecoveryFailure(file.Name, ex.Message));
                Log(RecoveryStatus.Failed, ex.Message, destination);
            }
        }

        // הדוח נכתב גם כשהשחזור נעצר באמצע — דווקא אז חשוב לדעת מה כבר נכתב.
        string? reportPath = null;
        try
        {
            reportPath = WriteReport(options.TargetFolder, entries);
        }
        catch
        {
            // הדוח הוא תוספת; כישלון בכתיבתו אינו מבטל שחזור שהצליח.
        }

        try
        {
            WriteReadme(options, entries, reportPath, clock.Elapsed, token.IsCancellationRequested);
        }
        catch
        {
            // גם קובץ ההסבר הוא תוספת בלבד.
        }

        return new RecoveryReport
        {
            Succeeded = succeeded,
            Skipped = skipped,
            BytesWritten = bytesWritten,
            Duration = clock.Elapsed,
            Cancelled = token.IsCancellationRequested,
            Failures = failures,
            PartialFiles = partial,
            EmptyFiles = empty,
            Entries = entries,
            ReportPath = reportPath,
            PartialFolder = partial.Count > 0 ? partialFolder : null,
            PreviewsSaved = previews,
        };
    }

    /// <summary>שם התיקייה לקבצים ששוחזרו חלקית, בתוך תיקיית היעד (בשפת הממשק).</summary>
    public static string PartialFolderName => L.T("_חלקיים");

    /// <summary>
    /// JPEG שחזר חלקי או מדורג "פגום חלקית": אם הוא אכן אינו מתפענח עד סופו,
    /// התמונה המוקטנת השלמה שבתוכו נשמרת לצדו — "שם (תמונה מוקטנת).jpg".
    /// על כרטיס אמיתי שחולץ: ב-267 מתוך 269 תמונות פגומות נמצאה תמונה כזו.
    /// </summary>
    private static (string Path, int Width, int Height)? SavePreviewIfDamaged(RecoveredFile file, string destination, bool partial)
    {
        if (file.Extension is not ("jpg" or "jpeg") || (!partial && file.Quality < RecoveryQuality.Poor)) return null;

        try
        {
            var info = new FileInfo(destination);
            if (info.Length > 64L * 1024 * 1024) return null;

            byte[] data = File.ReadAllBytes(destination);
            if (JpegDecoder.Check(JpegBytes.Of(data)).Verdict != JpegVerdict.Corrupt) return null;
            if (JpegPreviews.Best(data) is not { } preview) return null;

            string folder = Path.GetDirectoryName(destination)!;
            string stem = Path.GetFileNameWithoutExtension(destination);
            string path = Path.Combine(folder, L.T("{0} (תמונה מוקטנת).jpg", stem));
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, L.T("{0} (תמונה מוקטנת {1}).jpg", stem, i));

            File.WriteAllBytes(path, preview.Data);
            return (path, preview.Width, preview.Height);
        }
        catch
        {
            return null;                                           // תוספת בלבד — לעולם לא תכשיל שחזור
        }
    }

    /// <summary>
    /// העברת קובץ מתחת לתיקייה אחרת, עם אותו מבנה תיקיות יחסי —
    /// "יעד\תמונות\א.jpg" הופך ל"יעד\_חלקיים\תמונות\א.jpg".
    /// </summary>
    private static string MoveUnder(string file, string root, string newRoot)
    {
        string relative = Path.GetRelativePath(root, file);
        string target = Path.Combine(newRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        string stem = Path.Combine(Path.GetDirectoryName(target)!, Path.GetFileNameWithoutExtension(target));
        string extension = Path.GetExtension(target);
        for (int i = 2; File.Exists(target); i++) target = $"{stem} ({i}){extension}";

        File.Move(file, target);
        return target;
    }

    /// <summary>
    /// דוח CSV בתיקיית היעד. UTF-8 עם BOM — בלעדיו Excel מציג עברית כג'יבריש.
    /// כל דוח בשם משלו, כדי ששחזור שני לאותה תיקייה לא ימחק את הדוח הראשון.
    /// </summary>
    private static string WriteReport(string folder, List<RecoveryEntry> entries)
    {
        string path = Path.Combine(folder, $"RAF-report-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(folder, $"RAF-report-{DateTime.Now:yyyyMMdd-HHmmss}-{i}.csv");

        var csv = new StringBuilder();
        csv.AppendLine(L.T("נתיב מקורי,נכתב אל,גודל (בתים),איכות,תוצאה,פירוט,SHA-256,מיקום בכונן"));
        foreach (var e in entries)
        {
            csv.AppendLine(string.Join(",",
                Csv(e.OriginalPath), Csv(e.Destination is null ? "" : Path.GetRelativePath(folder, e.Destination)),
                e.Size.ToString(CultureInfo.InvariantCulture), Csv(QualityLabel(e.Quality)),
                Csv(StatusLabel(e.Status)), Csv(e.Reason), e.Sha256 ?? "", Csv(e.Location)));
        }

        File.WriteAllText(path, csv.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    /// <summary>שם קובץ ההסבר בתיקיית היעד (בשפת הממשק).</summary>
    public static string ReadmeName => L.T("קרא אותי.txt");

    /// <summary>
    /// קובץ הסבר בעברית בתיקיית היעד — למי שפותח אותה אחר כך, או מקבל אותה ממישהו
    /// אחר, ולא יודע מה זו "_חלקיים" או הדוח. כל שחזור מוסיף פרק משלו לראש הקובץ,
    /// כך ששחזור שני לאותה תיקייה אינו מוחק את מה שנכתב על הראשון.
    /// </summary>
    internal static void WriteReadme(
        RecoveryOptions options, List<RecoveryEntry> entries, string? reportPath, TimeSpan duration, bool cancelled)
    {
        var recovered = entries.Where(e => e.Status is RecoveryStatus.Recovered or RecoveryStatus.Partial
                                           && e.Destination is not null).ToList();
        if (recovered.Count == 0) return;

        // התמונות המוקטנות נרשמות בדוח כשורה נוספת לאותו קובץ — לא נספרות כקובץ משוחזר.
        static bool IsPreview(RecoveryEntry e)
            => Path.GetFileName(e.Destination!).Contains(L.T("(תמונה מוקטנת"), StringComparison.Ordinal);
        int previews = recovered.Count(IsPreview);
        recovered.RemoveAll(IsPreview);

        int partial = recovered.Count(e => e.Status == RecoveryStatus.Partial);
        int notWritten = entries.Count(e => e.Status is RecoveryStatus.Empty or RecoveryStatus.Failed or RecoveryStatus.Skipped);
        long bytes = recovered.Sum(e => e.Size);

        var t = new StringBuilder();
        t.AppendLine(L.T("שחזור מ-{0} בשעה {1}", DateTime.Now.ToString("dd.MM.yyyy"), DateTime.Now.ToString("HH:mm")));
        t.AppendLine(new string('-', 40));
        if (options.Source.Length > 0) t.AppendLine(L.T("המקור: {0}", options.Source));
        t.AppendLine((recovered.Count == 1 ? L.T("שוחזר קובץ אחד") : L.T("שוחזרו {0} קבצים", recovered.Count.ToString("N0")))
                     + $" ({FormatSize(bytes)})"
                     + (partial == 0 ? "." : recovered.Count == 1 ? L.T(", חלקית.") : L.T(", מהם {0} חלקית.", partial.ToString("N0"))));
        if (notWritten > 0)
            t.AppendLine(notWritten == 1 ? L.T("קובץ אחד לא נכתב — הסיבה בדוח.")
                                         : L.T("{0} קבצים לא נכתבו — הסיבה לכל אחד מהם בדוח.", notWritten.ToString("N0")));
        if (cancelled) t.AppendLine(L.T("השחזור נעצר באמצע, ולכן לא כל הקבצים שנבחרו נמצאים כאן."));
        t.AppendLine(L.T("משך השחזור: {0} דקות ו-{1} שניות.", (int)duration.TotalMinutes, duration.Seconds));
        t.AppendLine();

        t.AppendLine(L.T("מה יש בתיקייה"));
        t.AppendLine(options.PreservePaths
            ? L.T("• הקבצים ששוחזרו במלואם — באותן תיקיות שבהן היו במקור.")
            : L.T("• הקבצים ששוחזרו במלואם — כולם ישירות בתיקייה הזו, בלי מבנה התיקיות המקורי."));
        t.AppendLine(L.T("  קבצים שנמצאו בסריקה מתקדמת מסודרים בתיקיות לפי הסוג שלהם, ובלי השמות המקוריים —"));
        t.AppendLine(L.T("  הסריקה הזו מוצאת קבצים לפי התוכן שלהם, והשם לא נשמר בתוכן."));
        if (partial > 0)
        {
            t.AppendLine(L.T("• {0} — קבצים שחלק מהם לא נקרא מהכונן. החלק החסר נכתב כאפסים:", PartialFolderName));
            t.AppendLine(L.T("  בתמונה זה נראה כפס אפור או כתמונה שנקטעת, בסרטון — כקפיצה או כסוף מוקדם."));
            t.AppendLine(L.T("  לפעמים הם נפתחים בכל זאת. אל תמחקו אותם לפני שבדקתם."));
        }
        if (previews > 0)
            t.AppendLine(L.T("• קבצים ששמם מסתיים ב\"(תמונה מוקטנת)\" — גרסה קטנה ושלמה של תמונה פגומה, שנמצאה בתוך התמונה עצמה."));
        if (reportPath is not null)
        {
            t.AppendLine(L.T("• {0} — דוח מלא: לכל קובץ, מאיפה הגיע, לאן נכתב ומה קרה לו.", Path.GetFileName(reportPath)));
            t.AppendLine(L.T("  נפתח באקסל. העמודה SHA-256 היא \"טביעת אצבע\" של הקובץ — לבדיקה שהוא לא השתנה מאז."));
        }
        t.AppendLine();

        t.AppendLine(L.T("קובץ לא נפתח?"));
        t.AppendLine(L.T("1. נסו לפתוח אותו בתוכנה אחרת — לפעמים תוכנה אחת מוותרת ואחרת מצליחה."));
        t.AppendLine(L.T("2. בתוכנת השחזור, במסך הכוננים: \"תיקון קבצים שלא נפתחים\". אפשר פשוט לגרור את הקבצים לחלון."));
        t.AppendLine(L.T("   שם יש גם תיקון לסרטון שלא מתנגן, בעזרת סרטון תקין שצולם באותו מכשיר."));
        t.AppendLine(L.T("3. עדיין חסרים קבצים? נסו סוג סריקה אחר — עמוקה או מתקדמת."));
        t.AppendLine();
        t.AppendLine(L.T("חשוב: עד שתסיימו לשחזר, אל תשמרו שום דבר על הכונן שממנו שחזרתם —"));
        t.AppendLine(L.T("כל קובץ חדש שנכתב אליו עלול לדרוס קבצים שעוד אפשר להציל."));
        t.AppendLine();
        t.AppendLine();

        string title = L.T("שחזור מתקדם חינם — הסבר על התיקייה הזו");
        string path = Path.Combine(options.TargetFolder, ReadmeName);
        string previous = Previous(path);
        // בתיקייה כבר יש קובץ בשם הזה שאינו שלנו — קובץ ששוחזר. לא נוגעים בו: ההסבר בשם אחר.
        if (previous.Length > 0 && !previous.StartsWith(title, StringComparison.Ordinal))
        {
            path = Path.Combine(options.TargetFolder, L.T("קרא אותי - שחזור מתקדם חינם.txt"));
            previous = Previous(path);
        }
        if (previous.StartsWith(title, StringComparison.Ordinal))
            previous = previous[title.Length..].TrimStart('\r', '\n', '=');
        else
            previous = "";   // לעולם לא מצרפים תוכן של קובץ שאינו שלנו
        previous = previous.TrimStart('\r', '\n');

        string header = title + Environment.NewLine + new string('=', 40) + Environment.NewLine + Environment.NewLine;
        File.WriteAllText(path, header + t + previous, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        static string Previous(string file)
        {
            try { return File.Exists(file) ? File.ReadAllText(file, Encoding.UTF8) : ""; }
            catch (IOException) { return ""; }
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { L.T("בתים"), "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? L.T("{0} בתים", bytes.ToString("N0")) : $"{value:0.#} {units[unit]}";
    }

    /// <summary>
    /// שדה CSV: במירכאות כשיש בו פסיק, מירכאות או שורה חדשה. שדה שמתחיל בתו
    /// שאקסל מפרש כנוסחה (=, +, -, @) מקבל גרש לפניו — שם קובץ אינו נוסחה.
    /// </summary>
    private static string Csv(string value)
    {
        if (value.Length > 0 && "=+-@".Contains(value[0])) value = "'" + value;
        return value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    /// <summary>
    /// קובץ מסריקה מתקדמת: הסקטור שבו התחיל. השם שלו נבנה מתוכנו, ולכן זה
    /// המזהה היחיד שקושר אותו למקום על הכונן — למשל לבדיקה חוזרת בעורך הקס.
    /// </summary>
    private static string Location(RecoveredFile file)
        => file.Source == DiscoverySource.Carving && file.Extents.Count > 0
            ? L.T("סקטור {0}", (file.Extents[0].StartCluster).ToString("N0"))
            : "";

    private static string StatusLabel(RecoveryStatus s) => s switch
    {
        RecoveryStatus.Recovered => L.T("שוחזר"),
        RecoveryStatus.Partial => L.T("שוחזר חלקית"),
        RecoveryStatus.Empty => L.T("לא נכתב — ריק"),
        RecoveryStatus.Skipped => L.T("דולג"),
        _ => L.T("נכשל"),
    };

    private static string QualityLabel(RecoveryQuality q) => q switch
    {
        RecoveryQuality.Excellent => L.T("מצוין"),
        RecoveryQuality.Good => L.T("טוב"),
        RecoveryQuality.Poor => L.T("חלש"),
        _ => L.T("לא ניתן לשחזור"),
    };

    /// <summary>כתיבת הקובץ, וחישוב SHA-256 של מה שנכתב — באותו מעבר, בלי לקרוא את הקובץ שוב.</summary>
    private static (CopyOutcome Outcome, string Sha256) WriteFile(
        IClusterVolume volume, RecoveredFile file, string destination, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        CopyOutcome outcome;

        using (var sink = new FileStream(
            destination, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 1 << 20, FileOptions.SequentialScan))
        using (var output = new HashingStream(sink, hash))
        {
            if (file.ResidentData is not null)
            {
                // תוכן קטן ששמור בתוך רשומת ה-MFT עצמה.
                int length = (int)Math.Min(file.ResidentData.Length, file.Size);
                output.Write(file.ResidentData, 0, length);

                bool hasContent = file.ResidentData.Take(length).Any(b => b != 0);
                outcome = new CopyOutcome(length, 0, hasContent);
            }
            else
            {
                var stream = new ClusterStream(
                    volume, file.Extents, file.Size,
                    file.IsCompressed ? file.CompressionUnitClusters : 0);

                outcome = stream.CopyTo(output, token);
            }
        }

        return (outcome, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* מחיקת ניקוי בלבד */ }
    }

    /// <summary>
    /// בניית נתיב היעד לקובץ, כולל ניקוי תווים אסורים ומניעת דריסה
    /// של קובץ שכבר שוחזר בעל אותו שם.
    /// </summary>
    private static string ResolveDestination(
        RecoveredFile file, RecoveryOptions options, HashSet<string> used)
    {
        string name = SanitizeSegment(file.Name);
        if (string.IsNullOrEmpty(name)) name = L.T("קובץ_{0}", file.Id);

        string folder = options.TargetFolder;

        if (options.PreservePaths && !string.IsNullOrEmpty(file.Path))
        {
            // בסריקה מתקדמת התיקיות הן שמות סוגים ("מסמך PDF") — נכתבות בשפת הממשק.
            // שם תיקייה אמיתי ממערכת הקבצים נשאר כמו שהוא.
            bool typeFolders = file.Source == DiscoverySource.Carving;
            var segments = file.Path
                .Split('\\', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => typeFolders ? L.T(s) : s)
                .Select(SanitizeSegment)
                .Where(s => s.Length > 0);

            folder = Path.Combine(new[] { folder }.Concat(segments).ToArray());
        }

        string candidate = Path.Combine(folder, name);

        // התנגשות שמות: מוסיפים מונה במקום לדרוס קובץ שכבר שוחזר.
        if (used.Add(candidate) && !File.Exists(candidate)) return candidate;

        string stem = Path.GetFileNameWithoutExtension(name);
        string extension = Path.GetExtension(name);

        for (int i = 2; i < 10000; i++)
        {
            candidate = Path.Combine(folder, $"{stem} ({i}){extension}");
            if (used.Add(candidate) && !File.Exists(candidate)) return candidate;
        }

        throw new IOException(L.T("לא נמצא שם פנוי לקובץ בתיקיית היעד."));
    }

    /// <summary>
    /// ניקוי מקטע נתיב מתווים שאינם חוקיים ב-Windows.
    /// שמות קבצים שנקראו מדיסק פגום עלולים להכיל כל רצף בתים.
    /// </summary>
    private static string SanitizeSegment(string segment)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(segment.Select(c => invalid.Contains(c) ? '_' : c).ToArray());

        cleaned = cleaned.Trim().TrimEnd('.');

        // שמות שמורים ב-Windows אינם ניתנים ליצירה גם עם סיומת.
        string[] reserved =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        string stem = Path.GetFileNameWithoutExtension(cleaned);
        if (reserved.Contains(stem, StringComparer.OrdinalIgnoreCase))
            cleaned = "_" + cleaned;

        // נתיבים ארוכים מדי נחתכים כדי למנוע כישלון כתיבה.
        return cleaned.Length > 200 ? cleaned[..200] : cleaned;
    }

    /// <summary>שחזור חותמות הזמן המקוריות על הקובץ שנכתב.</summary>
    private static void ApplyTimestamps(string path, RecoveredFile file)
    {
        try
        {
            if (file.Created.HasValue) File.SetCreationTime(path, file.Created.Value);
            if (file.Modified.HasValue) File.SetLastWriteTime(path, file.Modified.Value);
            if (file.Accessed.HasValue) File.SetLastAccessTime(path, file.Accessed.Value);
        }
        catch
        {
            // חותמות זמן הן נתון משני; כישלון בהן אינו מכשיל את השחזור.
        }
    }
}
