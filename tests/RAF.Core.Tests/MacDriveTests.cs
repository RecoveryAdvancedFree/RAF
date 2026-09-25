using System.Text;
using RAF.Core.FileSystems;
using RAF.Core.Imaging;
using RAF.Core.Model;
using RAF.Core.Recovery;
using Xunit;
using Xunit.Abstractions;

namespace RAF.Core.Tests;

/// <summary>
/// כונני מק אמיתיים (נוצרו ונכתבו ב-macOS) מנתוני הבדיקה של dfVFS — ראו Media/linux-images.txt.
/// בכל אחד: a_directory/a_file, a_directory/another_file ו-passwords.txt.
/// </summary>
public class MacDriveTests : IDisposable
{
    private readonly List<string> _temp = new();
    private readonly ITestOutputHelper _out;

    public MacDriveTests(ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        foreach (var f in _temp)
            try { if (Directory.Exists(f)) Directory.Delete(f, true); else File.Delete(f); } catch (IOException) { }
    }

    [Theory]
    [InlineData("hfsplus.raw", FileSystemKind.Hfs)]
    [InlineData("apfs.raw", FileSystemKind.Apfs)]
    public async Task A_mac_drive_is_read_with_names_folders_and_content(string image, FileSystemKind kind)
    {
        var disk = ImageDisk.Open(LinuxDriveTests.Unpack(image, _temp));
        try
        {
            var part = disk.Partitions.Single();
            Assert.Equal(kind, part.FileSystem);
            var result = await VolumeScanner.ScanAsync(part.FileSystem, disk.DiskNumber, part.OffsetBytes, part.SizeBytes,
                disk.LogicalSectorSize, ScanMode.Quick, includeExisting: true, TrimState.NotSupported, null, default);
            foreach (var f in result.Files) _out.WriteLine($"{(f.IsDeleted ? "deleted " : "")}'{f.Path}' {f.Name} {f.Size} {f.Quality}");
            foreach (var w in result.Warnings) _out.WriteLine(w);

            var aFile = Assert.Single(result.Files, f => f.Name == "a_file" && !f.IsDeleted);
            Assert.Equal("a_directory", aFile.Path);
            Assert.Contains(result.Files, f => f.Name == "another_file" && f.Path == "a_directory");
            var passwords = Assert.Single(result.Files, f => f.Name == "passwords.txt" && !f.IsDeleted);
            Assert.Equal("", passwords.Path);
            Assert.NotNull(passwords.Modified);
            Assert.DoesNotContain(result.Files, f => f.Name == "a_link");   // קיצור דרך — לא קובץ
            Assert.DoesNotContain(result.Files, f => f.IsDeleted);          // אין בכונן קבצים שנמחקו — ואין "מציאות" שווא

            // סריקה עמוקה (ב-APFS — עוברת על כל הכונן): אין קבצים שנמחקו, ואסור שקובץ קיים יופיע כ"נמחק".
            var deep = await VolumeScanner.ScanAsync(part.FileSystem, disk.DiskNumber, part.OffsetBytes, part.SizeBytes,
                disk.LogicalSectorSize, ScanMode.Deep, includeExisting: true, TrimState.NotSupported, null, default);
            foreach (var f in deep.Files.Where(f => f.IsDeleted)) _out.WriteLine($"deep: deleted '{f.Path}' {f.Name} {f.Size} {f.Quality}");
            Assert.DoesNotContain(deep.Files, f => f.IsDeleted && result.Files.Any(l => l.Name == f.Name && l.Path == f.Path));

            string target = Path.Combine(Path.GetTempPath(), "raf-mac-" + Guid.NewGuid().ToString("N"));
            _temp.Add(target);
            var report = await RecoveryWriter.RecoverAsync(part.FileSystem, disk.DiskNumber, part.OffsetBytes, part.SizeBytes,
                disk.LogicalSectorSize, new[] { aFile, passwords }, new RecoveryOptions { TargetFolder = target }, null, default);
            Assert.Equal(2, report.Succeeded);
            Assert.Equal("This is a text file.\n\nWe should be able to parse it.\n",
                File.ReadAllText(Path.Combine(target, "a_directory", "a_file"), Encoding.UTF8));
            Assert.StartsWith("place,user,password\nbank,joesmith,superrich", File.ReadAllText(Path.Combine(target, "passwords.txt")));
        }
        finally { ImageDisk.Close(disk.DiskNumber); }
    }
}
