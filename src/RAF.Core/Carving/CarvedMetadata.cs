using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using RAF.Core.Signatures;

namespace RAF.Core.Carving;

/// <summary>מה נקרא מתוך קובץ שנמצא בסריקה מתקדמת: שם, תאריך, והסוג המדויק.</summary>
internal sealed record CarvedInfo(string? Name, DateTime? Date, string? Extension = null, string? Folder = null);

/// <summary>
/// שם לקובץ שנמצא בסריקה מתקדמת. השם המקורי אבד יחד עם מערכת הקבצים, אבל
/// בתוך הקובץ עצמו שמור בדרך כלל מידע מזהה: תאריך הצילום ודגם המצלמה בתמונה,
/// תאריך ההקלטה בסרטון, הכותרת במסמך, והזמר ושם השיר בקובץ MP3.
///
/// כאן גם מתברר הסוג המדויק: מסמך Word הוא ארכיון ZIP מבפנים, ולפי החתימה
/// בלבד הוא היה נשמר כ-‎.zip.
/// </summary>
internal static class CarvedMetadata
{
    static CarvedMetadata() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>
    /// קריאה מתוך הקובץ. <paramref name="read"/> קורא (היסט, כמות) יחסית לתחילת
    /// הקובץ ומחזיר פחות אם הקובץ קצר יותר. מחזיר null אם אין מה להוסיף.
    /// </summary>
    internal static CarvedInfo? Read(FileSignature signature, long length, Func<long, int, byte[]> read)
    {
        try
        {
            return signature.Structure switch
            {
                "jpg" => Photo(ExifFromJpeg(read(0, 128 * 1024))),
                "tif" => Photo(Exif(read(0, 256 * 1024), 0)),
                "mp4" or "mov" => Movie(signature, read),
                "mp3" => Song(read(0, 256 * 1024)),
                "pdf" => Pdf(length, read),
                "zip" => Zip(length, read),
                "doc" => Ole(read),
                "asf" => Asf(read(0, 64 * 1024)),
                "ogg" => Ogg(read(0, 256)),
                "mpg" => Dvd(read(0, 4096)),
                _ => null,
            };
        }
        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException or InvalidDataException
                                      or FormatException or OverflowException or IOException)
        {
            return null;                                                     // מידע נוסף בלבד — לעולם לא מכשיל סריקה
        }
    }

    // ================================================================ שם

    /// <summary>
    /// שם קובץ חוקי: בלי תווים אסורים ובלי תווי בקרה, באורך סביר.
    /// מחזיר null לשם ריק או חסר משמעות.
    /// </summary>
    internal static string? CleanName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsControl(c) || c == '﻿') continue;
            sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? ' ' : c);
        }

        string name = Regex.Replace(sb.ToString(), @"\s+", " ").Trim().TrimEnd('.', ' ');
        if (name.Length > 90) name = name[..90].TrimEnd();

        return name.Length < 2 || name.Equals("untitled", StringComparison.OrdinalIgnoreCase) ? null : name;
    }

    private static string Stamp(DateTime d) => d.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);

    /// <summary>תאריך סביר: לא לפני שנות התשעים (שעון שלא כוון) ולא בעתיד הרחוק.</summary>
    private static DateTime? Sane(DateTime? d)
        => d is { } v && v.Year >= 1990 && v <= DateTime.Now.AddDays(2) ? v : null;

    // ============================================================ תמונות

    private sealed record ExifData(string? Make, string? Model, DateTime? Taken);

    private static CarvedInfo? Photo(ExifData? exif)
    {
        if (exif is null) return null;
        var date = Sane(exif.Taken);
        string? camera = Camera(exif.Make, exif.Model);
        if (date is null && camera is null) return null;

        string name = date is { } d ? Stamp(d) + (camera is null ? "" : " " + camera) : camera!;
        return new CarvedInfo(CleanName(name), date);
    }

    /// <summary>
    /// "Canon EOS 80D" ולא "Canon Canon EOS 80D": ברוב המצלמות הדגם כבר כולל את היצרן.
    /// </summary>
    private static string? Camera(string? make, string? model)
    {
        make = make?.Trim();
        model = model?.Trim();
        if (string.IsNullOrEmpty(model)) return string.IsNullOrEmpty(make) ? null : make;
        if (string.IsNullOrEmpty(make)) return model;

        string brand = make.Split(' ')[0];
        return model.StartsWith(brand, StringComparison.OrdinalIgnoreCase) ? model : $"{brand} {model}";
    }

    /// <summary>מקטע Exif (APP1) בתוך JPEG — אחרי מקטעי הפתיחה, לפני נתוני התמונה.</summary>
    private static ExifData? ExifFromJpeg(byte[] h)
    {
        int pos = 2;
        while (pos + 4 <= h.Length && h[pos] == 0xFF)
        {
            int marker = h[pos + 1];
            if (marker == 0xFF) { pos++; continue; }
            if (marker is 0xDA or 0xD9) break;                               // תחילת נתוני התמונה
            int length = BinaryPrimitives.ReadUInt16BigEndian(h.AsSpan(pos + 2));
            if (length < 2) break;

            if (marker == 0xE1 && pos + 10 <= h.Length && h.AsSpan(pos + 4, 6).SequenceEqual("Exif\0\0"u8))
                return Exif(h, pos + 10);

            pos += 2 + length;
        }
        return null;
    }

    /// <summary>
    /// מבנה TIFF (בתוך Exif, או קובץ RAW כולו): היצרן, הדגם והתאריך במדור
    /// הראשי, ותאריך הצילום המקורי במדור ה-Exif.
    /// </summary>
    private static ExifData? Exif(byte[] b, int tiff)
    {
        if (tiff + 8 > b.Length) return null;
        bool little = b[tiff] == 'I' && b[tiff + 1] == 'I';
        if (!little && !(b[tiff] == 'M' && b[tiff + 1] == 'M')) return null;

        int U16(int at) => at + 2 > b.Length ? -1
            : little ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at)) : BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(at));
        long U32(int at) => at + 4 > b.Length ? -1
            : little ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at));

        string? make = null, model = null;
        DateTime? changed = null, taken = null;

        string? Ascii(int entry)
        {
            long count = U32(entry + 4);
            if (count is <= 0 or > 256) return null;
            int at = count <= 4 ? entry + 8 : tiff + (int)U32(entry + 8);
            if (at < 0 || at + count > b.Length) return null;
            return Encoding.ASCII.GetString(b, at, (int)count).TrimEnd('\0', ' ');
        }

        void Directory(long offset, bool exif, int depth)
        {
            int at = tiff + (int)offset;
            int count = U16(at);
            if (offset <= 0 || count is <= 0 or > 500 || depth > 2) return;

            for (int i = 0; i < count; i++)
            {
                int entry = at + 2 + i * 12;
                if (entry + 12 > b.Length) return;
                switch (U16(entry))
                {
                    case 0x010F when !exif: make = Ascii(entry); break;
                    case 0x0110 when !exif: model = Ascii(entry); break;
                    case 0x0132 when !exif: changed = ExifDate(Ascii(entry)); break;
                    case 0x8769 when !exif: Directory(U32(entry + 8), true, depth + 1); break;
                    case 0x9003 when exif: taken = ExifDate(Ascii(entry)); break;
                    case 0x9004 when exif && taken is null: taken = ExifDate(Ascii(entry)); break;
                }
            }
        }

        Directory(U32(tiff + 4), false, 0);
        return make is null && model is null && taken is null && changed is null
            ? null
            : new ExifData(make, model, taken ?? changed);
    }

    private static DateTime? ExifDate(string? text)
        => DateTime.TryParseExact(text?.Trim(), "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture,
               DateTimeStyles.None, out var d) ? d : null;

    // ============================================================ סרטונים

    /// <summary>
    /// MP4 ו-MOV: תאריך ההקלטה נמצא ב-mvhd, בתוך תיבת האינדקס (moov) — בתחילת
    /// הקובץ או בסופו. המעבר על התיבות קורא רק את הכותרות שלהן: במצלמות קנון,
    /// למשל, לפני mvhd יושבת תמונה ממוזערת של 64KB.
    /// גם הסיומת מתבררת כאן: המותג ב-ftyp מבדיל בין MP4 ל-MOV, ומסלולי האינדקס —
    /// בין סרטון להקלטת קול (M4A), שמכשירים רבים שומרים עם מותג של וידאו.
    /// </summary>
    private static CarvedInfo? Movie(FileSignature signature, Func<long, int, byte[]> read)
    {
        string? extension = null, folder = null;
        DateTime? date = null;
        bool generic = signature.Extensions[0] == "mp4";

        foreach (var (type, at, size, header) in Boxes(read, 0, long.MaxValue))
        {
            if (type == "ftyp" && generic)
            {
                byte[] brand = read(at + header, 4);
                if (brand.Length == 4 && Encoding.ASCII.GetString(brand) == "qt  ") extension = "mov";
                if (brand.Length == 4 && Encoding.ASCII.GetString(brand) == "M4A ") extension = "m4a";
                if (brand.Length == 4 && Encoding.ASCII.GetString(brand) == "M4V ") extension = "m4v";
            }
            else if (type == "moov")
            {
                var handlers = new HashSet<string>();
                foreach (var (child, childAt, _, childHeader) in Boxes(read, at + header, at + size))
                {
                    if (child == "mvhd") date = MvhdDate(read(childAt + childHeader, 20));
                    else if (child == "trak" && Handler(read, childAt, childHeader) is { } h) handlers.Add(h);
                }

                // שמע בלבד: הקלטה או שיר, גם כשהמותג אומר "וידאו".
                if (generic && handlers.Contains("soun") && !handlers.Contains("vide")) extension = "m4a";
                break;
            }
        }

        if (extension == "m4a") folder = "שמע M4A";   // לא לתרגום: תווית, הממשק מתרגם
        date = Sane(date);
        if (date is null && extension is null) return null;
        return new CarvedInfo(date is { } d ? Stamp(d) : null, date, extension, folder);
    }

    /// <summary>התיבות שבין שני היסטים: סוג, היסט, גודל וגודל הכותרת. רק הכותרות נקראות.</summary>
    private static IEnumerable<(string Type, long At, long Size, int Header)> Boxes(
        Func<long, int, byte[]> read, long from, long to)
    {
        long at = from;
        for (int guard = 0; guard < 256 && at + 8 <= to; guard++)
        {
            byte[] h = read(at, 16);
            if (h.Length < 8) yield break;
            long size = BinaryPrimitives.ReadUInt32BigEndian(h);
            int header = 8;
            if (size == 1 && h.Length >= 16) { size = BinaryPrimitives.ReadInt64BigEndian(h.AsSpan(8)); header = 16; }
            string type = Encoding.ASCII.GetString(h, 4, 4);
            if (size < header || !type.All(c => c is >= ' ' and <= '~')) yield break;

            yield return (type, at, size, header);
            at += size;
        }
    }

    /// <summary>סוג המסלול (vide / soun) מתוך trak → mdia → hdlr.</summary>
    private static string? Handler(Func<long, int, byte[]> read, long trak, int header)
    {
        byte[] size = read(trak, 4);
        if (size.Length < 4) return null;
        long end = trak + BinaryPrimitives.ReadUInt32BigEndian(size);

        foreach (var (type, at, boxSize, h) in Boxes(read, trak + header, end))
        {
            if (type != "mdia") continue;
            foreach (var (inner, innerAt, _, innerHeader) in Boxes(read, at + h, at + boxSize))
            {
                if (inner != "hdlr") continue;
                byte[] body = read(innerAt + innerHeader, 12);
                return body.Length == 12 ? Encoding.ASCII.GetString(body, 8, 4) : null;
            }
        }
        return null;
    }

    /// <summary>זמן היצירה בגוף mvhd: שניות מ-1904, לפי UTC.</summary>
    private static DateTime? MvhdDate(byte[] body)
    {
        if (body.Length < 12) return null;
        ulong seconds = body[0] == 1
            ? BinaryPrimitives.ReadUInt64BigEndian(body.AsSpan(4))
            : BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(4));
        if (seconds == 0 || seconds > 10_000_000_000) return null;
        return new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds).ToLocalTime();
    }

    /// <summary>ASF: שמע בלבד (בלי זרם וידאו) הוא WMA.</summary>
    private static readonly byte[] AsfVideoMedia =
        { 0xC0, 0xEF, 0x19, 0xBC, 0x4D, 0x5B, 0xCF, 0x11, 0xA8, 0xFD, 0x00, 0x80, 0x5F, 0x5C, 0x44, 0x2B };

    private static CarvedInfo? Asf(byte[] header)
        => header.AsSpan().IndexOf(AsfVideoMedia) >= 0 ? null : new CarvedInfo(null, null, "wma", "שמע Windows Media");   // לא לתרגום: תווית, הממשק מתרגם

    /// <summary>
    /// DVD: חבילות של 2048 בתים, ובחבילה הראשונה מנת ניווט (00 00 01 BF) — זה VOB
    /// של תקליטור או של מקליט DVD, ולא סרט MPEG רגיל.
    /// </summary>
    private static CarvedInfo? Dvd(byte[] h)
        => h.Length >= 2052 && h.AsSpan(2048, 4).SequenceEqual(new byte[] { 0, 0, 1, 0xBA })
           && h.AsSpan(0, 2048).IndexOf(new byte[] { 0, 0, 1, 0xBF }) >= 0
            ? new CarvedInfo(null, null, "vob")
            : null;

    /// <summary>OGG: הדף הראשון מגלה אם זה קול של וואטסאפ (Opus), Vorbis או וידאו Theora.</summary>
    private static CarvedInfo? Ogg(byte[] h)
    {
        if (h.Length < 28) return null;
        var body = h.AsSpan(27 + h[26]);
        if (body.StartsWith("OpusHead"u8)) return new CarvedInfo(null, null, "opus");
        if (body.Length > 7 && body[0] == 0x80 && body[1..7].SequenceEqual("theora"u8))
            return new CarvedInfo(null, null, "ogv", "וידאו OGG");   // לא לתרגום: תווית, הממשק מתרגם
        return null;
    }

    // ================================================================ שמע

    /// <summary>תג ID3v2 בתחילת MP3: שם השיר (TIT2) והמבצע (TPE1).</summary>
    private static CarvedInfo? Song(byte[] h)
    {
        if (h.Length < 10 || !h.AsSpan(0, 3).SequenceEqual("ID3"u8)) return null;
        int version = h[3];
        int tagSize = (h[6] << 21) | (h[7] << 14) | (h[8] << 7) | h[9];
        int end = Math.Min(h.Length, 10 + tagSize);

        string? title = null, artist = null;
        int pos = 10;
        int idLength = version == 2 ? 3 : 4, headerLength = version == 2 ? 6 : 10;

        while (pos + headerLength <= end && h[pos] != 0)
        {
            string id = Encoding.ASCII.GetString(h, pos, idLength);
            int size = version switch
            {
                2 => (h[pos + 3] << 16) | (h[pos + 4] << 8) | h[pos + 5],
                4 => (h[pos + 4] << 21) | (h[pos + 5] << 14) | (h[pos + 6] << 7) | h[pos + 7],
                _ => (int)BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(pos + 4)),
            };
            if (size <= 0 || pos + headerLength + size > end) break;

            var body = h.AsSpan(pos + headerLength, size);
            if (id is "TIT2" or "TT2") title = Id3Text(body);
            else if (id is "TPE1" or "TP1") artist = Id3Text(body);

            pos += headerLength + size;
        }

        title = CleanName(title);
        artist = CleanName(artist);
        if (title is null) return null;
        return new CarvedInfo(CleanName(artist is null ? title : $"{artist} - {title}"), null);
    }

    /// <summary>
    /// טקסט במסגרת ID3. קידוד 0 הוא לפי התקן Latin-1 — אבל בשירים עבריים ישנים
    /// זה כמעט תמיד Windows-1255; כשכל הבתים העליונים הם אותיות עבריות, זה מה שנבחר.
    /// </summary>
    private static string? Id3Text(ReadOnlySpan<byte> body)
    {
        if (body.Length < 2) return null;
        var text = body[1..];
        string value = body[0] switch
        {
            // UTF-16 עם סימן סדר בתים בהתחלה.
            1 => text.Length >= 2 && text[0] == 0xFE && text[1] == 0xFF
                ? Encoding.BigEndianUnicode.GetString(text[2..])
                : Encoding.Unicode.GetString(text.Length >= 2 && text[0] == 0xFF && text[1] == 0xFE ? text[2..] : text),
            2 => Encoding.BigEndianUnicode.GetString(text),
            3 => Encoding.UTF8.GetString(text),
            _ => LooksHebrew(text) ? Encoding.GetEncoding(1255).GetString(text) : Encoding.Latin1.GetString(text),
        };
        return value.Split('\0')[0];
    }

    private static bool LooksHebrew(ReadOnlySpan<byte> text)
    {
        bool any = false;
        foreach (byte c in text)
        {
            if (c < 0x80) continue;
            if (c is < 0xE0 or > 0xFA) return false;
            any = true;
        }
        return any;
    }

    // ============================================================ PDF

    /// <summary>
    /// כותרת ותאריך של PDF: במילון המידע (Info) שהסיום של הקובץ מצביע עליו,
    /// ואם אין — במטא-דאטה XMP. נקראים רק ההתחלה והסוף, ואם צריך גם האובייקט
    /// עצמו לפי טבלת המיקומים.
    /// </summary>
    private static CarvedInfo? Pdf(long length, Func<long, int, byte[]> read)
    {
        const int Tail = 256 * 1024;
        byte[] head = read(0, 64 * 1024);
        long tailStart = Math.Max(0, length - Tail);
        byte[] tail = read(tailStart, (int)Math.Min(Tail, length));
        string h = Encoding.Latin1.GetString(head), t = Encoding.Latin1.GetString(tail);

        string? title = null;
        DateTime? date = null;

        var info = Regex.Matches(t, @"/Info\s+(\d+)\s+(\d+)\s+R").LastOrDefault()
                   ?? Regex.Matches(h, @"/Info\s+(\d+)\s+(\d+)\s+R").LastOrDefault();
        if (info is not null)
        {
            string obj = $@"(?<!\d){info.Groups[1].Value}\s+{info.Groups[2].Value}\s+obj";
            string? dict = null;
            if (Regex.Matches(t, obj).LastOrDefault() is { } inTail) dict = t[inTail.Index..Math.Min(t.Length, inTail.Index + 8192)];
            else if (Regex.Match(h, obj) is { Success: true } inHead) dict = h[inHead.Index..Math.Min(h.Length, inHead.Index + 8192)];
            else if (ObjectOffset(t, int.Parse(info.Groups[1].Value), read) is { } at)
                dict = Encoding.Latin1.GetString(read(at, 8192));

            if (dict is not null)
            {
                int end = dict.IndexOf("endobj", StringComparison.Ordinal);
                if (end > 0) dict = dict[..end];
                title = PdfString(dict, "/Title");
                date = PdfDate(PdfString(dict, "/CreationDate"));
            }
        }

        // XMP: בדרך כלל לא דחוס, ולכן אפשר לחפש בו טקסט.
        string both = h + t;
        title ??= XmlValue(Regex.Match(both, @"<dc:title>.*?<rdf:li[^>]*>(.*?)</rdf:li>", RegexOptions.Singleline), true);
        date ??= IsoDate(Regex.Match(both, @"<xmp:CreateDate>([^<]+)</xmp:CreateDate>").Groups[1].Value);

        title = CleanName(PdfTitleCleanup(title));
        date = Sane(date);
        return title is null && date is null ? null : new CarvedInfo(title ?? Stamp(date!.Value), date);
    }

    /// <summary>"Microsoft Word - דוח.docx" → "דוח": הכותרת שוורד נותן כשלא הוגדרה אחת.</summary>
    private static string? PdfTitleCleanup(string? title)
    {
        if (title is null) return null;
        title = Regex.Replace(title, @"^Microsoft (Word|PowerPoint|Excel) - ", "");
        return Regex.Replace(title, @"\.(docx?|pptx?|xlsx?|rtf|txt|odt)$", "", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// מיקום אובייקט לפי טבלת המיקומים הקלאסית (xref) — כשמילון המידע אינו
    /// בהתחלה ולא בסוף. טבלה דחוסה (PDF 1.5 ואילך) אינה נקראת כאן.
    /// </summary>
    private static long? ObjectOffset(string tail, int number, Func<long, int, byte[]> read)
    {
        var start = Regex.Matches(tail, @"startxref\s+(\d+)").LastOrDefault();
        if (start is null || !long.TryParse(start.Groups[1].Value, out long xref)) return null;

        string table = Encoding.Latin1.GetString(read(xref, 64 * 1024));
        if (!table.StartsWith("xref", StringComparison.Ordinal)) return null;

        int pos = 4;
        for (int guard = 0; guard < 1000; guard++)
        {
            var section = Regex.Match(table[pos..], @"^\s*(\d+)\s+(\d+)\s*\r?\n");
            if (!section.Success) return null;
            int first = int.Parse(section.Groups[1].Value), count = int.Parse(section.Groups[2].Value);
            pos += section.Length;

            if (number >= first && number < first + count)
            {
                long entry = xref + pos + (long)(number - first) * 20;
                string line = Encoding.Latin1.GetString(read(entry, 20));
                return long.TryParse(line[..Math.Min(10, line.Length)], out long at) ? at : null;
            }
            pos += count * 20;
            if (pos >= table.Length) return null;
        }
        return null;
    }

    /// <summary>ערך מחרוזת במילון PDF: (טקסט) עם בריחות, או &lt;הקס&gt;.</summary>
    private static string? PdfString(string dict, string key)
    {
        int at = dict.IndexOf(key, StringComparison.Ordinal);
        if (at < 0) return null;
        at += key.Length;
        while (at < dict.Length && char.IsWhiteSpace(dict[at])) at++;
        if (at >= dict.Length) return null;

        var bytes = new List<byte>();
        if (dict[at] == '(')
        {
            int depth = 1;
            for (int i = at + 1; i < dict.Length && depth > 0; i++)
            {
                char c = dict[i];
                if (c == '\\' && i + 1 < dict.Length)
                {
                    char n = dict[++i];
                    if (n is >= '0' and <= '7')
                    {
                        int value = 0, digits = 0;
                        for (; digits < 3 && i < dict.Length && dict[i] is >= '0' and <= '7'; digits++, i++)
                            value = value * 8 + (dict[i] - '0');
                        i--;
                        bytes.Add((byte)value);
                    }
                    else bytes.Add((byte)(n switch { 'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'f' => '\f', _ => n }));
                    continue;
                }
                if (c == '(') depth++;
                if (c == ')' && --depth == 0) break;
                bytes.Add((byte)c);
            }
        }
        else if (dict[at] == '<' && at + 1 < dict.Length && dict[at + 1] != '<')
        {
            int end = dict.IndexOf('>', at);
            if (end < 0) return null;
            string hex = Regex.Replace(dict[(at + 1)..end], @"\s", "");
            if (hex.Length % 2 == 1) hex += "0";
            for (int i = 0; i + 1 < hex.Length; i += 2)
                bytes.Add(byte.Parse(hex.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }
        else return null;

        byte[] raw = bytes.ToArray();
        if (raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(raw, 2, raw.Length - 2);
        if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF) return Encoding.UTF8.GetString(raw, 3, raw.Length - 3);
        return Encoding.Latin1.GetString(raw);
    }

    /// <summary>תאריך PDF: D:YYYYMMDDHHmmSS, והשאר (אזור זמן) אופציונלי.</summary>
    private static DateTime? PdfDate(string? text)
    {
        var m = Regex.Match(text ?? "", @"^(?:D:)?(\d{4})(\d{2})?(\d{2})?(\d{2})?(\d{2})?(\d{2})?");
        if (!m.Success) return null;
        int Part(int g, int fallback) => m.Groups[g].Success ? int.Parse(m.Groups[g].Value) : fallback;
        try { return new DateTime(Part(1, 1), Part(2, 1), Part(3, 1), Part(4, 0), Part(5, 0), Part(6, 0)); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static DateTime? IsoDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d))
            return d.LocalDateTime;
        return null;
    }

    private static string? XmlValue(Match m, bool decode)
    {
        if (!m.Success) return null;
        string v = m.Groups[1].Value.Trim();
        return decode ? System.Net.WebUtility.HtmlDecode(v) : v;
    }

    // ============================================================ ZIP / Office

    /// <summary>
    /// ארכיון ZIP: הספרייה המרכזית בסוף הקובץ מגלה מה הוא באמת — מסמך Word,
    /// גיליון Excel, מצגת, ספר אלקטרוני או אפליקציה — ומשם נקרא גם החלק
    /// שבו שמורים הכותרת והתאריך (docProps/core.xml, או meta.xml ב-ODF).
    /// </summary>
    private static CarvedInfo? Zip(long length, Func<long, int, byte[]> read)
    {
        int tailSize = (int)Math.Min(length, 66 * 1024);
        byte[] tail = read(length - tailSize, tailSize);
        int eocd = tail.AsSpan().LastIndexOf("PK\x05\x06"u8);
        if (eocd < 0 || eocd + 22 > tail.Length) return null;

        long cdSize = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 12));
        long cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 16));
        if (cdOffset + cdSize > length || cdSize > 8 * 1024 * 1024) return null;
        byte[] cd = read(cdOffset, (int)cdSize);

        var entries = new Dictionary<string, (int Method, long Compressed, long Local)>(StringComparer.OrdinalIgnoreCase);
        for (int at = 0; at + 46 <= cd.Length && cd.AsSpan(at, 4).SequenceEqual("PK\x01\x02"u8); )
        {
            int method = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(at + 10));
            long compressed = BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(at + 20));
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(at + 28));
            int extra = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(at + 30));
            int comment = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(at + 32));
            long local = BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(at + 42));
            if (at + 46 + nameLength > cd.Length) break;
            entries.TryAdd(Encoding.UTF8.GetString(cd, at + 46, nameLength), (method, compressed, local));
            at += 46 + nameLength + extra + comment;
        }
        if (entries.Count == 0) return null;

        byte[]? Part(string name)
        {
            if (!entries.TryGetValue(name, out var e) || e.Compressed > 4 * 1024 * 1024) return null;
            byte[] header = read(e.Local, 30);
            if (header.Length < 30 || !header.AsSpan(0, 4).SequenceEqual("PK\x03\x04"u8)) return null;
            long data = e.Local + 30 + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26))
                                    + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));
            byte[] packed = read(data, (int)e.Compressed);
            if (e.Method == 0) return packed;
            if (e.Method != 8) return null;
            using var inflate = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress);
            using var output = new MemoryStream();
            inflate.CopyTo(output);
            return output.ToArray();
        }

        bool Has(string name) => entries.ContainsKey(name);
        string? mimetype = Has("mimetype") && Part("mimetype") is { } m ? Encoding.ASCII.GetString(m).Trim() : null;

        (string Ext, string Folder)? kind =
            Has("word/document.xml") ? (Has("word/vbaProject.bin") ? "docm" : "docx", "מסמך Word")   // לא לתרגום: תווית, הממשק מתרגם
            : Has("xl/workbook.xml") ? (Has("xl/vbaProject.bin") ? "xlsm" : "xlsx", "גיליון Excel")   // לא לתרגום: תווית, הממשק מתרגם
            : Has("ppt/presentation.xml") ? (Has("ppt/vbaProject.bin") ? "pptm" : "pptx", "מצגת PowerPoint")   // לא לתרגום: תווית, הממשק מתרגם
            : Has("visio/document.xml") ? ("vsdx", "שרטוט Visio")   // לא לתרגום: תווית, הממשק מתרגם
            : mimetype == "application/vnd.oasis.opendocument.text" ? ("odt", "מסמך OpenDocument")   // לא לתרגום: תווית, הממשק מתרגם
            : mimetype == "application/vnd.oasis.opendocument.spreadsheet" ? ("ods", "גיליון OpenDocument")   // לא לתרגום: תווית, הממשק מתרגם
            : mimetype == "application/vnd.oasis.opendocument.presentation" ? ("odp", "מצגת OpenDocument")   // לא לתרגום: תווית, הממשק מתרגם
            : mimetype == "application/epub+zip" ? ("epub", "ספר אלקטרוני")   // לא לתרגום: תווית, הממשק מתרגם
            : Has("AndroidManifest.xml") && Has("classes.dex") ? ("apk", "אפליקציית אנדרואיד")   // לא לתרגום: תווית, הממשק מתרגם
            : Has("META-INF/MANIFEST.MF") ? ("jar", "ארכיון Java")   // לא לתרגום: תווית, הממשק מתרגם
            : null;

        string? title = null;
        DateTime? date = null;
        string? xml = Part("docProps/core.xml") is { } core ? Encoding.UTF8.GetString(core)
                      : Part("meta.xml") is { } meta ? Encoding.UTF8.GetString(meta)
                      : null;
        if (xml is not null)
        {
            title = XmlValue(Regex.Match(xml, @"<dc:title>([^<]*)</dc:title>"), true);
            date = IsoDate(Regex.Match(xml, @"<dcterms:modified[^>]*>([^<]+)<").Groups[1].Value)
                   ?? IsoDate(Regex.Match(xml, @"<dcterms:created[^>]*>([^<]+)<").Groups[1].Value)
                   ?? IsoDate(Regex.Match(xml, @"<(?:dc:date|meta:creation-date)>([^<]+)<").Groups[1].Value);
        }

        // בלי כותרת: השורה הראשונה במסמך — כמו השם שוורד עצמו מציע בשמירה —
        // או הכותרת של השקופית הראשונה.
        title = CleanName(title)
                ?? CleanName(FirstLine(Part("word/document.xml"), "w:p", "w:t"))
                ?? CleanName(FirstLine(Part("ppt/slides/slide1.xml"), "a:p", "a:t"));
        date = Sane(date);
        if (kind is null && title is null && date is null) return null;
        return new CarvedInfo(title ?? (date is { } d ? Stamp(d) : null), date, kind?.Ext, kind?.Folder);
    }

    /// <summary>
    /// הפסקה הראשונה שיש בה טקסט, מתוך XML של Office: חיבור של כל קטעי הטקסט
    /// (w:t בוורד, a:t בפאוורפוינט) בתוכה. עד 80 תווים — זה שם, לא תקציר.
    /// </summary>
    private static string? FirstLine(byte[]? xml, string paragraph, string text)
    {
        if (xml is null) return null;
        string doc = Encoding.UTF8.GetString(xml);
        foreach (Match p in Regex.Matches(doc, $@"<{paragraph}[ >].*?</{paragraph}>", RegexOptions.Singleline))
        {
            var line = new StringBuilder();
            foreach (Match t in Regex.Matches(p.Value, $@"<{text}(?: [^>]*)?>([^<]*)</{text}>"))
                line.Append(t.Groups[1].Value);
            string value = System.Net.WebUtility.HtmlDecode(line.ToString()).Trim();
            if (value.Length >= 2) return value.Length > 80 ? value[..80] : value;
        }
        return null;
    }

    // ============================================================ Office ישן

    /// <summary>
    /// מסמך Office ישן (OLE): שמות הזרמים בספרייה הראשית מגלים את הסוג —
    /// WordDocument, Workbook, "PowerPoint Document", או הודעת Outlook.
    /// </summary>
    private static CarvedInfo? Ole(Func<long, int, byte[]> read)
    {
        byte[] header = read(0, 512);
        if (header.Length < 512) return null;
        int shift = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x1E));
        if (shift is not (9 or 12)) return null;
        int sector = 1 << shift;
        long first = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x30));
        if (first >= 0xFFFFFFFA) return null;

        byte[] directory = read((first + 1) * sector, sector);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int at = 0; at + 128 <= directory.Length; at += 128)
        {
            int length = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(at + 64));
            if (length is < 2 or > 64) continue;
            names.Add(Encoding.Unicode.GetString(directory, at, length - 2));
        }

        (string Ext, string Folder)? kind =
            names.Contains("WordDocument") ? ("doc", "מסמך Word ישן")   // לא לתרגום: תווית, הממשק מתרגם
            : names.Contains("Workbook") || names.Contains("Book") ? ("xls", "גיליון Excel ישן")   // לא לתרגום: תווית, הממשק מתרגם
            : names.Contains("PowerPoint Document") ? ("ppt", "מצגת PowerPoint ישנה")   // לא לתרגום: תווית, הממשק מתרגם
            : names.Any(n => n.StartsWith("__substg1.0_", StringComparison.Ordinal)) ? ("msg", "הודעת Outlook")   // לא לתרגום: תווית, הממשק מתרגם
            : null;

        return kind is null ? null : new CarvedInfo(null, null, kind.Value.Ext, kind.Value.Folder);
    }
}
