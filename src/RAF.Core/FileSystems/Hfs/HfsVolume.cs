using System.Buffers.Binary;
using RAF.Core.Model;
using RAF.Core.Native;

namespace RAF.Core.FileSystems.Hfs;

/// <summary>
/// מחיצת HFS+ (או HFSX) פתוחה — מערכת הקבצים של מק עד 2017, של כוננים חיצוניים שפורמטו במק,
/// ושל גיבויי Time Machine ישנים. הכול בסדר בתים "גדול", והכותרת 1024 בתים מההתחלה.
/// "אשכול" במונחי התוכנה הוא בלוק של HFS+. מפת ההקצאה — קובץ של ביט לכל בלוק.
/// </summary>
internal sealed class HfsVolume : IClusterVolume
{
    private readonly VolumeReader _reader;
    private byte[]? _bitmap;

    internal int BlockSize { get; private init; }
    internal long TotalBlocks { get; private init; }
    internal uint JournalInfoBlock { get; private init; }
    internal bool Journaled { get; private init; }
    internal List<DataExtent> Allocation { get; private init; } = new();
    internal List<DataExtent> Extents { get; private init; } = new();
    internal List<DataExtent> Catalog { get; private init; } = new();
    internal long CatalogSize { get; private init; }
    public int BytesPerCluster => BlockSize;

    private HfsVolume(VolumeReader reader) => _reader = reader;

    internal static HfsVolume? Open(VolumeReader reader)
    {
        var h = reader.ReadBlock(1024, 512);
        if (h.Length < 512 || !(h[0] == 'H' && (h[1] == '+' || h[1] == 'X'))) return null;
        int blockSize = (int)BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(40));
        if (blockSize < 512 || (blockSize & (blockSize - 1)) != 0) return null;
        uint attributes = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(4));
        return new HfsVolume(reader)
        {
            BlockSize = blockSize,
            TotalBlocks = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(44)),
            JournalInfoBlock = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(12)),
            Journaled = (attributes & 0x2000) != 0,
            Allocation = ForkExtents(h.AsSpan(112)),
            Extents = ForkExtents(h.AsSpan(192)),
            Catalog = ForkExtents(h.AsSpan(272)),
            CatalogSize = (long)BinaryPrimitives.ReadUInt64BigEndian(h.AsSpan(272)),
        };
    }

    /// <summary>שמונת המקטעים שבתיאור של "מזלג" (80 בתים): גודל, ואז 8 זוגות של בלוק התחלה ומספר בלוקים.</summary>
    internal static List<DataExtent> ForkExtents(ReadOnlySpan<byte> fork) => Record(fork[16..]);

    /// <summary>רשומת מקטעים: 8 זוגות (התחלה, אורך), עד הזוג הריק הראשון.</summary>
    internal static List<DataExtent> Record(ReadOnlySpan<byte> r)
    {
        var list = new List<DataExtent>();
        for (int i = 0; i < 8 && i * 8 + 8 <= r.Length; i++)
        {
            uint count = BinaryPrimitives.ReadUInt32BigEndian(r[(i * 8 + 4)..]);
            if (count == 0) break;
            list.Add(new DataExtent(BinaryPrimitives.ReadUInt32BigEndian(r[(i * 8)..]), count, false));
        }
        return list;
    }

    public long ClusterToOffset(long cluster) => cluster * BlockSize;
    public int ReadRaw(long offset, Span<byte> destination) => _reader.Read(offset, destination);
    internal byte[] Read(long offset, int length) => _reader.ReadBlock(offset, length);
    internal long Length => _reader.Length;

    /// <summary>קריאת קטע מתוך קובץ לפי רשימת המקטעים שלו.</summary>
    internal byte[]? ReadFile(List<DataExtent> extents, long offset, int length)
    {
        var result = new byte[length];
        long at = 0;
        int done = 0;
        foreach (var e in extents)
        {
            long bytes = e.ClusterCount * BlockSize;
            while (done < length && offset + done < at + bytes)
            {
                long within = offset + done - at;
                int take = (int)Math.Min(length - done, bytes - within);
                if (_reader.Read(e.StartCluster * BlockSize + within, result.AsSpan(done, take)) != take) return null;
                done += take;
            }
            at += bytes;
            if (done == length) return result;
        }
        return done == length ? result : null;
    }

    public bool? IsClusterAllocated(long cluster)
    {
        if (cluster < 0 || cluster >= TotalBlocks) return null;
        _bitmap ??= ReadFile(Allocation, 0, (int)Math.Min(int.MaxValue, (TotalBlocks + 7) / 8)) ?? Array.Empty<byte>();
        if (cluster / 8 >= _bitmap.Length) return null;
        return (_bitmap[cluster / 8] & (0x80 >> (int)(cluster % 8))) != 0;
    }

    public void Dispose() { }
}
