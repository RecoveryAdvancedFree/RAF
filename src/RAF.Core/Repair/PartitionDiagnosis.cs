using RAF.Core.FileSystems.ExFat;
using RAF.Core.FileSystems.Fat;
using RAF.Core.FileSystems.Ntfs;
using RAF.Core.Model;
using RAF.Core.Native;

namespace RAF.Core.Repair;

/// <summary>מה ניתן לעשות עם מחיצה שמערכת הקבצים שלה אינה נקראת.</summary>
public enum RepairOutlook
{
    /// <summary>המחיצה תקינה ואינה זקוקה לתיקון.</summary>
    Healthy = 0,

    /// <summary>נמצא עותק גיבוי תקין של מגזר האתחול — ניתן לתקן.</summary>
    BackupFound,

    /// <summary>לא נמצא עותק גיבוי. נותר רק שיחזור קבצים.</summary>
    NoBackup,

    /// <summary>לא ניתן היה לקרוא את המחיצה כלל.</summary>
    Unreadable,
}

/// <summary>תוצאת אבחון מחיצה.</summary>
public sealed class PartitionDiagnosisResult
{
    public RepairOutlook Outlook { get; init; }

    /// <summary>מערכת הקבצים שזוהתה מתוך עותק הגיבוי.</summary>
    public FileSystemKind DetectedFileSystem { get; init; }

    /// <summary>היסט עותק הגיבוי בבתים, יחסית לתחילת המחיצה.</summary>
    public long BackupOffset { get; init; }

    /// <summary>היסט מגזר האתחול הראשי, שאליו ייכתב התיקון.</summary>
    public long PrimaryOffset { get; init; }

    /// <summary>מספר הבתים שיוחלפו בתיקון.</summary>
    public int RepairLength { get; init; }

    /// <summary>הסבר בעברית למשתמש.</summary>
    public string Summary { get; init; } = "";

    /// <summary>מה בדיוק ישתנה על הדיסק, אם יבוצע תיקון.</summary>
    public string WhatWillChange { get; init; } = "";

    public bool CanRepair => Outlook == RepairOutlook.BackupFound;
}

/// <summary>
/// אבחון מחיצה שמערכת הקבצים שלה אינה נקראת.
///
/// כל מערכות הקבצים הנתמכות שומרות עותק גיבוי של מגזר האתחול במקום קבוע,
/// כדי לאפשר התאוששות בדיוק מהמצב הזה. האבחון מאתר את העותק, מוודא שהוא
/// מתפענח בהצלחה, ומדווח מה ניתן לעשות — בלי לגעת בדיסק.
///
/// הפעולה כולה היא קריאה בלבד.
/// </summary>
public static class PartitionDiagnosis
{
    /// <summary>
    /// אבחון מחיצה. אינו כותב דבר לדיסק.
    /// </summary>
    public static PartitionDiagnosisResult Diagnose(
        int diskNumber, long partitionOffset, long partitionSize, int sectorSize)
    {
        using var reader = VolumeReader.TryOpen(
            diskNumber, partitionOffset, partitionSize, sectorSize, sequential: false, applyOverlay: false);

        if (reader is null)
        {
            return new PartitionDiagnosisResult
            {
                Outlook = RepairOutlook.Unreadable,
                Summary = "לא ניתן לפתוח את הדיסק לקריאה. ודא שהתוכנה פועלת בהרשאות מנהל.",
            };
        }

        // אם מגזר האתחול הראשי תקין, אין מה לתקן.
        byte[] primary = reader.ReadBlock(0, 512);
        var primaryKind = IdentifyBootSector(primary);

        if (primaryKind != FileSystemKind.Unknown)
        {
            return new PartitionDiagnosisResult
            {
                Outlook = RepairOutlook.Healthy,
                DetectedFileSystem = primaryKind,
                Summary = $"מגזר האתחול של המחיצה תקין ומזהה מערכת קבצים {Name(primaryKind)}. " +
                          "אין צורך בתיקון.",
            };
        }

        // חיפוש עותק גיבוי בכל אחד מהמיקומים שהמפרטים מגדירים.
        foreach (var candidate in BackupLocations(partitionSize, sectorSize))
        {
            byte[] backup = reader.ReadBlock(candidate.Offset, candidate.Length);
            if (backup.Length < 512) continue;

            var kind = IdentifyBootSector(backup);
            if (kind == FileSystemKind.Unknown) continue;

            // אימות אמיתי: העותק חייב להתפענח ולהצביע על מבנה הגיוני.
            if (!Validates(kind, backup, partitionSize, sectorSize)) continue;

            return new PartitionDiagnosisResult
            {
                Outlook = RepairOutlook.BackupFound,
                DetectedFileSystem = kind,
                BackupOffset = candidate.Offset,
                PrimaryOffset = 0,
                RepairLength = candidate.Length,
                Summary =
                    $"מגזר האתחול של המחיצה פגום, אך נמצא עותק גיבוי תקין של {Name(kind)} " +
                    $"בהיסט {candidate.Offset:N0} בתים ({candidate.Description}). " +
                    "העותק נבדק והתפענח בהצלחה.",
                WhatWillChange =
                    $"התיקון יעתיק {candidate.Length:N0} בתים מעותק הגיבוי אל תחילת המחיצה. " +
                    "זו כתיבה לדיסק המקור. התוכנה תשמור תחילה עותק של הסקטורים שיוחלפו, " +
                    "כדי שניתן יהיה לבטל את הפעולה.",
            };
        }

        return new PartitionDiagnosisResult
        {
            Outlook = RepairOutlook.NoBackup,
            Summary =
                "מגזר האתחול של המחיצה פגום, ולא נמצא עותק גיבוי תקין במיקומים המוכרים. " +
                "לא ניתן לתקן את המחיצה, אך עדיין אפשר לשחזר ממנה קבצים בסריקה מתקדמת, " +
                "שאינה תלויה במערכת הקבצים.",
        };
    }

    /// <summary>מיקום אפשרי של עותק גיבוי.</summary>
    private readonly record struct BackupCandidate(long Offset, int Length, string Description);

    /// <summary>
    /// המיקומים שבהם מערכות הקבצים שומרות עותק גיבוי, לפי המפרטים שלהן.
    /// </summary>
    private static IEnumerable<BackupCandidate> BackupLocations(long partitionSize, int sectorSize)
    {
        // NTFS: עותק בסקטור האחרון של המחיצה.
        if (partitionSize > sectorSize)
            yield return new BackupCandidate(
                partitionSize - sectorSize, 512, "הסקטור האחרון של המחיצה — מיקום הגיבוי של NTFS");

        // NTFS מדווח לעיתים גודל הקטן בסקטור אחד; נבדק גם המיקום הסמוך.
        if (partitionSize > sectorSize * 2)
            yield return new BackupCandidate(
                partitionSize - sectorSize * 2, 512, "סקטור לפני האחרון");

        // FAT32: עותק בסקטור 6.
        yield return new BackupCandidate(6L * sectorSize, 512, "סקטור 6 — מיקום הגיבוי של FAT32");

        // exFAT: אזור אתחול משני בסקטורים 12 עד 23.
        yield return new BackupCandidate(12L * sectorSize, 512, "סקטור 12 — אזור האתחול המשני של exFAT");
    }

    /// <summary>זיהוי מערכת קבצים ממגזר אתחול.</summary>
    private static FileSystemKind IdentifyBootSector(ReadOnlySpan<byte> sector)
    {
        if (sector.Length < 512) return FileSystemKind.Unknown;

        if (NtfsBootSector.Parse(sector) is not null) return FileSystemKind.Ntfs;
        if (ExFatBootSector.Parse(sector) is not null) return FileSystemKind.ExFat;

        var fat = FatBootSector.Parse(sector);
        return fat?.Kind ?? FileSystemKind.Unknown;
    }

    /// <summary>
    /// אימות שהעותק אכן מתאר את המחיצה הזו, ולא שריד של מחיצה ישנה.
    /// זו הבדיקה שמונעת תיקון הרסני.
    /// </summary>
    private static bool Validates(
        FileSystemKind kind, ReadOnlySpan<byte> sector, long partitionSize, int sectorSize)
    {
        switch (kind)
        {
            case FileSystemKind.Ntfs:
            {
                var boot = NtfsBootSector.Parse(sector);
                if (boot is null) return false;

                // הגודל שמצהיר עליו הגיבוי חייב להתאים למחיצה בפועל.
                long declared = boot.TotalSectors * boot.BytesPerSector;
                if (!SizeMatches(declared, partitionSize)) return false;

                // ה-MFT חייב לשבת בתוך המחיצה.
                return boot.MftOffset > 0 && boot.MftOffset < partitionSize;
            }

            case FileSystemKind.ExFat:
            {
                var boot = ExFatBootSector.Parse(sector);
                if (boot is null) return false;

                long declared = boot.VolumeLengthSectors * boot.BytesPerSector;
                if (!SizeMatches(declared, partitionSize)) return false;

                return boot.ClusterHeapOffset < partitionSize;
            }

            case FileSystemKind.Fat12 or FileSystemKind.Fat16 or FileSystemKind.Fat32:
            {
                var boot = FatBootSector.Parse(sector);
                if (boot is null) return false;

                long declared = boot.TotalSectors * boot.BytesPerSector;
                if (!SizeMatches(declared, partitionSize)) return false;

                return boot.FirstDataSector * boot.BytesPerSector < partitionSize;
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// השוואת גודל מוצהר לגודל המחיצה בפועל, בסובלנות קטנה.
    /// מערכות קבצים מדווחות לעיתים גודל הקטן במעט מהמחיצה.
    /// </summary>
    private static bool SizeMatches(long declared, long actual)
    {
        if (declared <= 0 || actual <= 0) return false;

        double ratio = (double)declared / actual;
        return ratio is > 0.97 and <= 1.0001;
    }

    private static string Name(FileSystemKind kind) => kind switch
    {
        FileSystemKind.Ntfs => "NTFS",
        FileSystemKind.ExFat => "exFAT",
        FileSystemKind.Fat32 => "FAT32",
        FileSystemKind.Fat16 => "FAT16",
        FileSystemKind.Fat12 => "FAT12",
        _ => kind.ToString(),
    };
}
