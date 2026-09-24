using RAF.Core.Disks;
using RAF.Core.Model;
using RAF.Core.Native;

namespace RAF.Core.Repair;

/// <summary>תוצאת פעולת תיקון.</summary>
public sealed class RepairResult
{
    public bool Succeeded { get; init; }

    /// <summary>הודעה בעברית למשתמש.</summary>
    public string Message { get; init; } = "";

    /// <summary>נתיב קובץ הגיבוי שנוצר, ובאמצעותו ניתן לבטל את הפעולה.</summary>
    public string? UndoFile { get; init; }

    /// <summary>האם התיקון בוטל אוטומטית בגלל שהאימות נכשל.</summary>
    public bool RolledBack { get; init; }
}

/// <summary>
/// תיקון מגזר האתחול של מחיצה מתוך עותק הגיבוי שלה.
///
/// זו הפעולה היחידה בתוכנה שכותבת לדיסק המקור, ולכן היא בנויה כך
/// שתהיה הפיכה במלואה:
///
/// 1. הסקטורים שעומדים להידרס נשמרים לקובץ על כונן אחר.
/// 2. רק אז מתבצעת הכתיבה.
/// 3. הכתיבה נקראת בחזרה ומאומתת שהיא מתפענחת.
/// 4. אם האימות נכשל, המצב הקודם מוחזר אוטומטית.
///
/// קובץ הגיבוי משמש גם לביטול מאוחר, מתוך התוכנה (ראו UndoService).
/// </summary>
public static class PartitionRepair
{
    /// <summary>
    /// ביצוע התיקון.
    /// </summary>
    /// <param name="undoFolder">
    /// תיקייה לשמירת גיבוי הסקטורים. חייבת להיות על דיסק אחר מדיסק המקור.
    /// </param>
    public static RepairResult Repair(
        PhysicalDiskInfo disk, long partitionOffset, long partitionSize,
        PartitionDiagnosisResult diagnosis, string undoFolder, string driveLetter = "")
    {
        int diskNumber = disk.DiskNumber;
        int sectorSize = disk.LogicalSectorSize;

        if (!diagnosis.CanRepair)
            return new RepairResult { Message = "האבחון לא מצא עותק גיבוי תקין, ולכן אין מה לתקן." };

        // גיבוי על דיסק המקור היה נדרס יחד עם מה שהוא אמור להציל.
        int undoDisk = DiskEnumerator.GetDiskNumberForPath(undoFolder);
        if (undoDisk >= 0 && undoDisk == diskNumber)
            return new RepairResult
            {
                Message = "תיקיית הגיבוי חייבת להיות על כונן אחר מהדיסק שמתוקן.",
            };

        int length = Math.Max(diagnosis.RepairLength, sectorSize);
        length = (length + sectorSize - 1) / sectorSize * sectorSize;

        using var reader = VolumeReader.TryOpen(
            diskNumber, partitionOffset, partitionSize, sectorSize, sequential: false, applyOverlay: false);

        if (reader is null)
            return new RepairResult { Message = "לא ניתן לפתוח את הדיסק לקריאה." };

        byte[] replacement = reader.ReadBlock(diagnosis.BackupOffset, length);
        if (replacement.Length < length)
            return new RepairResult { Message = "לא ניתן לקרוא את עותק הגיבוי במלואו." };

        byte[] original = reader.ReadBlock(diagnosis.PrimaryOffset, length);

        // תיקון שאי אפשר לבטל במלואו לא מתבצע — כמו בהחזרת מחיצה לטבלה.
        if (original.Length < length)
            return new RepairResult
            {
                Message = "לא ניתן לקרוא את תחילת המחיצה כדי לגבות אותה לפני הכתיבה, ולכן לא נכתב דבר.",
            };

        // --- שלב 1: שמירת המצב הקיים לפני כל כתיבה ---
        long absoluteOffset = partitionOffset + diagnosis.PrimaryOffset;
        var undo = UndoFile.For(disk, UndoKind.BootSector,
            [new UndoRegion(absoluteOffset, original, replacement)]);

        string undoPath;
        try
        {
            Directory.CreateDirectory(undoFolder);
            undoPath = Path.Combine(
                undoFolder,
                $"RAF-undo-disk{diskNumber}-{partitionOffset}-{DateTime.Now:yyyyMMdd-HHmmss}.bin");

            undo.Save(undoPath);
        }
        catch (Exception ex)
        {
            return new RepairResult
            {
                Message = $"לא ניתן ליצור קובץ גיבוי, ולכן התיקון לא בוצע: {ex.Message}",
            };
        }

        // --- שלב 2: הכתיבה עצמה ---

        // Windows חוסם כתיבה לסקטור השייך לאמצעי אחסון מחובר, גם בהרשאות
        // מנהל. יש לנעול ולנתק אותו תחילה; הנעילה משתחררת ביציאה מהבלוק.
        using var volumeLock = VolumeLock.Acquire(driveLetter);

        string? target = DevicePaths.PathOf(diskNumber);
        using (var writer = target is null ? null : RawWriter.TryOpen(target, sectorSize))
        {
            if (writer is null)
                return new RepairResult
                {
                    Message = "לא ניתן לפתוח את הדיסק לכתיבה. ודאו שהתוכנה פועלת בהרשאות מנהל " +
                              "ושהמחיצה אינה בשימוש.",
                    UndoFile = undoPath,
                };

            if (!writer.Write(absoluteOffset, replacement))
                return new RepairResult
                {
                    Message = $"הכתיבה לדיסק נכשלה (שגיאת Windows {RawWriter.LastError}). " +
                              $"{volumeLock.Status} המחיצה לא שונתה.",
                    UndoFile = undoPath,
                };
        }

        // --- שלב 3: אימות שהמחיצה אכן נקראת עכשיו ---
        var check = PartitionDiagnosis.Diagnose(diskNumber, partitionOffset, partitionSize, sectorSize);

        if (check.Outlook == RepairOutlook.Healthy)
        {
            return new RepairResult
            {
                Succeeded = true,
                UndoFile = undoPath,
                Message =
                    $"המחיצה תוקנה. מערכת הקבצים {check.DetectedFileSystem} נקראת כעת בהצלחה. " +
                    "ייתכן שיהיה צורך לנתק ולחבר מחדש את הכונן, או להפעיל מחדש את המחשב, " +
                    "כדי ש-Windows יזהה את השינוי. " +
                    $"גיבוי המצב הקודם נשמר ב: {undoPath}",
            };
        }

        // --- שלב 4: האימות נכשל — החזרת המצב הקודם ---
        bool restored = undo.WriteBefore(diskNumber);

        return new RepairResult
        {
            Succeeded = false,
            RolledBack = restored,
            UndoFile = undoPath,
            Message = restored
                ? "התיקון נכתב אך המחיצה עדיין אינה נקראת, ולכן המצב הקודם הוחזר אוטומטית. " +
                  "הדיסק נותר כפי שהיה. נסו לשחזר קבצים בסריקה מתקדמת."
                : "התיקון נכתב, המחיצה עדיין אינה נקראת, וגם החזרת המצב הקודם נכשלה. " +
                  $"קובץ הגיבוי שמור ב: {undoPath}",
        };
    }
}
