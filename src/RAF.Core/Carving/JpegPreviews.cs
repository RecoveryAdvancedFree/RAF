using System.Buffers.Binary;

namespace RAF.Core.Carving;

/// <summary>
/// התמונות המוקטנות שבתוך תמונת JPEG של מצלמה או טלפון.
///
/// כמעט כל תמונה נושאת עותק מוקטן לתצוגה בסייר ובמצלמה (כ-160×120, ברשימת
/// ה-EXIF), ומצלמות רבות גם תצוגה מקדימה גדולה — לרוב ברוחב של כ-1920 — בחלק
/// ה-MPF. כשהתמונה עצמה פגומה (נקטעה, או שחציה נדרס), העותקים האלה שורדים לעיתים
/// קרובות: המוקטן יושב בתחילת הקובץ. תמונה משפחתית ישנה ב-1920 עדיפה על כלום.
///
/// כל מועמד נבדק במפענח עד סופו — מוחזרת רק תמונה שלמה.
/// </summary>
internal static class JpegPreviews
{
    internal sealed record Preview(byte[] Data, int Width, int Height, string Source);

    /// <summary>התמונה המוקטנת הגדולה ביותר שנמצאה שלמה, או null.</summary>
    internal static Preview? Best(byte[] file)
    {
        Preview? best = null;
        foreach (var candidate in Candidates(file))
        {
            if (candidate.Length < 4 || candidate[0] != 0xFF || candidate[1] != 0xD8) continue;
            if (Dimensions(candidate) is not { } size) continue;

            // רק תמונה שהמפענח מאשר שלמה עד הסוף. פורמט שהמפענח אינו מכריע בו
            // (פרוגרסיבי) מתקבל אם הוא נגמר בסימן הסיום.
            var check = JpegDecoder.Check(JpegBytes.Of(candidate));
            bool whole = check.Verdict == JpegVerdict.Complete ||
                         (check.Verdict == JpegVerdict.Unsupported && EndsWithEoi(candidate));
            if (!whole) continue;

            var preview = new Preview(candidate, size.Width, size.Height, "");
            if (best is null || (long)size.Width * size.Height > (long)best.Width * best.Height) best = preview;
        }

        return best;
    }

    /// <summary>המועמדים: התמונה הממוזערת של EXIF, והתמונות הנוספות של MPF.</summary>
    private static IEnumerable<byte[]> Candidates(byte[] file)
    {
        var found = new List<byte[]>();
        int pos = 2;

        while (pos + 4 <= file.Length && file[pos] == 0xFF)
        {
            byte marker = file[pos + 1];
            if (marker is 0xD9 or 0xDA) break;                          // סוף הכותרות — מכאן נתוני התמונה
            if (marker is 0xFF or >= 0xD0 and <= 0xD7 or 0x01) { pos++; continue; }

            int length = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(pos + 2));
            if (length < 2 || pos + 2 + length > file.Length) break;
            int data = pos + 4;

            if (marker == 0xE1 && Has(file, data, "Exif\0\0"u8))
                AddExifThumbnail(file, data + 6, found);
            else if (marker == 0xE2 && Has(file, data, "MPF\0"u8))
                AddMpfImages(file, data + 4, found);

            pos += 2 + length;
        }

        return found;
    }

    /// <summary>ה-TIFF שבתוך EXIF: הרשימה השנייה (IFD1) מצביעה לתמונה הממוזערת.</summary>
    private static void AddExifThumbnail(byte[] file, int tiff, List<byte[]> found)
    {
        if (Tiff(file, tiff) is not { } t) return;

        long ifd0 = t.U32(4);
        if (ifd0 <= 0 || tiff + ifd0 + 2 > file.Length) return;
        long entries = t.U16(ifd0);
        long ifd1 = t.U32(ifd0 + 2 + entries * 12);
        if (ifd1 <= 0 || tiff + ifd1 + 2 > file.Length) return;

        long offset = -1, length = -1;
        long count = t.U16(ifd1);
        for (long e = ifd1 + 2; e < ifd1 + 2 + count * 12 && tiff + e + 12 <= file.Length; e += 12)
        {
            long tag = t.U16(e);
            if (tag == 0x201) offset = t.U32(e + 8);
            else if (tag == 0x202) length = t.U32(e + 8);
        }

        Add(file, tiff + offset, length, found);
    }

    /// <summary>
    /// MPF (Multi-Picture Format): רשימת התמונות שבקובץ. הראשונה היא התמונה עצמה;
    /// השאר — תצוגות מקדימות. ההיסטים יחסיים לתחילת כותרת ה-MPF.
    /// </summary>
    private static void AddMpfImages(byte[] file, int header, List<byte[]> found)
    {
        if (Tiff(file, header) is not { } t) return;

        long ifd = t.U32(4);
        if (ifd <= 0 || header + ifd + 2 > file.Length) return;

        long count = t.U16(ifd);
        for (long e = ifd + 2; e < ifd + 2 + count * 12 && header + e + 12 <= file.Length; e += 12)
        {
            if (t.U16(e) != 0xB002) continue;                           // MPEntry
            long bytes = t.U32(e + 4);
            long table = t.U32(e + 8);

            for (long i = 16; i + 16 <= bytes; i += 16)                 // הרשומה הראשונה — התמונה עצמה
            {
                long size = t.U32(table + i + 4);
                long offset = t.U32(table + i + 8);
                if (offset > 0) Add(file, header + offset, size, found);
            }
        }
    }

    private static void Add(byte[] file, long offset, long length, List<byte[]> found)
    {
        if (offset <= 0 || length <= 0 || length > 64L * 1024 * 1024 || offset + length > file.Length) return;
        found.Add(file.AsSpan((int)offset, (int)length).ToArray());
    }

    /// <summary>קורא מספרים מתוך מבנה TIFF קטן, לפי סדר הבתים שבכותרת שלו.</summary>
    private readonly record struct TiffView(byte[] File, int Start, bool Little)
    {
        public long U16(long at)
        {
            long p = Start + at;
            if (p < 0 || p + 2 > File.Length) return -1;
            var s = File.AsSpan((int)p, 2);
            return Little ? BinaryPrimitives.ReadUInt16LittleEndian(s) : BinaryPrimitives.ReadUInt16BigEndian(s);
        }

        public long U32(long at)
        {
            long p = Start + at;
            if (p < 0 || p + 4 > File.Length) return -1;
            var s = File.AsSpan((int)p, 4);
            return Little ? BinaryPrimitives.ReadUInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32BigEndian(s);
        }
    }

    private static TiffView? Tiff(byte[] file, int start)
    {
        if (start + 8 > file.Length) return null;
        if (file[start] == 'I' && file[start + 1] == 'I') return new TiffView(file, start, true);
        if (file[start] == 'M' && file[start + 1] == 'M') return new TiffView(file, start, false);
        return null;
    }

    private static bool Has(byte[] file, int at, ReadOnlySpan<byte> text)
        => at + text.Length <= file.Length && file.AsSpan(at, text.Length).SequenceEqual(text);

    private static bool EndsWithEoi(byte[] data)
        => data.Length >= 2 && data[^2] == 0xFF && data[^1] == 0xD9;

    /// <summary>רוחב וגובה מתוך כותרת ה-SOF של JPEG.</summary>
    internal static (int Width, int Height)? Dimensions(byte[] jpeg)
    {
        int pos = 2;
        while (pos + 9 <= jpeg.Length && jpeg[pos] == 0xFF)
        {
            byte marker = jpeg[pos + 1];
            if (marker is 0xFF) { pos++; continue; }
            int length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(pos + 2));
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                int h = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(pos + 5));
                int w = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(pos + 7));
                return w > 0 && h > 0 ? (w, h) : null;
            }
            if (marker is 0xDA or 0xD9 || length < 2) return null;
            pos += 2 + length;
        }
        return null;
    }
}
