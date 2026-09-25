using System.Buffers.Binary;
using System.Text;

namespace RAF.Core.FileSystems.Xfs;

/// <summary>
/// הכותרת של מחיצת XFS, בתחילת המחיצה (כל המספרים ב-XFS בסדר בתים "גדול קודם").
/// המחיצה מחולקת ל"קבוצות הקצאה" (AG) שוות; מספר בלוק ומספר אינוד מקודדים
/// כ"מספר קבוצה" בסיביות העליונות ו"מיקום בתוך הקבוצה" בתחתונות.
/// </summary>
internal sealed class XfsSuperblock
{
    internal int BlockSize { get; private init; }
    internal long DataBlocks { get; private init; }
    internal ulong RootInode { get; private init; }
    internal long AgBlocks { get; private init; }
    internal long AgCount { get; private init; }
    internal int SectorSize { get; private init; }
    internal int InodeSize { get; private init; }
    internal int InodesPerBlockLog { get; private init; }
    internal int AgBlocksLog { get; private init; }
    internal int DirBlockLog { get; private init; }
    internal int Version { get; private init; }
    internal uint Features2 { get; private init; }
    internal uint Incompat { get; private init; }
    internal string Label { get; private init; } = "";

    /// <summary>גרסה 5: לכל מבנה יש סכום ביקורת, וכותרות הבלוקים ארוכות יותר.</summary>
    internal bool IsV5 => Version == 5;

    /// <summary>סוג הקובץ שמור גם ברשומת התיקייה (בית נוסף אחרי השם).</summary>
    internal bool FileTypeInEntries => IsV5 ? (Incompat & 0x1) != 0 : (Features2 & 0x200) != 0;

    internal int DirBlockSize => BlockSize << DirBlockLog;

    internal static XfsSuperblock? Parse(ReadOnlySpan<byte> s)
    {
        if (s.Length < 512 || !s[..4].SequenceEqual("XFSB"u8)) return null;
        var sb = new XfsSuperblock
        {
            BlockSize = (int)BinaryPrimitives.ReadUInt32BigEndian(s[4..]),
            DataBlocks = (long)BinaryPrimitives.ReadUInt64BigEndian(s[8..]),
            RootInode = BinaryPrimitives.ReadUInt64BigEndian(s[56..]),
            AgBlocks = BinaryPrimitives.ReadUInt32BigEndian(s[84..]),
            AgCount = BinaryPrimitives.ReadUInt32BigEndian(s[88..]),
            Version = BinaryPrimitives.ReadUInt16BigEndian(s[100..]) & 0xF,
            SectorSize = BinaryPrimitives.ReadUInt16BigEndian(s[102..]),
            InodeSize = BinaryPrimitives.ReadUInt16BigEndian(s[104..]),
            Label = Encoding.UTF8.GetString(s.Slice(108, 12)).TrimEnd('\0', ' '),
            InodesPerBlockLog = s[123],
            AgBlocksLog = s[124],
            DirBlockLog = s[192],
            Features2 = BinaryPrimitives.ReadUInt32BigEndian(s[200..]),
            Incompat = BinaryPrimitives.ReadUInt32BigEndian(s[216..]),
        };

        if (sb.BlockSize is < 512 or > 65536 || (sb.BlockSize & (sb.BlockSize - 1)) != 0) return null;
        if (sb.AgBlocks == 0 || sb.AgCount == 0 || sb.InodeSize is < 256 or > 2048) return null;
        if (sb.Version is not (4 or 5)) return null;
        return sb;
    }

    /// <summary>בלוק מקודד (קבוצה | מיקום) → מספר בלוק רציף מתחילת המחיצה.</summary>
    internal long Linear(ulong fsBlock)
    {
        ulong ag = fsBlock >> AgBlocksLog;
        ulong within = fsBlock & ((1UL << AgBlocksLog) - 1);
        return (long)(ag * (ulong)AgBlocks + within);
    }

    /// <summary>היכן יושב אינוד: הבית בתוך המחיצה.</summary>
    internal long InodeOffset(ulong inode)
    {
        int shift = AgBlocksLog + InodesPerBlockLog;
        ulong ag = inode >> shift;
        ulong block = (inode >> InodesPerBlockLog) & ((1UL << AgBlocksLog) - 1);
        ulong index = inode & ((1UL << InodesPerBlockLog) - 1);
        if (ag >= (ulong)AgCount || block >= (ulong)AgBlocks) return -1;
        return (long)((ag * (ulong)AgBlocks + block) * (ulong)BlockSize + index * (ulong)InodeSize);
    }
}
