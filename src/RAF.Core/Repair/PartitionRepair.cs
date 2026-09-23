using System.Text;
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
/// </summary>
public static class PartitionRepair
{
    /// <summary>חתימה בראש קובץ הגיבוי, לזיהוי ולמניעת שחזור שגוי.</summary>
    private const string UndoSignature = "RAF-UNDO-1";

    /// <summary>
    /// ביצוע התיקון.
    /// </summary>
    /// <param name="undoFolder">
    /// תיקייה לשמירת גיבוי הסקטורים. חייבת להיות על דיסק אחר מדיסק המקור.
    /// </param>
    public static RepairResult Repair(
        int diskNumber, long partitionOffset, long partitionSize, int sectorSize,
        PartitionDiagnosisResult diagnosis, string undoFolder, string driveLetter = "")
    {
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

        // --- שלב 1: שמירת המצב הקיים לפני כל כתיבה ---
        string undoPath;
        try
        {
            Directory.CreateDirectory(undoFolder);
            undoPath = Path.Combine(
                undoFolder,
                $"RAF-undo-disk{diskNumber}-{partitionOffset}-{DateTime.Now:yyyyMMdd-HHmmss}.bin");

            WriteUndoFile(undoPath, diskNumber, partitionOffset, diagnosis.PrimaryOffset, original);
        }
        catch (Exception ex)
        {
            return new RepairResult
            {
                Message = $"לא ניתן ליצור קובץ גיבוי, ולכן התיקון לא בוצע: {ex.Message}",
            };
        }

        // --- שלב 2: הכתיבה עצמה ---
        long absoluteOffset = partitionOffset + diagnosis.PrimaryOffset;

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
        bool restored = Undo(undoPath, sectorSize);

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

    /// <summary>
    /// ביטול תיקון קודם: כתיבת הסקטורים המקוריים חזרה למקומם.
    /// </summary>
    public static bool Undo(string undoPath, int sectorSize)
    {
        try
        {
            byte[] file = File.ReadAllBytes(undoPath);
            if (file.Length < 64) return false;

            // כותרת: חתימה, מספר דיסק, היסט המחיצה, היסט בתוך המחיצה, אורך.
            string signature = Encoding.ASCII.GetString(file, 0, UndoSignature.Length);
            if (signature != UndoSignature) return false;

            int diskNumber = BitConverter.ToInt32(file, 16);
            long partitionOffset = BitConverter.ToInt64(file, 24);
            long primaryOffset = BitConverter.ToInt64(file, 32);
            int length = BitConverter.ToInt32(file, 40);

            if (length <= 0 || 64 + length > file.Length) return false;

            byte[] original = file.AsSpan(64, length).ToArray();

            // קובץ ביטול של תמונה מצביע על מספר וירטואלי, שתקף רק כל עוד
            // התמונה פתוחה. מספר שאינו רשום מחזיר null — ולא נכתב דבר.
            string? target = DevicePaths.PathOf(diskNumber);
            if (target is null) return false;

            using var writer = RawWriter.TryOpen(target, sectorSize);
            return writer is not null && writer.Write(partitionOffset + primaryOffset, original);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>כתיבת קובץ הגיבוי, עם כל המידע הדרוש כדי לבטל את הפעולה.</summary>
    private static void WriteUndoFile(
        string path, int diskNumber, long partitionOffset, long primaryOffset, byte[] original)
    {
        byte[] header = new byte[64];
        Encoding.ASCII.GetBytes(UndoSignature).CopyTo(header, 0);
        BitConverter.GetBytes(diskNumber).CopyTo(header, 16);
        BitConverter.GetBytes(partitionOffset).CopyTo(header, 24);
        BitConverter.GetBytes(primaryOffset).CopyTo(header, 32);
        BitConverter.GetBytes(original.Length).CopyTo(header, 40);

        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(header);
        stream.Write(original);
        stream.Flush(flushToDisk: true);
    }
}
