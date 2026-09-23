using System.Diagnostics;
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
}

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
            throw new ArgumentException("לא נבחרה תיקיית יעד לשחזור.");

        string full;
        try
        {
            full = Path.GetFullPath(targetFolder);
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"נתיב היעד אינו תקין: {ex.Message}");
        }

        int targetDisk = DiskEnumerator.GetDiskNumberForPath(full);

        // זהו הכלל החשוב ביותר בשחזור מידע: כתיבה לדיסק המקור
        // דורסת בדיוק את האשכולות שטרם שוחזרו.
        if (targetDisk >= 0 && targetDisk == sourceDiskNumber)
            throw new InvalidOperationException(
                "לא ניתן לשחזר לאותו דיסק שממנו משחזרים. " +
                "כתיבה לדיסק המקור תדרוס את הקבצים שטרם שוחזרו ותמנע את שחזורם. " +
                "בחרו כונן אחר, למשל התקן USB חיצוני.");

        var root = new DriveInfo(Path.GetPathRoot(full)!);
        if (!root.IsReady)
            throw new InvalidOperationException("כונן היעד אינו זמין.");
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
        var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int succeeded = 0, skipped = 0, done = 0, empty = 0;
        long bytesWritten = 0;

        // המחיצה נפתחת פעם אחת לכל פעולת השחזור, בגישה אקראית:
        // הקבצים מפוזרים על הדיסק ואין יתרון לקריאה רציפה.
        using var reader = VolumeReader.TryOpen(
            diskNumber, partitionOffset, partitionSize, sectorSize, sequential: false);

        if (reader is null)
            throw new IOException("לא ניתן לפתוח את דיסק המקור לקריאה. ודאו שהתוכנה פועלת בהרשאות מנהל.");

        using var volume = VolumeScanner.Open(reader, fileSystem, sectorSize);
        if (volume is null)
            throw new InvalidDataException("לא ניתן לקרוא את מבנה המחיצה לצורך השחזור.");

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

            if (!file.HasContent)
            {
                skipped++;
                failures.Add(new RecoveryFailure(file.Name,
                    "לא נמצא מידע על מיקום תוכן הקובץ — רשומת המטא-דאטה שלו נדרסה."));
                continue;
            }

            if (options.SkipUnrecoverable && !file.IsWorthRecovering)
            {
                skipped++;
                if (file.Content == ContentCheck.Empty) empty++;
                continue;
            }

            try
            {
                string destination = ResolveDestination(file, options, usedPaths);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                var outcome = WriteFile(volume, file, destination, token);

                // קובץ שכל תוכנו אפסים אינו שחזור אלא אשליה: הגודל נכון,
                // התוכן אינו קיים. עדיף למחוק אותו ולומר זאת מפורשות.
                if (!outcome.SawContent && file.Size > 0)
                {
                    TryDelete(destination);
                    empty++;
                    failures.Add(new RecoveryFailure(file.Name,
                        "תוכן הקובץ כבר אינו קיים על הדיסק — אזור הנתונים שלו מכיל אפסים בלבד."));
                    continue;
                }

                bytesWritten += outcome.BytesWritten;

                if (outcome.UnreadableBytes > 0 || outcome.BytesWritten < file.Size)
                    partial.Add(file.Name);

                ApplyTimestamps(destination, file);
                succeeded++;
            }
            catch (Exception ex)
            {
                failures.Add(new RecoveryFailure(file.Name, ex.Message));
            }
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
        };
    }

    private static CopyOutcome WriteFile(
        IClusterVolume volume, RecoveredFile file, string destination, CancellationToken token)
    {
        using var output = new FileStream(
            destination, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 1 << 20, FileOptions.SequentialScan);

        if (file.ResidentData is not null)
        {
            // תוכן קטן ששמור בתוך רשומת ה-MFT עצמה.
            int length = (int)Math.Min(file.ResidentData.Length, file.Size);
            output.Write(file.ResidentData, 0, length);

            bool hasContent = file.ResidentData.Take(length).Any(b => b != 0);
            return new CopyOutcome(length, 0, hasContent);
        }

        var stream = new ClusterStream(
            volume, file.Extents, file.Size,
            file.IsCompressed ? file.CompressionUnitClusters : 0);

        return stream.CopyTo(output, token);
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
        if (string.IsNullOrEmpty(name)) name = $"קובץ_{file.Id}";

        string folder = options.TargetFolder;

        if (options.PreservePaths && !string.IsNullOrEmpty(file.Path))
        {
            var segments = file.Path
                .Split('\\', StringSplitOptions.RemoveEmptyEntries)
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

        throw new IOException("לא נמצא שם פנוי לקובץ בתיקיית היעד.");
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
