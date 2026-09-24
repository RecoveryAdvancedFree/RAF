using System.IO.Compression;
using System.Text;
using RAF.Core.Repair;
using Xunit;

namespace RAF.Core.Tests;

/// <summary>
/// חילוץ טקסט ממסמך פגום — המוצא האחרון כשהמסמך עצמו אינו נפתח.
/// הבדיקות כאן נגזרו מבדיקה על מסמכים עבריים אמיתיים: קבצי Word שנקטעו, PDF
/// שהעברית בו שמורה הפוך או בקידוד ישן, ועיתונים שהטקסט שלהם אינו ניתן לפענוח.
/// </summary>
public class TextExtractionTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"raf-text-{Guid.NewGuid():N}");

    public TextExtractionTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch { }
    }

    private static byte[] Word(params string[] paragraphs)
    {
        using var s = new MemoryStream();
        using (var zip = new ZipArchive(s, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string text)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                w.Write(text);
            }
            Add("[Content_Types].xml", "<Types/>");
            Add("word/document.xml", "<w:document><w:body>" +
                string.Concat(paragraphs.Select(p => $"<w:p><w:r><w:t xml:space=\"preserve\">{p}</w:t></w:r></w:p>")) +
                "</w:body></w:document>");
            Add("word/media/image1.png", Encoding.ASCII.GetString(RealFormats.Random(20000, 4)));
        }
        return s.ToArray();
    }

    private string Save(string name, byte[] data)
    {
        string path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static readonly string[] Letter =
    {
        "שלום רב לכל המשפחה",
        "רציתי לספר לכם על הטיול שעשינו בשבוע שעבר לגליל העליון",
        "היה נפלא, ונחזור לשם בקיץ הבא בעזרת השם",
    };

    [Fact]
    public void A_word_document_whose_end_was_lost_gives_back_its_text()
    {
        byte[] doc = Word(Letter);
        string path = Save("מכתב.docx", doc[..(doc.Length - 150)]);            // תוכן העניינים שבסוף אבד

        var result = FileDoctor.Repair(path, Path.Combine(_folder, "out"));

        Assert.NotNull(result.TextPath);
        string text = File.ReadAllText(result.TextPath!);
        foreach (string line in Letter) Assert.Contains(line, text);
        Assert.EndsWith("(טקסט).txt", result.TextPath);
    }

    [Fact]
    public void A_word_document_whose_start_was_wiped_still_gives_back_its_text()
    {
        byte[] doc = Word(Letter);
        Array.Clear(doc, 0, 4);                                                    // החתימה נמחקה
        var diagnosis = FileDoctor.Diagnose(Save("מכתב.docx", doc));

        Assert.Contains(diagnosis.Issues, i => i.Kind == FileIssueKind.TextRecoverable && i.Fixable);
    }

    [Fact]
    public void A_healthy_document_is_not_offered_a_text_copy()
    {
        var diagnosis = FileDoctor.Diagnose(Save("תקין.docx", Word(Letter)));
        Assert.DoesNotContain(diagnosis.Issues, i => i.Kind == FileIssueKind.TextRecoverable);
    }

    /// <summary>Word 97–2003 שנקטע: הטקסט העברי (UTF-16) נמצא גם בלי המבנה.</summary>
    [Fact]
    public void An_old_word_document_gives_back_its_hebrew_text_without_its_structure()
    {
        byte[] body = Encoding.Unicode.GetBytes(string.Join("\r", Letter) + "\r");
        byte[] doc = [.. new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }, .. new byte[1528],
                      .. Encoding.Unicode.GetBytes("Times New Roman"), .. new byte[100], .. body, .. new byte[512]];

        string? text = TextExtractor.Extract(doc, "doc");

        Assert.NotNull(text);
        foreach (string line in Letter) Assert.Contains(line, text);
        Assert.DoesNotContain("Times New Roman", text);                            // שם גופן — לא טקסט
    }

    private static byte[] Pdf(string content, string fontDict = "")
    {
        string pdf = "%PDF-1.4\n" +
                     "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
                     "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
                     $"3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n" +
                     $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n" +
                     $"5 0 obj\n<< /Type /Font {fontDict} >>\nendobj\n";
        return Encoding.Latin1.GetBytes(pdf);
    }

    [Fact]
    public void Pdf_text_is_decoded_through_the_fonts_tounicode_table()
    {
        // גופן מוטמע: קוד 1 = ש, 2 = ל, 3 = ו, 4 = ם — כמו ש-Word כותב עברית.
        string cmap = "begincodespacerange <00> <FF> endcodespacerange\n" +
                      "4 beginbfchar <01> <05E9> <02> <05DC> <03> <05D5> <04> <05DD> endbfchar";
        byte[] data = [.. Pdf(string.Concat(Enumerable.Repeat("BT /F1 12 Tf 0 -20 Td <01020304> Tj ET\n", 6)), "/ToUnicode 6 0 R"),
                       .. Encoding.Latin1.GetBytes($"6 0 obj\n<< /Length {cmap.Length} >>\nstream\n{cmap}\nendstream\nendobj\n")];

        string? text = TextExtractor.Extract(data, "pdf");
        Assert.NotNull(text);
        Assert.Contains("שלום", text);
    }

    /// <summary>
    /// נמצא בבדיקה על PDF עבריים אמיתיים: אותיות ב-Windows-1255 בלי טבלת ToUnicode,
    /// ובסדר תצוגה — "ïéðòá" הוא "בענין" הפוך.
    /// </summary>
    [Fact]
    public void Old_hebrew_pdf_text_in_visual_order_is_read_right_to_left()
    {
        var cp1255 = CodePages();
        string content = string.Concat(new[] { "שנה שנים עשר", "בענין כל השביעין חביבין", "משה רבנו ואהרן הכהן" }
            .Select(l => $"BT /F1 12 Tf 0 -20 Td ({Encoding.Latin1.GetString(cp1255.GetBytes(new string(l.Reverse().ToArray())))}) Tj ET\n"));

        string? text = TextExtractor.Extract(Pdf(content), "pdf");

        Assert.NotNull(text);
        Assert.Contains("שנה שנים עשר", text);
        Assert.Contains("בענין כל השביעין חביבין", text);
    }

    [Fact]
    public void Gibberish_from_fonts_with_private_codes_is_not_offered()
    {
        string content = string.Concat(Enumerable.Range(0, 30).Select(i => $"BT /F1 12 Tf (|xw\x87t\x83}}s |xo\x89s x\x83{{) Tj ET\n"));
        Assert.Null(TextExtractor.Extract(Pdf(content), "pdf"));
    }

    private static Encoding CodePages()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1255);
    }
}
