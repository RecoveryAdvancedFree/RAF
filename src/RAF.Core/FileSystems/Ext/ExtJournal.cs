using System.Buffers.Binary;
using RAF.Core.Model;

namespace RAF.Core.FileSystems.Ext;

/// <summary>
/// היומן של ext3/4 (jbd2). לפני כל שינוי במטא-דאטה המערכת כותבת ליומן עותק מלא של
/// הבלוק כפי שיהיה — כולל בלוקים של טבלת האינודים. היומן מעגלי, ולכן עותקים ישנים
/// נשארים בו עד שהוא נכתב מעליהם.
///
/// זה מה שמאפשר לשחזר ב-ext3/4: המחיקה מאפסת באינוד את מיקום התוכן, אבל ביומן יש
/// עדיין עותק של אותו בלוק מלפני המחיקה, עם המיקום שלם.
///
/// כאן לא "משחזרים" טרנזקציות — עוברים על כל היומן, מוצאים כל בלוק מתאר, ורושמים
/// לכל בלוק במחיצה אילו עותקים שלו יש ביומן ומאיזה רצף (חדש יותר — גבוה יותר).
/// </summary>
internal sealed class ExtJournal
{
    private const uint Magic = 0xC03B3998;

    /// <summary>לכל בלוק במחיצה: העותקים ביומן — (מספר רצף, מספר בלוק ביומן).</summary>
    private readonly Dictionary<long, List<(uint Sequence, long JournalBlock)>> _copies = new();
    private readonly List<DataExtent> _extents;
    private readonly ExtVolume _volume;

    internal int Copies { get; private set; }

    private ExtJournal(ExtVolume volume, List<DataExtent> extents)
    {
        _volume = volume;
        _extents = extents;
    }

    /// <summary>פתיחת היומן. null — אין יומן, או שהוא אינו נקרא.</summary>
    internal static ExtJournal? Open(ExtVolume volume, CancellationToken token)
    {
        var sb = volume.Super;
        if (!sb.HasJournal || sb.JournalInode == 0) return null;
        var inode = volume.ReadInode(sb.JournalInode);
        if (inode is null || !inode.HasBlockMap) return null;
        var extents = volume.ExtentsOf(inode);
        if (extents is null) return null;

        var journal = new ExtJournal(volume, extents);
        var header = journal.Read(0);
        if (header is null || BinaryPrimitives.ReadUInt32BigEndian(header) != Magic) return null;

        uint type = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
        if (type is not (3 or 4)) return null;
        int journalBlock = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0xC));
        if (journalBlock != sb.BlockSize) return null;
        long length = Math.Min(BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x10)), journal.Blocks);
        uint incompat = type == 4 ? BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x28)) : 0;

        journal.Index(length, incompat, token);
        return journal;
    }

    private long Blocks => _extents.Sum(e => e.ClusterCount);

    /// <summary>בלוק n של היומן — דרך מיקום הקובץ של היומן במחיצה.</summary>
    private byte[]? Read(long n)
    {
        foreach (var e in _extents)
        {
            if (n < e.ClusterCount) return e.IsSparse ? null : _volume.ReadBlock(e.StartCluster + n);
            n -= e.ClusterCount;
        }
        return null;
    }

    private void Index(long length, uint incompat, CancellationToken token)
    {
        bool is64 = (incompat & 0x2) != 0;
        bool csum3 = (incompat & 0x10) != 0;
        bool csum2 = (incompat & 0x8) != 0;
        int tagSize = csum3 ? 16 : 8 + (is64 ? 4 : 0);
        int tail = csum2 || csum3 ? 4 : 0;
        int bs = _volume.Super.BlockSize;

        for (long j = 1; j < length; j++)
        {
            if (token.IsCancellationRequested) return;
            var block = Read(j);
            if (block is null || BinaryPrimitives.ReadUInt32BigEndian(block) != Magic) continue;
            if (BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(4)) != 1) continue;   // רק בלוק מתאר
            uint sequence = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(8));

            long data = j;
            for (int at = 12; at + tagSize <= bs - tail; )
            {
                long target = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(at));
                uint flags = csum3 ? BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(at + 4))
                                   : BinaryPrimitives.ReadUInt16BigEndian(block.AsSpan(at + 6));
                if (is64) target |= (long)BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(at + 8)) << 32;

                data++;
                if (data >= length) data = 1;   // היומן מעגלי
                if ((flags & 0x4) == 0 && target > 0 && target < _volume.Super.BlocksCount)
                {
                    if (!_copies.TryGetValue(target, out var list)) _copies[target] = list = new();
                    list.Add((sequence, data));
                    Copies++;
                }

                at += tagSize + ((flags & 0x2) != 0 ? 0 : 16);
                if ((flags & 0x8) != 0) break;   // התג האחרון
            }
        }
    }

    /// <summary>
    /// העותקים ביומן של האינוד, מהחדש לישן. עותק שהבלוק שלו התחיל במקור בחתימת
    /// היומן נשמר עם אפסים במקומה ("escape") — אצל אינוד זה אינו משנה דבר.
    /// </summary>
    internal IEnumerable<ExtInode> InodeCopies(long number)
    {
        long offset = _volume.InodeOffset(number);
        if (offset < 0) yield break;
        int bs = _volume.Super.BlockSize;
        if (!_copies.TryGetValue(offset / bs, out var list)) yield break;

        int within = (int)(offset % bs);
        foreach (var (_, journalBlock) in list.OrderByDescending(c => c.Sequence))
        {
            var block = Read(journalBlock);
            if (block is null) continue;
            yield return ExtInode.Parse(number, block.AsSpan(within, _volume.Super.InodeSize).ToArray());
        }
    }

    /// <summary>עותקים ביומן של בלוק כלשהו (למשל בלוק של עץ מקטעים או של תיקייה), מהחדש לישן.</summary>
    internal IEnumerable<byte[]> BlockCopies(long block)
    {
        if (!_copies.TryGetValue(block, out var list)) yield break;
        foreach (var (_, journalBlock) in list.OrderByDescending(c => c.Sequence))
            if (Read(journalBlock) is { } data) yield return data;
    }

    /// <summary>כל הבלוקים במחיצה שיש להם עותק ביומן.</summary>
    internal IEnumerable<long> JournaledBlocks => _copies.Keys;
}
