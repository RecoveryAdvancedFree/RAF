using System.Buffers.Binary;
using System.Text;
using RAF.Core.Model;

namespace RAF.Core.FileSystems.Fat;

/// <summary>
/// מגזר האתחול של מחיצת FAT.
///
/// שלוש הווריאציות — FAT12, FAT16 ו-FAT32 — חולקות את אותו מבנה בסיסי,
/// והבחנה ביניהן נקבעת אך ורק לפי מספר האשכולות בפועל. זהו הכלל שבמפרט,
/// ולא לפי המחרוזת שכתובה במגזר, שאינה אמינה.
/// </summary>
internal sealed class FatBootSector
{
    internal int BytesPerSector { get; private init; }
    internal int SectorsPerCluster { get; private init; }
    internal int BytesPerCluster => BytesPerSector * SectorsPerCluster;

    internal int ReservedSectors { get; private init; }
    internal int NumberOfFats { get; private init; }
    internal int RootEntryCount { get; private init; }
    internal long TotalSectors { get; private init; }
    internal long SectorsPerFat { get; private init; }

    /// <summary>אשכול תחילת ספריית השורש. רלוונטי ל-FAT32 בלבד.</summary>
    internal long RootCluster { get; private init; }

    internal FileSystemKind Kind { get; private init; }
    internal string Label { get; private init; } = "";

    /// <summary>מספר הסקטורים שתופסת ספריית השורש הקבועה. אפס ב-FAT32.</summary>
    internal long RootDirSectors =>
        (RootEntryCount * 32L + BytesPerSector - 1) / BytesPerSector;

    /// <summary>הסקטור הראשון של אזור הנתונים.</summary>
    internal long FirstDataSector =>
        ReservedSectors + NumberOfFats * SectorsPerFat + RootDirSectors;

    /// <summary>הסקטור הראשון של ספריית השורש הקבועה ב-FAT12/16.</summary>
    internal long RootDirSector => ReservedSectors + NumberOfFats * SectorsPerFat;

    /// <summary>מספר האשכולות באזור הנתונים.</summary>
    internal long ClusterCount =>
        SectorsPerCluster == 0 ? 0 : (TotalSectors - FirstDataSector) / SectorsPerCluster;

    /// <summary>היסט תחילת טבלת ה-FAT הראשונה בבתים.</summary>
    internal long FatOffset => (long)ReservedSectors * BytesPerSector;

    /// <summary>גודל טבלת FAT אחת בבתים.</summary>
    internal long FatBytes => SectorsPerFat * BytesPerSector;

    /// <summary>
    /// ניתוח מגזר האתחול. מחזיר null אם אינו מחיצת FAT תקינה.
    /// </summary>
    internal static FatBootSector? Parse(ReadOnlySpan<byte> sector)
    {
        if (sector.Length < 512) return null;

        // מגזר אתחול של FAT מתחיל בקפיצה קצרה או ארוכה.
        if (sector[0] is not (0xEB or 0xE9 or 0x49)) return null;

        int bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(sector[11..]);
        int sectorsPerCluster = sector[13];
        int reserved = BinaryPrimitives.ReadUInt16LittleEndian(sector[14..]);
        int fatCount = sector[16];
        int rootEntries = BinaryPrimitives.ReadUInt16LittleEndian(sector[17..]);
        int totalSectors16 = BinaryPrimitives.ReadUInt16LittleEndian(sector[19..]);
        int sectorsPerFat16 = BinaryPrimitives.ReadUInt16LittleEndian(sector[22..]);
        uint totalSectors32 = BinaryPrimitives.ReadUInt32LittleEndian(sector[32..]);

        // בדיקות שפיות לפי המפרט.
        if (bytesPerSector is not (512 or 1024 or 2048 or 4096)) return null;
        if (sectorsPerCluster is 0 || (sectorsPerCluster & (sectorsPerCluster - 1)) != 0) return null;
        if (sectorsPerCluster > 128) return null;
        if (reserved == 0) return null;
        if (fatCount is 0 or > 4) return null;

        long totalSectors = totalSectors16 != 0 ? totalSectors16 : totalSectors32;
        if (totalSectors <= 0) return null;

        long sectorsPerFat = sectorsPerFat16;
        long rootCluster = 0;
        string label = "";

        if (sectorsPerFat == 0)
        {
            // שדות ההרחבה של FAT32.
            sectorsPerFat = BinaryPrimitives.ReadUInt32LittleEndian(sector[36..]);
            rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(sector[44..]);
            if (sector.Length >= 82) label = ReadLabel(sector[71..82]);
        }
        else if (sector.Length >= 54)
        {
            label = ReadLabel(sector[43..54]);
        }

        if (sectorsPerFat <= 0) return null;

        var boot = new FatBootSector
        {
            BytesPerSector = bytesPerSector,
            SectorsPerCluster = sectorsPerCluster,
            ReservedSectors = reserved,
            NumberOfFats = fatCount,
            RootEntryCount = rootEntries,
            TotalSectors = totalSectors,
            SectorsPerFat = sectorsPerFat,
            RootCluster = rootCluster,
            Label = label,
        };

        if (boot.FirstDataSector >= totalSectors) return null;

        long clusters = boot.ClusterCount;
        if (clusters <= 0) return null;

        // כלל ההבחנה שבמפרט: לפי מספר האשכולות בלבד.
        var kind = clusters switch
        {
            < 4085 => FileSystemKind.Fat12,
            < 65525 => FileSystemKind.Fat16,
            _ => FileSystemKind.Fat32,
        };

        // FAT32 חייב ספריית שורש מבוססת אשכול, והאחרים חייבים ספריית שורש קבועה.
        if (kind == FileSystemKind.Fat32 && rootCluster < 2) return null;
        if (kind != FileSystemKind.Fat32 && rootEntries == 0) return null;

        return new FatBootSector
        {
            BytesPerSector = boot.BytesPerSector,
            SectorsPerCluster = boot.SectorsPerCluster,
            ReservedSectors = boot.ReservedSectors,
            NumberOfFats = boot.NumberOfFats,
            RootEntryCount = boot.RootEntryCount,
            TotalSectors = boot.TotalSectors,
            SectorsPerFat = boot.SectorsPerFat,
            RootCluster = boot.RootCluster,
            Label = boot.Label,
            Kind = kind,
        };
    }

    private static string ReadLabel(ReadOnlySpan<byte> raw)
    {
        string label = Encoding.ASCII.GetString(raw).Trim();
        return label is "NO NAME" ? "" : label;
    }
}
