using System.Buffers.Binary;
using RAF.Core.Model;

namespace RAF.Core.FileSystems.Ext;

/// <summary>
/// רשומת קובץ ("אינוד") של ext2/3/4. בשונה מ-NTFS השם אינו כאן — הוא רק ברשומת
/// התיקייה שמצביעה על האינוד. כאן: סוג, גודל, תאריכים, ואיפה התוכן.
///
/// מיקום התוכן: ב-ext4 עץ מקטעים ("מ-בלוק X, Y בלוקים ברצף"), וב-ext2/3 רשימת
/// בלוקים — 12 ישירים ואחריהם בלוקים של מצביעים ברמה אחת, שתיים ושלוש.
/// </summary>
internal sealed class ExtInode
{
    internal const uint ExtentsFlag = 0x80000;
    internal const uint InlineDataFlag = 0x10000000;
    internal const ushort ExtentMagic = 0xF30A;

    internal long Number { get; init; }
    internal ushort Mode { get; init; }
    internal long Size { get; init; }
    internal int Links { get; init; }
    internal uint Flags { get; init; }
    internal uint DeletionTime { get; init; }
    internal DateTime? Accessed { get; init; }
    internal DateTime? Modified { get; init; }
    internal DateTime? Changed { get; init; }
    internal DateTime? Created { get; init; }
    internal byte[] Raw { get; init; } = Array.Empty<byte>();

    internal ReadOnlySpan<byte> BlockArea => Raw.AsSpan(0x28, 60);

    internal int Type => Mode & 0xF000;
    internal bool IsDirectory => Type == 0x4000;
    internal bool IsRegular => Type == 0x8000;
    internal bool IsSymlink => Type == 0xA000;
    internal bool UsesExtents => (Flags & ExtentsFlag) != 0;
    internal bool HasInlineData => (Flags & InlineDataFlag) != 0;

    /// <summary>האינוד בשימוש: יש לו קישורים, ולא נרשם לו מועד מחיקה.</summary>
    internal bool InUse => Links > 0 && DeletionTime == 0 && Mode != 0;

    /// <summary>
    /// האם נשאר מידע על מיקום התוכן. ext3/4 מאפסים אותו במחיקה (ext2 — לא);
    /// אז העותק הישן ביומן הוא התקווה היחידה.
    /// </summary>
    internal bool HasBlockMap
    {
        get
        {
            if (HasInlineData) return true;
            if (UsesExtents)
                return BinaryPrimitives.ReadUInt16LittleEndian(BlockArea) == ExtentMagic &&
                       BinaryPrimitives.ReadUInt16LittleEndian(BlockArea[2..]) > 0;
            foreach (byte b in BlockArea) if (b != 0) return true;
            return false;
        }
    }

    internal static ExtInode Parse(long number, byte[] raw)
    {
        ushort mode = BinaryPrimitives.ReadUInt16LittleEndian(raw);
        long size = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0x04)) |
                    (long)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0x6C)) << 32;

        int extra = raw.Length > 0x82 ? BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(0x80)) : 0;

        return new ExtInode
        {
            Number = number,
            Mode = mode,
            Size = size,
            Links = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(0x1A)),
            Flags = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0x20)),
            DeletionTime = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0x14)),
            Accessed = Time(raw, 0x08, extra >= 0x10 ? 0x8C : -1),
            Changed = Time(raw, 0x0C, extra >= 0x08 ? 0x84 : -1),
            Modified = Time(raw, 0x10, extra >= 0x0C ? 0x88 : -1),
            Created = extra >= 0x18 && raw.Length >= 0x98 ? Time(raw, 0x90, 0x94) : null,
            Raw = raw,
        };
    }

    /// <summary>
    /// שניות מ-1970 (עם סימן). השדה הנוסף מוסיף שתי סיביות של "תקופה" — כך
    /// ext4 עובר את 2038 — וננו-שניות.
    /// </summary>
    private static DateTime? Time(byte[] raw, int at, int extraAt)
    {
        if (at + 4 > raw.Length) return null;
        long seconds = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(at));
        long ticks = 0;
        if (extraAt >= 0 && extraAt + 4 <= raw.Length)
        {
            uint extra = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(extraAt));
            seconds += (long)(extra & 3) << 32;
            ticks = (extra >> 2) / 100;
        }
        if (seconds == 0) return null;
        try { return DateTime.UnixEpoch.AddSeconds(seconds).AddTicks(ticks).ToLocalTime(); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    // ------------------------------------------------------------ מיקום התוכן

    /// <summary>
    /// מקטעי התוכן לפי הסדר בקובץ, עם חורים (אזור שמעולם לא נכתב) כמקטעים דלילים.
    /// null — המבנה פגום (למשל בלוק מצביעים שנדרס).
    /// </summary>
    internal List<DataExtent>? Extents(Func<long, byte[]?> readBlock, int blockSize, long totalBlocks)
    {
        long needed = (Size + blockSize - 1) / blockSize;
        var runs = new List<(long Logical, long Physical, long Count, bool Unwritten)>();

        bool ok = UsesExtents
            ? WalkExtentTree(BlockArea.ToArray(), 0, readBlock, runs, totalBlocks, depthLimit: 6)
            : WalkBlockMap(readBlock, blockSize, runs, totalBlocks, needed);
        if (!ok) return null;

        runs.Sort((a, b) => a.Logical.CompareTo(b.Logical));
        var extents = new List<DataExtent>();
        long at = 0;
        foreach (var run in runs)
        {
            if (run.Logical >= needed) break;
            if (run.Logical < at) return null;   // מקטעים חופפים — מבנה פגום
            if (run.Logical > at) extents.Add(new DataExtent(0, run.Logical - at, IsSparse: true));
            long count = Math.Min(run.Count, needed - run.Logical);
            // מקטע שהוקצה ולא נכתב (fallocate) נקרא כאפסים.
            extents.Add(run.Unwritten ? new DataExtent(0, count, true) : new DataExtent(run.Physical, count, false));
            at = run.Logical + count;
        }
        if (at < needed) extents.Add(new DataExtent(0, needed - at, IsSparse: true));
        return extents;
    }

    private static bool WalkExtentTree(byte[] node, int offset, Func<long, byte[]?> readBlock,
        List<(long, long, long, bool)> runs, long totalBlocks, int depthLimit)
    {
        var span = node.AsSpan(offset);
        if (span.Length < 12 || BinaryPrimitives.ReadUInt16LittleEndian(span) != ExtentMagic) return false;
        int entries = BinaryPrimitives.ReadUInt16LittleEndian(span[2..]);
        int depth = BinaryPrimitives.ReadUInt16LittleEndian(span[6..]);
        if (depth > depthLimit || 12 + entries * 12 > span.Length) return false;

        for (int i = 0; i < entries; i++)
        {
            var e = span.Slice(12 + i * 12, 12);
            uint logical = BinaryPrimitives.ReadUInt32LittleEndian(e);
            if (depth == 0)
            {
                int length = BinaryPrimitives.ReadUInt16LittleEndian(e[4..]);
                bool unwritten = length > 32768;
                if (unwritten) length -= 32768;
                long start = (long)BinaryPrimitives.ReadUInt16LittleEndian(e[6..]) << 32 | BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
                if (length == 0) continue;
                if (start + length > totalBlocks) return false;
                runs.Add((logical, start, length, unwritten));
            }
            else
            {
                long child = (long)BinaryPrimitives.ReadUInt16LittleEndian(e[8..]) << 32 | BinaryPrimitives.ReadUInt32LittleEndian(e[4..]);
                if (child <= 0 || child >= totalBlocks) return false;
                var block = readBlock(child);
                if (block is null || !WalkExtentTree(block, 0, readBlock, runs, totalBlocks, depth - 1)) return false;
            }
        }
        return true;
    }

    private bool WalkBlockMap(Func<long, byte[]?> readBlock, int blockSize,
        List<(long, long, long, bool)> runs, long totalBlocks, long needed)
    {
        int perBlock = blockSize / 4;
        long logical = 0;

        void Add(long physical)
        {
            if (physical != 0)
            {
                if (runs.Count > 0 && runs[^1] is var last && last.Item1 + last.Item3 == logical && last.Item2 + last.Item3 == physical)
                    runs[^1] = (last.Item1, last.Item2, last.Item3 + 1, false);
                else
                    runs.Add((logical, physical, 1, false));
            }
            logical++;
        }

        bool Indirect(long block, int level)
        {
            long span = level switch { 1 => perBlock, 2 => (long)perBlock * perBlock, _ => (long)perBlock * perBlock * perBlock };
            if (block == 0) { logical += span; return true; }
            if (block >= totalBlocks) return false;
            var data = readBlock(block);
            if (data is null) return false;
            for (int i = 0; i < perBlock && logical < needed; i++)
            {
                long child = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i * 4));
                if (child >= totalBlocks) return false;
                if (level == 1) Add(child);
                else if (!Indirect(child, level - 1)) return false;
            }
            return true;
        }

        var area = BlockArea;
        for (int i = 0; i < 12 && logical < needed; i++)
        {
            long b = BinaryPrimitives.ReadUInt32LittleEndian(area[(i * 4)..]);
            if (b >= totalBlocks) return false;
            Add(b);
        }
        for (int level = 1; level <= 3 && logical < needed; level++)
            if (!Indirect(BinaryPrimitives.ReadUInt32LittleEndian(area[((11 + level) * 4)..]), level)) return false;
        return true;
    }

    /// <summary>
    /// תוכן ששמור בתוך האינוד עצמו: קובץ קטן עם "נתונים מוטבעים" (60 הבתים של אזור
    /// הבלוקים, ואם צריך — המשך בתכונה system.data שבסוף האינוד), או קיצור דרך קצר.
    /// </summary>
    internal byte[]? InlineContent()
    {
        if (IsSymlink && !HasInlineData && Size < 60 && !UsesExtents)
            return BlockArea[..(int)Size].ToArray();
        if (!HasInlineData) return null;

        var data = new List<byte>(BlockArea[..(int)Math.Min(60, Size)].ToArray());
        if (Size > 60 && InlineAttribute("data") is { } more)
            data.AddRange(more.Take((int)Math.Min(more.Length, Size - 60)));
        return data.ToArray();
    }

    /// <summary>תכונה מורחבת מהמרחב system (מספר 7) ששמורה בתוך האינוד, אחרי השדות הקבועים.</summary>
    private byte[]? InlineAttribute(string name)
    {
        if (Raw.Length <= 0x84) return null;
        int start = 0x80 + BinaryPrimitives.ReadUInt16LittleEndian(Raw.AsSpan(0x80));
        if (start + 4 > Raw.Length || BinaryPrimitives.ReadUInt32LittleEndian(Raw.AsSpan(start)) != 0xEA020000) return null;

        int entries = start + 4;
        for (int at = entries; at + 16 <= Raw.Length; )
        {
            int nameLen = Raw[at];
            if (nameLen == 0 && BinaryPrimitives.ReadUInt32LittleEndian(Raw.AsSpan(at)) == 0) break;
            int index = Raw[at + 1];
            int valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(Raw.AsSpan(at + 2));
            int valueSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(Raw.AsSpan(at + 8));
            if (at + 16 + nameLen > Raw.Length) break;
            string n = System.Text.Encoding.ASCII.GetString(Raw, at + 16, nameLen);
            if (index == 7 && n == name && entries + valueOffset + valueSize <= Raw.Length)
                return Raw.AsSpan(entries + valueOffset, valueSize).ToArray();
            at += (16 + nameLen + 3) & ~3;
        }
        return null;
    }
}
