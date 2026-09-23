using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using RAF.Core.Disks;

namespace RAF.Core.Repair;

/// <summary>
/// בנייה מחדש של ארכיון ZIP — ובכלל זה מסמכי Office (docx, xlsx, pptx) — מתוך
/// הכותרות המקומיות של הקבצים שבתוכו.
///
/// בסוף כל ZIP יש "תוכן עניינים" (הספרייה המרכזית). כשהוא נפגע או חסר — קובץ
/// שנקטע, שחזור שלא הגיע עד הסוף — Word ו-Excel מסרבים לפתוח את המסמך, אף שכל
/// קובץ פנימי עדיין שלם בגוף הארכיון, עם כותרת משלו. כאן עוברים על הכותרות האלה,
/// בודקים כל קובץ פנימי בפריסה ובסכום ביקורת (CRC-32), וכותבים ארכיון חדש עם
/// תוכן עניינים חדש — רק מהקבצים שנמצאו שלמים. שום תוכן אינו מומצא.
/// </summary>
internal static class ZipRebuilder
{
    private const uint LocalSignature = 0x04034B50;     // PK 03 04
    private const uint CentralSignature = 0x02014B50;   // PK 01 02
    private const uint EndSignature = 0x06054B50;       // PK 05 06
    private const uint DescriptorSignature = 0x08074B50; // PK 07 08

    /// <summary>קובץ פנימי אחד כפי שנמצא בגוף הארכיון.</summary>
    internal sealed record Entry(
        string Name, long LocalOffset, ushort Version, ushort Flags, ushort Method,
        ushort Time, ushort Date, uint Crc, uint CompressedSize, uint Size,
        byte[] NameBytes, byte[] Extra, long DataOffset, bool Intact, bool Verified, string? Problem);

    internal sealed record Analysis(
        List<Entry> Entries, bool DirectoryIntact, bool Unsupported, string? UnsupportedReason)
    {
        public IEnumerable<Entry> Intact => Entries.Where(e => e.Intact);
        public IEnumerable<Entry> Damaged => Entries.Where(e => !e.Intact);
    }

    /// <summary>ניתוח הארכיון: אילו קבצים פנימיים שלמים, והאם תוכן העניינים תקין.</summary>
    internal static Analysis Analyze(byte[] data)
    {
        var central = ReadCentralDirectory(data);
        var entries = new List<Entry>();

        long pos = 0;
        while (pos + 30 <= data.Length)
        {
            if (U32(data, pos) != LocalSignature)
            {
                // אזור פגום בין קבצים: מדלגים לכותרת המקומית הבאה.
                long next = IndexOf(data, LocalSignature, pos + 1);
                if (next < 0) break;
                pos = next;
                continue;
            }

            ushort flags = U16(data, pos + 6);
            uint compressed = U32(data, pos + 18), size = U32(data, pos + 22);
            int nameLength = U16(data, pos + 26), extraLength = U16(data, pos + 28);
            long dataOffset = pos + 30 + nameLength + extraLength;
            if (dataOffset > data.Length) break;

            if (compressed == 0xFFFFFFFF || size == 0xFFFFFFFF)
                return new Analysis(entries, false, true, "ארכיון Zip64 (קבצים מעל 4GB) — מחוץ לתחום התיקון.");

            byte[] nameBytes = data.AsSpan((int)(pos + 30), nameLength).ToArray();
            byte[] extra = data.AsSpan((int)(pos + 30 + nameLength), extraLength).ToArray();
            string name = DecodeName(nameBytes, flags);
            uint crc = U32(data, pos + 14);

            // סיביות 3: הגדלים וה-CRC נכתבו אחרי הנתונים ("data descriptor").
            long descriptorLength = 0;
            if ((flags & 0x08) != 0)
            {
                if (central.TryGetValue(pos, out var fromCentral))
                {
                    (crc, compressed, size) = fromCentral;
                    descriptorLength = U32(data, dataOffset + compressed) == DescriptorSignature ? 16 : 12;
                }
                else
                {
                    var found = FindDescriptor(data, dataOffset);
                    if (found is null)
                    {
                        entries.Add(Broken(name, pos, "סוף הקובץ הפנימי לא נמצא — ככל הנראה נקטע."));
                        break;
                    }
                    (crc, compressed, size, descriptorLength) = found.Value;
                }
            }

            long end = dataOffset + compressed;
            if (end > data.Length)
            {
                entries.Add(Broken(name, pos, "הקובץ הפנימי נקטע באמצע."));
                break;
            }

            var (intact, verified, problem) = Verify(data.AsSpan((int)dataOffset, (int)compressed),
                                                      U16(data, pos + 8), flags, crc, size);

            entries.Add(new Entry(name, pos, U16(data, pos + 4), (ushort)(flags & ~0x08), U16(data, pos + 8),
                U16(data, pos + 10), U16(data, pos + 12), crc, compressed, size,
                nameBytes, extra, dataOffset, intact, verified, problem));

            pos = end + descriptorLength;
        }

        bool directoryIntact = central.Count > 0 && central.Count == entries.Count &&
                               entries.All(e => central.ContainsKey(e.LocalOffset));
        return new Analysis(entries, directoryIntact, false, null);
    }

    private static Entry Broken(string name, long offset, string problem)
        => new(name, offset, 0, 0, 0, 0, 0, 0, 0, 0, Array.Empty<byte>(), Array.Empty<byte>(), 0, false, false, problem);

    /// <summary>
    /// בדיקת קובץ פנימי: פריסה וסכום ביקורת. קובץ מוצפן, או בשיטת דחיסה
    /// שאינה נתמכת, נשמר כפי שהוא — אי אפשר לאמת אותו, אבל גם אין סיבה לזרוק.
    /// </summary>
    private static (bool Intact, bool Verified, string? Problem) Verify(
        ReadOnlySpan<byte> compressed, ushort method, ushort flags, uint crc, uint size)
    {
        if ((flags & 0x01) != 0 || method is not (0 or 8)) return (true, false, null);

        byte[] content;
        if (method == 0)
        {
            content = compressed.ToArray();
        }
        else
        {
            try
            {
                using var input = new MemoryStream(compressed.ToArray());
                using var inflate = new DeflateStream(input, CompressionMode.Decompress);
                content = new byte[size];
                int total = 0, n;
                while (total < content.Length && (n = inflate.Read(content, total, content.Length - total)) > 0)
                    total += n;
                if (total != size || inflate.ReadByte() >= 0)
                    return (false, false, "הנתונים הדחוסים אינם באורך הצפוי.");
            }
            catch (InvalidDataException)
            {
                return (false, false, "הנתונים הדחוסים פגומים.");
            }
        }

        if (content.Length != size) return (false, false, "הקובץ הפנימי אינו באורך הצפוי.");
        return Crc32.Compute(content) == crc
            ? (true, true, null)
            : (false, false, "סכום הביקורת (CRC) אינו תואם — התוכן השתנה.");
    }

    /// <summary>
    /// סוף קובץ שהגדלים שלו נכתבו אחריו: מחפשים תיאור נתונים שהגודל הדחוס
    /// שרשום בו שווה בדיוק למרחק ממנו לתחילת הנתונים. גם בלי חתימת PK 07 08 —
    /// היא אופציונלית בתקן — אם אחריו מתחילה כותרת או ספרייה.
    /// </summary>
    private static (uint Crc, uint Compressed, uint Size, long Length)? FindDescriptor(byte[] data, long dataOffset)
    {
        for (long p = dataOffset; p + 12 <= data.Length; p++)
        {
            if (U32(data, p) == DescriptorSignature && p + 16 <= data.Length && U32(data, p + 8) == p - dataOffset)
                return (U32(data, p + 4), U32(data, p + 8), U32(data, p + 12), 16);

            uint next = p + 12 + 4 <= data.Length ? U32(data, p + 12) : 0;
            if (next is LocalSignature or CentralSignature && U32(data, p + 4) == p - dataOffset)
                return (U32(data, p), U32(data, p + 4), U32(data, p + 8), 12);
        }
        return null;
    }

    /// <summary>
    /// תוכן העניינים הקיים, אם הוא שלם: היסט כותרת מקומית ← (CRC, גודל דחוס, גודל).
    /// מילון ריק — חסר או פגום.
    /// </summary>
    private static Dictionary<long, (uint Crc, uint Compressed, uint Size)> ReadCentralDirectory(byte[] data)
    {
        var result = new Dictionary<long, (uint, uint, uint)>();

        // רשומת הסיום נמצאת ב-22 הבתים האחרונים, או לפני הערה של עד 64KB.
        long eocd = -1;
        for (long p = data.Length - 22; p >= Math.Max(0, data.Length - 22 - 65535); p--)
            if (U32(data, p) == EndSignature) { eocd = p; break; }
        if (eocd < 0) return result;

        int count = U16(data, eocd + 10);
        long offset = U32(data, eocd + 16);

        for (int i = 0; i < count; i++)
        {
            if (offset + 46 > data.Length || U32(data, offset) != CentralSignature) return new();

            long local = U32(data, offset + 42);
            if (local + 4 > data.Length || U32(data, local) != LocalSignature) return new();

            result[local] = (U32(data, offset + 16), U32(data, offset + 20), U32(data, offset + 24));
            offset += 46 + U16(data, offset + 28) + U16(data, offset + 30) + U16(data, offset + 32);
        }
        return result;
    }

    /// <summary>ארכיון חדש מהקבצים השלמים בלבד, עם תוכן עניינים חדש.</summary>
    internal static byte[] Rebuild(byte[] data, Analysis analysis)
    {
        using var output = new MemoryStream();
        var written = new List<(Entry Entry, long Offset)>();

        // חוצצים אחד לכל סוג רשומה, מחוץ ללולאות: stackalloc בתוך לולאה היה מצטבר
        // על המחסנית — וארכיון של עשרות אלפי קבצים פנימיים היה מפיל את התוכנה.
        Span<byte> header = stackalloc byte[30];
        Span<byte> c = stackalloc byte[46];

        foreach (var e in analysis.Intact)
        {
            written.Add((e, output.Position));

            // כותרת מקומית עם הגדלים וה-CRC במקומם — בלי "data descriptor".
            BinaryPrimitives.WriteUInt32LittleEndian(header, LocalSignature);
            BinaryPrimitives.WriteUInt16LittleEndian(header[4..], e.Version);
            BinaryPrimitives.WriteUInt16LittleEndian(header[6..], e.Flags);
            BinaryPrimitives.WriteUInt16LittleEndian(header[8..], e.Method);
            BinaryPrimitives.WriteUInt16LittleEndian(header[10..], e.Time);
            BinaryPrimitives.WriteUInt16LittleEndian(header[12..], e.Date);
            BinaryPrimitives.WriteUInt32LittleEndian(header[14..], e.Crc);
            BinaryPrimitives.WriteUInt32LittleEndian(header[18..], e.CompressedSize);
            BinaryPrimitives.WriteUInt32LittleEndian(header[22..], e.Size);
            BinaryPrimitives.WriteUInt16LittleEndian(header[26..], (ushort)e.NameBytes.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(header[28..], (ushort)e.Extra.Length);
            output.Write(header);
            output.Write(e.NameBytes);
            output.Write(e.Extra);
            output.Write(data, (int)e.DataOffset, (int)e.CompressedSize);
        }

        long directory = output.Position;
        foreach (var (e, offset) in written)
        {
            c.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(c, CentralSignature);
            BinaryPrimitives.WriteUInt16LittleEndian(c[4..], e.Version);       // נוצר על ידי
            BinaryPrimitives.WriteUInt16LittleEndian(c[6..], e.Version);       // נדרש לפתיחה
            BinaryPrimitives.WriteUInt16LittleEndian(c[8..], e.Flags);
            BinaryPrimitives.WriteUInt16LittleEndian(c[10..], e.Method);
            BinaryPrimitives.WriteUInt16LittleEndian(c[12..], e.Time);
            BinaryPrimitives.WriteUInt16LittleEndian(c[14..], e.Date);
            BinaryPrimitives.WriteUInt32LittleEndian(c[16..], e.Crc);
            BinaryPrimitives.WriteUInt32LittleEndian(c[20..], e.CompressedSize);
            BinaryPrimitives.WriteUInt32LittleEndian(c[24..], e.Size);
            BinaryPrimitives.WriteUInt16LittleEndian(c[28..], (ushort)e.NameBytes.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(c[30..], (ushort)e.Extra.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(c[42..], (uint)offset);
            output.Write(c);
            output.Write(e.NameBytes);
            output.Write(e.Extra);
        }

        long directorySize = output.Position - directory;
        Span<byte> end = stackalloc byte[22];
        end.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(end, EndSignature);
        BinaryPrimitives.WriteUInt16LittleEndian(end[8..], (ushort)written.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(end[10..], (ushort)written.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(end[12..], (uint)directorySize);
        BinaryPrimitives.WriteUInt32LittleEndian(end[16..], (uint)directory);
        output.Write(end);

        return output.ToArray();
    }

    /// <summary>שם קובץ פנימי: UTF-8 כשסיבית 11 דלוקה, אחרת קידוד DOS (437).</summary>
    private static string DecodeName(byte[] bytes, ushort flags)
    {
        if ((flags & 0x800) != 0) return Encoding.UTF8.GetString(bytes);
        return bytes.All(b => b < 0x80) ? Encoding.ASCII.GetString(bytes) : Encoding.Latin1.GetString(bytes);
    }

    private static ushort U16(byte[] d, long at)
        => at + 2 <= d.Length && at >= 0 ? BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan((int)at)) : (ushort)0;

    private static uint U32(byte[] d, long at)
        => at + 4 <= d.Length && at >= 0 ? BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)at)) : 0;

    private static long IndexOf(byte[] d, uint signature, long from)
    {
        for (long p = from; p + 4 <= d.Length; p++)
            if (U32(d, p) == signature) return p;
        return -1;
    }
}
