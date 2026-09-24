using RAF.Core.Model;

namespace RAF.App;

/// <summary>תרגום ערכי המנוע לטקסט עברי להצגה בממשק.</summary>
internal static class Display
{
    internal static string FileSystem(FileSystemKind k) => k switch
    {
        FileSystemKind.Ntfs => "NTFS",
        FileSystemKind.ExFat => "exFAT",
        FileSystemKind.Fat32 => "FAT32",
        FileSystemKind.Fat16 => "FAT16",
        FileSystemKind.Fat12 => "FAT12",
        FileSystemKind.ReFS => "ReFS",
        FileSystemKind.Ext => "ext2/3/4",
        FileSystemKind.Apfs => "APFS",
        FileSystemKind.Hfs => "HFS+",
        FileSystemKind.BitLocker => "BitLocker",
        FileSystemKind.Raw => "לא מזוהה",
        _ => "לא ידוע",
    };

    internal static string Media(MediaKind m) => m switch
    {
        MediaKind.HardDisk => "דיסק קשיח מגנטי",
        MediaKind.Ssd => "כונן SSD",
        MediaKind.NvmeSsd => "כונן NVMe SSD",
        MediaKind.UsbFlash => "התקן USB נייד",
        MediaKind.MemoryCard => "כרטיס זיכרון",
        MediaKind.Optical => "כונן אופטי",
        MediaKind.Virtual => "דיסק וירטואלי",
        MediaKind.NetworkOrRam => "כונן רשת או זיכרון",
        MediaKind.Image => "תמונת דיסק",
        _ => "סוג לא ידוע",
    };

    internal static string MediaShort(MediaKind m) => m switch
    {
        MediaKind.HardDisk => "HDD",
        MediaKind.Ssd => "SSD",
        MediaKind.NvmeSsd => "NVMe",
        MediaKind.UsbFlash => "USB",
        MediaKind.MemoryCard => "SD",
        MediaKind.Optical => "ODD",
        MediaKind.Virtual => "VHD",
        MediaKind.Image => "IMG",
        _ => "?",
    };

    internal static string Trim(TrimState t) => t switch
    {
        TrimState.Enabled => "TRIM פעיל",
        TrimState.NotSupported => "ללא TRIM",
        _ => "TRIM לא ידוע",
    };

    internal static string Scheme(PartitionScheme s) => s switch
    {
        PartitionScheme.Gpt => "GPT",
        PartitionScheme.Mbr => "MBR",
        PartitionScheme.SuperFloppy => "ללא טבלת מחיצות",
        _ => "לא נקרא",
    };

    internal static string Mode(ScanMode m) => m switch
    {
        ScanMode.Quick => "סריקה מהירה",
        ScanMode.Deep => "סריקה עמוקה",
        ScanMode.Advanced => "סריקה מתקדמת",
        _ => "סריקה",
    };

    internal static string Quality(RecoveryQuality q) => q switch
    {
        RecoveryQuality.Excellent => "מצוין",
        RecoveryQuality.Good => "טוב",
        RecoveryQuality.Poor => "פגום חלקית",
        _ => "לא ניתן לשחזור",
    };

    internal static string Source(DiscoverySource s) => s switch
    {
        DiscoverySource.MftActive => "טבלת הקבצים",
        DiscoverySource.MftOrphan => "שריד בטבלת הקבצים",
        DiscoverySource.UsnJournal => "יומן שינויים",
        DiscoverySource.LogFile => "יומן מערכת הקבצים",
        DiscoverySource.Carving => "זיהוי לפי תוכן",
        _ => "",
    };

    /// <summary>האם מערכת הקבצים נתמכת לסריקת מטא-דאטה בגרסה זו.</summary>
    internal static bool IsSupported(FileSystemKind k) => k
        is FileSystemKind.Ntfs or FileSystemKind.ExFat
        or FileSystemKind.Fat32 or FileSystemKind.Fat16 or FileSystemKind.Fat12;
}
