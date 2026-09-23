using System.Buffers.Binary;
using RAF.Core.FileSystems;
using RAF.Core.Signatures;

namespace RAF.Core.Carving;

/// <summary>
/// קריאה חלונאית מקובץ שזוהה על המחיצה, לפי היסט יחסי לתחילתו.
/// מאפשרת למפענחי המבנה לעבור על קבצים גדולים בלי לקרוא אותם לזיכרון.
/// </summary>
internal sealed class WindowReader
{
    private const int WindowSize = 1 << 20;

    private readonly IClusterVolume _volume;
    private readonly long _start;
    private readonly byte[] _buffer = new byte[WindowSize];
    private long _bufferStart = -1;
    private int _bufferLength;

    /// <summary>הגבול העליון: מעבר לו אין לקרוא.</summary>
    internal long Limit { get; }

    internal WindowReader(IClusterVolume volume, long start, long limit)
    {
        _volume = volume;
        _start = start;
        Limit = limit;
    }

    /// <summary>הבית בהיסט נתון, או ‎-1 מעבר לגבול או לסוף הנתונים.</summary>
    internal int Byte(long pos)
    {
        if (pos < 0 || pos >= Limit) return -1;

        if (pos < _bufferStart || pos >= _bufferStart + _bufferLength)
        {
            _bufferStart = pos;
            int want = (int)Math.Min(WindowSize, Limit - pos);
            _bufferLength = Math.Max(0, _volume.ReadRaw(_start + pos, _buffer.AsSpan(0, want)));
            if (_bufferLength == 0) return -1;
        }

        return _buffer[pos - _bufferStart];
    }

    internal int BigEndian16(long pos)
    {
        int a = Byte(pos), b = Byte(pos + 1);
        return a < 0 || b < 0 ? -1 : (a << 8) | b;
    }

    internal int LittleEndian16(long pos)
    {
        int a = Byte(pos), b = Byte(pos + 1);
        return a < 0 || b < 0 ? -1 : a | (b << 8);
    }

    internal long LittleEndian32(long pos)
    {
        long value = 0;
        for (int i = 3; i >= 0; i--)
        {
            int b = Byte(pos + i);
            if (b < 0) return -1;
            value = (value << 8) | (uint)b;
        }
        return value;
    }

    /// <summary>ההיסט הבא שבו מופיע בית נתון, או ‎-1.</summary>
    internal long IndexOf(byte value, long from)
    {
        for (long pos = from; pos < Limit; )
        {
            if (Byte(pos) < 0) return -1;

            // חיפוש בתוך החלון הטעון, בלי קריאה לכל בית בנפרד.
            int local = (int)(pos - _bufferStart);
            int found = Array.IndexOf(_buffer, value, local, _bufferLength - local);
            if (found >= 0) return _bufferStart + found;

            pos = _bufferStart + _bufferLength;
        }

        return -1;
    }

    /// <summary>ההיסט הבא שבו מופיע רצף בתים, או ‎-1.</summary>
    internal long IndexOf(ReadOnlySpan<byte> pattern, long from)
    {
        for (long pos = IndexOf(pattern[0], from); pos >= 0; pos = IndexOf(pattern[0], pos + 1))
        {
            bool match = true;
            for (int i = 1; i < pattern.Length && match; i++)
                match = Byte(pos + i) == pattern[i];

            if (match) return pos;
        }

        return -1;
    }
}

/// <summary>
/// אימות מבני וקריאת אורך לפורמטים שחתימתם קצרה.
///
/// חתימה של שני בתים — BM או MZ — מופיעה בנתונים אקראיים פעם בכ-65,536
/// סקטורים, כלומר מאות פעמים בכל כונן. בסריקה על כונן אמיתי נמצאו כך
/// מאות "קבצים" שלא היו קיימים. האימות כאן בודק שהמבנה שאחרי החתימה
/// הגיוני, ובכך מבדיל בין קובץ לבין צירוף מקרים.
/// </summary>
internal static class StructureCheck
{
    private static string Kind(FileSignature signature)
        => signature.Extensions.Length > 0 ? signature.Extensions[0] : "";

    /// <summary>
    /// פורמטים שמבנם נקרא במלואו. כישלון בקריאתם מעיד על התאמת שווא
    /// ולא על קובץ שאורכו אינו ידוע, ולכן אין לנחש להם אורך.
    /// </summary>
    internal static bool HasFullParser(FileSignature signature) => Kind(signature)
        is "bmp" or "exe" or "ico" or "gif" or "png" or "jpg"
        or "wav" or "avi" or "webp" or "db" or "mp4";

    /// <summary>האם המבנה שאחרי החתימה עקבי עם קובץ אמיתי.</summary>
    internal static bool IsPlausible(FileSignature signature, byte[] head) => Kind(signature) switch
    {
        "bmp" => Bmp(head),
        "exe" => Pe(head),
        "ico" => Ico(head),
        "gif" => head.Length >= 10 && head[4] is (byte)'7' or (byte)'9' && head[5] == (byte)'a'
                 && BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(6)) > 0
                 && BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(8)) > 0,
        "png" => head.Length >= 16 && BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(8)) == 13
                 && head.AsSpan(12, 4).SequenceEqual("IHDR"u8),
        "jpg" => Jpeg(head),
        "wav" or "avi" or "webp" => head.Length >= 12
                 && BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4)) >= 12,
        "mp4" => Mp4(head),
        "db" => Sqlite(head),
        "mp3" => Id3(head),
        "gz" => head.Length >= 10 && (head[3] & 0xE0) == 0,
        "tif" => head.Length >= 8 && BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4)) >= 8,
        _ => true,
    };

    // ======================================================= אימות

    /// <summary>BMP חייב כותרת DIB בגודל מוכר, מישור אחד ועומק צבע חוקי.</summary>
    private static bool Bmp(byte[] h)
    {
        if (h.Length < 30) return false;

        uint fileSize = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(2));
        uint pixelOffset = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(10));
        uint dibSize = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(14));

        if (dibSize is not (12 or 40 or 52 or 56 or 64 or 108 or 124)) return false;
        if (fileSize < 26 || pixelOffset < 14 + dibSize || pixelOffset >= fileSize) return false;

        if (dibSize == 12)
        {
            // כותרת OS/2 הישנה: שדות של 16 ביט.
            return BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(22)) == 1
                   && IsBitDepth(BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(24)));
        }

        int width = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(18));
        int height = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(22));

        return width is > 0 and <= 100_000
               && height != 0 && Math.Abs(height) <= 100_000
               && BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(26)) == 1
               && IsBitDepth(BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(28)));
    }

    private static bool IsBitDepth(int bpp) => bpp is 1 or 2 or 4 or 8 or 16 or 24 or 32 or 64;

    /// <summary>קובץ הרצה אמיתי מצביע מכותרת ה-DOS אל חתימת PE.</summary>
    private static bool Pe(byte[] h)
    {
        if (h.Length < 0x40) return false;

        uint peOffset = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(0x3C));
        if (peOffset < 0x40 || peOffset + 24 > h.Length) return false;

        return h.AsSpan((int)peOffset, 4).SequenceEqual("PE\0\0"u8);
    }

    /// <summary>
    /// ICO: ספריית תמונות שכל שדותיה חוקיים, ותמונה ראשונה שמתחילה
    /// בכותרת BMP או PNG.
    ///
    /// הבדיקה המלאה נחוצה כי טבלת האותיות הגדולות שכל מחיצת exFAT כותבת
    /// בתחילתה — רצף 00 00 01 00 02 00... — פותחת בדיוק בחתימת ICO, והמשך
    /// הרצף נקרא כספרייה עם גדלים והיסטים סבירים. על כונן אמיתי "סמל"
    /// כזה בלע את כל הקבצים שנכתבו אחריו.
    /// </summary>
    private static bool Ico(byte[] h)
    {
        if (h.Length < 22) return false;

        int count = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(4));
        int directoryEnd = 6 + count * 16;
        if (count is 0 or > 64 || directoryEnd > h.Length) return false;

        uint firstOffset = uint.MaxValue;

        for (int i = 0; i < count; i++)
        {
            int at = 6 + i * 16;
            if (h[at + 3] != 0) return false;                       // שדה שמור

            int planes = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(at + 4));
            int bitDepth = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(at + 6));
            if (planes > 1) return false;
            if (bitDepth is not (0 or 1 or 4 or 8 or 16 or 24 or 32)) return false;

            uint size = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(at + 8));
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(at + 12));

            if (size is 0 or > 4 * 1024 * 1024) return false;
            if (offset < directoryEnd || offset > 4 * 1024 * 1024) return false;

            firstOffset = Math.Min(firstOffset, offset);
        }

        // התמונה הראשונה חייבת להתחיל בכותרת DIB או בחתימת PNG.
        if (firstOffset + 8 <= h.Length)
        {
            var image = h.AsSpan((int)firstOffset, 8);
            bool dib = BinaryPrimitives.ReadUInt32LittleEndian(image) == 40;
            bool png = image.SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            if (!dib && !png) return false;
        }

        return true;
    }

    /// <summary>
    /// מידת הוודאות באורך שנקרא. הסורק מדלג על גוף קובץ רק כשהאורך אומת —
    /// אחרת התאמת שווא אחת עם אורך "סביר" הייתה מסתירה כל מה שאחריה.
    /// </summary>
    internal static LengthConfidence ConfidenceOf(
        FileSignature signature, IClusterVolume volume, long offset, long length)
    {
        switch (Kind(signature))
        {
            // שדה גודל יחיד בכותרת: מדויק לקובץ אמיתי, אך אינו מוכיח שזה קובץ.
            case "bmp" or "ico" or "wav" or "avi" or "webp" or "db":
                return LengthConfidence.Declared;

            // JPEG שנקטע (קובץ מפוצל) לא הגיע לסמן הסיום, ולכן גבולו אינו ודאי.
            case "jpg":
                byte[] tail = new byte[2];
                return volume.ReadRaw(offset + length - 2, tail) == 2 && tail[0] == 0xFF && tail[1] == 0xD9
                    ? LengthConfidence.Exact
                    : LengthConfidence.Declared;

            // מבנה שנקרא מתחילתו ועד סופו: PNG, MP4, EXE, GIF, ZIP.
            default:
                return LengthConfidence.Exact;
        }
    }

    /// <summary>JPEG: אחרי החתימה בא סמן מקטע מוכר עם אורך סביר.</summary>
    private static bool Jpeg(byte[] h)
    {
        if (h.Length < 6) return false;

        byte marker = h[3];
        bool known = marker is >= 0xE0 and <= 0xEF   // מקטעי יישום (JFIF, Exif)
                     or 0xDB or 0xC4 or 0xFE        // טבלאות והערה
                     or >= 0xC0 and <= 0xC3;        // תחילת תמונה

        return known && BinaryPrimitives.ReadUInt16BigEndian(h.AsSpan(4)) >= 2;
    }

    /// <summary>MP4: התיבה הראשונה היא ftyp קטנה, ואחריה מזהה מותג מודפס.</summary>
    private static bool Mp4(byte[] h)
    {
        if (h.Length < 12) return false;

        uint size = BinaryPrimitives.ReadUInt32BigEndian(h);
        if (size is < 8 or > 1024) return false;

        for (int i = 8; i < 12; i++)
            if (h[i] is < 0x20 or > 0x7E) return false;

        return true;
    }

    /// <summary>SQLite: גודל דף שהוא חזקת 2 וגרסאות קריאה וכתיבה חוקיות.</summary>
    private static bool Sqlite(byte[] h)
    {
        if (h.Length < 32) return false;

        int page = BinaryPrimitives.ReadUInt16BigEndian(h.AsSpan(16));
        bool pageOk = page == 1 || (page is >= 512 and <= 32768 && (page & (page - 1)) == 0);

        return pageOk && h[18] is 1 or 2 && h[19] is 1 or 2
               && BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(28)) > 0;
    }

    /// <summary>תג ID3: גרסה 2 עד 4, וגודל בקידוד שבו הביט העליון של כל בית כבוי.</summary>
    private static bool Id3(byte[] h)
    {
        if (h.Length < 10) return false;
        if (h[3] is < 2 or > 4 || h[4] == 0xFF) return false;
        if ((h[5] & 0x0F) != 0) return false;

        for (int i = 6; i < 10; i++)
            if (h[i] >= 0x80) return false;

        return true;
    }

    // ======================================================= אורך

    /// <summary>
    /// אורך JPEG מתוך מבנה המקטעים.
    ///
    /// חיפוש פשוט של FF D9 שגוי פעמיים: הוא נעצר בסוף תמונה ממוזערת שבתוך
    /// מקטע Exif, ובנתונים שאחרי הקובץ הוא מוצא מופע אקראי. המעבר על
    /// המקטעים לפי אורכם מדלג על התמונה הממוזערת, והסריקה של הנתונים
    /// הדחוסים מבחינה בין FF D9 אמיתי לבין בתים מרופדים.
    /// </summary>
    internal static long ReadJpeg(WindowReader r)
    {
        long pos = 2;

        // סוף הנתונים הדחוסים האחרונים שנקראו. סמן שנראה כמו מקטע נוסף אחריהם
        // עשוי להיות סריקה נוספת בתמונה מתקדמת — או סתם בתים של קובץ אחר,
        // שבמקרה נראים כך (כ-8% מהמקרים בקובץ מפוצל). אם ה"מקטע" אינו נקרא,
        // הקובץ נחתך כאן, ואינו נפסל כולו: החלק התקין עד כאן הוא עדיין תמונה.
        long afterScan = 0;
        long Fail() => afterScan;

        for (int guard = 0; guard < 100_000; guard++)
        {
            if (r.Byte(pos) != 0xFF) return Fail();
            while (r.Byte(pos + 1) == 0xFF) pos++;           // בתי מילוי

            int marker = r.Byte(pos + 1);
            if (marker < 0) return Fail();

            if (marker is 0x01 or >= 0xD0 and <= 0xD7) { pos += 2; continue; }
            if (marker == 0xD9) return pos + 2;
            if (marker is 0x00 or 0xD8) return Fail();

            int length = r.BigEndian16(pos + 2);
            if (length < 2) return Fail();

            if (marker != 0xDA)
            {
                pos += 2 + length;
                continue;
            }

            // תחילת נתונים דחוסים: סורקים עד לסמן שאינו ריפוד או איפוס.
            pos += 2 + length;

            while (true)
            {
                long ff = r.IndexOf(0xFF, pos);
                if (ff < 0) return Fail();

                int next = r.Byte(ff + 1);
                if (next < 0) return Fail();

                if (next == 0x00 || next is >= 0xD0 and <= 0xD7) { pos = ff + 2; continue; }
                if (next == 0xFF) { pos = ff + 1; continue; }
                if (next == 0xD9) return ff + 2;

                // סמן שאינו יכול להופיע אחרי נתונים דחוסים פירושו שהנתונים
                // נקטעו — בדרך כלל קובץ מפוצל. החלק התקין עד כאן עדיין שימושי,
                // ואת חתימת הסיום החסרה ניתן להשלים בתיקון הקבצים.
                if (!IsJpegSegmentMarker(next)) return ff;

                // סמן אחר — בתמונה מתקדמת זו טבלה או סריקה נוספת.
                afterScan = ff;
                pos = ff;
                break;
            }
        }

        return Fail();
    }

    /// <summary>סמנים שמותר להם להופיע בין סריקות של תמונה מתקדמת.</summary>
    private static bool IsJpegSegmentMarker(int marker) => marker
        is 0xC4 or 0xDA or 0xDB or 0xDD or 0xFE or >= 0xE0 and <= 0xEF;

    /// <summary>
    /// אורך קובץ הרצה: הכותרות, המקטעים והחתימה הדיגיטלית.
    /// נתונים שתוכנות התקנה מצמידות לסוף הקובץ אינם מתוארים בכותרות
    /// ולכן אינם נכללים — מגבלה ידועה של שחזור לפי חתימה.
    /// </summary>
    internal static long ReadPe(WindowReader r)
    {
        long pe = r.LittleEndian32(0x3C);
        if (pe < 0x40 || r.LittleEndian32(pe) != 0x00004550) return 0;   // "PE\0\0"

        long coff = pe + 4;
        int sections = r.LittleEndian16(coff + 2);
        int optionalSize = r.LittleEndian16(coff + 16);
        if (sections is <= 0 or > 96 || optionalSize < 0) return 0;

        long optional = coff + 20;
        int magic = r.LittleEndian16(optional);
        if (magic is not (0x10B or 0x20B)) return 0;

        long end = r.LittleEndian32(optional + 60);                     // גודל הכותרות
        if (end <= 0) return 0;

        long table = optional + optionalSize;
        for (int i = 0; i < sections; i++)
        {
            long raw = r.LittleEndian32(table + i * 40 + 16);
            long pointer = r.LittleEndian32(table + i * 40 + 20);
            if (raw < 0 || pointer < 0) return 0;
            if (raw > 0) end = Math.Max(end, pointer + raw);
        }

        // חתימה דיגיטלית: רשומה 4 בטבלת הנתונים, והיסט שלה הוא היסט בקובץ.
        long directories = optional + (magic == 0x10B ? 96 : 112);
        if (r.LittleEndian32(directories - 4) > 4)
        {
            long certOffset = r.LittleEndian32(directories + 32);
            long certSize = r.LittleEndian32(directories + 36);
            if (certOffset > 0 && certSize > 0) end = Math.Max(end, certOffset + certSize);
        }

        return end <= r.Limit ? end : 0;
    }

    /// <summary>אורך ICO: סוף התמונה הרחוקה ביותר בספרייה.</summary>
    internal static long ReadIco(byte[] h)
    {
        if (!Ico(h)) return 0;

        int count = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(4));
        long end = 0;

        for (int i = 0; i < count; i++)
        {
            int at = 6 + i * 16;
            end = Math.Max(end,
                (long)BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(at + 12)) +
                BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(at + 8)));
        }

        return end;
    }

    /// <summary>
    /// אורך GIF מתוך מבנה הבלוקים. בית הסיום 3B לבדו מופיע בכל מקום,
    /// ולכן רק מעבר על הבלוקים מגיע אליו בוודאות.
    /// </summary>
    internal static long ReadGif(WindowReader r)
    {
        int packed = r.Byte(10);
        if (packed < 0) return 0;

        long pos = 13;
        if ((packed & 0x80) != 0) pos += 3L << ((packed & 7) + 1);     // טבלת צבעים גלובלית

        for (int guard = 0; guard < 1_000_000; guard++)
        {
            int block = r.Byte(pos);

            switch (block)
            {
                case 0x3B:
                    return pos + 1;

                case 0x21:
                    pos += 2;                                            // סימן ותווית ההרחבה
                    if (!SkipSubBlocks(r, ref pos)) return 0;
                    break;

                case 0x2C:
                    int local = r.Byte(pos + 9);
                    if (local < 0) return 0;
                    pos += 10;
                    if ((local & 0x80) != 0) pos += 3L << ((local & 7) + 1);
                    pos += 1;                                            // גודל קוד LZW מינימלי
                    if (!SkipSubBlocks(r, ref pos)) return 0;
                    break;

                default:
                    return 0;
            }
        }

        return 0;
    }

    private static bool SkipSubBlocks(WindowReader r, ref long pos)
    {
        for (int guard = 0; guard < 10_000_000; guard++)
        {
            int size = r.Byte(pos);
            if (size < 0) return false;

            pos += 1 + size;
            if (size == 0) return true;
        }

        return false;
    }

    /// <summary>
    /// אורך ZIP (וגם docx, xlsx ו-pptx): רשומת סוף הספרייה המרכזית,
    /// ואחריה אורך ההערה שבה.
    /// </summary>
    internal static long ReadZip(WindowReader r)
    {
        ReadOnlySpan<byte> endOfDirectory = [0x50, 0x4B, 0x05, 0x06];
        long searchLimit = Math.Min(r.Limit, 256L * 1024 * 1024);

        long found = r.IndexOf(endOfDirectory, 4);
        if (found < 0 || found > searchLimit) return 0;

        int commentLength = r.LittleEndian16(found + 20);
        return commentLength < 0 ? 0 : found + 22 + commentLength;
    }
}
