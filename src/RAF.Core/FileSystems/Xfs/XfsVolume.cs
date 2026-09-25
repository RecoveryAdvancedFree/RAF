using System.Buffers.Binary;
using RAF.Core.Model;
using RAF.Core.Native;

namespace RAF.Core.FileSystems.Xfs;

/// <summary>
/// מחיצת XFS פתוחה. "אשכול" במונחי התוכנה הוא בלוק, במספור רציף מתחילת המחיצה
/// (קבוצה × גודל קבוצה + מיקום) — לא המספר המקודד של XFS.
///
/// מפת ההקצאה: בכל קבוצה עץ של המקטעים הפנויים, לפי מספר בלוק. בלוק שאינו
/// באף מקטע פנוי — תפוס.
/// </summary>
internal sealed class XfsVolume : IClusterVolume
{
    private readonly VolumeReader _reader;
    private readonly Dictionary<long, byte[]> _cache = new();
    private List<(long Start, long Count)>? _free;
    private const int CacheChunk = 64 * 1024;

    internal XfsSuperblock Super { get; }
    public int BytesPerCluster => Super.BlockSize;
    internal long TotalBlocks => Super.AgCount * Super.AgBlocks;

    private XfsVolume(VolumeReader reader, XfsSuperblock super)
    {
        _reader = reader;
        Super = super;
    }

    internal static XfsVolume? Open(VolumeReader reader)
    {
        var super = XfsSuperblock.Parse(reader.ReadBlock(0, 512));
        return super is null ? null : new XfsVolume(reader, super);
    }

    public long ClusterToOffset(long cluster) => cluster * Super.BlockSize;
    public int ReadRaw(long offset, Span<byte> destination) => _reader.Read(offset, destination);

    internal byte[]? ReadCached(long offset, int length)
    {
        byte[] result = new byte[length];
        lock (_cache)
            for (int done = 0; done < length; )
            {
                long chunk = (offset + done) / CacheChunk;
                if (!_cache.TryGetValue(chunk, out var data))
                {
                    data = _reader.ReadBlock(chunk * CacheChunk, CacheChunk);
                    if (data.Length == 0) return null;
                    if (_cache.Count >= 1024) _cache.Clear();
                    _cache[chunk] = data;
                }
                int within = (int)(offset + done - chunk * CacheChunk);
                int take = Math.Min(length - done, data.Length - within);
                if (take <= 0) return null;
                data.AsSpan(within, take).CopyTo(result.AsSpan(done));
                done += take;
            }
        return result;
    }

    internal byte[]? ReadBlock(long linear) =>
        linear < 0 || linear >= TotalBlocks ? null : ReadCached(linear * Super.BlockSize, Super.BlockSize);

    internal XfsInode? ReadInode(ulong number)
    {
        long at = Super.InodeOffset(number);
        if (at < 0) return null;
        var raw = ReadCached(at, Super.InodeSize);
        return raw is null ? null : XfsInode.Parse(number, raw, Super.IsV5);
    }

    /// <summary>גודל כותרת של בלוק בעץ ארוך (מצביעים של 64 סיביות): BMAP, או BMA3 בגרסה 5.</summary>
    private int LongHeader => Super.IsV5 ? 72 : 24;
    private int ShortHeader => Super.IsV5 ? 56 : 16;

    /// <summary>
    /// הליכה על בלוק בעץ המקטעים של קובץ. level — 0 הוא עלה. owner — האינוד שהבלוק
    /// אמור להיות שלו (בגרסה 5 הוא רשום בכותרת): כך בלוק של קובץ שנמחק, שכבר עבר
    /// לקובץ אחר, לא ייקרא בטעות כשלו.
    /// </summary>
    internal bool WalkBmapBlock(long linear, int level, List<(long, long, long, bool)> runs, int depth, ulong owner)
    {
        if (depth > 10) return false;
        var block = ReadBlock(linear);
        if (block is null) return false;
        var magic = block.AsSpan(0, 4);
        if (!magic.SequenceEqual("BMAP"u8) && !magic.SequenceEqual("BMA3"u8)) return false;
        if (Super.IsV5 && BinaryPrimitives.ReadUInt64BigEndian(block.AsSpan(56)) != owner) return false;
        int count = BinaryPrimitives.ReadUInt16BigEndian(block.AsSpan(6));
        int hdr = LongHeader;

        if (level == 0)
        {
            for (int i = 0; i < count && hdr + (i + 1) * 16 <= block.Length; i++)
            {
                var (offset, fsBlock, blocks, unwritten) = XfsInode.Record(block.AsSpan(hdr + i * 16, 16));
                runs.Add((offset, Super.Linear(fsBlock), blocks, unwritten));
            }
            return true;
        }

        int max = (block.Length - hdr) / 16;
        for (int i = 0; i < count; i++)
        {
            ulong child = BinaryPrimitives.ReadUInt64BigEndian(block.AsSpan(hdr + max * 8 + i * 8));
            if (!WalkBmapBlock(Super.Linear(child), level - 1, runs, depth + 1, owner)) return false;
        }
        return true;
    }

    internal List<DataExtent>? ExtentsOf(XfsInode inode, long size)
    {
        var runs = inode.Runs(this);
        if (runs is null) return null;
        return XfsInode.ToExtents(runs, (size + Super.BlockSize - 1) / Super.BlockSize);
    }

    /// <summary>
    /// המקטעים שבמזלג לפי הסדר, בלי להגביל לגודל — לתיקייה: בלוקי הנתונים שלה
    /// מתחילים ב-0, והאינדקס שלה יושב הרחק (מ-32GB), וממנו מתעלמים.
    /// </summary>
    internal List<(long Offset, long Block, long Count, bool Unwritten)>? RunsOf(XfsInode inode) => inode.Runs(this);

    // ------------------------------------------------------------ הקצאה

    public bool? IsClusterAllocated(long cluster)
    {
        if (cluster < 0 || cluster >= TotalBlocks) return null;
        var free = FreeExtents();
        if (free is null) return null;

        int lo = 0, hi = free.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (free[mid].Start + free[mid].Count <= cluster) lo = mid + 1;
            else hi = mid;
        }
        return !(lo < free.Count && free[lo].Start <= cluster);
    }

    /// <summary>כל המקטעים הפנויים, ממוינים — מעצי המקום הפנוי של כל הקבוצות.</summary>
    private List<(long Start, long Count)>? FreeExtents()
    {
        lock (_freeGate)
        {
            if (!_freeLoaded)
            {
                _free = LoadFree();
                _freeLoaded = true;
            }
            return _free;
        }
    }

    private readonly object _freeGate = new();

    private List<(long Start, long Count)>? LoadFree()
    {
        var list = new List<(long, long)>();
        for (long ag = 0; ag < Super.AgCount; ag++)
        {
            long agStart = ag * Super.AgBlocks;
            var agf = ReadCached(agStart * Super.BlockSize + Super.SectorSize, Super.SectorSize);
            if (agf is null || !agf.AsSpan(0, 4).SequenceEqual("XAGF"u8)) return null;
            long root = BinaryPrimitives.ReadUInt32BigEndian(agf.AsSpan(16));
            int levels = (int)BinaryPrimitives.ReadUInt32BigEndian(agf.AsSpan(28));
            if (!WalkFree(agStart, root, levels - 1, list, 0)) return null;
        }
        list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return list;
    }

    /// <summary>המפה נקראה (או נכשלה — ואז _free נשאר null ו"לא ידוע").</summary>
    private bool _freeLoaded;

    private bool WalkFree(long agStart, long agBlock, int level, List<(long, long)> list, int depth)
    {
        if (depth > 10) return false;
        var block = ReadBlock(agStart + agBlock);
        if (block is null) return false;
        var magic = block.AsSpan(0, 4);
        if (!magic.SequenceEqual("ABTB"u8) && !magic.SequenceEqual("AB3B"u8)) return false;
        int count = BinaryPrimitives.ReadUInt16BigEndian(block.AsSpan(6));
        int hdr = ShortHeader;

        if (level == 0)
        {
            for (int i = 0; i < count && hdr + (i + 1) * 8 <= block.Length; i++)
                list.Add((agStart + BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(hdr + i * 8)),
                          BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(hdr + i * 8 + 4))));
            return true;
        }

        int max = (block.Length - hdr) / 12;
        for (int i = 0; i < count; i++)
            if (!WalkFree(agStart, BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(hdr + max * 8 + i * 4)), level - 1, list, depth + 1))
                return false;
        return true;
    }

    public void Dispose() { }
}
