using System.Buffers.Binary;
using RAF.Core.Model;
using RAF.Core.Native;

namespace RAF.Core.FileSystems.Apfs;

/// <summary>מפתח ורשומה בצומת של עץ.</summary>
internal readonly record struct ApfsEntry(byte[] Key, byte[] Value);

/// <summary>
/// "מכולה" של APFS — מערכת הקבצים של מק מאז 2017. בתוכה כמה כרכים (אצל מק: מערכת, נתונים,
/// שחזור...), שחולקים את אותו מקום. כל בלוק מתחיל בטביעת אצבע (פלטשר 64). עצמים "וירטואליים"
/// (כמו העצים של הכרכים) מתורגמים למקום בדיסק דרך "מפת עצמים", לפי מספר הגרסה.
/// "אשכול" במונחי התוכנה הוא בלוק של APFS (בדרך כלל 4KB).
/// </summary>
internal sealed class ApfsVolume : IClusterVolume
{
    private readonly VolumeReader _reader;
    internal int BlockSize { get; private init; }
    internal long BlockCount { get; private init; }
    internal ulong Xid { get; private init; }
    internal ulong OmapOid { get; private init; }
    internal List<ulong> FsOids { get; } = new();
    public int BytesPerCluster => BlockSize;

    private ApfsVolume(VolumeReader reader) => _reader = reader;

    internal static ApfsVolume? Open(VolumeReader reader)
    {
        var b0 = reader.ReadBlock(0, 4096);
        if (b0.Length < 4096 || !b0.AsSpan(32, 4).SequenceEqual("NXSB"u8)) return null;
        int bs = (int)BinaryPrimitives.ReadUInt32LittleEndian(b0.AsSpan(36));
        if (bs is < 4096 or > 65536) return null;
        var sb = bs == 4096 ? b0 : reader.ReadBlock(0, bs);

        // הכותרת העדכנית ביותר — באזור נקודות הביקורת (בלוק 0 עשוי להיות ישן).
        uint descBlocks = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(104));
        long descBase = BinaryPrimitives.ReadInt64LittleEndian(sb.AsSpan(112));
        if (descBase > 0 && (descBlocks & 0x80000000) == 0)
            for (long i = 0; i < Math.Min(descBlocks, 10000u); i++)
            {
                var b = reader.ReadBlock((descBase + i) * bs, bs);
                if (b.Length == bs && b.AsSpan(32, 4).SequenceEqual("NXSB"u8) && ChecksumOk(b)
                    && BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(16)) > BinaryPrimitives.ReadUInt64LittleEndian(sb.AsSpan(16)))
                    sb = b;
            }

        var volume = new ApfsVolume(reader)
        {
            BlockSize = bs,
            BlockCount = (long)BinaryPrimitives.ReadUInt64LittleEndian(sb.AsSpan(40)),
            Xid = BinaryPrimitives.ReadUInt64LittleEndian(sb.AsSpan(16)),
            OmapOid = BinaryPrimitives.ReadUInt64LittleEndian(sb.AsSpan(160)),
        };
        int max = (int)Math.Min(100, BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(180)));
        for (int i = 0; i < max; i++)
        {
            ulong oid = BinaryPrimitives.ReadUInt64LittleEndian(sb.AsSpan(184 + i * 8));
            if (oid != 0) volume.FsOids.Add(oid);
        }
        return volume;
    }

    /// <summary>פלטשר 64 על הבלוק (בלי 8 הבתים הראשונים) — כך APFS מאמת כל בלוק.</summary>
    internal static bool ChecksumOk(ReadOnlySpan<byte> block)
    {
        ulong sum1 = 0, sum2 = 0;
        const ulong mod = 0xFFFFFFFF;
        for (int i = 8; i + 4 <= block.Length; i += 4)
        {
            sum1 = (sum1 + BinaryPrimitives.ReadUInt32LittleEndian(block[i..])) % mod;
            sum2 = (sum2 + sum1) % mod;
        }
        ulong c1 = mod - (sum1 + sum2) % mod, c2 = mod - (sum1 + c1) % mod;
        return BinaryPrimitives.ReadUInt64LittleEndian(block) == (c2 << 32 | c1);
    }

    internal byte[]? Block(ulong paddr)
    {
        if (paddr == 0 || (long)paddr >= BlockCount) return null;
        var b = _reader.ReadBlock((long)paddr * BlockSize, BlockSize);
        return b.Length == BlockSize && ChecksumOk(b) ? b : null;
    }

    internal byte[] RawBlock(long paddr) => _reader.ReadBlock(paddr * BlockSize, BlockSize);

    // ------------------------------------------------------------ עצים

    /// <summary>
    /// הרשומות בצומת: טבלת תוכן (מפתח והיסט של הערך), המפתחות מתחילים אחרי הטבלה, הערכים
    /// נמדדים אחורה מסוף הצומת (בשורש — לפני 40 בתים של מידע על העץ).
    /// </summary>
    internal static List<ApfsEntry> Entries(byte[] node)
    {
        var list = new List<ApfsEntry>();
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(32));
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(36));
        int tocOff = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(40)), tocLen = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(42));
        int toc = 56 + tocOff, keys = toc + tocLen, values = node.Length - ((flags & 1) != 0 ? 40 : 0);
        bool fixedSize = (flags & 4) != 0, leaf = (flags & 2) != 0;
        for (int i = 0; i < count; i++)
        {
            int kOff, kLen, vOff, vLen;
            if (fixedSize)
            {
                if (toc + i * 4 + 4 > keys) break;
                kOff = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(toc + i * 4));
                vOff = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(toc + i * 4 + 2));
                kLen = 16;
                vLen = leaf ? 16 : 8;
            }
            else
            {
                if (toc + i * 8 + 8 > keys) break;
                kOff = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(toc + i * 8));
                kLen = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(toc + i * 8 + 2));
                vOff = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(toc + i * 8 + 4));
                vLen = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(toc + i * 8 + 6));
            }
            if (vOff == 0xFFFF) continue;
            int k = keys + kOff, v = values - vOff;
            if (k + kLen > node.Length || v < 0 || v + vLen > node.Length) continue;
            list.Add(new ApfsEntry(node.AsSpan(k, kLen).ToArray(), node.AsSpan(v, vLen).ToArray()));
        }
        return list;
    }

    internal static bool IsLeaf(byte[] node) => (BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(32)) & 2) != 0;

    /// <summary>כל רשומות העלים בעץ. resolve — תרגום מספר צאצא למקום (בעץ וירטואלי — דרך מפת העצמים).</summary>
    internal IEnumerable<ApfsEntry> Walk(ulong root, Func<ulong, ulong?> resolve, CancellationToken token = default)
    {
        var stack = new Stack<(ulong, int)>();
        stack.Push((root, 0));
        while (stack.Count > 0 && !token.IsCancellationRequested)
        {
            var (paddr, depth) = stack.Pop();
            if (depth > 16 || Block(paddr) is not { } node) continue;
            var entries = Entries(node);
            if (IsLeaf(node)) { foreach (var e in entries) yield return e; continue; }
            for (int i = entries.Count - 1; i >= 0; i--)
                if (entries[i].Value.Length >= 8 && resolve(BinaryPrimitives.ReadUInt64LittleEndian(entries[i].Value)) is { } child)
                    stack.Push((child, depth + 1));
        }
    }

    /// <summary>מפת עצמים: לכל עצם וירטואלי — המקום בגרסה העדכנית ביותר שאינה אחרי xid.</summary>
    internal Dictionary<ulong, ulong> LoadOmap(ulong omapPaddr)
    {
        var map = new Dictionary<ulong, (ulong Xid, ulong Paddr)>();
        if (Block(omapPaddr) is not { } omap) return new();
        ulong tree = BinaryPrimitives.ReadUInt64LittleEndian(omap.AsSpan(48));
        foreach (var e in Walk(tree, c => c))
        {
            if (e.Key.Length < 16 || e.Value.Length < 16) continue;
            ulong oid = BinaryPrimitives.ReadUInt64LittleEndian(e.Key), xid = BinaryPrimitives.ReadUInt64LittleEndian(e.Key.AsSpan(8));
            if (xid > Xid || (BinaryPrimitives.ReadUInt32LittleEndian(e.Value) & 1) != 0) continue;   // נמחק
            if (!map.TryGetValue(oid, out var had) || had.Xid < xid) map[oid] = (xid, BinaryPrimitives.ReadUInt64LittleEndian(e.Value.AsSpan(8)));
        }
        return map.ToDictionary(m => m.Key, m => m.Value.Paddr);
    }

    public long ClusterToOffset(long cluster) => cluster * BlockSize;
    public int ReadRaw(long offset, Span<byte> destination) => _reader.Read(offset, destination);
    public bool? IsClusterAllocated(long cluster) => Allocation?.Invoke(cluster);
    internal Func<long, bool?>? Allocation { get; set; }
    public void Dispose() { }
}
