using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace RAF.Core.Repair;

/// <summary>
/// בנייה מחדש של טבלת המיקומים (xref) של מסמך PDF.
///
/// בסוף כל PDF יש טבלה שאומרת באיזה בית בקובץ מתחיל כל אובייקט — עמוד, גופן,
/// תמונה — ומצביע ("startxref") אליה. PDF שנקטע, או שהטבלה בו נפגעה, לא נפתח
/// בחלק מהתוכנות אף שכל העמודים בתוכו. הטבלה נבנית מחדש מהאובייקטים עצמם.
///
/// ב-PDF מודרני (1.5 ואילך) רוב האובייקטים ארוזים בתוך "זרמי אובייקטים"
/// דחוסים (ObjStm), ואינם נראים בסריקה של הקובץ. טבלה שמכירה רק את הגלויים
/// הייתה מפנה לאובייקטים שאינם קיימים — לכן הזרמים נפתחים, והטבלה החדשה
/// נכתבת כזרם xref, שיודע להצביע גם לאובייקט שבתוך זרם.
///
/// מסמך מוצפן נשאר מוצפן: מילון ההצפנה והמזהה (/Encrypt, /ID) עוברים לטבלה החדשה.
/// </summary>
internal static class PdfRebuilder
{
    /// <summary>ניתוח המבנה: האובייקטים, השורש, והאם הטבלה הקיימת תקינה.</summary>
    internal sealed class Analysis
    {
        /// <summary>מספר אובייקט → היכן הוא: היסט בקובץ, או (זרם, מקום בזרם).</summary>
        public Dictionary<int, Entry> Objects { get; } = new();
        public int? Root { get; set; }

        /// <summary>
        /// שורש עץ העמודים (/Pages בלי /Parent). כשהחלק הראשי (/Catalog) אבד,
        /// נבנה חלק ראשי חדש שמצביע אליו — כך נוהגים גם קוראי PDF כשהם מתקנים.
        /// </summary>
        public int? PagesRoot { get; set; }

        public int? Info { get; set; }
        public string? Encrypt { get; set; }
        public string? Id { get; set; }

        /// <summary>סוף האובייקט השלם האחרון — מה שאחריו הוא טבלה ישנה או שארית קטועה.</summary>
        public long ContentEnd { get; set; }

        public bool XrefValid { get; set; }
        public string? XrefProblem { get; set; }

        public bool CanRebuild => (Root is not null || PagesRoot is not null) && Objects.Count > 0;

        /// <summary>החלק הראשי אבד, והטבלה החדשה תבוא עם חלק ראשי חדש.</summary>
        public bool CatalogMissing => Root is null && PagesRoot is not null;
    }

    internal readonly record struct Entry(long Offset, int Generation, int Stream, int Index, long Position)
    {
        public bool Compressed => Stream >= 0;
    }

    private static readonly Regex ObjectHeader = new(@"(\d{1,7})\s+(\d{1,5})\s+obj\b", RegexOptions.Compiled);

    internal static Analysis Analyze(byte[] data)
    {
        var a = new Analysis();

        // latin1: בית אחד לתו, כך שהיסט בטקסט הוא היסט בקובץ.
        string text = Encoding.Latin1.GetString(data);
        var streams = new List<(int Number, long Position, string Dict, int Start, int End)>();

        var match = ObjectHeader.Match(text);
        while (match.Success)
        {
            int at = match.Index;
            char before = at > 0 ? text[at - 1] : '\n';
            if (before > ' ' && before != '>' && before != ']')
            {
                match = match.NextMatch();
                continue;
            }

            int number = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            int generation = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            int bodyStart = match.Index + match.Length;

            // תוכן זרם בינארי עלול להכיל "endobj" או "N 0 obj" במקרה — מדלגים עליו כולו.
            int streamKeyword = FindStreamKeyword(text, bodyStart);
            int end;
            int dataStart = -1, dataEnd = -1;

            if (streamKeyword >= 0)
            {
                dataStart = SkipEol(text, streamKeyword + "stream".Length);
                int declared = DirectLength(text.AsSpan(bodyStart, streamKeyword - bodyStart));
                dataEnd = declared >= 0 && dataStart + declared <= text.Length &&
                          text.AsSpan(dataStart + declared, Math.Min(32, text.Length - dataStart - declared))
                              .TrimStart().StartsWith("endstream")
                    ? dataStart + declared
                    : text.IndexOf("endstream", dataStart, StringComparison.Ordinal);
                if (dataEnd < 0) break;                                   // זרם קטוע — סוף החלק השלם
                end = text.IndexOf("endobj", dataEnd, StringComparison.Ordinal);
            }
            else
            {
                end = text.IndexOf("endobj", bodyStart, StringComparison.Ordinal);
            }

            if (end < 0) break;                                           // אובייקט קטוע
            end += "endobj".Length;

            // עדכונים מצטברים: הגדרה מאוחרת בקובץ גוברת על מוקדמת.
            a.Objects[number] = new Entry(at, generation, -1, 0, at);
            a.ContentEnd = end;

            string dict = text.Substring(bodyStart, Math.Min((streamKeyword >= 0 ? streamKeyword : end) - bodyStart, 8192));
            Classify(a, number, dict);
            if (streamKeyword >= 0 && Regex.IsMatch(dict, @"/Type\s*/ObjStm\b"))
                streams.Add((number, at, dict, dataStart, dataEnd));

            match = ObjectHeader.Match(text, end);
        }

        foreach (var s in streams) ReadObjectStream(a, data, s.Number, s.Position, s.Dict, s.Start, s.End);

        // הצפנה ומזהה — מהמילון האחרון שמזכיר אותם (trailer או זרם xref).
        var encrypt = Regex.Matches(text, @"/Encrypt\s+(\d+\s+\d+\s+R)");
        if (encrypt.Count > 0) a.Encrypt = encrypt[^1].Groups[1].Value;
        var id = Regex.Matches(text, @"/ID\s*(\[\s*<[0-9A-Fa-f\s]*>\s*<[0-9A-Fa-f\s]*>\s*\])");
        if (id.Count > 0) a.Id = id[^1].Groups[1].Value;

        CheckExistingXref(a, text);
        return a;
    }

    private static void Classify(Analysis a, int number, string dict)
    {
        if (Regex.IsMatch(dict, @"/Type\s*/Catalog\b")) a.Root = number;
        else if (Regex.IsMatch(dict, @"/Type\s*/Pages\b") && !Regex.IsMatch(dict, @"/Parent\b")) a.PagesRoot = number;
        else if (a.Info is null && Regex.IsMatch(dict, @"/(Producer|Creator|CreationDate)\b") &&
                 !Regex.IsMatch(dict, @"/Type\b"))
            a.Info = number;
    }

    /// <summary>פתיחת זרם אובייקטים: אילו אובייקטים בתוכו, ואם אחד מהם הוא השורש.</summary>
    private static void ReadObjectStream(Analysis a, byte[] data, int streamNumber, long position,
        string dict, int start, int end)
    {
        if (!Regex.IsMatch(dict, @"/Filter\s*/FlateDecode\b") || Regex.IsMatch(dict, @"/DecodeParms")) return;
        if (!TryInt(dict, "N", out int count) || !TryInt(dict, "First", out int first)) return;

        byte[] content;
        try
        {
            using var input = new ZLibStream(new MemoryStream(data, start, end - start), CompressionMode.Decompress);
            using var output = new MemoryStream();
            input.CopyTo(output);
            content = output.ToArray();
        }
        catch
        {
            return;                                                       // זרם פגום — האובייקטים שבו אבודים
        }

        string text = Encoding.Latin1.GetString(content);
        string[] header = text[..Math.Min(first, text.Length)]
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i + 1 < header.Length && i / 2 < count; i += 2)
        {
            if (!int.TryParse(header[i], out int number) || !int.TryParse(header[i + 1], out int offset)) break;

            // אובייקט גלוי מאוחר יותר באותו מספר גובר (עדכון מצטבר שנכתב אחרי הזרם).
            if (a.Objects.TryGetValue(number, out var existing) && existing.Position > position) continue;
            a.Objects[number] = new Entry(0, 0, streamNumber, i / 2, position);

            int from = first + offset;
            if (from >= text.Length) continue;
            int next = i + 3 < header.Length && int.TryParse(header[i + 3], out int o) ? first + o : text.Length;
            Classify(a, number, text[from..Math.Min(Math.Max(next, from), text.Length)]);
        }
    }

    /// <summary>
    /// האם הטבלה הקיימת תקינה: startxref מצביע לטבלה או לזרם xref, והאובייקטים
    /// שהיא מציינת נמצאים באמת במקום שכתוב בה. מספיקה סתירה אחת כדי לבנות מחדש.
    /// </summary>
    private static void CheckExistingXref(Analysis a, string text)
    {
        int tail = Math.Max(0, text.Length - 2048);
        int sx = text.LastIndexOf("startxref", StringComparison.Ordinal);
        if (sx < tail || sx < 0)
        {
            a.XrefProblem = L.T("סוף הקובץ חסר — ככל הנראה הקובץ נקטע, ואיתו טבלת המיקומים של המסמך.");
            return;
        }

        var num = Regex.Match(text[(sx + 9)..], @"^\s*(\d+)");
        if (!num.Success || !long.TryParse(num.Groups[1].Value, out long offset) || offset >= text.Length)
        {
            a.XrefProblem = L.T("ההפניה לטבלת המיקומים של המסמך שבורה.");
            return;
        }

        if (string.CompareOrdinal(text, (int)offset, "xref", 0, 4) == 0)
        {
            if (!ClassicTableMatches(a, text, (int)offset))
            {
                a.XrefProblem = L.T("טבלת המיקומים של המסמך אינה תואמת את תוכנו — היא מצביעה למקומות שבהם אין את החלקים.");
                return;
            }
        }
        else if (!ObjectHeader.Match(text, (int)offset).Success || ObjectHeader.Match(text, (int)offset).Index != offset ||
                 !Regex.IsMatch(text.Substring((int)offset, Math.Min(4096, text.Length - (int)offset)), @"/Type\s*/XRef\b"))
        {
            a.XrefProblem = L.T("ההפניה לטבלת המיקומים של המסמך מצביעה למקום שאין בו טבלה.");
            return;
        }

        a.XrefValid = true;
    }

    /// <summary>דגימה של טבלה רגילה: כל רשומה בשימוש מצביעה ל-"N G obj" עם המספר הנכון.</summary>
    private static bool ClassicTableMatches(Analysis a, string text, int offset)
    {
        var sections = Regex.Matches(text[(offset + 4)..Math.Min(text.Length, offset + 4 + 2_000_000)],
            @"(\d+)\s+(\d+)\s*[\r\n]+((?:\d{10}\s\d{5}\s[nf]\s*[\r\n]+)+)");
        if (sections.Count == 0) return false;

        int checkedEntries = 0;
        foreach (Match section in sections)
        {
            int number = int.Parse(section.Groups[1].Value, CultureInfo.InvariantCulture);
            foreach (Match row in Regex.Matches(section.Groups[3].Value, @"(\d{10})\s(\d{5})\s([nf])"))
            {
                if (row.Groups[3].Value == "n" && checkedEntries++ < 200)
                {
                    long at = long.Parse(row.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (at >= text.Length) return false;
                    var header = ObjectHeader.Match(text, (int)at);
                    if (!header.Success || header.Index != at ||
                        int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture) != number)
                        return false;
                }
                number++;
            }
        }

        return true;
    }

    /// <summary>
    /// המסמך עם טבלה חדשה: כל האובייקטים השלמים כמו שהם, ואחריהם זרם xref
    /// שמצביע לכל אחד מהם — גם לארוזים בתוך זרמי אובייקטים.
    /// </summary>
    internal static byte[] Rebuild(byte[] data, Analysis a)
    {
        if (!a.CanRebuild) throw new InvalidOperationException(L.T("אין במסמך מספיק מבנה כדי לבנות אותו מחדש."));

        using var output = new MemoryStream();
        output.Write(data, 0, (int)a.ContentEnd);
        Ascii(output, "\n");

        int next = a.Objects.Keys.Max() + 1;
        int root = a.Root ?? next;

        // חלק ראשי חדש, כשהמקורי אבד: מצביע לעץ העמודים שנמצא.
        long? catalogOffset = null;
        if (a.Root is null)
        {
            catalogOffset = output.Position;
            Ascii(output, $"{root} 0 obj\n<< /Type /Catalog /Pages {a.PagesRoot} 0 R >>\nendobj\n");
            next++;
        }

        int xrefNumber = next;
        int size = xrefNumber + 1;
        long xrefOffset = output.Position;

        // W [1 4 2]: סוג (0 פנוי, 1 בקובץ, 2 בתוך זרם), היסט או מספר זרם, דור או מקום בזרם.
        byte[] table = new byte[size * 7];
        for (int n = 0; n < size; n++)
        {
            int at = n * 7;
            if (n == xrefNumber) Row(table, at, 1, xrefOffset, 0);
            else if (n == root && catalogOffset is { } c) Row(table, at, 1, c, 0);
            else if (a.Objects.TryGetValue(n, out var e))
                Row(table, at, e.Compressed ? 2 : 1, e.Compressed ? e.Stream : e.Offset, e.Compressed ? e.Index : e.Generation);
            else Row(table, at, 0, 0, n == 0 ? 65535 : 0);
        }

        var dict = new StringBuilder();
        dict.Append(CultureInfo.InvariantCulture, $"{xrefNumber} 0 obj\n<< /Type /XRef /Size {size} /W [1 4 2] ");
        dict.Append(CultureInfo.InvariantCulture, $"/Root {root} 0 R ");
        if (a.Info is { } info && a.Objects.ContainsKey(info)) dict.Append(CultureInfo.InvariantCulture, $"/Info {info} 0 R ");
        if (a.Encrypt is not null) dict.Append("/Encrypt ").Append(a.Encrypt).Append(' ');
        if (a.Id is not null) dict.Append("/ID ").Append(a.Id).Append(' ');
        dict.Append(CultureInfo.InvariantCulture, $"/Length {table.Length} >>\nstream\n");

        Ascii(output, dict.ToString());
        output.Write(table);
        Ascii(output, $"\nendstream\nendobj\nstartxref\n{xrefOffset}\n%%EOF\n");
        return output.ToArray();
    }

    private static void Row(byte[] table, int at, int type, long field2, int field3)
    {
        table[at] = (byte)type;
        table[at + 1] = (byte)(field2 >> 24);
        table[at + 2] = (byte)(field2 >> 16);
        table[at + 3] = (byte)(field2 >> 8);
        table[at + 4] = (byte)field2;
        table[at + 5] = (byte)(field3 >> 8);
        table[at + 6] = (byte)field3;
    }

    private static void Ascii(Stream s, string text) => s.Write(Encoding.Latin1.GetBytes(text));

    /// <summary>המילה "stream" שפותחת את תוכן הזרם — לפני ה-endobj של האובייקט.</summary>
    private static int FindStreamKeyword(string text, int from)
    {
        int end = text.IndexOf("endobj", from, StringComparison.Ordinal);
        int limit = end < 0 ? text.Length : end;
        int at = from;
        while (true)
        {
            at = text.IndexOf("stream", at, StringComparison.Ordinal);
            if (at < 0 || at >= limit) return -1;
            bool afterDict = text.AsSpan(from, at - from).TrimEnd().EndsWith(">>");
            bool notEnd = at < 3 || text.Substring(at - 3, 3) != "end";
            if (afterDict && notEnd) return at;
            at += 6;
        }
    }

    private static int SkipEol(string text, int at)
    {
        if (at < text.Length && text[at] == '\r') at++;
        if (at < text.Length && text[at] == '\n') at++;
        return at;
    }

    /// <summary>‎/Length כמספר ישיר. ‎-1 כשהוא הפניה לאובייקט אחר, או חסר.</summary>
    private static int DirectLength(ReadOnlySpan<char> dict)
    {
        var m = Regex.Match(dict.ToString(), @"/Length\s+(\d+)(?!\s+\d+\s+R)");
        return m.Success && int.TryParse(m.Groups[1].Value, out int n) ? n : -1;
    }

    private static bool TryInt(string dict, string key, out int value)
    {
        var m = Regex.Match(dict, $@"/{key}\s+(\d+)");
        value = m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        return m.Success;
    }
}
