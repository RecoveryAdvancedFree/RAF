using System.Buffers.Binary;
using System.Text;
using RAF.Core.Native;

namespace RAF.Core.FileSystems.Btrfs;

/// <summary>מפתח של רשומה בעץ: מספר עצם, סוג, והיסט — הרשומות ממוינות לפיו.</summary>
internal readonly record struct BtrfsKey(ulong ObjectId, byte Type, ulong Offset);

/// <summary>רשומה בעלה של עץ: המפתח והנתונים שלה.</summary>
internal readonly record struct BtrfsItem(BtrfsKey Key, byte[] Leaf, int DataOffset, int DataSize)
{
    internal ReadOnlySpan<byte> Data => Leaf.AsSpan(DataOffset, DataSize);
}

/// <summary>
/// מחיצת btrfs פתוחה — מערכת הקבצים של שרתי Synology ושל הרבה הפצות לינוקס.
///
/// הכול שמור בעצים של בלוקים ("צמתים"), והכתובות בהם לוגיות: טבלת "נתחים" מתרגמת
/// כתובת לוגית למקום בדיסק. חלק מהטבלה שמור בכותרת עצמה (כדי שאפשר יהיה להתחיל),
/// והשאר — בעץ משלו. "אשכול" במונחי התוכנה הוא סקטור של btrfs (בדרך כלל 4KB),
/// במספור פיזי מתחילת המחיצה.
///
/// שינוי אף פעם לא נכתב במקום: כל בלוק שהשתנה נכתב במקום חדש, והישן נשאר עד שנדרס.
/// </summary>
internal sealed class BtrfsVolume : IClusterVolume
{
    internal const long SuperOffset = 0x10000;
    private readonly VolumeReader _reader;
    private readonly Dictionary<long, byte[]> _cache = new();

    internal Guid FsId { get; private set; }
    internal int NodeSize { get; private init; }
    internal int SectorSize { get; private init; }
    internal ulong Generation { get; private init; }
    internal ulong RootTree { get; private init; }
    internal ulong ChunkRoot { get; private init; }
    internal ulong NumDevices { get; private init; }
    internal ushort ChecksumType { get; private init; }
    internal string Label { get; private init; } = "";
    internal long Length => _reader.Length;

    /// <summary>הנתחים: טווח לוגי → מקום בדיסק. Type — נתונים (1), מערכת (2), מטא-דאטה (4), ופרופיל.</summary>
    internal List<(ulong Logical, ulong Length, ulong Physical, ulong Type)> Chunks { get; } = new();

    public int BytesPerCluster => SectorSize;

    private BtrfsVolume(VolumeReader reader) => _reader = reader;

    internal static bool IsBtrfs(ReadOnlySpan<byte> super) => super.Length >= 0x48 && super.Slice(0x40, 8).SequenceEqual("_BHRfS_M"u8);

    internal static BtrfsVolume? Open(VolumeReader reader)
    {
        var sb = reader.ReadBlock(SuperOffset, 4096);
        if (sb.Length < 4096 || !IsBtrfs(sb)) return null;

        int sectorSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(0x90));
        int nodeSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(0x94));
        if (sectorSize is < 512 or > 65536 || nodeSize is < 4096 or > 65536) return null;

        var volume = new BtrfsVolume(reader)
        {
            FsId = new Guid(sb.AsSpan(0x20, 16)),
            Generation = BinaryPrimitives.ReadUInt64LittleEndian(sb.AsSpan(0x48)),
            RootTree = BinaryPrimitives.ReadUInt64LittleEndian(sb.AsSpan(0x50)),
            ChunkRoot = BinaryPrimitives.ReadUInt64LittleEndian(sb.AsSpan(0x58)),
            NumDevices = BinaryPrimitives.ReadUInt64LittleEndian(sb.AsSpan(0x88)),
            SectorSize = sectorSize,
            NodeSize = nodeSize,
            ChecksumType = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(0xC4)),
            Label = Encoding.UTF8.GetString(sb.AsSpan(0x12B, 256)).TrimEnd('\0'),
        };
        // תכונה "fsid אחר ב-metadata_uuid": הבלוקים נושאים את המזהה הזה ולא את fsid.
        ulong incompat = BinaryPrimitives.ReadUInt64LittleEndian(sb.AsSpan(0xBC));
        if ((incompat & 0x400) != 0) volume.FsId = new Guid(sb.AsSpan(0x23B, 16));

        // הנתחים שבכותרת: מפתח (17 בתים) ואז רשומת נתח.
        int arraySize = (int)BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(0xA0));
        var array = sb.AsSpan(0x32B, Math.Min(arraySize, 2048));
        for (int at = 0; at + 17 + 48 <= array.Length; )
        {
            ulong logical = BinaryPrimitives.ReadUInt64LittleEndian(array[(at + 9)..]);
            at += 17;
            int size = volume.AddChunk(logical, array[at..]);
            if (size <= 0) break;
            at += size;
        }
        if (volume.Chunks.Count == 0) return null;

        // שאר הנתחים — מעץ הנתחים.
        foreach (var item in volume.Items(volume.ChunkRoot))
            if (item.Key.Type == 228 && !volume.Chunks.Any(c => c.Logical == item.Key.Offset))
                volume.AddChunk(item.Key.Offset, item.Data);
        volume.Chunks.Sort((a, b) => a.Logical.CompareTo(b.Logical));
        return volume;
    }

    /// <summary>רשומת נתח: אורך, סוג, ואז "רצועות" — בכונן אחד, הרצועה הראשונה היא המקום.</summary>
    private int AddChunk(ulong logical, ReadOnlySpan<byte> c)
    {
        if (c.Length < 48 + 32) return -1;
        ulong length = BinaryPrimitives.ReadUInt64LittleEndian(c);
        ulong type = BinaryPrimitives.ReadUInt64LittleEndian(c[24..]);
        int stripes = BinaryPrimitives.ReadUInt16LittleEndian(c[44..]);
        if (stripes == 0 || c.Length < 48 + stripes * 32) return -1;
        ulong physical = BinaryPrimitives.ReadUInt64LittleEndian(c[(48 + 8)..]);
        Chunks.Add((logical, length, physical, type));
        return 48 + stripes * 32;
    }

    /// <summary>
    /// פרופילים שמפזרים נתונים על כמה כוננים (RAID0/10/5/6 של btrfs עצמו) — הרצועה הראשונה
    /// אינה כל הנתח. כוננים של שרתי אחסון ביתיים מגיעים עם כונן אחד (המערך מתחת), ואז אין כאלה.
    /// </summary>
    internal bool HasStripedProfiles => Chunks.Any(c => (c.Type & (0x8 | 0x40 | 0x80 | 0x100)) != 0);

    /// <summary>כתובת לוגית → היסט בדיסק, וכמה בתים רצופים נשארו עד סוף הנתח. null — לא ממופה.</summary>
    internal (long Offset, long Contiguous)? Map(ulong logical)
    {
        int lo = 0, hi = Chunks.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var c = Chunks[mid];
            if (logical < c.Logical) hi = mid - 1;
            else if (logical >= c.Logical + c.Length) lo = mid + 1;
            else return ((long)(c.Physical + (logical - c.Logical)), (long)(c.Logical + c.Length - logical));
        }
        return null;
    }

    /// <summary>היסט בדיסק → כתובת לוגית (לבדיקה אם אשכול תפוס). null — מחוץ לכל נתח.</summary>
    internal ulong? Unmap(long physical)
    {
        foreach (var c in Chunks)
            if ((ulong)physical >= c.Physical && (ulong)physical < c.Physical + c.Length)
                return c.Logical + ((ulong)physical - c.Physical);
        return null;
    }

    public long ClusterToOffset(long cluster) => cluster * SectorSize;
    public int ReadRaw(long offset, Span<byte> destination) => _reader.Read(offset, destination);

    /// <summary>הקצאה: ממלאים מעץ ההקצאה בסורק (אחרי שנקרא), אחרת "לא ידוע".</summary>
    internal Func<long, bool?>? Allocation { get; set; }
    public bool? IsClusterAllocated(long cluster) => Allocation?.Invoke(cluster);

    /// <summary>צומת בעץ לפי כתובת לוגית, אחרי בדיקה שהוא באמת של מערכת הקבצים הזו ובמקום הזה.</summary>
    internal byte[]? ReadNode(ulong logical)
    {
        lock (_cache)
            if (_cache.TryGetValue((long)logical, out var cached)) return cached;
        if (Map(logical) is not { } at || at.Contiguous < NodeSize) return null;
        var node = _reader.ReadBlock(at.Offset, NodeSize);
        if (node.Length != NodeSize || !IsNode(node, logical)) return null;
        lock (_cache)
        {
            if (_cache.Count > 4096) _cache.Clear();
            _cache[(long)logical] = node;
        }
        return node;
    }

    /// <summary>כותרת צומת: מזהה מערכת הקבצים בהיסט 32, והכתובת של הצומת עצמו בהיסט 48.</summary>
    internal bool IsNode(byte[] node, ulong logical)
        => new Guid(node.AsSpan(32, 16)) == FsId && BinaryPrimitives.ReadUInt64LittleEndian(node.AsSpan(48)) == logical;

    /// <summary>
    /// טביעת האצבע של הבלוק תקינה — כלומר הוא לא נדרס בחלקו. רק CRC32C (ברירת המחדל) נבדק;
    /// בשיטות האחרות — מסתמכים על המזהה והכתובת שבכותרת.
    /// </summary>
    internal bool ChecksumOk(ReadOnlySpan<byte> node)
        => ChecksumType != 0 || Crc32C.Compute(node[32..]) == BinaryPrimitives.ReadUInt32LittleEndian(node);

    /// <summary>קריאה ישירה של טווח לוגי רצוף (בתוך נתח אחד).</summary>
    internal byte[]? ReadLogical(ulong logical, int length)
    {
        if (Map(logical) is not { } at || at.Contiguous < length) return null;
        var data = _reader.ReadBlock(at.Offset, length);
        return data.Length == length ? data : null;
    }

    internal static ulong NodeGeneration(byte[] node) => BinaryPrimitives.ReadUInt64LittleEndian(node.AsSpan(80));
    internal static ulong NodeOwner(byte[] node) => BinaryPrimitives.ReadUInt64LittleEndian(node.AsSpan(88));
    internal static int NodeLevel(byte[] node) => node[100];

    /// <summary>הרשומות שבעלה (צומת ברמה 0).</summary>
    internal static IEnumerable<BtrfsItem> LeafItems(byte[] leaf)
    {
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(leaf.AsSpan(96));
        for (int i = 0; i < count && 101 + (i + 1) * 25 <= leaf.Length; i++)
        {
            int at = 101 + i * 25;
            var key = new BtrfsKey(BinaryPrimitives.ReadUInt64LittleEndian(leaf.AsSpan(at)), leaf[at + 8],
                BinaryPrimitives.ReadUInt64LittleEndian(leaf.AsSpan(at + 9)));
            int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(leaf.AsSpan(at + 17));
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(leaf.AsSpan(at + 21));
            if (101 + offset + size > leaf.Length) yield break;
            yield return new BtrfsItem(key, leaf, 101 + offset, size);
        }
    }

    /// <summary>כל הרשומות בעץ, לפי הסדר. צומת שאינו נקרא — מדלגים עליו.</summary>
    internal IEnumerable<BtrfsItem> Items(ulong root, CancellationToken token = default)
    {
        var stack = new Stack<(ulong Logical, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0 && !token.IsCancellationRequested)
        {
            var (logical, depth) = stack.Pop();
            if (depth > 16 || ReadNode(logical) is not { } node) continue;
            if (NodeLevel(node) == 0)
            {
                foreach (var item in LeafItems(node)) yield return item;
                continue;
            }
            int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(96));
            // מכניסים מהסוף כדי שהראשון ייצא ראשון.
            for (int i = Math.Min(count, (node.Length - 101) / 33) - 1; i >= 0; i--)
                stack.Push((BinaryPrimitives.ReadUInt64LittleEndian(node.AsSpan(101 + i * 33 + 17)), depth + 1));
        }
    }

    public void Dispose() { }
}
