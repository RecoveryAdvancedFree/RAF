using System.Buffers.Binary;
using System.Text;
using RAF.Core.FileSystems.Ntfs;
using RAF.Core.Native;

namespace RAF.Core.Tests;

/// <summary>
/// בונה תמונת מחיצת NTFS סינתטית כקובץ על הדיסק, וטוען אותה דרך אותו
/// נתיב קריאה שבו משתמשת התוכנה מול דיסק אמיתי. כך ניתן לבדוק את
/// החילוץ ואת אימות התוכן מקצה לקצה, ללא הרשאות מנהל וללא דיסק פיזי.
/// </summary>
internal sealed class NtfsImageBuilder : IDisposable
{
    internal const int BytesPerSector = 512;
    internal const int SectorsPerCluster = 1;
    internal const int BytesPerCluster = BytesPerSector * SectorsPerCluster;
    internal const int MftRecordSize = 1024;

    private const long MftStartCluster = 8;
    private const long MftClusterCount = 64;
    private const int TotalClusters = 512;

    private readonly byte[] _image = new byte[TotalClusters * BytesPerCluster];
    private readonly string _path;

    private RawDevice? _device;

    internal NtfsImageBuilder()
    {
        _path = Path.Combine(Path.GetTempPath(), $"raf-ntfs-{Guid.NewGuid():N}.img");
        WriteBootSector();
        WriteMftRecordZero();
    }

    /// <summary>היסט רשומת MFT בתוך התמונה.</summary>
    private static long RecordOffset(long recordNumber)
        => MftStartCluster * BytesPerCluster + recordNumber * MftRecordSize;

    private void WriteBootSector()
    {
        var sector = new byte[BytesPerSector];
        Encoding.ASCII.GetBytes("NTFS    ").CopyTo(sector, 3);

        BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(11), BytesPerSector);
        sector[13] = SectorsPerCluster;
        BinaryPrimitives.WriteInt64LittleEndian(sector.AsSpan(40), TotalClusters * SectorsPerCluster);
        BinaryPrimitives.WriteInt64LittleEndian(sector.AsSpan(48), MftStartCluster);
        BinaryPrimitives.WriteInt64LittleEndian(sector.AsSpan(56), MftStartCluster + MftClusterCount);
        sector[64] = unchecked((byte)-10);  // 2^10 = 1024 בתים לרשומה
        sector[68] = 1;                     // אשכול אחד לבלוק אינדקס
        sector[510] = 0x55;
        sector[511] = 0xAA;

        sector.CopyTo(_image, 0);
    }

    /// <summary>רשומה 0 היא ‎$MFT עצמו, ובה ריצות הנתונים המתארות את הטבלה.</summary>
    private void WriteMftRecordZero()
    {
        // 0x11 = אורך בבית אחד, היסט בבית אחד.
        byte[] runList = { 0x11, (byte)MftClusterCount, (byte)MftStartCluster, 0x00 };

        byte[] record = new MftRecordBuilder(MftRecordSize, BytesPerSector)
        {
            RecordNumber = 0,
        }
        .WithFileName("$MFT", parentRecord: 5)
        .WithNonResidentData(runList, realSize: MftClusterCount * BytesPerCluster)
        .Build();

        record.CopyTo(_image, RecordOffset(0));
    }

    /// <summary>כתיבת תוכן לאשכולות נתונים בתמונה.</summary>
    internal void WriteClusters(long startCluster, byte[] data)
    {
        long offset = startCluster * BytesPerCluster;
        data.CopyTo(_image, offset);
    }

    /// <summary>כתיבת רשומת MFT מוכנה למקומה בטבלה.</summary>
    internal void WriteRecord(long recordNumber, byte[] record) => record.CopyTo(_image, RecordOffset(recordNumber));

    /// <summary>עותק של התמונה כפי שנבנתה, לכתיבה כקובץ תמונת מחיצה.</summary>
    internal byte[] ToArray() => (byte[])_image.Clone();

    /// <summary>מילוי אשכולות באפסים — מדמה בלוקים שנמחקו פיזית על ידי TRIM.</summary>
    internal void ZeroClusters(long startCluster, int clusterCount)
        => Array.Clear(_image, (int)(startCluster * BytesPerCluster), clusterCount * BytesPerCluster);

    /// <summary>שמירת התמונה ופתיחתה דרך נתיב הקריאה האמיתי של התוכנה.</summary>
    internal NtfsVolume OpenVolume()
    {
        File.WriteAllBytes(_path, _image);

        _device = RawDevice.TryOpen(_path, BytesPerSector)
                  ?? throw new IOException($"לא ניתן לפתוח את תמונת הבדיקה. Win32: {RawDevice.LastError}");

        var reader = VolumeReader.Wrap(_device, offset: 0, length: _image.Length);

        return NtfsVolume.Open(reader)
               ?? throw new InvalidDataException("תמונת הבדיקה לא זוהתה כ-NTFS תקין.");
    }

    public void Dispose()
    {
        _device?.Dispose();
        try { File.Delete(_path); } catch { /* ניקוי בלבד */ }
    }
}
