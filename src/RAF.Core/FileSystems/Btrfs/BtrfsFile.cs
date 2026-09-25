using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using RAF.Core.Model;

namespace RAF.Core.FileSystems.Btrfs;

/// <summary>רשומת קובץ (אינוד): גודל, סוג, תאריכים.</summary>
internal sealed record BtrfsInode(ulong Number, long Size, uint Mode, ulong Generation, DateTime? Accessed, DateTime? Changed, DateTime? Modified, DateTime? Created)
{
    /// <summary>כמה שמות מצביעים על הקובץ. 0 — הגרסה האחרונה, שנכתבה ברגע המחיקה (אז גם הגודל מתאפס).</summary>
    internal uint Links { get; init; }

    internal bool IsDirectory => (Mode & 0xF000) == 0x4000;
    internal bool IsRegular => (Mode & 0xF000) == 0x8000;

    internal static BtrfsInode? Parse(ulong number, ReadOnlySpan<byte> d)
    {
        if (d.Length < 160) return null;
        return new BtrfsInode(number,
            (long)BinaryPrimitives.ReadUInt64LittleEndian(d[16..]),
            BinaryPrimitives.ReadUInt32LittleEndian(d[52..]),
            BinaryPrimitives.ReadUInt64LittleEndian(d),
            Time(d[112..]), Time(d[124..]), Time(d[136..]), Time(d[148..]))
        {
            Links = BinaryPrimitives.ReadUInt32LittleEndian(d[40..]),
        };
    }

    private static DateTime? Time(ReadOnlySpan<byte> t)
    {
        long seconds = BinaryPrimitives.ReadInt64LittleEndian(t);
        uint nanos = BinaryPrimitives.ReadUInt32LittleEndian(t[8..]);
        if (seconds <= 0 || nanos >= 1_000_000_000) return null;
        try { return DateTime.UnixEpoch.AddSeconds(seconds).AddTicks(nanos / 100).ToLocalTime(); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}

/// <summary>רשומה בתיקייה: שם, ולאן היא מצביעה — קובץ (1) או "תיקייה משותפת" (עץ, 132).</summary>
internal readonly record struct BtrfsDirEntry(ulong Target, byte TargetType, byte FileType, string Name)
{
    internal static BtrfsDirEntry? Parse(ReadOnlySpan<byte> d)
    {
        if (d.Length < 30) return null;
        int dataLen = BinaryPrimitives.ReadUInt16LittleEndian(d[25..]);
        int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(d[27..]);
        if (30 + nameLen + dataLen > d.Length || nameLen == 0) return null;
        string name;
        try { name = new UTF8Encoding(false, true).GetString(d.Slice(30, nameLen)); }
        catch (DecoderFallbackException) { return null; }
        return new BtrfsDirEntry(BinaryPrimitives.ReadUInt64LittleEndian(d), d[8], d[29], name);
    }
}

/// <summary>
/// קטע של קובץ: היכן בקובץ, ומה יש שם — תוכן בתוך הרשומה (קובץ זעיר), קטע בדיסק
/// (אולי דחוס), או חור. בקטע בדיסק: הקטע השלם (כפי שנכתב, אולי דחוס) וחלק ממנו ששייך לקובץ.
/// </summary>
internal sealed record BtrfsExtent(long FileOffset, byte Compression, byte Type, byte[]? Inline,
    ulong DiskStart, ulong DiskLength, long RamBytes, long Offset, long Length)
{
    internal bool IsHole => Type != 0 && DiskStart == 0;
    internal bool IsPrealloc => Type == 2;

    internal static BtrfsExtent? Parse(long fileOffset, ReadOnlySpan<byte> d)
    {
        if (d.Length < 21) return null;
        long ram = (long)BinaryPrimitives.ReadUInt64LittleEndian(d[8..]);
        byte compression = d[16];
        byte type = d[20];
        if (type == 0)
            return new BtrfsExtent(fileOffset, compression, 0, d[21..].ToArray(), 0, 0, ram, 0, compression == 0 ? d.Length - 21 : ram);
        if (d.Length < 53) return null;
        return new BtrfsExtent(fileOffset, compression, type, null,
            BinaryPrimitives.ReadUInt64LittleEndian(d[21..]), BinaryPrimitives.ReadUInt64LittleEndian(d[29..]), ram,
            (long)BinaryPrimitives.ReadUInt64LittleEndian(d[37..]), (long)BinaryPrimitives.ReadUInt64LittleEndian(d[45..]));
    }
}

/// <summary>הרכבת תוכן הקובץ מהקטעים: כרשימת מקומות בדיסק, או — בדחוס ובזעיר — כתוכן מלא בזיכרון.</summary>
internal static class BtrfsContent
{
    /// <summary>רשימת מקומות בדיסק (באשכולות), כשכל הקטעים רגילים. null — יש קטע דחוס או בתוך הרשומה.</summary>
    internal static List<DataExtent>? Runs(BtrfsVolume volume, List<BtrfsExtent> extents, long size)
    {
        if (extents.Any(e => e.Type == 0 || (e.Compression != 0 && !e.IsHole))) return null;
        var runs = new List<DataExtent>();
        long cluster = volume.SectorSize;
        long at = 0;
        long end = (size + cluster - 1) / cluster * cluster;
        foreach (var e in extents.OrderBy(e => e.FileOffset))
        {
            if (e.FileOffset >= end) break;
            if (e.FileOffset < at) continue;
            if (e.FileOffset > at) runs.Add(new DataExtent(0, (e.FileOffset - at) / cluster, true));
            long length = Math.Min(e.Length, end - e.FileOffset);
            if (e.IsHole || e.IsPrealloc) runs.Add(new DataExtent(0, (length + cluster - 1) / cluster, true));
            else
            {
                ulong logical = e.DiskStart + (ulong)e.Offset;
                for (long done = 0; done < length; )
                {
                    if (volume.Map(logical + (ulong)done) is not { } p) return new List<DataExtent>();   // לא ממופה — פגום
                    long take = Math.Min(length - done, p.Contiguous);
                    runs.Add(new DataExtent(p.Offset / cluster, (take + cluster - 1) / cluster, false));
                    done += take;
                }
            }
            at = e.FileOffset + (length + cluster - 1) / cluster * cluster;
        }
        if (at < end) runs.Add(new DataExtent(0, (end - at) / cluster, true));
        return runs;
    }

    /// <summary>
    /// התוכן המלא בזיכרון — לקובץ שיש בו קטע דחוס או קטע בתוך הרשומה. null ו-problem — אי אפשר.
    /// </summary>
    internal static byte[]? Materialize(BtrfsVolume volume, List<BtrfsExtent> extents, long size, out string? problem)
    {
        problem = null;
        var content = new byte[size];
        foreach (var e in extents.OrderBy(e => e.FileOffset))
        {
            if (e.FileOffset >= size || e.IsHole || e.IsPrealloc) continue;
            byte[]? plain;
            if (e.Type == 0)
                plain = e.Compression == 0 ? e.Inline : Decompress(e.Compression, e.Inline!, (int)e.RamBytes, out problem);
            else
            {
                if (volume.Map(e.DiskStart) is not { } p || p.Contiguous < (long)e.DiskLength) { problem = L.T("מיקום התוכן פגום."); return null; }
                var raw = new byte[e.DiskLength];
                if (volume.ReadRaw(p.Offset, raw) != raw.Length) { problem = L.T("לא ניתן היה לקרוא את התוכן מהכונן."); return null; }
                plain = e.Compression == 0 ? raw : Decompress(e.Compression, raw, (int)e.RamBytes, out problem);
            }
            if (plain is null) return null;
            long from = e.Type == 0 ? 0 : e.Offset;
            long take = Math.Min(Math.Min(e.Length, plain.Length - from), size - e.FileOffset);
            if (take > 0) plain.AsSpan((int)from, (int)take).CopyTo(content.AsSpan((int)e.FileOffset));
        }
        return content;
    }

    /// <summary>פריסת קטע דחוס: zlib (1), LZO (2) או zstd (3).</summary>
    internal static byte[]? Decompress(byte compression, byte[] data, int ramBytes, out string? problem)
    {
        problem = null;
        try
        {
            switch (compression)
            {
                case 1:
                {
                    using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
                    var result = new byte[ramBytes];
                    int done = 0, n;
                    while (done < ramBytes && (n = z.Read(result, done, ramBytes - done)) > 0) done += n;
                    return result;
                }
                case 2:
                    return Lzo.Btrfs(data, ramBytes);
                default:
                    problem = L.T("הקובץ דחוס בשיטת zstd, שעוד לא נתמכת. סריקה מתקדמת לא תעזור כאן — התוכן בדיסק דחוס.");
                    return null;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException)
        {
            problem = L.T("התוכן הדחוס פגום — לא ניתן לפרוס אותו.");
            return null;
        }
    }
}
