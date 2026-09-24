using System.Security.Cryptography;
using System.Text;
using RAF.Core.Imaging;
using RAF.Core.Model;
using RAF.Core.Recovery;
using Xunit;

namespace RAF.Core.Tests;

/// <summary>
/// שחזור מלא מתמונת דיסק לתיקיית יעד: מה נכתב, לאן, ומה מדווח.
///
/// זה השלב שבו המשתמש מקבל את הקבצים בחזרה — ולכן נבדק כאן מה שהוא רואה
/// בפועל בתיקיית היעד: הקבצים, התיקייה של החלקיים, והדוח.
/// </summary>
public class RecoveryWriterTests : IDisposable
{
    private const int Sector = 512;
    private const int ImageSectors = 64;

    private readonly string _image = Path.Combine(Path.GetTempPath(), $"raf-recover-{Guid.NewGuid():N}.img");
    private readonly string _target = Path.Combine(Path.GetTempPath(), $"raf-recover-out-{Guid.NewGuid():N}");

    public void Dispose()
    {
        foreach (var p in new[] { _image, ImageMap.PathFor(_image) })
            try { File.Delete(p); } catch { }
        try { Directory.Delete(_target, recursive: true); } catch { }
    }

    private static byte[] Pattern(int length, int seed)
    {
        var data = new byte[length];
        for (int i = 0; i < length; i++) data[i] = (byte)((i * 7 + seed) % 251 + 1);   // ללא אפסים
        return data;
    }

    /// <summary>קובץ בתמונה "גולמית": כמו בסריקה מתקדמת, אשכול הוא סקטור.</summary>
    private static RecoveredFile File_(long id, string name, string path, long size, long sector, long sectors)
        => new()
        {
            Id = id, Name = name, Path = path, Size = size,
            Quality = RecoveryQuality.Excellent, Content = ContentCheck.HasData,
            Extents = new List<DataExtent> { new(sector, sectors, false) },
        };

    private async Task<RecoveryReport> Recover(byte[] image, params RecoveredFile[] files)
    {
        File.WriteAllBytes(_image, image);
        new ImageMap { Kind = "partition", Size = image.Length, Complete = true }.Save(ImageMap.PathFor(_image));
        var disk = ImageDisk.Open(_image);
        try
        {
            return await RecoveryWriter.RecoverAsync(
                FileSystemKind.Raw, disk.DiskNumber, 0, image.Length, Sector, files,
                new RecoveryOptions { TargetFolder = _target, PreservePaths = true },
                progress: null, CancellationToken.None);
        }
        finally
        {
            ImageDisk.Close(disk.DiskNumber);
        }
    }

    [Fact]
    public async Task Whole_partial_and_empty_files_end_up_where_they_belong_with_a_report()
    {
        byte[] image = new byte[ImageSectors * Sector];
        byte[] whole = Pattern(2000, 1);
        whole.CopyTo(image, 10 * Sector);                                   // קובץ שלם בסקטורים 10–13
        Pattern(4 * Sector, 2).CopyTo(image, 60 * Sector);                 // תחילת קובץ שממשיך מעבר לסוף התמונה
        // סקטורים 20–25 נשארים אפסים — קובץ שתוכנו נמחק

        var report = await Recover(image,
            File_(1, "דוח.txt", "מסמכים", 2000, 10, 4),
            File_(2, "ריק.bin", "מסמכים", 3000, 20, 6),
            File_(3, "תמונה.jpg", @"תמונות\2025", 10 * Sector, 60, 10));

        // קובץ שלם — במקומו, זהה בית-בית, והגיבוב בדוח הוא של התוכן האמיתי.
        string wholePath = Path.Combine(_target, "מסמכים", "דוח.txt");
        Assert.Equal(whole, File.ReadAllBytes(wholePath));
        var wholeEntry = report.Entries.Single(e => e.OriginalPath == @"מסמכים\דוח.txt");
        Assert.Equal(RecoveryStatus.Recovered, wholeEntry.Status);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(whole)).ToLowerInvariant(), wholeEntry.Sha256);

        // קובץ ריק — לא נכתב כלל.
        Assert.False(File.Exists(Path.Combine(_target, "מסמכים", "ריק.bin")));
        Assert.Equal(RecoveryStatus.Empty, report.Entries.Single(e => e.OriginalPath.EndsWith("ריק.bin")).Status);

        // קובץ חלקי — עבר ל-_חלקיים עם מבנה התיקיות שלו, ולא נשאר במקום המקורי.
        string partialPath = Path.Combine(_target, RecoveryWriter.PartialFolderName, "תמונות", "2025", "תמונה.jpg");
        Assert.True(File.Exists(partialPath), "הקובץ החלקי אמור להיות בתיקיית החלקיים");
        Assert.False(File.Exists(Path.Combine(_target, "תמונות", "2025", "תמונה.jpg")));
        var partialEntry = report.Entries.Single(e => e.OriginalPath.EndsWith("תמונה.jpg"));
        Assert.Equal(RecoveryStatus.Partial, partialEntry.Status);
        Assert.Equal(partialPath, partialEntry.Destination);
        Assert.Equal(Path.Combine(_target, RecoveryWriter.PartialFolderName), report.PartialFolder);

        Assert.Equal(2, report.Succeeded);
        Assert.Single(report.PartialFiles);
        Assert.Equal(1, report.EmptyFiles);
    }

    [Fact]
    public async Task Report_is_a_csv_excel_opens_in_hebrew_with_one_row_per_file()
    {
        byte[] image = new byte[ImageSectors * Sector];
        Pattern(600, 3).CopyTo(image, 5 * Sector);

        var report = await Recover(image,
            File_(1, "a,\"b\".txt", "", 600, 5, 2),                  // פסיק ומירכאות בשם
            File_(2, "=SUM(A1).txt", "", 600, 5, 2));                // שם שאקסל היה מריץ כנוסחה

        Assert.NotNull(report.ReportPath);
        Assert.StartsWith(_target, report.ReportPath);

        byte[] raw = File.ReadAllBytes(report.ReportPath!);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, raw[..3]);          // BOM — אחרת Excel מציג ג'יבריש

        string[] lines = Encoding.UTF8.GetString(raw[3..]).TrimEnd().Split("\r\n");
        Assert.StartsWith("נתיב מקורי,", lines[0]);
        Assert.Equal(3, lines.Length);                                      // כותרת + שני קבצים
        Assert.StartsWith("\"a,\"\"b\"\".txt\",", lines[1]);
        Assert.StartsWith("'=SUM(A1).txt,", lines[2]);
        Assert.Contains(",שוחזר,", lines[1]);
    }

    [Fact]
    public async Task A_second_recovery_to_the_same_folder_keeps_the_first_report()
    {
        byte[] image = new byte[ImageSectors * Sector];
        Pattern(600, 4).CopyTo(image, 5 * Sector);

        var first = await Recover(image, File_(1, "x.txt", "", 600, 5, 2));
        var second = await Recover(image, File_(1, "x.txt", "", 600, 5, 2));

        Assert.NotEqual(first.ReportPath, second.ReportPath);
        Assert.True(File.Exists(first.ReportPath));
        Assert.True(File.Exists(second.ReportPath));
    }

    [Fact]
    public async Task Readme_explains_the_folder_and_a_second_recovery_adds_its_own_section()
    {
        byte[] image = new byte[ImageSectors * Sector];
        Pattern(600, 5).CopyTo(image, 5 * Sector);
        Pattern(4 * Sector, 6).CopyTo(image, 60 * Sector);

        await Recover(image, File_(1, "x.txt", "", 600, 5, 2));
        string readme = Path.Combine(_target, RecoveryWriter.ReadmeName);
        string once = File.ReadAllText(readme, Encoding.UTF8);

        Assert.Contains("שוחזר קובץ אחד (600 בתים).", once);
        Assert.DoesNotContain(RecoveryWriter.PartialFolderName, once);   // אין חלקיים — אין הסבר עליהם
        Assert.Contains("RAF-report-", once);

        await Recover(image, File_(1, "y.jpg", "", 10 * Sector, 60, 10));
        string twice = File.ReadAllText(readme, Encoding.UTF8);

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(twice, "הסבר על התיקייה הזו"));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(twice, "^שחזור מ-", System.Text.RegularExpressions.RegexOptions.Multiline).Count);
        Assert.Contains("שוחזר קובץ אחד (5 KB), חלקית.", twice);
        Assert.Contains(RecoveryWriter.PartialFolderName + " —", twice);
        Assert.True(twice.IndexOf(", חלקית.", StringComparison.Ordinal)
                    < twice.IndexOf("(600 בתים)", StringComparison.Ordinal), "השחזור החדש אמור להופיע ראשון");
    }
}
