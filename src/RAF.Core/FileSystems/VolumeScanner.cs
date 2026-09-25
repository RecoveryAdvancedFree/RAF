using RAF.Core.Carving;
using RAF.Core.FileSystems.ExFat;
using RAF.Core.FileSystems.Ext;
using RAF.Core.FileSystems.Fat;
using RAF.Core.FileSystems.Ntfs;
using RAF.Core.FileSystems.Xfs;
using RAF.Core.Model;
using RAF.Core.Native;
using RAF.Core.Recovery;

namespace RAF.Core.FileSystems;

/// <summary>
/// נקודת הכניסה האחידה לסריקה ולחילוץ.
///
/// בוחרת את המנוע המתאים למערכת הקבצים, כך שכל שאר התוכנה —
/// הממשק, השחזור והתצוגה המקדימה — אינה צריכה לדעת באיזו
/// מערכת קבצים מדובר.
/// </summary>
public static class VolumeScanner
{
    /// <summary>האם קיים מנוע סריקה למערכת קבצים זו.</summary>
    public static bool IsSupported(FileSystemKind kind) => kind
        is FileSystemKind.Ntfs
        or FileSystemKind.ExFat
        or FileSystemKind.Fat32
        or FileSystemKind.Fat16
        or FileSystemKind.Fat12
        or FileSystemKind.Ext
        or FileSystemKind.Xfs
        or FileSystemKind.Btrfs
        or FileSystemKind.Hfs
        or FileSystemKind.Apfs;

    /// <summary>
    /// סריקת מחיצה במנוע המתאים לה. checkpoint מקבל נקודות ביניים — כרגע רק
    /// מהסריקה המתקדמת, שהיא היחידה שנמשכת שעות.
    /// </summary>
    public static Task<ScanResult> ScanAsync(
        FileSystemKind kind,
        int diskNumber, long partitionOffset, long partitionSize, int sectorSize,
        ScanMode mode, bool includeExisting, TrimState trim,
        IProgress<ScanProgress>? progress, CancellationToken token,
        Action<ScanResult>? checkpoint = null, bool freeSpaceOnly = false,
        Func<Signatures.FileSignature, bool>? accept = null,
        ScanResult? resumeFrom = null, List<string>? types = null)
    {
        // סריקה מתקדמת אינה תלויה במערכת הקבצים כלל, ולכן היא זהה
        // בכל מחיצה — כולל כזו שמערכת הקבצים שלה נהרסה.
        if (mode == ScanMode.Advanced)
            return FileCarver.ScanAsync(
                diskNumber, partitionOffset, partitionSize, sectorSize, progress, token, checkpoint,
                kind, freeSpaceOnly && IsSupported(kind), accept, resumeFrom, types);

        return AfterMetadataScan(ScanMetadataAsync(
            kind, diskNumber, partitionOffset, partitionSize, sectorSize, mode, includeExisting, trim, progress, token),
            file => FileContentReader.ReadHead(kind, diskNumber, partitionOffset, partitionSize, sectorSize, file, 4096),
            files => IdentifyChkFiles(files, kind, diskNumber, partitionOffset, partitionSize, sectorSize));
    }

    /// <summary>
    /// אחרי סריקת מטא-דאטה: קבצים מסל המחזור מקבלים את שמם המקורי (ראו RecycleBinNames),
    /// קבצים שבדיקת הדיסק השאירה מקבלים את הסוג שלהם (ראו ChkFiles), ואז קבצים
    /// מחוקים שקובץ מחוק מאוחר יותר תפס את אשכולותיהם מדורגים מחדש —
    /// מפת ההקצאה לבדה אינה רואה זאת (ראו OverlapCheck). השמות קודם, כדי שגם
    /// ההסבר על דריסה יציג את השם האמיתי של הקובץ שדרס.
    /// </summary>
    private static async Task<ScanResult> AfterMetadataScan(
        Task<ScanResult> scan, Func<RecoveredFile, byte[]> read, Func<List<RecoveredFile>, int> identifyChk)
    {
        var result = await scan.ConfigureAwait(false);

        int named = RecycleBinNames.Apply(result.Files, read);
        if (named > 0)
            result.Warnings.Add(L.T("{0} קבצים ותיקיות שנמחקו דרך סל המחזור קיבלו בחזרה את השם והתיקייה המקוריים.", named.ToString("N0")));

        int typed = identifyChk(result.Files);
        if (typed > 0)
            result.Warnings.Add(L.T("{0} קבצים שבדיקת הדיסק של Windows השאירה בתיקיית FOUND בלי שם " +
                                "זוהו לפי התוכן שלהם וקיבלו בחזרה את הסוג הנכון.", typed.ToString("N0")));

        int changed = OverlapCheck.Apply(result.Files);
        if (changed > 0)
            result.Warnings.Add(L.T("{0} קבצים מחוקים דורגו מחדש: קובץ מחוק אחר, שנכתב אחריהם, " +
                                "נכתב במקום שלהם בכונן — גם אם עכשיו המקום נראה פנוי.", changed.ToString("N0")));
        return result;
    }

    /// <summary>זיהוי קבצי CHK מתוך המחיצה, שנפתחת פעם אחת לכולם.</summary>
    private static int IdentifyChkFiles(
        List<RecoveredFile> files, FileSystemKind kind,
        int diskNumber, long partitionOffset, long partitionSize, int sectorSize)
    {
        var candidates = ChkFiles.Candidates(files);
        if (candidates.Count == 0) return 0;

        using var reader = VolumeReader.TryOpen(diskNumber, partitionOffset, partitionSize, sectorSize, sequential: false);
        if (reader is null) return 0;

        using var volume = Open(reader, kind, sectorSize);
        return volume is null ? 0 : ChkFiles.Apply(candidates, volume);
    }

    private static Task<ScanResult> ScanMetadataAsync(
        FileSystemKind kind,
        int diskNumber, long partitionOffset, long partitionSize, int sectorSize,
        ScanMode mode, bool includeExisting, TrimState trim,
        IProgress<ScanProgress>? progress, CancellationToken token)
    {
        return kind switch
        {
            FileSystemKind.Ntfs => NtfsScanner.ScanAsync(
                diskNumber, partitionOffset, partitionSize, sectorSize,
                mode, includeExisting, trim, progress, token),

            FileSystemKind.ExFat => ExFatScanner.ScanAsync(
                diskNumber, partitionOffset, partitionSize, sectorSize,
                mode, includeExisting, trim, progress, token),

            FileSystemKind.Fat32 or FileSystemKind.Fat16 or FileSystemKind.Fat12 =>
                FatScanner.ScanAsync(
                    diskNumber, partitionOffset, partitionSize, sectorSize,
                    mode, includeExisting, trim, progress, token),

            FileSystemKind.Ext => ExtScanner.ScanAsync(
                diskNumber, partitionOffset, partitionSize, sectorSize,
                mode, includeExisting, trim, progress, token),

            FileSystemKind.Xfs => XfsScanner.ScanAsync(
                diskNumber, partitionOffset, partitionSize, sectorSize,
                mode, includeExisting, trim, progress, token),

            FileSystemKind.Apfs => Apfs.ApfsScanner.ScanAsync(
                diskNumber, partitionOffset, partitionSize, sectorSize,
                mode, includeExisting, trim, progress, token),

            FileSystemKind.Hfs => Hfs.HfsScanner.ScanAsync(
                diskNumber, partitionOffset, partitionSize, sectorSize,
                mode, includeExisting, trim, progress, token),

            FileSystemKind.Btrfs => Btrfs.BtrfsScanner.ScanAsync(
                diskNumber, partitionOffset, partitionSize, sectorSize,
                mode, includeExisting, trim, progress, token),

            _ => throw new InvalidOperationException(
                L.T("מערכת הקבצים {0} אינה נתמכת לסריקה בגרסה זו.", kind)),
        };
    }

    /// <summary>האם ניתן לסרוק את המחיצה במצב הסריקה הזה.</summary>
    public static bool CanScan(FileSystemKind kind, ScanMode mode)
        => mode == ScanMode.Advanced || IsSupported(kind);

    /// <summary>
    /// פתיחת מחיצה לצורך חילוץ תוכן.
    /// מחזיר null אם המחיצה אינה תואמת למערכת הקבצים שנמסרה.
    /// </summary>
    internal static IClusterVolume? Open(VolumeReader reader, FileSystemKind kind, int sectorSize = 512)
        => kind switch
    {
        // תוצאות סריקה מתקדמת מתוארות ביחידות סקטור, ללא מערכת קבצים.
        FileSystemKind.Raw => RawVolume.Open(reader, sectorSize),
        FileSystemKind.Ntfs => NtfsVolume.Open(reader),
        FileSystemKind.ExFat => ExFatVolume.Open(reader),
        FileSystemKind.Fat32 or FileSystemKind.Fat16 or FileSystemKind.Fat12 => FatVolume.Open(reader),
        FileSystemKind.Ext => ExtVolume.Open(reader),
        FileSystemKind.Xfs => XfsVolume.Open(reader),
        FileSystemKind.Btrfs => FileSystems.Btrfs.BtrfsVolume.Open(reader),
        FileSystemKind.Hfs => FileSystems.Hfs.HfsVolume.Open(reader),
        FileSystemKind.Apfs => FileSystems.Apfs.ApfsVolume.Open(reader),
        _ => null,
    };
}
