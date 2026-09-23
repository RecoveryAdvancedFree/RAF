using System.Buffers.Binary;
using RAF.Core.Model;
using RAF.Core.Native;

namespace RAF.Core.FileSystems.ExFat;

/// <summary>
/// מחיצת exFAT פתוחה לקריאה.
///
/// בניגוד ל-FAT הקלאסי, טבלת ההקצאה משמשת רק לקבצים מפוצלים: קובץ רציף
/// מסומן בדגל ייעודי ואינו מופיע בטבלה כלל. הקצאת האשכולות נשמרת במפת
/// ביטים נפרדת, שממנה נגזרת גם הערכת סיכויי השחזור.
/// </summary>
internal sealed class ExFatVolume : IClusterVolume
{
    internal const long FirstCluster = 2;

    private readonly VolumeReader _reader;
    private readonly bool _ownsReader;

    private byte[]? _fat;
    private byte[]? _allocationBitmap;

    internal ExFatBootSector Boot { get; }
    internal string Label { get; private set; } = "";

    public int BytesPerCluster => Boot.BytesPerCluster;

    internal long MaxCluster => FirstCluster + Boot.ClusterCount - 1;

    private ExFatVolume(VolumeReader reader, ExFatBootSector boot, bool ownsReader)
    {
        _reader = reader;
        Boot = boot;
        _ownsReader = ownsReader;
    }

    /// <summary>פתיחת מחיצת exFAT. מחזיר null אם אינה exFAT תקין.</summary>
    internal static ExFatVolume? Open(VolumeReader reader, bool ownsReader = false)
    {
        byte[] sector = reader.ReadBlock(0, 512);
        var boot = ExFatBootSector.Parse(sector);
        if (boot is null) return null;

        var volume = new ExFatVolume(reader, boot, ownsReader);
        volume.LoadFat();
        volume.LoadAllocationBitmap();
        return volume;
    }

    private void LoadFat()
    {
        try
        {
            long size = Math.Min(Boot.FatLengthSectors * Boot.BytesPerSector, 256L * 1024 * 1024);
            byte[] fat = _reader.ReadBlock(Boot.FatOffset, (int)size);
            _fat = fat.Length > 0 ? fat : null;
        }
        catch
        {
            _fat = null;
        }
    }

    /// <summary>
    /// מפת ההקצאה מתוארת בערך ייעודי בספריית השורש, ולכן יש לקרוא
    /// תחילה את השורש כדי למצוא אותה.
    /// </summary>
    private void LoadAllocationBitmap()
    {
        try
        {
            byte[] root = ReadRootDirectory(4 * 1024 * 1024);
            Label = ExFatDirectory.FindVolumeLabel(root);

            var bitmap = ExFatDirectory.FindAllocationBitmap(root);
            if (bitmap is null) return;

            var extents = FollowChain(bitmap.Value.FirstCluster);
            long size = Math.Min(bitmap.Value.Length, 256L * 1024 * 1024);
            _allocationBitmap = ReadChain(extents, (int)size);
        }
        catch
        {
            _allocationBitmap = null;
        }
    }

    /// <summary>קריאת ספריית השורש, שהיא שרשרת אשכולות רגילה.</summary>
    internal byte[] ReadRootDirectory(int maxBytes)
        => ReadChain(FollowChain(Boot.RootCluster), maxBytes);

    public long ClusterToOffset(long cluster)
        => Boot.ClusterHeapOffset + (cluster - FirstCluster) * Boot.BytesPerCluster;

    public int ReadRaw(long offset, Span<byte> destination) => _reader.Read(offset, destination);

    /// <summary>האם האשכול תפוס לפי מפת ההקצאה.</summary>
    public bool? IsClusterAllocated(long cluster)
    {
        if (_allocationBitmap is null) return null;
        if (cluster < FirstCluster || cluster > MaxCluster) return null;

        long index = cluster - FirstCluster;
        long byteIndex = index / 8;
        if (byteIndex >= _allocationBitmap.Length) return null;

        return (_allocationBitmap[byteIndex] & (1 << (int)(index % 8))) != 0;
    }

    internal bool IsValidCluster(long cluster)
        => cluster >= FirstCluster && cluster <= MaxCluster;

    private long? ReadFatEntry(long cluster)
    {
        if (_fat is null) return null;

        long at = cluster * 4;
        if (at < 0 || at + 4 > _fat.Length) return null;

        return BinaryPrimitives.ReadUInt32LittleEndian(_fat.AsSpan((int)at));
    }

    private static bool IsEndOfChain(long entry) => entry >= 0xFFFFFFF8;

    /// <summary>מעקב אחר שרשרת אשכולות דרך טבלת ה-FAT.</summary>
    internal List<DataExtent> FollowChain(long startCluster, long maxClusters = 1 << 22)
    {
        var extents = new List<DataExtent>();
        if (!IsValidCluster(startCluster)) return extents;

        var visited = new HashSet<long>();
        long runStart = startCluster;
        long runLength = 0;
        long current = startCluster;

        while (IsValidCluster(current) && runLength < maxClusters)
        {
            if (!visited.Add(current)) break;

            if (runLength == 0 || current == runStart + runLength)
            {
                runLength++;
            }
            else
            {
                extents.Add(new DataExtent(runStart, runLength, false));
                runStart = current;
                runLength = 1;
            }

            long? next = ReadFatEntry(current);
            if (next is null || next.Value == 0 || IsEndOfChain(next.Value)) break;

            current = next.Value;
        }

        if (runLength > 0) extents.Add(new DataExtent(runStart, runLength, false));
        return extents;
    }

    /// <summary>מקטע רציף, לקובץ שסומן כלא-מפוצל או לקובץ שנמחק.</summary>
    internal List<DataExtent> ContiguousExtent(long startCluster, long sizeBytes)
    {
        var extents = new List<DataExtent>();
        if (!IsValidCluster(startCluster) || sizeBytes <= 0) return extents;

        long clusters = (sizeBytes + BytesPerCluster - 1) / BytesPerCluster;
        clusters = Math.Min(clusters, MaxCluster - startCluster + 1);
        if (clusters <= 0) return extents;

        extents.Add(new DataExtent(startCluster, clusters, false));
        return extents;
    }

    /// <summary>קריאת תוכן מקטעים למאגר אחד.</summary>
    internal byte[] ReadChain(IReadOnlyList<DataExtent> extents, int maxBytes)
    {
        using var buffer = new MemoryStream();

        foreach (var extent in extents)
        {
            for (long i = 0; i < extent.ClusterCount && buffer.Length < maxBytes; i++)
            {
                int take = (int)Math.Min(BytesPerCluster, maxBytes - buffer.Length);
                byte[] chunk = new byte[take];

                int read = _reader.Read(ClusterToOffset(extent.StartCluster + i), chunk);
                if (read <= 0) return buffer.ToArray();

                buffer.Write(chunk, 0, read);
            }
        }

        return buffer.ToArray();
    }

    public void Dispose()
    {
        if (_ownsReader) _reader.Dispose();
    }
}
