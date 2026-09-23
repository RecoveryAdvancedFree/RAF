using System.Buffers.Binary;
using System.Text;

namespace RAF.Core.FileSystems.Ntfs;

/// <summary>
/// מגזר האתחול של NTFS. ממנו נגזרים כל הפרמטרים של המחיצה:
/// גודל אשכול, מיקום ה-MFT וגודל רשומה.
/// </summary>
internal sealed class NtfsBootSector
{
    internal int BytesPerSector { get; private init; }
    internal int SectorsPerCluster { get; private init; }
    internal int BytesPerCluster => BytesPerSector * SectorsPerCluster;

    internal long TotalSectors { get; private init; }
    internal long TotalClusters => TotalSectors / SectorsPerCluster;

    /// <summary>מספר האשכול שבו מתחילה טבלת ה-MFT.</summary>
    internal long MftStartCluster { get; private init; }

    /// <summary>מספר האשכול של עותק הגיבוי של תחילת ה-MFT.</summary>
    internal long MftMirrorCluster { get; private init; }

    /// <summary>גודל רשומת MFT יחידה בבתים. כמעט תמיד 1024.</summary>
    internal int MftRecordSize { get; private init; }

    /// <summary>גודל בלוק אינדקס ספרייה בבתים. כמעט תמיד 4096.</summary>
    internal int IndexBlockSize { get; private init; }

    internal ulong VolumeSerial { get; private init; }

    /// <summary>היסט תחילת ה-MFT בבתים מתחילת המחיצה.</summary>
    internal long MftOffset => MftStartCluster * BytesPerCluster;

    /// <summary>
    /// ניתוח מגזר האתחול. מחזיר null אם המגזר אינו NTFS תקין
    /// או שהערכים שבו אינם הגיוניים.
    /// </summary>
    internal static NtfsBootSector? Parse(ReadOnlySpan<byte> sector)
    {
        if (sector.Length < 512) return null;
        if (Encoding.ASCII.GetString(sector.Slice(3, 8)) != "NTFS    ") return null;

        int bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(sector[11..]);
        byte sectorsPerClusterRaw = sector[13];

        // ערך גדול מ-0x80 מקודד חזקה של 2 בהשלמה ל-2 (אשכולות גדולים מ-64KB).
        int sectorsPerCluster = sectorsPerClusterRaw <= 0x80
            ? sectorsPerClusterRaw
            : 1 << (256 - sectorsPerClusterRaw);

        long totalSectors = BinaryPrimitives.ReadInt64LittleEndian(sector[40..]);
        long mftCluster = BinaryPrimitives.ReadInt64LittleEndian(sector[48..]);
        long mftMirrorCluster = BinaryPrimitives.ReadInt64LittleEndian(sector[56..]);

        int recordSize = DecodeSizeField((sbyte)sector[64], bytesPerSector * sectorsPerCluster);
        int indexSize = DecodeSizeField((sbyte)sector[68], bytesPerSector * sectorsPerCluster);

        ulong serial = BinaryPrimitives.ReadUInt64LittleEndian(sector[72..]);

        // בדיקות שפיות — מגזר אתחול פגום מכיל לעיתים ערכים אבסורדיים.
        if (bytesPerSector is < 256 or > 8192) return null;
        if ((bytesPerSector & (bytesPerSector - 1)) != 0) return null;   // חייב להיות חזקת 2
        if (sectorsPerCluster <= 0) return null;

        // NTFS תומך באשכולות עד 2MB (מ-Windows 10 גרסה 1709).
        // מעבר לכך מדובר במגזר אתחול פגום.
        long bytesPerClusterValue = (long)bytesPerSector * sectorsPerCluster;
        if (bytesPerClusterValue > 2 * 1024 * 1024) return null;
        if (totalSectors <= 0) return null;
        if (mftCluster <= 0) return null;
        if (recordSize is < 256 or > 65536) return null;

        return new NtfsBootSector
        {
            BytesPerSector = bytesPerSector,
            SectorsPerCluster = sectorsPerCluster,
            TotalSectors = totalSectors,
            MftStartCluster = mftCluster,
            MftMirrorCluster = mftMirrorCluster,
            MftRecordSize = recordSize,
            IndexBlockSize = indexSize,
            VolumeSerial = serial,
        };
    }

    /// <summary>
    /// שדות גודל ב-NTFS מקודדים בשתי דרכים: ערך חיובי הוא מספר אשכולות,
    /// וערך שלילי הוא חזקה שלילית של 2 בבתים.
    /// </summary>
    private static int DecodeSizeField(sbyte raw, int bytesPerCluster)
        => raw > 0 ? raw * bytesPerCluster : 1 << -raw;
}
