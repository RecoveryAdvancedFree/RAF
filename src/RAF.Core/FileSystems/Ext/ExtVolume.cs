using System.Buffers.Binary;
using RAF.Core.Native;

namespace RAF.Core.FileSystems.Ext;

/// <summary>
/// מחיצת ext2/3/4 פתוחה. המחיצה מחולקת לקבוצות בלוקים; לכל קבוצה רשומה בטבלת
/// הקבוצות שאומרת איפה מפת הבלוקים שלה, מפת האינודים וטבלת האינודים. "אשכול"
/// במונחי התוכנה הוא בלוק.
/// </summary>
internal sealed class ExtVolume : IClusterVolume
{
    private readonly VolumeReader _reader;
    private readonly long[] _blockBitmap;
    private readonly long[] _inodeTable;
    private readonly bool[] _blockUninit;
    private readonly Dictionary<long, byte[]?> _bitmaps = new();
    private readonly Dictionary<long, byte[]> _cache = new();
    private const int CacheChunk = 64 * 1024;
    private const int CacheLimit = 1024;   // 64MB

    internal ExtSuperblock Super { get; }
    public int BytesPerCluster => Super.BlockSize;

    private ExtVolume(VolumeReader reader, ExtSuperblock super, long[] blockBitmap, long[] inodeTable, bool[] uninit)
    {
        _reader = reader;
        Super = super;
        _blockBitmap = blockBitmap;
        _inodeTable = inodeTable;
        _blockUninit = uninit;
    }

    internal static ExtVolume? Open(VolumeReader reader)
    {
        var super = ExtSuperblock.Parse(reader.ReadBlock(ExtSuperblock.Offset, 1024));
        if (super is null) return null;

        long groups = super.GroupCount;
        if (groups <= 0 || groups > 50_000_000) return null;

        int bs = super.BlockSize;
        int perBlock = bs / super.DescriptorSize;
        var blockBitmap = new long[groups];
        var inodeTable = new long[groups];
        var uninit = new bool[groups];

        long descBlocks = (groups + perBlock - 1) / perBlock;
        for (long d = 0; d < descBlocks; d++)
        {
            long at = DescriptorBlock(super, d, perBlock);
            byte[] block = reader.ReadBlock(at * bs, bs);
            if (block.Length < bs) return null;

            for (int i = 0; i < perBlock; i++)
            {
                long g = d * perBlock + i;
                if (g >= groups) break;
                var e = block.AsSpan(i * super.DescriptorSize, super.DescriptorSize);
                long bb = BinaryPrimitives.ReadUInt32LittleEndian(e);
                long it = BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
                if (super.DescriptorSize >= 64)
                {
                    bb |= (long)BinaryPrimitives.ReadUInt32LittleEndian(e[0x20..]) << 32;
                    it |= (long)BinaryPrimitives.ReadUInt32LittleEndian(e[0x28..]) << 32;
                }
                blockBitmap[g] = bb;
                inodeTable[g] = it;
                uninit[g] = (BinaryPrimitives.ReadUInt16LittleEndian(e[0x12..]) & 0x2) != 0;
            }
        }

        // טבלה שמצביעה אל מחוץ למחיצה אינה טבלה.
        if (inodeTable[0] <= 0 || inodeTable[0] >= super.BlocksCount) return null;
        return new ExtVolume(reader, super, blockBitmap, inodeTable, uninit);
    }

    /// <summary>
    /// איפה בלוק d של טבלת הקבוצות. רגיל — ברצף אחרי הכותרת. עם META_BG — כל בלוק
    /// של הטבלה יושב בתחילת הקבוצה הראשונה שהוא מתאר.
    /// </summary>
    private static long DescriptorBlock(ExtSuperblock super, long d, int perBlock)
    {
        if (!super.MetaBg || d < super.FirstMetaBg) return super.FirstDataBlock + 1 + d;
        long group = d * perBlock;
        return super.FirstDataBlock + group * super.BlocksPerGroup + (super.GroupHasSuperblock(group) ? 1 : 0);
    }

    public long ClusterToOffset(long cluster) => cluster * Super.BlockSize;

    public int ReadRaw(long offset, Span<byte> destination) => _reader.Read(offset, destination);

    internal bool Disconnected => _reader.Disconnected;

    /// <summary>קריאת מטא-דאטה דרך מטמון של גושים בני 64KB: טבלאות אינודים נקראות שוב ושוב.</summary>
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
                if (_cache.Count >= CacheLimit) _cache.Clear();
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

    internal byte[]? ReadBlock(long block)
        => block <= 0 || block >= Super.BlocksCount ? null : ReadCached(block * Super.BlockSize, Super.BlockSize);

    /// <summary>הבית שבו יושב אינוד n, והבלוק שמכיל אותו.</summary>
    internal long InodeOffset(long number)
    {
        long group = (number - 1) / Super.InodesPerGroup;
        long index = (number - 1) % Super.InodesPerGroup;
        if (group >= _inodeTable.Length) return -1;
        return _inodeTable[group] * Super.BlockSize + index * Super.InodeSize;
    }

    internal ExtInode? ReadInode(long number)
    {
        if (number < 1 || number > Super.InodesCount) return null;
        long at = InodeOffset(number);
        if (at < 0) return null;
        var raw = ReadCached(at, Super.InodeSize);
        return raw is null ? null : ExtInode.Parse(number, raw);
    }

    internal List<Model.DataExtent>? ExtentsOf(ExtInode inode)
        => inode.Extents(ReadBlock, Super.BlockSize, Super.BlocksCount);

    /// <summary>התוכן של תיקייה או של קובץ קטן — עד גבול, כדי שאינוד פגום לא יבקש ג'יגה-בתים.</summary>
    internal byte[]? ReadContent(ExtInode inode, int limit)
    {
        if (inode.InlineContent() is { } inline) return inline;
        var extents = ExtentsOf(inode);
        if (extents is null) return null;

        long size = Math.Min(inode.Size, limit);
        byte[] data = new byte[size];
        long at = 0;
        foreach (var e in extents)
        {
            for (long i = 0; i < e.ClusterCount && at < size; i++)
            {
                int take = (int)Math.Min(Super.BlockSize, size - at);
                if (!e.IsSparse)
                {
                    var block = ReadBlock(e.StartCluster + i);
                    if (block is null) return null;
                    block.AsSpan(0, take).CopyTo(data.AsSpan((int)at));
                }
                at += take;
            }
        }
        return data;
    }

    // ------------------------------------------------------------ הקצאה

    public bool? IsClusterAllocated(long cluster)
    {
        if (cluster < Super.FirstDataBlock || cluster >= Super.BlocksCount) return null;
        long group = (cluster - Super.FirstDataBlock) / Super.BlocksPerGroup;
        long bit = (cluster - Super.FirstDataBlock) % Super.BlocksPerGroup;

        // קבוצה שמפת הבלוקים שלה מעולם לא אותחלה — ריקה (חוץ מהטבלאות שבה).
        if (_blockUninit[group]) return false;

        byte[]? bitmap;
        lock (_bitmaps)
        if (!_bitmaps.TryGetValue(group, out bitmap))
        {
            bitmap = ReadBlock(_blockBitmap[group]);
            _bitmaps[group] = bitmap;
        }
        if (bitmap is null || bit / 8 >= bitmap.Length) return null;
        return (bitmap[bit / 8] & (1 << (int)(bit % 8))) != 0;
    }

    internal long GroupCount => _inodeTable.Length;
    internal long InodeTableOf(long group) => _inodeTable[group];

    /// <summary>המחיצה אינה הבעלים של הקורא — מי שפתח אותו סוגר.</summary>
    public void Dispose() { }
}
