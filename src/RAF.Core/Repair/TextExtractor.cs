using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace RAF.Core.Repair;

/// <summary>
/// חילוץ הטקסט ממסמך פגום — מוצא אחרון כשהמסמך עצמו אינו נפתח ואינו ניתן לתיקון.
/// העיצוב, התמונות והטבלאות אובדים; המילים — מה שאנשים צריכים באמת — נשמרות.
///
/// כל מחלץ עובד על קובץ שבור: ארכיון Office בלי תוכן עניינים (לפי הכותרות המקומיות),
/// XML קטוע (עד המקום שבו נקטע), זרם PDF דחוס שנקטע (עד המקום שבו הפענוח נכשל).
/// </summary>
internal static class TextExtractor
{
    static TextExtractor() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>
    /// הטקסט של המסמך, או null אם אין בו טקסט של ממש. <paramref name="kind"/> הוא
    /// הסוג שזוהה (docx, pdf, doc…) — לפי התוכן או לפי הסיומת.
    /// </summary>
    internal static string? Extract(byte[] data, string kind)
    {
        string? text;
        try
        {
            text = kind switch
            {
                "pdf" => Pdf(data),
                "doc" => LegacyWord(data),
                _ => Office(data),
            };
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or IndexOutOfRangeException
                                      or FormatException or OverflowException or DecoderFallbackException)
        {
            return null;
        }

        if (text is null) return null;
        text = Regex.Replace(text, @"[ \t]+\n", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
        return Words(text) >= 5 && LooksLikeText(text) ? text : null;
    }

    /// <summary>
    /// האם זה טקסט ולא ג'יבריש. PDF עם גופנים בקידוד פרטי (בלי טבלת ToUnicode) —
    /// נפוץ בעיתונים ובספרים ישנים — מחזיר רצפי סמלים שנראים כמו טקסט. מילה
    /// "סבירה" היא עברית, אנגלית שיש בה תנועה, או מספר; נדרשות לפחות 70% כאלה.
    /// </summary>
    internal static bool LooksLikeText(string text)
    {
        int words = 0, plausible = 0;
        foreach (string raw in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            string w = raw.Trim(".,;:!?\"'()[]-–—״׳“”‘’".ToCharArray());   // לא לתרגום
            if (w.Length == 0) continue;
            if (++words > 5000) break;

            // עברית: אותיות, ניקוד וטעמים, וגרשיים (צה"ל, ר').
            bool hebrew = w.All(c => c is (>= 'א' and <= 'ת') or (>= '֑' and <= 'ׇ')
                                     or '"' or '\'' or '״' or '׳' or '-');
            bool latin = w.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '\'' or '-')
                         && (w.Length <= 3 || w.Any(c => "aeiouyAEIOUY".Contains(c))) && w.Length <= 25;
            bool number = w.All(c => char.IsDigit(c) || c is '.' or ',' or '/' or ':' or '%' or '-');
            bool other = w.All(char.IsLetter) && !w.Any(c => c < 'Ā');     // סינית, ערבית, רוסית…
            if (hebrew || latin || number || other) plausible++;
        }
        return words > 0 && plausible * 10 >= words * 7;
    }

    /// <summary>מספר המילים — להסבר למשתמש כמה טקסט נמצא.</summary>
    internal static int Words(string text)
        => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Count(w => w.Any(char.IsLetterOrDigit));

    /// <summary>הסוגים שיש בהם טקסט לחלץ.</summary>
    internal static string? KindOf(string extension) => extension.ToLowerInvariant() switch
    {
        "pdf" => "pdf",
        "doc" or "dot" => "doc",
        "docx" or "docm" or "dotx" or "dotm" or "pptx" or "pptm" or "ppsx" or "xlsx" or "xlsm"
            or "odt" or "ods" or "odp" => "office",
        _ => null,
    };

    // ============================================================ Office (ZIP)

    /// <summary>
    /// מסמך Office חדש או OpenDocument: הקבצים הפנימיים נמצאים לפי הכותרות
    /// המקומיות (ולא לפי תוכן העניינים שבסוף, שאולי אבד), ונפרסים ככל שאפשר.
    /// </summary>
    private static string? Office(byte[] data)
    {
        var parts = ZipRebuilder.Analyze(data).Entries
            .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.Intact).First(), StringComparer.OrdinalIgnoreCase);

        string? Xml(string name) => parts.TryGetValue(name, out var e) ? Inflate(data, e) : null;

        if (Xml("word/document.xml") is { } word)
            return Paragraphs(word, "w:p", @"<w:t(?:\s[^>]*)?>([^<]*)</w:t>|<w:(tab|br|cr)\b[^>]*/>");

        var slides = parts.Keys
            .Select(n => Regex.Match(n, @"^ppt/slides/slide(\d+)\.xml$", RegexOptions.IgnoreCase))
            .Where(m => m.Success).OrderBy(m => int.Parse(m.Groups[1].Value)).ToList();
        if (slides.Count > 0)
            return string.Join("\n\n", slides.Select((m, i) =>
                L.T("— שקופית {0} —\n", i + 1) + Paragraphs(Xml(m.Value) ?? "", "a:p", @"<a:t>([^<]*)</a:t>|<a:(br)\b[^>]*/>")));

        if (Xml("xl/sharedStrings.xml") is { } cells)
            return Paragraphs(cells, "si", @"<t(?:\s[^>]*)?>([^<]*)</t>");

        if (Xml("content.xml") is { } odf)
            return Paragraphs(odf, "text:(?:p|h)", @"(?<=>)([^<]+)(?=<)|<text:(tab|line-break)\b[^>]*/>");

        return null;
    }

    /// <summary>
    /// פריסת קובץ פנימי. גם כשהנתונים הדחוסים שבורים באמצע — מה שנפרס עד שם נשמר,
    /// וזה בדיוק המקרה של מסמך שנקטע.
    /// </summary>
    private static string? Inflate(byte[] data, ZipRebuilder.Entry e)
    {
        long end = e.Intact && e.CompressedSize > 0 ? Math.Min(data.Length, e.DataOffset + e.CompressedSize) : data.Length;
        if (e.DataOffset >= end) return null;
        var packed = new MemoryStream(data, (int)e.DataOffset, (int)(end - e.DataOffset));

        if (e.Method == 0) return Encoding.UTF8.GetString(packed.ToArray());
        if (e.Method != 8) return null;

        using var inflate = new DeflateStream(packed, CompressionMode.Decompress);
        return Encoding.UTF8.GetString(ReadPartial(inflate));
    }

    /// <summary>קריאה עד הסוף — או עד המקום שבו הנתונים נשברים.</summary>
    private static byte[] ReadPartial(Stream s)
    {
        var output = new MemoryStream();
        byte[] buffer = new byte[64 * 1024];
        try
        {
            int read;
            while ((read = s.Read(buffer, 0, buffer.Length)) > 0) output.Write(buffer, 0, read);
        }
        catch (InvalidDataException) { }
        return output.ToArray();
    }

    /// <summary>
    /// פסקאות מתוך XML: כל פסקה בשורה, והטקסט שבה מחובר מקטעי הטקסט. בכל
    /// ביטוי, קבוצה 1 היא טקסט וקבוצה 2 היא סימן (טאב או ירידת שורה).
    /// XML שנקטע באמצע הפסקה האחרונה — גם החלק שלה נשמר.
    /// </summary>
    private static string Paragraphs(string xml, string paragraph, string pieces)
    {
        var text = new StringBuilder();
        int last = 0;
        foreach (Match p in Regex.Matches(xml, $@"<{paragraph}(?:\s[^>]*)?>(.*?)</{paragraph}>|<{paragraph}(?:\s[^>]*)?/>",
                     RegexOptions.Singleline))
        {
            AppendPieces(text, p.Groups[1].Value, pieces);
            text.Append('\n');
            last = p.Index + p.Length;
        }
        AppendPieces(text, xml[last..], pieces);                              // הפסקה שנקטעה
        return text.ToString();
    }

    private static void AppendPieces(StringBuilder text, string xml, string pieces)
    {
        foreach (Match m in Regex.Matches(xml, pieces))
        {
            if (m.Groups[1].Success) text.Append(WebUtility.HtmlDecode(m.Groups[1].Value));
            else text.Append(m.Groups[2].Value.StartsWith("tab", StringComparison.Ordinal) ? '\t' : '\n');
        }
    }

    // ============================================================ Word ישן

    /// <summary>
    /// Word 97–2003. המבנה הפנימי (OLE ו"טבלת החלקים") נשבר בדיוק כשצריך את
    /// המחלץ הזה, ולכן הוא אינו נשען עליו: הוא מחפש רצפים ארוכים של טקסט —
    /// UTF-16 (שבו נשמר כל מסמך עם עברית), ו-ANSI למסמכים באנגלית בלבד — לפי
    /// סדרם בקובץ. שמות גופנים וסגנונות קצרים מדי ונופלים בסינון.
    /// </summary>
    private static string? LegacyWord(byte[] data)
    {
        var runs = new List<(int At, string Text)>();

        // UTF-16LE: אותיות (עברית ולטינית), ספרות, פיסוק וסוף פסקה.
        static bool Wide(char c) => c is >= ' ' and <= '~' or '\r' or '\t' or '–' or '—' or '‘' or '’'
                                    or '“' or '”' or 'ְ' or (>= 'א' and <= 'ת') or '׳' or '״';
        for (int even = 0; even < 2; even++)
        {
            int start = -1;
            var sb = new StringBuilder();
            for (int i = even; i + 1 < data.Length; i += 2)
            {
                char c = (char)(data[i] | (data[i + 1] << 8));
                if (Wide(c))
                {
                    if (start < 0) start = i;
                    sb.Append(c);
                    continue;
                }
                Flush(runs, start, sb, wide: true);
                start = -1;
            }
            Flush(runs, start, sb, wide: true);
        }

        // ANSI: מסמך באנגלית בלבד נשמר בבית אחד לתו.
        {
            int start = -1;
            var sb = new StringBuilder();
            for (int i = 0; i < data.Length; i++)
            {
                byte b = data[i];
                if (b is >= 0x20 and <= 0x7E or 0x0D or 0x09 or 0x92 or 0x93 or 0x94 or 0x96)
                {
                    if (start < 0) start = i;
                    sb.Append(b switch { 0x92 => '’', 0x93 => '“', 0x94 => '”', 0x96 => '–', _ => (char)b });
                    continue;
                }
                Flush(runs, start, sb, wide: false);
                start = -1;
            }
            Flush(runs, start, sb, wide: false);
        }

        if (runs.Count == 0) return null;
        return string.Join("\n", runs.OrderBy(r => r.At).Select(r => r.Text.Replace('\r', '\n')));
    }

    private static void Flush(List<(int, string)> runs, int start, StringBuilder sb, bool wide)
    {
        string s = sb.ToString();
        sb.Clear();
        if (start < 0) return;

        int spaces = s.Count(c => c == ' ');
        bool hebrew = s.Count(c => c is >= 'א' and <= 'ת') >= 8;
        // בלי עברית נדרש משפט של ממש — "Times New Roman" ושמות סגנונות אינם טקסט.
        bool keep = wide ? (hebrew ? s.Length >= 12 : s.Length >= 20 && spaces >= 3) : s.Length >= 40 && spaces >= 4;
        if (keep && s.Count(char.IsLetter) * 2 >= s.Length) runs.Add((start, s));
    }

    // ============================================================ PDF

    private sealed record PdfObject(string Dict, int StreamStart, int StreamEnd);

    /// <summary>
    /// PDF: הטקסט של כל עמוד, לפי סדר העמודים. זרמי התוכן נפרסים, והתווים
    /// מתורגמים לפי טבלת ToUnicode של כל גופן — כך נשמרת עברית, שנכתבת בגופנים
    /// מוטמעים עם מספרי תווים משלהם.
    /// </summary>
    private static string? Pdf(byte[] data)
    {
        string text = Encoding.Latin1.GetString(data);
        var objects = new Dictionary<int, PdfObject>();

        foreach (Match m in Regex.Matches(text, @"(?<![\d])(\d{1,7})\s+\d{1,5}\s+obj\b"))
        {
            int number = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            int body = m.Index + m.Length;
            int endobj = text.IndexOf("endobj", body, StringComparison.Ordinal);
            int stream = text.IndexOf("stream", body, StringComparison.Ordinal);
            bool hasStream = stream >= 0 && (endobj < 0 || stream < endobj) && !IsEndStream(text, stream);

            if (!hasStream)
            {
                objects[number] = new PdfObject(text[body..(endobj < 0 ? Math.Min(text.Length, body + 4096) : endobj)], -1, -1);
                continue;
            }

            int start = stream + 6;
            if (start < text.Length && text[start] == '\r') start++;
            if (start < text.Length && text[start] == '\n') start++;
            int end = text.IndexOf("endstream", start, StringComparison.Ordinal);
            objects[number] = new PdfObject(text[body..stream], start, end < 0 ? text.Length : end);  // זרם קטוע — עד סוף הקובץ
        }

        // אובייקטים דחוסים בתוך זרמי אובייקטים (PDF 1.5 ואילך) — שם יושבים לרוב העמודים והגופנים.
        foreach (var (_, o) in objects.ToList())
        {
            if (o.StreamStart < 0 || !Regex.IsMatch(o.Dict, @"/Type\s*/ObjStm\b")) continue;
            string inner = Encoding.Latin1.GetString(Decode(data, o));
            int first = Int(o.Dict, "/First") ?? 0;
            var header = Regex.Matches(inner.Length >= first ? inner[..first] : inner, @"(\d+)\s+(\d+)");
            for (int i = 0; i < header.Count; i++)
            {
                int number = int.Parse(header[i].Groups[1].Value, CultureInfo.InvariantCulture);
                int from = first + int.Parse(header[i].Groups[2].Value, CultureInfo.InvariantCulture);
                int to = i + 1 < header.Count ? first + int.Parse(header[i + 1].Groups[2].Value, CultureInfo.InvariantCulture) : inner.Length;
                if (from < inner.Length && to <= inner.Length && from < to)
                    objects.TryAdd(number, new PdfObject(inner[from..to], -1, -1));
            }
        }

        var cmaps = new Dictionary<int, CMap?>();
        CMap? FontMap(int font)
        {
            if (cmaps.TryGetValue(font, out var map)) return map;
            map = objects.TryGetValue(font, out var f) && Ref(f.Dict, "/ToUnicode") is { } tu
                  && objects.TryGetValue(tu, out var cm) && cm.StreamStart >= 0
                ? CMap.Parse(Encoding.Latin1.GetString(Decode(data, cm)))
                : null;
            return cmaps[font] = map;
        }

        var output = new StringBuilder();
        foreach (int page in Pages(objects))
        {
            var fonts = PageFonts(objects, page);
            foreach (int content in Contents(objects[page].Dict))
            {
                if (!objects.TryGetValue(content, out var c) || c.StreamStart < 0) continue;
                string ops = Encoding.Latin1.GetString(Decode(data, c));
                ShowText(ops, name => fonts.TryGetValue(name, out int f) ? FontMap(f) : null, output);
            }
            output.Append("\n\n");
        }

        // המסמך נקטע: ב-PDF של Word, רשימת העמודים והגופנים נכתבות בסוף, ואחרי שהן
        // אבדו אין "עמודים". זרמי התוכן עצמם — בתחילת הקובץ — עדיין שם, ונקראים לפי הסדר.
        if (Words(output.ToString()) < 5)
        {
            var allFonts = new Dictionary<string, int>();
            foreach (var o in objects.Values)
                foreach (Match f in Regex.Matches(Regex.Match(o.Dict, @"/Font\s*<<(.*?)>>", RegexOptions.Singleline).Groups[1].Value,
                             @"/([^\s/<>\[\]()]+)\s+(\d+)\s+\d+\s+R"))
                    allFonts.TryAdd(f.Groups[1].Value, int.Parse(f.Groups[2].Value, CultureInfo.InvariantCulture));

            foreach (var (_, o) in objects.OrderBy(p => p.Key))
            {
                if (o.StreamStart < 0 || Regex.IsMatch(o.Dict, @"/Subtype\s*/Image|/Type\s*/(ObjStm|XRef)|/Length1")) continue;
                string ops = Encoding.Latin1.GetString(Decode(data, o));
                if (!ops.Contains("BT", StringComparison.Ordinal) || !ops.Contains("Tf", StringComparison.Ordinal)) continue;
                ShowText(ops, name => allFonts.TryGetValue(name, out int f) ? FontMap(f) : null, output);
                output.Append("\n\n");
            }
        }

        return FixVisualOrder(FixMisreadHebrew(output.ToString()));
    }

    private static bool IsEndStream(string text, int at) => at >= 3 && string.CompareOrdinal(text, at - 3, "end", 0, 3) == 0;

    /// <summary>פריסת זרם: FlateDecode, או כמו שהוא. זרם שנקטע נפרס עד המקום שבו נשבר.</summary>
    private static byte[] Decode(byte[] data, PdfObject o)
    {
        int length = Math.Max(0, Math.Min(o.StreamEnd, data.Length) - o.StreamStart);
        var raw = new MemoryStream(data, o.StreamStart, length);
        if (!Regex.IsMatch(o.Dict, @"/FlateDecode\b")) return raw.ToArray();
        using var z = new ZLibStream(raw, CompressionMode.Decompress);
        return ReadPartial(z);
    }

    private static int? Int(string dict, string key)
        => Regex.Match(dict, Regex.Escape(key) + @"\s+(\d+)") is { Success: true } m
            ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;

    private static int? Ref(string dict, string key)
        => Regex.Match(dict, Regex.Escape(key) + @"\s+(\d+)\s+\d+\s+R") is { Success: true } m
            ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;

    /// <summary>
    /// העמודים לפי הסדר: מעבר על עץ העמודים משורשו. עץ שבור — כל עמוד שנמצא,
    /// לפי מספר האובייקט (שבדרך כלל תואם לסדר).
    /// </summary>
    private static List<int> Pages(Dictionary<int, PdfObject> objects)
    {
        bool IsPage(PdfObject o) => Regex.IsMatch(o.Dict, @"/Type\s*/Page(?![s\w])");
        var result = new List<int>();
        var seen = new HashSet<int>();

        void Walk(int node, int depth)
        {
            if (depth > 32 || !seen.Add(node) || !objects.TryGetValue(node, out var o)) return;
            if (IsPage(o)) { result.Add(node); return; }
            var kids = Regex.Match(o.Dict, @"/Kids\s*\[([^\]]*)\]");
            foreach (Match k in Regex.Matches(kids.Groups[1].Value, @"(\d+)\s+\d+\s+R"))
                Walk(int.Parse(k.Groups[1].Value, CultureInfo.InvariantCulture), depth + 1);
        }

        foreach (var (number, o) in objects.OrderBy(p => p.Key))
            if (Regex.IsMatch(o.Dict, @"/Type\s*/Pages\b") && !o.Dict.Contains("/Parent", StringComparison.Ordinal))
                Walk(number, 0);

        foreach (var (number, o) in objects.OrderBy(p => p.Key))
            if (IsPage(o) && !seen.Contains(number)) result.Add(number);
        return result;
    }

    private static IEnumerable<int> Contents(string page)
    {
        var list = Regex.Match(page, @"/Contents\s*\[([^\]]*)\]");
        string refs = list.Success ? list.Groups[1].Value : Regex.Match(page, @"/Contents\s+\d+\s+\d+\s+R").Value;
        foreach (Match m in Regex.Matches(refs, @"(\d+)\s+\d+\s+R"))
            yield return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// שמות הגופנים בעמוד (/F1 → אובייקט הגופן): במשאבי העמוד, ישירים או בהפניה,
    /// ואם אין — במשאבים של הורה בעץ העמודים.
    /// </summary>
    private static Dictionary<string, int> PageFonts(Dictionary<int, PdfObject> objects, int page)
    {
        var fonts = new Dictionary<string, int>();
        for (int node = page, depth = 0; depth < 32 && objects.TryGetValue(node, out var o); depth++)
        {
            string? resources = Ref(o.Dict, "/Resources") is { } r && objects.TryGetValue(r, out var ro) ? ro.Dict : o.Dict;
            string? fontDict = Ref(resources, "/Font") is { } f && objects.TryGetValue(f, out var fo)
                ? fo.Dict
                : Regex.Match(resources, @"/Font\s*<<(.*?)>>", RegexOptions.Singleline).Groups[1].Value;

            foreach (Match m in Regex.Matches(fontDict, @"/([^\s/<>\[\]()]+)\s+(\d+)\s+\d+\s+R"))
                fonts.TryAdd(m.Groups[1].Value, int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));

            if (fonts.Count > 0 || Ref(o.Dict, "/Parent") is not { } parent) break;
            node = parent;
        }
        return fonts;
    }

    /// <summary>
    /// מעבר על פקודות התוכן: Tf בוחר גופן, Tj/TJ/'/" מציגים טקסט, ומעבר שורה
    /// (T*, Td עם תזוזה אנכית, Tm חדשה) — שורה חדשה בפלט.
    /// </summary>
    private static void ShowText(string ops, Func<string, CMap?> font, StringBuilder output)
    {
        CMap? current = null;
        var operands = new List<object>();
        double lastY = double.NaN;
        int i = 0;

        void Show(object operand)
        {
            if (operand is byte[] bytes) output.Append(current is null ? SingleByte(bytes) : current.Decode(bytes));
            else if (operand is List<object> array)
                foreach (var item in array)
                {
                    if (item is byte[] b) Show(b);
                    else if (item is double gap && gap < -250) output.Append(' ');   // רווח בין מילים בתוך TJ
                }
        }

        void NewLine() { if (output.Length > 0 && output[^1] != '\n') output.Append('\n'); }

        while (i < ops.Length)
        {
            char c = ops[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '%') { while (i < ops.Length && ops[i] != '\n' && ops[i] != '\r') i++; continue; }

            if (c == '(') { operands.Add(LiteralString(ops, ref i)); continue; }
            if (c == '<' && i + 1 < ops.Length && ops[i + 1] != '<') { operands.Add(HexString(ops, ref i)); continue; }
            if (c == '[')
            {
                i++;
                var array = new List<object>();
                while (i < ops.Length && ops[i] != ']')
                {
                    char a = ops[i];
                    if (a == '(') array.Add(LiteralString(ops, ref i));
                    else if (a == '<') array.Add(HexString(ops, ref i));
                    else if (a is '-' or '+' or '.' || char.IsDigit(a))
                    {
                        int s = i++;
                        while (i < ops.Length && (char.IsDigit(ops[i]) || ops[i] == '.')) i++;
                        if (double.TryParse(ops.AsSpan(s, i - s), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                            array.Add(v);
                    }
                    else i++;
                }
                i++;
                operands.Add(array);
                continue;
            }
            if (c == '/')
            {
                int s = ++i;
                while (i < ops.Length && !char.IsWhiteSpace(ops[i]) && "/[]()<>".IndexOf(ops[i]) < 0) i++;
                operands.Add("/" + ops[s..i]);
                continue;
            }
            if (c is '-' or '+' or '.' || char.IsDigit(c))
            {
                int s = i++;
                while (i < ops.Length && (char.IsDigit(ops[i]) || ops[i] == '.')) i++;
                operands.Add(double.TryParse(ops.AsSpan(s, i - s), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0.0);
                continue;
            }
            if (c is '>' or ']' or '{' or '}') { i++; continue; }
            if (c == '<') { i += 2; continue; }                                  // מילון מוטבע

            int op = i;
            while (i < ops.Length && !char.IsWhiteSpace(ops[i]) && "/[]()<>%".IndexOf(ops[i]) < 0) i++;
            if (i == op) { i++; continue; }

            switch (ops[op..i])
            {
                case "Tf" when operands.Count >= 2 && operands[^2] is string name: current = font(name[1..]); break;
                case "Tj" when operands.Count >= 1: Show(operands[^1]); break;
                case "TJ" when operands.Count >= 1: Show(operands[^1]); break;
                case "'" or "\"" when operands.Count >= 1: NewLine(); Show(operands[^1]); break;
                case "T*": NewLine(); break;
                case "Td" or "TD" when operands.Count >= 2 && operands[^1] is double dy:
                    if (Math.Abs(dy) > 0.1) NewLine();
                    else if (operands[^2] is double dx && dx > 1) output.Append(' ');
                    break;
                case "Tm" when operands.Count >= 6 && operands[^1] is double y:
                    if (!double.IsNaN(lastY) && Math.Abs(y - lastY) > 0.1) NewLine();
                    lastY = y;
                    break;
                case "ET": output.Append(' '); break;
            }
            operands.Clear();
        }
    }

    /// <summary>
    /// גופן בלי טבלת ToUnicode: בית לכל תו. בקבצים עבריים ישנים אלה כמעט תמיד
    /// אותיות ב-Windows-1255 (0xE0 ואילך) — ב-1252 הן היו יוצאות "ïéðòá".
    /// </summary>
    private static string SingleByte(byte[] bytes)
    {
        bool hebrew = false, other = false;
        foreach (byte b in bytes)
        {
            if (b is >= 0xE0 and <= 0xFA) hebrew = true;
            else if (b >= 0x80 && b is not (>= 0xC0 and <= 0xD8)) other = true;       // 0xC0–0xD8: ניקוד
        }
        return Encoding.GetEncoding(hebrew && !other ? 1255 : 1252).GetString(bytes);
    }

    /// <summary>
    /// גופנים עבריים ישנים מגיעים לעיתים עם טבלת ToUnicode שגויה, שממפה כל אות לתו
    /// הלטיני באותו מספר (א ← à, ב ← á). מילה שכולה תווים כאלה, בלי אף אות אנגלית,
    /// היא עברית שנקראה לא נכון — צרפתית, למשל, מערבבת אותם עם אותיות רגילות.
    /// התיקון חל רק כשמילים כאלה הן לפחות רבע מהמילים.
    /// </summary>
    internal static string FixMisreadHebrew(string text)
    {
        var misread = new Regex("[à-ú]+");
        int words = Regex.Matches(text, @"\S+").Count;
        int hebrewish = Regex.Matches(text, @"(?<!\S)[à-ú""'.,:;!?()\-]*[à-ú]{2,}[à-ú""'.,:;!?()\-]*(?!\S)").Count;
        if (words == 0 || hebrewish * 4 < words) return text;

        var cp1255 = Encoding.GetEncoding(1255);
        return misread.Replace(text, m => cp1255.GetString(Encoding.Latin1.GetBytes(m.Value)));
    }

    /// <summary>
    /// PDF שנכתב בסדר תצוגה: האותיות העבריות שמורות משמאל לימין, כך שכל שורה
    /// יוצאת הפוכה ("ïéðòá" = "בענין" הפוך). הסימן: אותיות סופיות (ךםןףץ) בתחילת
    /// מילים ולא בסופן. שורה כזו מתהפכת, ומספרים ומילים לועזיות שבתוכה מתהפכים
    /// בחזרה — הם נכתבו בסדר הנכון.
    /// </summary>
    internal static string FixVisualOrder(string text)
    {
        const string finals = "ךםןףץ";   // לא לתרגום
        int atStart = 0, atEnd = 0;
        foreach (Match w in Regex.Matches(text, "[א-ת]{2,}"))   // לא לתרגום)
        {
            if (finals.Contains(w.Value[0])) atStart++;
            if (finals.Contains(w.Value[^1])) atEnd++;
        }
        if (atStart <= atEnd) return text;

        var lines = text.Split('\n').Select(line =>
        {
            if (!Regex.IsMatch(line, "[א-ת]")) return line;   // לא לתרגום
            char[] chars = line.ToCharArray();
            Array.Reverse(chars);
            for (int k = 0; k < chars.Length; k++)
                chars[k] = chars[k] switch { '(' => ')', ')' => '(', '[' => ']', ']' => '[', _ => chars[k] };
            return Regex.Replace(new string(chars), @"[A-Za-z0-9][A-Za-z0-9./:,%+\-]*[A-Za-z0-9]|[A-Za-z0-9]",
                m => new string(m.Value.Reverse().ToArray()));
        });
        return string.Join('\n', lines);
    }

    private static byte[] LiteralString(string s, ref int i)
    {
        var bytes = new List<byte>();
        int depth = 0;
        for (; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '(' && depth++ == 0) continue;
            if (c == ')' && --depth == 0) { i++; break; }
            if (c == '\\' && i + 1 < s.Length)
            {
                char n = s[++i];
                if (n is >= '0' and <= '7')
                {
                    int v = 0, k = 0;
                    for (; k < 3 && i < s.Length && s[i] is >= '0' and <= '7'; k++, i++) v = v * 8 + (s[i] - '0');
                    i--;
                    bytes.Add((byte)v);
                }
                else if (n is '\r' or '\n') { }                                // המשך שורה
                else bytes.Add((byte)(n switch { 'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'f' => '\f', _ => n }));
                continue;
            }
            bytes.Add((byte)c);
        }
        return bytes.ToArray();
    }

    private static byte[] HexString(string s, ref int i)
    {
        int end = s.IndexOf('>', i);
        if (end < 0) end = s.Length;
        string hex = Regex.Replace(s[(i + 1)..end], @"[^0-9A-Fa-f]", "");
        i = end + 1;
        if (hex.Length % 2 == 1) hex += "0";
        return Convert.FromHexString(hex);
    }

    /// <summary>טבלת ToUnicode: קוד תו בגופן ← הטקסט שהוא מייצג.</summary>
    private sealed class CMap
    {
        private readonly Dictionary<int, string> _map = new();
        private int _bytes = 1;

        internal static CMap Parse(string text)
        {
            var map = new CMap();
            if (Regex.Match(text, @"begincodespacerange\s*<([0-9A-Fa-f]+)>") is { Success: true } space)
                map._bytes = Math.Max(1, space.Groups[1].Value.Length / 2);

            foreach (Match block in Regex.Matches(text, @"beginbfchar(.*?)endbfchar", RegexOptions.Singleline))
                foreach (Match m in Regex.Matches(block.Groups[1].Value, @"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]*)>"))
                    map._map[Convert.ToInt32(m.Groups[1].Value, 16)] = Utf16(m.Groups[2].Value);

            foreach (Match block in Regex.Matches(text, @"beginbfrange(.*?)endbfrange", RegexOptions.Singleline))
                foreach (Match m in Regex.Matches(block.Groups[1].Value,
                             @"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>\s*(?:<([0-9A-Fa-f]*)>|\[([^\]]*)\])"))
                {
                    int low = Convert.ToInt32(m.Groups[1].Value, 16), high = Convert.ToInt32(m.Groups[2].Value, 16);
                    if (high - low > 65535) continue;
                    if (m.Groups[3].Success)
                    {
                        string start = Utf16(m.Groups[3].Value);
                        if (start.Length == 0) continue;
                        for (int code = low; code <= high; code++)
                            map._map[code] = start[..^1] + (char)(start[^1] + code - low);
                    }
                    else
                    {
                        var items = Regex.Matches(m.Groups[4].Value, @"<([0-9A-Fa-f]*)>");
                        for (int k = 0; k < items.Count && low + k <= high; k++) map._map[low + k] = Utf16(items[k].Groups[1].Value);
                    }
                }
            return map;
        }

        private static string Utf16(string hex)
        {
            if (hex.Length % 4 != 0) hex = hex.PadLeft((hex.Length + 3) / 4 * 4, '0');
            return Encoding.BigEndianUnicode.GetString(Convert.FromHexString(hex));
        }

        internal string Decode(byte[] bytes)
        {
            var sb = new StringBuilder();
            for (int i = 0; i + _bytes <= bytes.Length; i += _bytes)
            {
                int code = 0;
                for (int k = 0; k < _bytes; k++) code = (code << 8) | bytes[i + k];
                if (_map.TryGetValue(code, out var s)) sb.Append(s);
            }
            return sb.ToString();
        }
    }
}
