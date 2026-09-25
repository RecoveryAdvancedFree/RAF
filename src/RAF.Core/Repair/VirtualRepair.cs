using RAF.Core.Native;

namespace RAF.Core.Repair;

/// <summary>
/// קריאת מחיצה ש-Windows מבקש לפרמט, בלי לתקן אותה.
///
/// Windows מציע "לפרמט" כשמגזר האתחול של המחיצה פגום — אבל שאר המחיצה,
/// כולל טבלת הקבצים, בדרך כלל שלם. התיקון האמיתי מעתיק את עותק הגיבוי אל
/// הדיסק; כאן אותו עותק מוצג לקוראי המחיצה בזיכרון בלבד. הסריקה רואה
/// מחיצה תקינה, ומוצאת את כל הקבצים עם שמותיהם ותיקיותיהם — ועל הכונן
/// לא נכתב אף בית.
/// </summary>
public static class VirtualRepair
{
    /// <summary>
    /// הפעלת הקריאה דרך עותק הגיבוי. מחזיר את האבחון: אם לא נמצא עותק
    /// תקין (<see cref="PartitionDiagnosisResult.CanRepair"/> שלילי), דבר לא הופעל.
    /// </summary>
    public static PartitionDiagnosisResult Apply(int diskNumber, long partitionOffset, long partitionSize, int sectorSize)
    {
        // האבחון קורא את הדיסק עצמו, ולכן תיקון בזיכרון קודם אינו משפיע עליו.
        var diagnosis = PartitionDiagnosis.Diagnose(diskNumber, partitionOffset, partitionSize, sectorSize);
        if (!diagnosis.CanRepair) return diagnosis;

        using var reader = VolumeReader.TryOpen(
            diskNumber, partitionOffset, partitionSize, sectorSize, sequential: false, applyOverlay: false);

        byte[]? backup = reader?.ReadBlock(diagnosis.BackupOffset, diagnosis.RepairLength);
        if (backup is null || backup.Length < diagnosis.RepairLength)
            return new PartitionDiagnosisResult
            {
                Outlook = RepairOutlook.Unreadable,
                Summary = L.T("לא ניתן לקרוא את עותק הגיבוי של תחילת המחיצה (מגזר האתחול)."),
            };

        ReadOverlays.Set(diskNumber, partitionOffset, diagnosis.PrimaryOffset, backup);
        return diagnosis;
    }

    /// <summary>
    /// קריאת מחיצת NTFS שנבנתה מחדש מרשומות הקבצים שלה (NtfsRebuild): מגזר האתחול
    /// שחושב מוצג במקום הראשון של המחיצה — בזיכרון בלבד, כמו עותק הגיבוי למעלה.
    /// </summary>
    public static void ApplyRebuilt(int diskNumber, RAF.Core.FileSystems.Ntfs.RebuiltNtfs rebuilt)
        => ReadOverlays.Set(diskNumber, rebuilt.Offset, 0, rebuilt.BootSector);

    public static void Remove(int diskNumber, long partitionOffset) => ReadOverlays.Remove(diskNumber, partitionOffset);

    public static bool IsActive(int diskNumber, long partitionOffset) => ReadOverlays.Has(diskNumber, partitionOffset);
}
