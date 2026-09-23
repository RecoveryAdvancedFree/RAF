using RAF.Core.Carving;
using RAF.Core.FileSystems.ExFat;
using RAF.Core.FileSystems.Fat;
using RAF.Core.FileSystems.Ntfs;
using RAF.Core.Model;
using RAF.Core.Native;

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
        or FileSystemKind.Fat12;

    /// <summary>סריקת מחיצה במנוע המתאים לה.</summary>
    public static Task<ScanResult> ScanAsync(
        FileSystemKind kind,
        int diskNumber, long partitionOffset, long partitionSize, int sectorSize,
        ScanMode mode, bool includeExisting, TrimState trim,
        IProgress<ScanProgress>? progress, CancellationToken token)
    {
        // סריקה מתקדמת אינה תלויה במערכת הקבצים כלל, ולכן היא זהה
        // בכל מחיצה — כולל כזו שמערכת הקבצים שלה נהרסה.
        if (mode == ScanMode.Advanced)
            return FileCarver.ScanAsync(
                diskNumber, partitionOffset, partitionSize, sectorSize, progress, token);

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

            _ => throw new InvalidOperationException(
                $"מערכת הקבצים {kind} אינה נתמכת לסריקה בגרסה זו."),
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
        _ => null,
    };
}
