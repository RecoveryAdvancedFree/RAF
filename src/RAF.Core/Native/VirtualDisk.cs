using System.Buffers.Binary;

namespace RAF.Core.Native;

/// <summary>
/// קובץ כונן וירטואלי של Windows — VHD (גיבוי Windows 7, מכונות וירטואליות ישנות)
/// או VHDX (Hyper-V, "גיבוי ושחזור" החדש) — נקרא ישירות, בלי לחבר אותו למערכת:
/// חיבור היה נותן ל-Windows לכתוב אליו. כאן רק מתרגמים היסט בדיסק הווירטואלי
/// למקום בקובץ; אזור שמעולם לא נכתב נקרא כאפסים.
/// </summary>
internal sealed class VirtualDisk
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

    private VirtualDisk(string format, long size, int sectorSize, long blockSize, long[]? blocks, bool dirty)
    {
        Format = format;
        Size = size;
        SectorSize = sectorSize;
        _blockSize = blockSize;
        _blocks = blocks;
        Dirty = dirty;
    }

    /// <summary>
    /// המקום בקובץ של היסט בדיסק, וכמה בתים רצופים יש משם עד סוף הבלוק.
    /// null — האזור לא הוקצה, והוא אפסים.
    /// </summary>
    public long? Locate(long offset, out long contiguous)
    {
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
    public static VirtualDisk? TryOpen(Func<long, int, byte[]> read, long fileLength)
    {
        if (fileLength < 1024) return null;

        if (read(0, 8).AsSpan().SequenceEqual("vhdxfile"u8)) return Vhdx(read, fileLength);

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

    private static Exception Differencing(string format) => new InvalidOperationException(
        $"זה כונן {format} מסוג \"הפרשים\": הוא שומר רק את מה שהשתנה, והשאר נמצא בקובץ אחר (קובץ ההורה). " +
        "פתחו את קובץ ההורה במקום — או חברו את שניהם ב-Windows (ניהול דיסקים ← צירוף VHD) וסרקו את הכונן שנוסף.");
}
