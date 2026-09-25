using System.Buffers.Binary;
using RAF.Core.Model;

namespace RAF.Core.FileSystems.Xfs;

/// <summary>
/// אינוד של XFS: כותרת קבועה, ואחריה "מזלג הנתונים" — התוכן עצמו (קובץ או תיקייה
/// קטנים), רשימת מקטעים, או שורש של עץ מקטעים.
///
/// מחיקה ב-XFS מאפסת את הסוג, הגודל ומספר המקטעים — אבל לא את רשומות המקטעים
/// עצמן, שנשארות במזלג. מהן אפשר לדעת איפה היה התוכן, כמו שעושה xfs_undelete.
/// </summary>
internal sealed class XfsInode
{
    internal ulong Number { get; init; }
    internal ushort Mode { get; init; }
    internal int Format { get; init; }       // 1 מקומי, 2 מקטעים, 3 עץ
    internal long Size { get; init; }
    internal long ExtentCount { get; init; }
    internal int Links { get; init; }
    internal DateTime? Accessed { get; init; }
    internal DateTime? Modified { get; init; }
    internal DateTime? Changed { get; init; }
    internal DateTime? Created { get; init; }
    internal byte[] Raw { get; init; } = Array.Empty<byte>();
    internal int CoreSize { get; init; }
    internal int ForkSize { get; init; }

    internal int Type => Mode & 0xF000;
    internal bool IsDirectory => Type == 0x4000;
    internal bool IsRegular => Type == 0x8000;
    internal bool InUse => Mode != 0;

    internal ReadOnlySpan<byte> DataFork => Raw.AsSpan(CoreSize, ForkSize);

    internal static XfsInode? Parse(ulong number, byte[] raw, bool v5)
    {
        if (raw.Length < 176 || raw[0] != (byte)'I' || raw[1] != (byte)'N') return null;
        int version = raw[4];
        int core = version >= 3 ? 176 : 100;
        ulong flags2 = version >= 3 ? BinaryPrimitives.ReadUInt64BigEndian(raw.AsSpan(120)) : 0;
        bool bigTime = (flags2 & 0x8) != 0;
        bool bigExtents = (flags2 & 0x10) != 0;

        int forkOffset = raw[82] * 8;
        int forkSize = forkOffset > 0 ? forkOffset : raw.Length - core;
        if (core + forkSize > raw.Length) forkSize = raw.Length - core;

        return new XfsInode
        {
            Number = number,
            Mode = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(2)),
            Format = raw[5],
            Links = (int)(version >= 2 ? BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(16)) : BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(6))),
            Accessed = Time(raw, 32, bigTime),
            Modified = Time(raw, 40, bigTime),
            Changed = Time(raw, 48, bigTime),
            Created = version >= 3 ? Time(raw, 144, bigTime) : null,
            Size = (long)BinaryPrimitives.ReadUInt64BigEndian(raw.AsSpan(56)),
            ExtentCount = bigExtents ? (long)BinaryPrimitives.ReadUInt64BigEndian(raw.AsSpan(24)) : BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(76)),
            Raw = raw,
            CoreSize = core,
            ForkSize = forkSize,
        };
    }

    /// <summary>
    /// זמן: שניות ונאנו-שניות מ-1970; עם "זמן גדול" — מספר אחד של נאנו-שניות
    /// מ-1901 (כך XFS עובר את 2038).
    /// </summary>
    private static DateTime? Time(byte[] raw, int at, bool big)
    {
        long seconds, nanos;
        if (big)
        {
            ulong value = BinaryPrimitives.ReadUInt64BigEndian(raw.AsSpan(at));
            seconds = (long)(value / 1_000_000_000) - 2147483648L;
            nanos = (long)(value % 1_000_000_000);
        }
        else
        {
            seconds = BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(at));
            nanos = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(at + 4));
        }
        if (seconds == 0 || nanos >= 1_000_000_000) return null;
        try { return DateTime.UnixEpoch.AddSeconds(seconds).AddTicks(nanos / 100).ToLocalTime(); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>רשומת מקטע: 128 סיביות — דגל "לא נכתב", מיקום בקובץ, בלוק, ואורך.</summary>
    internal static (long Offset, ulong Block, long Count, bool Unwritten) Record(ReadOnlySpan<byte> r)
    {
        ulong hi = BinaryPrimitives.ReadUInt64BigEndian(r);
        ulong lo = BinaryPrimitives.ReadUInt64BigEndian(r[8..]);
        bool unwritten = (hi >> 63) != 0;
        long offset = (long)((hi >> 9) & ((1UL << 54) - 1));
        ulong block = ((hi & 0x1FF) << 43) | (lo >> 21);
        long count = (long)(lo & ((1UL << 21) - 1));
        return (offset, block, count, unwritten);
    }

    /// <summary>
    /// מקטעי התוכן, לפי הסדר בקובץ. אינוד שנמחק: הרשומות שנשארו במזלג, כל עוד הן
    /// נראות תקינות (אורך חיובי, בתוך המחיצה, בסדר עולה). null — אין מה לקרוא.
    /// </summary>
    internal List<(long Offset, long Block, long Count, bool Unwritten)>? Runs(XfsVolume volume)
    {
        var sb = volume.Super;
        var runs = new List<(long, long, long, bool)>();

        if (Format == 3) return TreeRuns(volume);
        if (Format != 2) return null;
        var area = DataFork;
        long limit = InUse ? Math.Min(ExtentCount, area.Length / 16) : area.Length / 16;
        long lastEnd = -1;
        for (int i = 0; i < limit; i++)
        {
            var (offset, block, blocks, unwritten) = Record(area.Slice(i * 16, 16));
            if (blocks == 0) break;
            long linear = sb.Linear(block);
            if (linear + blocks > sb.AgCount * sb.AgBlocks || offset < lastEnd) { if (InUse) return null; break; }
            runs.Add((offset, linear, blocks, unwritten));
            lastEnd = offset + blocks;
        }
        if (runs.Count == 0 && !InUse)
        {
            // קובץ מפוצל שנמחק: המחיקה הופכת את הסוג ל"מקטעים", אבל שורש העץ נשאר במזלג,
            // והבלוקים של העץ נשארים בחלל הפנוי עד שנכתב עליהם — מזוהים לפי החתימה שלהם.
            return TreeRuns(volume);
        }
        return runs;
    }

    /// <summary>שורש עץ המקטעים שבמזלג: רמה, מספר רשומות, מפתחות ואז מצביעים.</summary>
    private List<(long Offset, long Block, long Count, bool Unwritten)>? TreeRuns(XfsVolume volume)
    {
        var fork = DataFork;
        int level = BinaryPrimitives.ReadUInt16BigEndian(fork);
        int count = BinaryPrimitives.ReadUInt16BigEndian(fork[2..]);
        int max = (fork.Length - 4) / 16;
        if (level is 0 or > 8 || count == 0 || count > max) return null;
        if (InUse) return Walk(max);

        // המצביעים יושבים אחרי max מפתחות, ו-max נגזר מגודל המזלג. בקובץ עם תכונות נוספות
        // המזלג היה קטן יותר — וגבול המזלג אופס במחיקה. מנסים כל גודל; העץ עצמו מאמת.
        for (int m = max; m >= count; m--)
            if (Walk(m) is { } runs) return runs;
        return null;

        List<(long, long, long, bool)>? Walk(int keys)
        {
            var runs = new List<(long, long, long, bool)>();
            for (int i = 0; i < count; i++)
            {
                ulong child = BinaryPrimitives.ReadUInt64BigEndian(DataFork[(4 + keys * 8 + i * 8)..]);
                if (!volume.WalkBmapBlock(volume.Super.Linear(child), level - 1, runs, 0, Number)) return null;
            }
            return runs;
        }
    }

    /// <summary>המקטעים כרשימה רציפה לשחזור, עם חורים דלילים; size — עד כמה.</summary>
    internal static List<DataExtent> ToExtents(List<(long Offset, long Block, long Count, bool Unwritten)> runs, long blocksNeeded)
    {
        var extents = new List<DataExtent>();
        long at = 0;
        foreach (var run in runs.OrderBy(r => r.Offset))
        {
            if (run.Offset >= blocksNeeded) break;
            if (run.Offset < at) continue;
            if (run.Offset > at) extents.Add(new DataExtent(0, run.Offset - at, true));
            long count = Math.Min(run.Count, blocksNeeded - run.Offset);
            extents.Add(run.Unwritten ? new DataExtent(0, count, true) : new DataExtent(run.Block, count, false));
            at = run.Offset + count;
        }
        if (at < blocksNeeded) extents.Add(new DataExtent(0, blocksNeeded - at, true));
        return extents;
    }
}
