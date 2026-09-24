using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace RAF.Core.Native;

/// <summary>
/// קובץ כונן וירטואלי — VHD (גיבוי Windows 7, מכונות וירטואליות ישנות), VHDX
/// (Hyper-V, "גיבוי ושחזור" החדש) או VMDK (VirtualBox ו-VMware) — נקרא ישירות,
/// בלי לחבר אותו למערכת: חיבור היה נותן ל-Windows לכתוב אליו. כאן רק מתרגמים
/// היסט בדיסק הווירטואלי למקום בקובץ; אזור שמעולם לא נכתב נקרא כאפסים.
/// </summary>
internal sealed partial class VirtualDisk
{
    /// <summary>גודל הדיסק הווירטואלי — כפי שהמערכת שבתוכו רואה אותו.</summary>
    public long Size { get; }

    /// <summary>גודל הסקטור של הדיסק הווירטואלי.</summary>
    public int SectorSize { get; }

    /// <summary>"VHD" או "VHDX", להצגה.</summary>
    public string Format { get; }

    /// <summary>הקובץ לא נסגר כראוי (יומן שלא הוחל) — ייתכן שהשינויים האחרונים חסרים.</summary>
    public bool Dirty { get; }

    private readonly long[]? _blocks;      // לכל בלוק: היסט בקובץ, או ‎-1 — לא הוקצה
    private readonly long _blockSize;       // 0 — דיסק קבוע: היסט בקובץ = היסט בדיסק
    private readonly VmdkGrains? _grains;   // VMDK: הטבלאות נקראות לפי הצורך
    private readonly List<Extent>? _extents; // VMDK מכמה קבצים: כל חלק בקובץ משלו
    private readonly EwfImage? _ewf;         // E01: חלקים דחוסים, אולי בכמה קבצים

    /// <summary>טביעת האצבע של הכונן המקורי שנשמרה בתמונת E01. null — אין.</summary>
    public byte[]? Md5 => _ewf?.Md5;

    private VirtualDisk(string format, long size, int sectorSize, long blockSize, long[]? blocks, bool dirty,
        VmdkGrains? grains = null, List<Extent>? extents = null, EwfImage? ewf = null)
    {
        _ewf = ewf;
        Format = format;
        Size = size;
        SectorSize = sectorSize;
        _blockSize = blockSize;
        _blocks = blocks;
        Dirty = dirty;
        _grains = grains;
        _extents = extents;
    }

    /// <summary>
    /// המקום בקובץ של היסט בדיסק, וכמה בתים רצופים יש משם עד סוף הבלוק.
    /// null — האזור לא הוקצה, והוא אפסים.
    /// </summary>
    public long? Locate(long offset, out long contiguous)
    {
        if (_grains is not null) return _grains.Locate(offset, out contiguous);

        if (_blocks is null)
        {
            contiguous = Size - offset;
            return offset;
        }

        long block = offset / _blockSize;
        long within = offset % _blockSize;
        contiguous = _blockSize - within;
        if (block >= _blocks.Length || _blocks[block] < 0) return null;
        return _blocks[block] + within;
    }

    /// <summary>
    /// זיהוי ופענוח. null — הקובץ אינו כונן וירטואלי (תמונה רגילה). כונן מסוג
    /// "הפרשים" — שתלוי בקובץ הורה — נדחה עם הסבר.
    /// </summary>
    public static VirtualDisk? TryOpen(Func<long, int, byte[]> read, long fileLength, string? path = null)
    {
        if (fileLength < 1024)
            return path is not null && IsVmdkDescriptor(read(0, (int)fileLength)) ? VmdkDescriptor(path, read(0, (int)fileLength)) : null;

        byte[] start = read(0, 512);
        if (start.AsSpan().StartsWith("vhdxfile"u8)) return Vhdx(read, fileLength);
        if (path is not null && EwfImage.IsEwf(start))
        {
            var ewf = EwfImage.Open(path);
            return new VirtualDisk("E01", ewf.Size, ewf.SectorSize, 0, null, false, ewf: ewf);
        }
        if (EwfImage.IsEwf2(start))
            throw new InvalidOperationException(
                "זו תמונה בפורמט Ex01 — הגרסה החדשה של E01, שהתוכנה עוד לא קוראת. " +
                "אם אפשר, צרו את התמונה מחדש בפורמט E01 הרגיל, או המירו אותה לתמונה גולמית (dd).");
        if (start.AsSpan().StartsWith("KDMV"u8)) return VmdkSparse(read, start, fileLength);
        if (path is not null && fileLength <= 64 * 1024 && IsVmdkDescriptor(start))
            return VmdkDescriptor(path, read(0, (int)fileLength));

        byte[] footer = read(fileLength - 512, 512);
        if (!footer.AsSpan(0, 8).SequenceEqual("conectix"u8))
        {
            // כותרת בלי ה-00 האחרון (חלק מהכלים כותבים זנב של 511 בתים).
            footer = read(fileLength - 511, 511);
            if (!footer.AsSpan(0, 8).SequenceEqual("conectix"u8)) return null;
        }
        return Vhd(read, footer, fileLength);
    }

    // =============================================================== VHD

    private static VirtualDisk Vhd(Func<long, int, byte[]> read, byte[] footer, long fileLength)
    {
        long size = BinaryPrimitives.ReadInt64BigEndian(footer.AsSpan(48));
        uint type = BinaryPrimitives.ReadUInt32BigEndian(footer.AsSpan(60));

        if (type == 2)                                                        // קבוע: הנתונים כמו שהם, והכותרת בסוף
            return new VirtualDisk("VHD", Math.Min(size, fileLength - 512), 512, 0, null, false);

        if (type == 4) throw Differencing("VHD");
        if (type != 3) throw new InvalidDataException($"סוג כונן VHD לא מוכר ({type}).");

        long headerAt = BinaryPrimitives.ReadInt64BigEndian(footer.AsSpan(16));
        byte[] header = read(headerAt, 1024);
        if (header.Length < 1024 || !header.AsSpan(0, 8).SequenceEqual("cxsparse"u8))
            throw new InvalidDataException("הכותרת של הכונן הווירטואלי פגומה.");

        long tableAt = BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(16));
        int entries = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(28));
        long blockSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(32));
        if (blockSize is < 512 or > 256 * 1024 * 1024 || entries is < 1 or > 16 * 1024 * 1024)
            throw new InvalidDataException("טבלת הבלוקים של הכונן הווירטואלי פגומה.");

        // לפני כל בלוק: מפת סקטורים, מעוגלת לסקטור שלם.
        long bitmap = ((blockSize / 512 + 7) / 8 + 511) / 512 * 512;
        byte[] table = read(tableAt, entries * 4);
        var blocks = new long[entries];
        for (int i = 0; i < entries; i++)
        {
            uint sector = i * 4 + 4 <= table.Length ? BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(i * 4)) : 0xFFFFFFFF;
            blocks[i] = sector == 0xFFFFFFFF ? -1 : sector * 512L + bitmap;
        }

        return new VirtualDisk("VHD", size, 512, blockSize, blocks, false);
    }

    // ============================================================== VHDX

    private static readonly byte[] BatRegion = Guid("2DC27766-F623-4200-9D64-115E9BFD4A08");
    private static readonly byte[] MetadataRegion = Guid("8B7CA206-4790-4B9A-B8FE-575F050F886E");
    private static readonly byte[] FileParameters = Guid("CAA16737-FA36-4D43-B3B6-33F0AA44E76B");
    private static readonly byte[] VirtualDiskSize = Guid("2FA54224-CD1B-4876-B211-5DBED83BF4B8");
    private static readonly byte[] LogicalSectorSize = Guid("8141BF1D-A96F-4709-BA47-F233A8FAAB5F");

    private static byte[] Guid(string text) => new System.Guid(text).ToByteArray();

    private static VirtualDisk Vhdx(Func<long, int, byte[]> read, long fileLength)
    {
        // שתי כותרות; התקפה היא זו עם מספר הרצף הגבוה.
        byte[]? header = null;
        long best = -1;
        foreach (long at in new long[] { 64 * 1024, 128 * 1024 })
        {
            byte[] h = read(at, 4096);
            if (h.Length < 80 || !h.AsSpan(0, 4).SequenceEqual("head"u8)) continue;
            long sequence = BinaryPrimitives.ReadInt64LittleEndian(h.AsSpan(8));
            if (sequence > best) { best = sequence; header = h; }
        }
        if (header is null) throw new InvalidDataException("הכותרות של הכונן הווירטואלי פגומות.");
        bool dirty = !header.AsSpan(48, 16).SequenceEqual(new byte[16]);    // יומן פתוח

        long batAt = 0, batLength = 0, metaAt = 0, metaLength = 0;
        foreach (long at in new long[] { 192 * 1024, 256 * 1024 })
        {
            byte[] r = read(at, 64 * 1024);
            if (r.Length < 16 || !r.AsSpan(0, 4).SequenceEqual("regi"u8)) continue;
            int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(8));
            for (int i = 0; i < count && 16 + i * 32 + 32 <= r.Length; i++)
            {
                var e = r.AsSpan(16 + i * 32, 32);
                long offset = BinaryPrimitives.ReadInt64LittleEndian(e[16..]);
                if (e[..16].SequenceEqual(BatRegion)) { batAt = offset; batLength = BinaryPrimitives.ReadUInt32LittleEndian(e[24..]); }
                else if (e[..16].SequenceEqual(MetadataRegion)) { metaAt = offset; metaLength = BinaryPrimitives.ReadUInt32LittleEndian(e[24..]); }
            }
            if (batAt > 0 && metaAt > 0) break;
        }
        if (batAt == 0 || metaAt == 0) throw new InvalidDataException("טבלת האזורים של הכונן הווירטואלי פגומה.");

        // הנתונים עצמם יושבים אחרי 64KB מתחילת האזור — קוראים את כולו (בדרך כלל 1MB).
        byte[] meta = read(metaAt, (int)Math.Clamp(metaLength, 64 * 1024, 16 * 1024 * 1024));
        if (!meta.AsSpan(0, 8).SequenceEqual("metadata"u8)) throw new InvalidDataException("המידע על הכונן הווירטואלי פגום.");
        int items = BinaryPrimitives.ReadUInt16LittleEndian(meta.AsSpan(10));
        long blockSize = 0, size = 0;
        int sectorSize = 512;
        bool hasParent = false;
        for (int i = 0; i < items && 32 + i * 32 + 32 <= meta.Length; i++)
        {
            var e = meta.AsSpan(32 + i * 32, 32);
            int at = (int)BinaryPrimitives.ReadUInt32LittleEndian(e[16..]);
            if (at < 0 || at + 8 > meta.Length) continue;
            if (e[..16].SequenceEqual(FileParameters))
            {
                blockSize = BinaryPrimitives.ReadUInt32LittleEndian(meta.AsSpan(at));
                hasParent = (BinaryPrimitives.ReadUInt32LittleEndian(meta.AsSpan(at + 4)) & 2) != 0;
            }
            else if (e[..16].SequenceEqual(VirtualDiskSize)) size = BinaryPrimitives.ReadInt64LittleEndian(meta.AsSpan(at));
            else if (e[..16].SequenceEqual(LogicalSectorSize)) sectorSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(meta.AsSpan(at));
        }
        if (hasParent) throw Differencing("VHDX");
        if (blockSize is < 1024 * 1024 or > 256 * 1024 * 1024 || size <= 0 || sectorSize is not (512 or 4096))
            throw new InvalidDataException("המידע על הכונן הווירטואלי פגום.");

        // אחרי כל "נתח" של בלוקי נתונים בא ברשומת הטבלה בלוק של מפת סקטורים — מדלגים עליו.
        long chunkRatio = (1L << 23) * sectorSize / blockSize;
        int payloadBlocks = (int)((size + blockSize - 1) / blockSize);
        byte[] bat = read(batAt, (int)Math.Min(batLength, int.MaxValue));
        var blocks = new long[payloadBlocks];
        for (int b = 0; b < payloadBlocks; b++)
        {
            long index = b + b / chunkRatio;
            if (index * 8 + 8 > bat.Length) { blocks[b] = -1; continue; }
            ulong entry = BinaryPrimitives.ReadUInt64LittleEndian(bat.AsSpan((int)(index * 8)));
            int state = (int)(entry & 7);
            long fileOffset = (long)(entry >> 20) * 1024 * 1024;
            // 6: הבלוק קיים במלואו. כל מצב אחר — לא הוקצה, אפסים, או נמחק (TRIM).
            blocks[b] = state == 6 && fileOffset + blockSize <= fileLength + blockSize ? fileOffset : -1;
        }

        return new VirtualDisk("VHDX", size, sectorSize, blockSize, blocks, dirty);
    }

    // ============================================================== VMDK

    /// <summary>
    /// VMDK שגדל לפי הצורך (ברירת המחדל ב-VirtualBox, וב-VMware כשבוחרים קובץ יחיד):
    /// כותרת, ספריית טבלאות, וטבלאות שמצביעות על "גרגרים" — בדרך כלל 64KB כל אחד.
    /// </summary>
    private static VirtualDisk VmdkSparse(Func<long, int, byte[]> read, byte[] h, long fileLength)
    {
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(8));
        long capacity = BinaryPrimitives.ReadInt64LittleEndian(h.AsSpan(12)) * 512;
        long grain = BinaryPrimitives.ReadInt64LittleEndian(h.AsSpan(20)) * 512;
        long descriptorAt = BinaryPrimitives.ReadInt64LittleEndian(h.AsSpan(28)) * 512;
        long descriptorLength = BinaryPrimitives.ReadInt64LittleEndian(h.AsSpan(36)) * 512;
        int perTable = (int)BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(44));
        long directoryAt = BinaryPrimitives.ReadInt64LittleEndian(h.AsSpan(56));
        bool dirty = h[72] != 0;
        int compression = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(77));

        // התיאור שבתוך הקובץ אומר אם זה כונן "הפרשים" של תמונת מצב.
        if (descriptorAt > 0 && descriptorLength is > 0 and <= 1024 * 1024 &&
            VmdkHasParent(Encoding.ASCII.GetString(read(descriptorAt, (int)descriptorLength))))
            throw Differencing("VMDK");

        // כונן דחוס (ייצוא של מכונה לקובץ העברה): כל גרגר דחוס, והטבלאות נכתבות רק בסוף —
        // ולכן הכותרת שבתחילה לא יודעת היכן הן. הכותרת האמיתית היא עותק שנכתב לפני סוף הקובץ.
        bool compressed = compression != 0 || (flags & (1 << 16)) != 0;
        if (compressed && compression is not (0 or 1))
            throw new InvalidOperationException($"כונן VMDK בשיטת דחיסה שהתוכנה לא מכירה ({compression}).");
        if (directoryAt == -1)
        {
            byte[] footer = read(fileLength - 1024, 512);
            if (footer.Length < 512 || !footer.AsSpan().StartsWith("KDMV"u8))
                throw new InvalidDataException("סוף הכונן הווירטואלי הדחוס חסר או פגום — ייתכן שהייצוא לא הושלם.");
            capacity = BinaryPrimitives.ReadInt64LittleEndian(footer.AsSpan(12)) * 512;
            grain = BinaryPrimitives.ReadInt64LittleEndian(footer.AsSpan(20)) * 512;
            perTable = (int)BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(44));
            directoryAt = BinaryPrimitives.ReadInt64LittleEndian(footer.AsSpan(56));
        }

        if (grain is < 4096 or > 64 * 1024 * 1024 || capacity <= 0 || perTable is < 1 or > 65536 || directoryAt <= 0)
            throw new InvalidDataException("הכותרת של הכונן הווירטואלי פגומה.");

        long grains = (capacity + grain - 1) / grain;
        long tables = (grains + perTable - 1) / perTable;
        if (tables > 16 * 1024 * 1024) throw new InvalidDataException("הכותרת של הכונן הווירטואלי פגומה.");

        byte[] directory = read(directoryAt * 512, (int)(tables * 4));
        var tableAt = new uint[tables];
        for (int i = 0; i < tables && i * 4 + 4 <= directory.Length; i++)
            tableAt[i] = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(i * 4));

        return new VirtualDisk("VMDK", capacity, 512, 0, null, dirty,
            grains: new VmdkGrains(read, grain, perTable, tableAt, fileLength, compressed, zeroMarker: (flags & 4) != 0));
    }

    /// <summary>
    /// מיפוי גרגרים. ספריית הטבלאות קטנה ונקראת מראש; טבלה עצמה (2KB) נקראת
    /// בפעם הראשונה שצריך אותה — בכונן של טרה-בייט יש אלפי טבלאות.
    /// </summary>
    private sealed class VmdkGrains(Func<long, int, byte[]> read, long grain, int perTable, uint[] tableAt, long fileLength,
        bool compressed = false, bool zeroMarker = false)
    {
        private readonly Dictionary<long, uint[]> _tables = new();
        private readonly object _gate = new();

        /// <summary>הגרגרים דחוסים — הקריאה עוברת דרך Read, ולא דרך מיקום בקובץ.</summary>
        internal bool Compressed => compressed;

        internal long? Locate(long offset, out long contiguous)
        {
            long index = offset / grain;
            long within = offset % grain;
            contiguous = grain - within;
            uint sector = SectorOf(index);
            return sector == 0 ? null : sector * 512L + within;
        }

        /// <summary>הסקטור בקובץ שבו מתחיל הגרגר. 0 — הגרגר לא נכתב (אפסים).</summary>
        private uint SectorOf(long index)
        {
            long table = index / perTable;
            if (table >= tableAt.Length || tableAt[table] == 0) return 0;

            uint[] entries;
            lock (_gate)
            {
                if (!_tables.TryGetValue(table, out entries!))
                {
                    byte[] raw = read(tableAt[table] * 512L, perTable * 4);
                    entries = new uint[perTable];
                    for (int i = 0; i < perTable && i * 4 + 4 <= raw.Length; i++)
                        entries[i] = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(i * 4));
                    _tables[table] = entries;
                }
            }

            // 0 — הגרגר לא נכתב. 1 — סומן כמאופס, אבל רק כשהכותרת מצהירה על הסימון הזה;
            // בלעדיה סקטור 1 הוא מקום חוקי לנתונים. בשני המקרים הראשונים: אפסים.
            uint sector = entries[index % perTable];
            return sector == 0 || (sector == 1 && zeroMarker) || sector * 512L >= fileLength ? 0 : sector;
        }

        // ------------------------------------------------ גרגרים דחוסים

        private readonly Dictionary<long, byte[]?> _grains = new();
        private readonly LinkedList<long> _order = new();

        /// <summary>
        /// קריאה מכונן דחוס: לפני כל גרגר — מספר הסקטור שלו בכונן (8 בתים) ואורך הנתונים
        /// הדחוסים (4 בתים), ואחריהם הנתונים. גרגרים שנפרסו לאחרונה נשמרים — הסריקה קוראת ברצף.
        /// </summary>
        internal int Read(long offset, Span<byte> destination, long size)
        {
            if (offset >= size) return 0;
            int total = (int)Math.Min(destination.Length, size - offset);

            for (int done = 0; done < total;)
            {
                long position = offset + done;
                long index = position / grain;
                int within = (int)(position % grain);
                int part = (int)Math.Min(total - done, grain - within);
                var target = destination.Slice(done, part);

                uint sector = SectorOf(index);
                if (sector == 0) target.Clear();
                else
                {
                    byte[]? data = Grain(index, sector);
                    if (data is null) return done;                               // גרגר פגום — כמו סקטור שלא נקרא
                    data.AsSpan(within, part).CopyTo(target);
                }
                done += part;
            }
            return total;
        }

        private byte[]? Grain(long index, uint sector)
        {
            lock (_gate)
            {
                if (_grains.TryGetValue(index, out var cached)) return cached;
            }

            byte[] marker = read(sector * 512L, 12);
            int length = marker.Length == 12 ? (int)BinaryPrimitives.ReadUInt32LittleEndian(marker.AsSpan(8)) : 0;
            byte[]? data = null;
            if (length > 0 && length <= grain + 64 * 1024)
            {
                try
                {
                    using var z = new System.IO.Compression.ZLibStream(
                        new MemoryStream(read(sector * 512L + 12, length)), System.IO.Compression.CompressionMode.Decompress);
                    data = new byte[grain];
                    int got = 0;
                    while (got < data.Length)
                    {
                        int n = z.Read(data, got, data.Length - got);
                        if (n <= 0) break;
                        got += n;
                    }
                }
                catch (InvalidDataException)
                {
                    data = null;
                }
            }

            lock (_gate)
            {
                _grains[index] = data;
                _order.AddLast(index);
                if (_order.Count > 64) { _grains.Remove(_order.First!.Value); _order.RemoveFirst(); }
            }
            return data;
        }
    }

    /// <summary>חלק של VMDK מכמה קבצים: טווח בדיסק הווירטואלי, והקובץ שמכיל אותו (null — אפסים).</summary>
    private sealed record Extent(long Start, long Length, RawDevice? Device, long FileOffset);

    /// <summary>קובץ התיאור של VMDK: קובץ טקסט קטן שמפרט מאילו קבצים הכונן בנוי.</summary>
    private static bool IsVmdkDescriptor(byte[] start)
    {
        string text = Encoding.ASCII.GetString(start, 0, Math.Min(start.Length, 1024));
        return text.Contains("# Disk DescriptorFile", StringComparison.OrdinalIgnoreCase)
               || (text.Contains("createType=", StringComparison.OrdinalIgnoreCase) && text.Contains("version=", StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex("""^\s*(?:RW|RDONLY|NOACCESS)\s+(\d+)\s+(\w+)(?:\s+"([^"]+)"(?:\s+(\d+))?)?""", RegexOptions.Multiline)]
    private static partial Regex ExtentLine();

    [GeneratedRegex("""^\s*parentCID\s*=\s*(\w+)""", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex ParentLine();

    private static bool VmdkHasParent(string descriptor)
        => ParentLine().Match(descriptor) is { Success: true } m && !m.Groups[1].Value.Equals("ffffffff", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// VMDK מכמה קבצים (ברירת המחדל ב-VMware Workstation, ובשרתי VMware): כל חלק
    /// נפתח בנפרד — חלק שגדל לפי הצורך מתורגם בעצמו, וחלק בגודל מלא נקרא כמו שהוא.
    /// </summary>
    private static VirtualDisk VmdkDescriptor(string path, byte[] raw)
    {
        string text = Encoding.UTF8.GetString(raw);
        if (VmdkHasParent(text)) throw Differencing("VMDK");

        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var extents = new List<Extent>();
        long at = 0;

        try
        {
            foreach (Match m in ExtentLine().Matches(text))
            {
                long length = long.Parse(m.Groups[1].Value) * 512;
                string type = m.Groups[2].Value.ToUpperInvariant();
                string file = m.Groups[3].Value;
                long fileOffset = m.Groups[4].Success ? long.Parse(m.Groups[4].Value) * 512 : 0;

                RawDevice? device = null;
                if (type != "ZERO")
                {
                    if (type is not ("FLAT" or "VMFS" or "SPARSE"))
                        throw new InvalidOperationException($"כונן VMDK מסוג שהתוכנה לא מכירה ({type}).");

                    string full = Path.Combine(folder, file);
                    if (!File.Exists(full))
                        throw new InvalidOperationException(
                            $"חסר קובץ של הכונן הווירטואלי: \"{file}\". כל קובצי ה-VMDK של הכונן צריכים להיות באותה תיקייה.");

                    device = RawDevice.TryOpen(full, 512, sequential: false)
                             ?? throw new IOException($"לא ניתן לפתוח את \"{file}\". ייתכן שהוא בשימוש בתוכנה אחרת.");
                    if (type == "SPARSE" && device.Virtual is null)
                        throw new InvalidDataException($"החלק \"{file}\" של הכונן הווירטואלי פגום.");
                }

                extents.Add(new Extent(at, length, device, fileOffset));
                at += length;
            }
        }
        catch
        {
            foreach (var e in extents) e.Device?.Dispose();
            throw;
        }

        if (extents.Count == 0) throw new InvalidDataException("קובץ התיאור של הכונן הווירטואלי לא מפרט אף קובץ נתונים.");
        return new VirtualDisk("VMDK", at, 512, 0, null, false, extents: extents);
    }

    /// <summary>כונן שבנוי מכמה קבצים — הקריאה עוברת לכל חלק לפי תורו.</summary>
    public bool IsComposite => _extents is not null || _ewf is not null || _grains is { Compressed: true };

    public int ReadComposite(long offset, Span<byte> destination)
    {
        if (_ewf is not null) return _ewf.Read(offset, destination);
        if (_grains is { Compressed: true }) return _grains.Read(offset, destination, Size);
        if (offset >= Size) return 0;
        int total = (int)Math.Min(destination.Length, Size - offset);

        for (int done = 0; done < total;)
        {
            long position = offset + done;
            var extent = _extents!.First(e => position < e.Start + e.Length);
            int part = (int)Math.Min(total - done, extent.Start + extent.Length - position);
            var target = destination.Slice(done, part);

            if (extent.Device is null) target.Clear();
            else
            {
                int read = extent.Device.Read(extent.FileOffset + position - extent.Start, target);
                if (read < part) return done + Math.Max(0, read);
            }
            done += part;
        }
        return total;
    }

    /// <summary>סגירת קובצי החלקים.</summary>
    public void Close()
    {
        _ewf?.Close();
        if (_extents is null) return;
        foreach (var e in _extents) e.Device?.Dispose();
    }

    private static Exception Differencing(string format) => new InvalidOperationException(
        $"זה כונן {format} מסוג \"הפרשים\": הוא שומר רק את מה שהשתנה, והשאר נמצא בקובץ אחר (קובץ ההורה). " +
        "פתחו את קובץ ההורה במקום — או חברו את שניהם ב-Windows (ניהול דיסקים ← צירוף VHD) וסרקו את הכונן שנוסף.");
}
