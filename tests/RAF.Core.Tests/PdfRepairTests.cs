using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using RAF.Core.Repair;
using Xunit;

namespace RAF.Core.Tests;

/// <summary>
/// בנייה מחדש של טבלת המיקומים של PDF.
///
/// המקרה שנבדק על מחשב המשתמש: 298 מתוך 300 ספרים מאוצר החכמה נפתחים רק
/// "עם אזהרה" — ההיסטים בטבלה שלהם זזו, והקורא בונה אותה בעצמו בכל פתיחה.
/// הבדיקות כאן בונות מסמכים קטנים בכל מצב, ובודקות שכל שורה בטבלה החדשה
/// מצביעה בדיוק לאובייקט שלה — כולל אובייקט שארוז בתוך זרם דחוס.
/// </summary>
public class PdfRepairTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"raf-pdf-{Guid.NewGuid():N}");

    public PdfRepairTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static readonly string[] Objects =
    {
        "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
        "2 0 obj\n<< /Type /Pages /Count 1 /Kids [3 0 R] >>\nendobj\n",
        "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>\nendobj\n",
        "4 0 obj\n<< /Length 11 >>\nstream\nBT ET q Q \nendstream\nendobj\n",
    };

    /// <summary>PDF קטן ותקין, עם טבלה רגילה. shift מזיז את כל ההיסטים בטבלה — כמו בקבצים שנבדקו.</summary>
    private static byte[] Pdf(int shift = 0, bool withTable = true, string trailerExtra = "")
    {
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        foreach (var o in Objects) { offsets.Add(sb.Length); sb.Append(o); }

        if (withTable)
        {
            int xref = sb.Length;
            sb.Append("xref\n0 5\n0000000000 65535 f \n");
            foreach (int at in offsets) sb.Append($"{at + shift:D10} 00000 n \n");
            sb.Append($"trailer\n<< /Size 5 /Root 1 0 R {trailerExtra}>>\nstartxref\n{xref + shift}\n%%EOF\n");
        }

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>כל רשומה בזרם ה-xref החדש מצביעה לאובייקט שלה: סוג, והיסט או (זרם, מקום).</summary>
    private static Dictionary<int, (int Type, long Field2, int Field3)> ReadXrefStream(byte[] pdf)
    {
        string text = Encoding.Latin1.GetString(pdf);
        int sx = text.LastIndexOf("startxref", StringComparison.Ordinal);
        int offset = int.Parse(text[(sx + 9)..].Trim().Split('\n')[0]);
        int start = text.IndexOf("stream\n", offset, StringComparison.Ordinal) + "stream\n".Length;
        int end = text.IndexOf("\nendstream", start, StringComparison.Ordinal);

        var rows = new Dictionary<int, (int, long, int)>();
        for (int at = start, n = 0; at + 7 <= end; at += 7, n++)
            rows[n] = (pdf[at], BinaryPrimitives.ReadUInt32BigEndian(pdf.AsSpan(at + 1)), BinaryPrimitives.ReadUInt16BigEndian(pdf.AsSpan(at + 5)));
        return rows;
    }

    private static void AssertEveryObjectIsWhereTheTableSays(byte[] rebuilt, params int[] numbers)
    {
        string text = Encoding.Latin1.GetString(rebuilt);
        var rows = ReadXrefStream(rebuilt);
        foreach (int n in numbers)
        {
            var (type, offset, _) = rows[n];
            Assert.Equal(1, type);
            Assert.StartsWith($"{n} 0 obj", text[(int)offset..]);
        }
    }

    [Fact]
    public void A_healthy_pdf_is_left_alone()
    {
        var a = PdfRebuilder.Analyze(Pdf());
        Assert.True(a.XrefValid, a.XrefProblem);
        Assert.Equal(1, a.Root);
    }

    [Fact]
    public void Shifted_offsets_are_detected_and_the_new_table_points_exactly_at_every_object()
    {
        byte[] broken = Pdf(shift: 7);
        var a = PdfRebuilder.Analyze(broken);
        Assert.False(a.XrefValid);
        Assert.True(a.CanRebuild);

        byte[] rebuilt = PdfRebuilder.Rebuild(broken, a);
        Assert.True(PdfRebuilder.Analyze(rebuilt).XrefValid);
        AssertEveryObjectIsWhereTheTableSays(rebuilt, 1, 2, 3, 4);
    }

    [Fact]
    public void A_truncated_pdf_without_its_table_is_rebuilt()
    {
        byte[] cut = Pdf(withTable: false);
        var a = PdfRebuilder.Analyze(cut);
        Assert.False(a.XrefValid);

        byte[] rebuilt = PdfRebuilder.Rebuild(cut, a);
        AssertEveryObjectIsWhereTheTableSays(rebuilt, 1, 2, 3, 4);
    }

    [Fact]
    public void An_object_cut_in_the_middle_is_not_included()
    {
        byte[] full = Pdf(withTable: false);
        byte[] cut = full.AsSpan(0, full.Length - 20).ToArray();      // האובייקט האחרון נחתך באמצע

        var a = PdfRebuilder.Analyze(cut);
        Assert.False(a.Objects.ContainsKey(4));
        Assert.True(a.Objects.ContainsKey(3));
    }

    [Fact]
    public void Objects_packed_in_a_compressed_object_stream_are_found_including_the_catalog()
    {
        // האובייקטים 1–3 בתוך זרם אובייקטים דחוס (מספר 5), כמו ב-PDF מודרני.
        string[] inner = { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Count 1 /Kids [3 0 R] >>",
                           "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>" };
        var body = new StringBuilder();
        var header = new StringBuilder();
        for (int i = 0; i < inner.Length; i++) { header.Append($"{i + 1} {body.Length} "); body.Append(inner[i]).Append('\n'); }
        byte[] plain = Encoding.Latin1.GetBytes(header.ToString() + body);

        using var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(plain);
        byte[] compressed = packed.ToArray();

        using var pdf = new MemoryStream();
        pdf.Write(Encoding.Latin1.GetBytes(
            $"%PDF-1.5\n5 0 obj\n<< /Type /ObjStm /N 3 /First {header.Length} /Filter /FlateDecode /Length {compressed.Length} >>\nstream\n"));
        pdf.Write(compressed);
        pdf.Write(Encoding.Latin1.GetBytes("\nendstream\nendobj\n"));

        var a = PdfRebuilder.Analyze(pdf.ToArray());
        Assert.Equal(1, a.Root);
        Assert.True(a.Objects[2].Compressed);

        var rows = ReadXrefStream(PdfRebuilder.Rebuild(pdf.ToArray(), a));
        Assert.Equal((2, 5L, 0), rows[1]);      // הקטלוג: בתוך זרם 5, במקום 0
        Assert.Equal((2, 5L, 2), rows[3]);
    }

    [Fact]
    public void A_lost_catalog_is_replaced_by_one_that_points_at_the_page_tree()
    {
        string withoutCatalog = Encoding.Latin1.GetString(Pdf(withTable: false))
            .Replace(Objects[0], "");
        var a = PdfRebuilder.Analyze(Encoding.Latin1.GetBytes(withoutCatalog));

        Assert.True(a.CatalogMissing);
        Assert.Equal(2, a.PagesRoot);

        string rebuilt = Encoding.Latin1.GetString(PdfRebuilder.Rebuild(Encoding.Latin1.GetBytes(withoutCatalog), a));
        Assert.Contains("<< /Type /Catalog /Pages 2 0 R >>", rebuilt);
        Assert.True(PdfRebuilder.Analyze(Encoding.Latin1.GetBytes(rebuilt)).Root is not null);
    }

    [Fact]
    public void An_encrypted_document_keeps_its_encryption_and_id()
    {
        byte[] broken = Pdf(shift: 3, trailerExtra: "/Encrypt 9 0 R /ID [<AB12> <CD34>] ");
        var a = PdfRebuilder.Analyze(broken);
        string rebuilt = Encoding.Latin1.GetString(PdfRebuilder.Rebuild(broken, a));

        Assert.Contains("/Encrypt 9 0 R", rebuilt[rebuilt.LastIndexOf("/Type /XRef", StringComparison.Ordinal)..]);
        Assert.Contains("/ID [<AB12> <CD34>]", rebuilt);
    }

    [Fact]
    public void The_doctor_rebuilds_a_broken_pdf_and_a_healthy_one_ending_in_a_newline_has_no_issues()
    {
        string healthy = Path.Combine(_dir, "ok.pdf");
        File.WriteAllBytes(healthy, Pdf());                                // מסתיים ב-"%%EOF\n"
        Assert.True(FileDoctor.Diagnose(healthy).IsHealthy);

        string broken = Path.Combine(_dir, "broken.pdf");
        File.WriteAllBytes(broken, Pdf(shift: 5));
        var d = FileDoctor.Diagnose(broken);
        Assert.Contains(d.Issues, i => i.Kind == FileIssueKind.PdfStructureDamaged && i.Fixable);

        var r = FileDoctor.Repair(broken, Path.Combine(_dir, "out"));
        Assert.True(r.Succeeded, r.Message);
        Assert.True(r.After!.IsHealthy);
    }
}
