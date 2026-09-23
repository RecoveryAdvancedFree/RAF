using System.Buffers.Binary;
using System.Text;

namespace RAF.Core.FileSystems.ExFat;

/// <summary>
/// מגזר האתחול של exFAT.
///
/// בניגוד ל-FAT הקלאסי, כל הגדלים מקודדים כחזקות של 2, והמיקומים נמדדים
/// בסקטורים מתחילת המחיצה. אין ספריית שורש קבועה — השורש הוא שרשרת
/// אשכולות רגילה.
/// </summary>
internal sealed class ExFatBootSector
{
    internal int BytesPerSector { get; private init; }
    internal int SectorsPerCluster { get; private init; }
    internal int BytesPerCluster => BytesPerSector * SectorsPerCluster;

    internal long VolumeLengthSectors { get; private init; }
    internal long FatOffsetSectors { get; private init; }
    internal long FatLengthSectors { get; private init; }
    internal long ClusterHeapOffsetSectors { get; private init; }
    internal long ClusterCount { get; private init; }
    internal long RootCluster { get; private init; }

    internal uint VolumeSerial { get; private init; }
    internal int NumberOfFats { get; private init; }

    /// <summary>היסט טבלת ה-FAT בבתים מתחילת המחיצה.</summary>
    internal long FatOffset => FatOffsetSectors * BytesPerSector;

    /// <summary>היסט תחילת אזור האשכולות בבתים.</summary>
    internal long ClusterHeapOffset => ClusterHeapOffsetSectors * BytesPerSector;

    /// <summary>ניתוח מגזר האתחול. מחזיר null אם אינו exFAT תקין.</summary>
    internal static ExFatBootSector? Parse(ReadOnlySpan<byte> sector)
    {
        if (sector.Length < 512) return null;
        if (Encoding.ASCII.GetString(sector.Slice(3, 8)) != "EXFAT   ") return null;

        long volumeLength = BinaryPrimitives.ReadInt64LittleEndian(sector[72..]);
        long fatOffset = BinaryPrimitives.ReadUInt32LittleEndian(sector[80..]);
        long fatLength = BinaryPrimitives.ReadUInt32LittleEndian(sector[84..]);
        long heapOffset = BinaryPrimitives.ReadUInt32LittleEndian(sector[88..]);
        long clusterCount = BinaryPrimitives.ReadUInt32LittleEndian(sector[92..]);
        long rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(sector[96..]);
        uint serial = BinaryPrimitives.ReadUInt32LittleEndian(sector[100..]);

        byte sectorShift = sector[108];
        byte clusterShift = sector[109];
        byte fatCount = sector[110];

        // הגדלים מקודדים כחזקות של 2, ולכן ערך חורג מעיד על מגזר פגום.
        if (sectorShift is < 9 or > 12) return null;        // 512 עד 4096 בתים
        if (clusterShift > 25 - sectorShift) return null;   // אשכול עד 32MB
        if (fatCount is 0 or > 2) return null;

        int bytesPerSector = 1 << sectorShift;
        int sectorsPerCluster = 1 << clusterShift;

        if (volumeLength <= 0 || clusterCount <= 0) return null;
        if (rootCluster < 2) return null;
        if (fatOffset <= 0 || heapOffset <= 0) return null;
        if (heapOffset >= volumeLength) return null;

        return new ExFatBootSector
        {
            BytesPerSector = bytesPerSector,
            SectorsPerCluster = sectorsPerCluster,
            VolumeLengthSectors = volumeLength,
            FatOffsetSectors = fatOffset,
            FatLengthSectors = fatLength,
            ClusterHeapOffsetSectors = heapOffset,
            ClusterCount = clusterCount,
            RootCluster = rootCluster,
            VolumeSerial = serial,
            NumberOfFats = fatCount,
        };
    }
}
