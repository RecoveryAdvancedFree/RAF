using System.Buffers.Binary;
using System.Text;

namespace RAF.Core.FileSystems.Ext;

/// <summary>
/// הכותרת של מחיצת ext2/3/4 ("סופר-בלוק"): 1024 בתים, תמיד בהיסט 1024 מתחילת המחיצה.
/// ממנה נגזר הכול — גודל בלוק, כמה בלוקים ורשומות קבצים ("אינודים") יש בכל קבוצה,
/// ואילו תכונות הופעלו (עצי מקטעים של ext4, יומן, מספרי בלוקים של 64 סיביות).
/// </summary>
internal sealed class ExtSuperblock
{
    internal const int Offset = 1024;
    internal const ushort Magic = 0xEF53;

    internal long InodesCount { get; private init; }
    internal long BlocksCount { get; private init; }
    internal long FirstDataBlock { get; private init; }
    internal int BlockSize { get; private init; }
    internal long BlocksPerGroup { get; private init; }
    internal long InodesPerGroup { get; private init; }
    internal int InodeSize { get; private init; }
    internal int DescriptorSize { get; private init; }
    internal uint Compat { get; private init; }
    internal uint Incompat { get; private init; }
    internal uint RoCompat { get; private init; }
    internal uint JournalInode { get; private init; }
    internal uint FirstMetaBg { get; private init; }
    internal string Label { get; private init; } = "";

    internal bool HasJournal => (Compat & 0x4) != 0;
    internal bool FileTypeInEntries => (Incompat & 0x2) != 0;
    internal bool MetaBg => (Incompat & 0x10) != 0;
    internal bool Extents => (Incompat & 0x40) != 0;
    internal bool Is64Bit => (Incompat & 0x80) != 0;
    internal bool SparseSuper => (RoCompat & 0x1) != 0;
    internal bool Encrypted => (Incompat & 0x10000) != 0;

    /// <summary>"ext4" אם יש תכונות של ext4, "ext3" אם יש יומן, אחרת "ext2".</summary>
    internal string Version => (Incompat & (0x40 | 0x80 | 0x200)) != 0 ? "ext4" : HasJournal ? "ext3" : "ext2";

    internal long GroupCount => (BlocksCount - FirstDataBlock + BlocksPerGroup - 1) / BlocksPerGroup;

    /// <summary>פענוח. null — אין כאן כותרת תקינה (חתימה, או ערכים שאינם הגיוניים).</summary>
    internal static ExtSuperblock? Parse(ReadOnlySpan<byte> s)
    {
        if (s.Length < 1024 || BinaryPrimitives.ReadUInt16LittleEndian(s[0x38..]) != Magic) return null;

        int logBlock = (int)BinaryPrimitives.ReadUInt32LittleEndian(s[0x18..]);
        if (logBlock > 6) return null;   // עד 64KB
        int blockSize = 1024 << logBlock;

        uint incompat = BinaryPrimitives.ReadUInt32LittleEndian(s[0x60..]);
        bool is64 = (incompat & 0x80) != 0;
        long blocks = BinaryPrimitives.ReadUInt32LittleEndian(s[0x04..]);
        if (is64) blocks |= (long)BinaryPrimitives.ReadUInt32LittleEndian(s[0x150..]) << 32;

        uint rev = BinaryPrimitives.ReadUInt32LittleEndian(s[0x4C..]);
        int inodeSize = rev == 0 ? 128 : BinaryPrimitives.ReadUInt16LittleEndian(s[0x58..]);
        int descSize = is64 ? BinaryPrimitives.ReadUInt16LittleEndian(s[0xFE..]) : 32;
        if (descSize < 32) descSize = 32;

        var sb = new ExtSuperblock
        {
            InodesCount = BinaryPrimitives.ReadUInt32LittleEndian(s),
            BlocksCount = blocks,
            FirstDataBlock = BinaryPrimitives.ReadUInt32LittleEndian(s[0x14..]),
            BlockSize = blockSize,
            BlocksPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(s[0x20..]),
            InodesPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(s[0x28..]),
            InodeSize = inodeSize,
            DescriptorSize = descSize,
            Compat = BinaryPrimitives.ReadUInt32LittleEndian(s[0x5C..]),
            Incompat = incompat,
            RoCompat = BinaryPrimitives.ReadUInt32LittleEndian(s[0x64..]),
            JournalInode = BinaryPrimitives.ReadUInt32LittleEndian(s[0xE0..]),
            FirstMetaBg = BinaryPrimitives.ReadUInt32LittleEndian(s[0x104..]),
            Label = Encoding.UTF8.GetString(s.Slice(0x78, 16)).TrimEnd('\0', ' '),
        };

        if (sb.BlocksPerGroup == 0 || sb.InodesPerGroup == 0 || sb.BlocksCount == 0) return null;
        if (sb.InodeSize < 128 || sb.InodeSize > blockSize || (sb.InodeSize & (sb.InodeSize - 1)) != 0) return null;
        return sb;
    }

    /// <summary>האם בתחילת הקבוצה יש עותק גיבוי של הכותרת (ואחריו של טבלת הקבוצות).</summary>
    internal bool GroupHasSuperblock(long group)
    {
        if (group <= 1 || !SparseSuper) return true;
        return IsPower(group, 3) || IsPower(group, 5) || IsPower(group, 7);

        static bool IsPower(long n, int b)
        {
            while (n % b == 0) n /= b;
            return n == 1;
        }
    }
}
