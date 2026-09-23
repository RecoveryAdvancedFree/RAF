using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using RAF.Core.Repair;
using Xunit;

namespace RAF.Core.Tests;

/// <summary>
/// תיקון ארכיוני ZIP ומסמכי Office: בנייה מחדש של תוכן העניינים מהקבצים הפנימיים.
///
/// המקרה הנפוץ: קובץ ששוחזר או הועתק ונקטע לפני הסוף. כל הקבצים הפנימיים שלמים,
/// אבל תוכן העניינים — שנמצא בסוף — חסר, ולכן Word ו-Excel מסרבים לפתוח. האמת
/// נבדקת כאן מול ZipArchive של .NET: העותק המתוקן חייב להיפתח, ובתוכן זהה.
/// </summary>
public class ZipRepairTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"raf-zip-{Guid.NewGuid():N}");

    public ZipRepairTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    /// <summary>זרם שאי אפשר לחזור בו אחורה — כך ZipArchive כותב "data descriptor" אחרי כל קובץ.</summary>
    private sealed class ForwardOnly(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => inner.Write(b, o, c);
    }

    private static readonly Dictionary<string, byte[]> Parts = new()
    {
        ["[Content_Types].xml"] = Encoding.UTF8.GetBytes("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>"),
        ["word/document.xml"] = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("<w:p>דוח שנתי — תוכן שחייב לחזור</w:p>", 400))),
        ["word/media/תמונה.bin"] = RealFormats.Random(20_000, 3),       // לא דחיס — נשמר כמעט כמות שהוא
        ["docProps/core.xml"] = Encoding.UTF8.GetBytes("<cp:coreProperties/>"),
    };

    private static byte[] Zip(bool streaming)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(streaming ? new ForwardOnly(buffer) : buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in Parts)
            {
                using var s = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
                s.Write(content);
            }
        }
        return buffer.ToArray();
    }

    /// <summary>היכן מתחיל תוכן העניינים — חיתוך שם מדמה קובץ שנקטע לפני הסוף.</summary>
    private static int DirectoryStart(byte[] zip)
        => (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(zip.Length - 22 + 16));

    private string Write(string name, byte[] data)
    {
        string path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static Dictionary<string, byte[]> Contents(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return archive.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var s = e.Open();
            using var m = new MemoryStream();
            s.CopyTo(m);
            return m.ToArray();
        });
    }

    [Fact]
    public void A_healthy_archive_is_healthy()
    {
        Assert.True(FileDoctor.Diagnose(Write("ok.docx", Zip(streaming: false))).IsHealthy);
        Assert.True(FileDoctor.Diagnose(Write("ok2.docx", Zip(streaming: true))).IsHealthy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]     // עם data descriptor — הגדלים רשומים רק אחרי הנתונים
    public void A_document_cut_before_its_table_of_contents_is_rebuilt_and_opens(bool streaming)
    {
        byte[] zip = Zip(streaming);
        string path = Write("report.docx", zip[..DirectoryStart(zip)]);

        // וידוא שהבדיקה אכן מכסה את המקרה הקשה: סיבית 3 בכותרת המקומית.
        Assert.Equal(streaming, (zip[6] & 0x08) != 0);

        var d = FileDoctor.Diagnose(path);
        var issue = Assert.Single(d.Issues);
        Assert.Equal(FileIssueKind.ArchiveDirectoryDamaged, issue.Kind);
        Assert.True(issue.Fixable);

        var r = FileDoctor.Repair(path, Path.Combine(_folder, "out"));
        Assert.True(r.Succeeded, r.Message);
        Assert.True(r.After!.IsHealthy);

        var contents = Contents(r.OutputPath!);
        Assert.Equal(Parts.Keys.OrderBy(k => k), contents.Keys.OrderBy(k => k));
        foreach (var (name, content) in Parts) Assert.Equal(content, contents[name]);

        // המקור לא השתנה.
        Assert.Equal(zip[..DirectoryStart(zip)], File.ReadAllBytes(path));
    }

    [Fact]
    public void A_damaged_inner_file_is_left_out_by_name_and_the_rest_survive()
    {
        byte[] zip = Zip(streaming: false);
        byte[] cut = zip[..DirectoryStart(zip)];

        // נזק באמצע התמונה הפנימית: הבתים בגוף הקובץ שלה מתהפכים.
        int media = IndexOf(cut, Encoding.UTF8.GetBytes("word/media/"));
        for (int i = media + 200; i < media + 400; i++) cut[i] ^= 0x5A;

        var d = FileDoctor.Diagnose(Write("damaged.docx", cut));
        var entries = Assert.Single(d.Issues, i => i.Kind == FileIssueKind.ArchiveEntriesDamaged);
        Assert.Contains("תמונה.bin", entries.Description);

        var r = FileDoctor.Repair(d.Path, Path.Combine(_folder, "out"));
        var contents = Contents(r.OutputPath!);

        Assert.DoesNotContain("word/media/תמונה.bin", contents.Keys);
        Assert.Equal(Parts["word/document.xml"], contents["word/document.xml"]);
        Assert.Contains(r.Applied, a => a.Contains("הושמטו"));
    }

    [Fact]
    public void Without_content_types_the_document_is_rebuilt_but_the_user_is_told_office_will_refuse()
    {
        byte[] zip = Zip(streaming: false);
        byte[] cut = zip[..DirectoryStart(zip)];
        int types = IndexOf(cut, Encoding.UTF8.GetBytes("[Content_Types].xml"));
        for (int i = types + 30; i < types + 60; i++) cut[i] ^= 0xFF;

        var d = FileDoctor.Diagnose(Write("notypes.docx", cut));
        var missing = Assert.Single(d.Issues, i => i.Kind == FileIssueKind.ArchivePartMissing);
        Assert.False(missing.Fixable);
        Assert.True(d.CanRepair);
    }

    [Fact]
    public void An_overwritten_region_between_inner_files_is_skipped()
    {
        byte[] zip = Zip(streaming: false);
        byte[] cut = zip[..DirectoryStart(zip)];

        // הקובץ הפנימי השני נדרס כולו באפסים — הסריקה צריכה להגיע לשלישי.
        int second = IndexOf(cut, Encoding.UTF8.GetBytes("word/document.xml")) - 30;
        int third = IndexOf(cut, Encoding.UTF8.GetBytes("word/media/")) - 30;
        Array.Clear(cut, second, third - second);

        var r = FileDoctor.Repair(Write("hole.docx", cut), Path.Combine(_folder, "out"));
        var contents = Contents(r.OutputPath!);

        Assert.Equal(Parts["word/media/תמונה.bin"], contents["word/media/תמונה.bin"]);
        Assert.Equal(Parts["docProps/core.xml"], contents["docProps/core.xml"]);
        Assert.DoesNotContain("word/document.xml", contents.Keys);
    }

    [Fact]
    public void An_archive_with_many_inner_files_is_rebuilt_without_exhausting_the_stack()
    {
        // stackalloc בתוך לולאת הכתיבה הצטבר על המחסנית — 20,000 קבצים פנימיים היו מפילים את התהליך.
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            for (int i = 0; i < 20_000; i++)
            {
                using var s = archive.CreateEntry($"OEBPS/p{i}.xhtml", CompressionLevel.Fastest).Open();
                s.Write(Encoding.UTF8.GetBytes($"<p>{i}</p>"));
            }

        byte[] zip = buffer.ToArray();
        var r = FileDoctor.Repair(Write("book.epub", zip[..DirectoryStart(zip)]), Path.Combine(_folder, "out"));

        Assert.True(r.Succeeded, r.Message);
        using var rebuilt = ZipFile.OpenRead(r.OutputPath!);
        Assert.Equal(20_000, rebuilt.Entries.Count);
    }

    private static int IndexOf(byte[] data, byte[] pattern)
    {
        for (int i = 0; i <= data.Length - pattern.Length; i++)
            if (data.AsSpan(i, pattern.Length).SequenceEqual(pattern)) return i;
        throw new InvalidOperationException("pattern not found");
    }
}
