using System.IO.Compression;
using System.Text;
using RAF.Core.FileSystems;
using RAF.Core.Imaging;
using RAF.Core.Model;
using RAF.Core.Recovery;
using Xunit;

namespace RAF.Core.Tests;

/// <summary>
/// כונני לינוקס אמיתיים (ext2, ext4 ו-XFS שנוצרו ונכתבו בלינוקס) מנתוני הבדיקה של dfVFS —
/// ראו Media/linux-images.txt. בכל אחד: a_directory/a_file, a_directory/another_file ו-passwords.txt.
/// </summary>
public class LinuxDriveTests : IDisposable
{
    private readonly List<string> _temp = new();

    internal static string Unpack(string name, List<string> temp)
    {
        string path = Path.Combine(Path.GetTempPath(), $"raf-{Guid.NewGuid():N}-{name}");
        using (var input = new GZipStream(File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Media", name + ".gz")), CompressionMode.Decompress))
        using (var output = File.Create(path))
            input.CopyTo(output);
        temp.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var f in _temp)
        {
            try { if (Directory.Exists(f)) Directory.Delete(f, true); else File.Delete(f); } catch (IOException) { }
        }
    }

    [Theory]
    [InlineData("ext2.raw", "ext2")]
    [InlineData("ext4.raw", "ext4")]
    [InlineData("xfs.raw", "XFS")]
    [InlineData("xfs-bigtime.raw", "XFS")]
    public async Task A_linux_drive_is_read_with_names_folders_and_content(string image, string version)
    {
        var disk = ImageDisk.Open(Unpack(image, _temp));
        try
        {
            var part = disk.Partitions.Single();
            Assert.Equal(version == "XFS" ? FileSystemKind.Xfs : FileSystemKind.Ext, part.FileSystem);
            Assert.True(VolumeScanner.IsSupported(part.FileSystem));

            var result = await VolumeScanner.ScanAsync(part.FileSystem, disk.DiskNumber, part.OffsetBytes, part.SizeBytes,
                disk.LogicalSectorSize, ScanMode.Quick, includeExisting: true, TrimState.NotSupported, null, default);

            Assert.Equal(version, result.FileSystem);
            var aFile = Assert.Single(result.Files, f => f.Name == "a_file");
            Assert.Equal("a_directory", aFile.Path);
            Assert.Contains(result.Files, f => f.Name == "another_file" && f.Path == "a_directory");
            var passwords = Assert.Single(result.Files, f => f.Name == "passwords.txt");
            Assert.Equal("", passwords.Path);
            Assert.False(passwords.IsDeleted);
            Assert.Equal(RecoveryQuality.Excellent, passwords.Quality);
            Assert.NotNull(passwords.Modified);

            // קיצור הדרך a_link אינו קובץ עם תוכן — הוא לא מוצג.
            Assert.DoesNotContain(result.Files, f => f.Name == "a_link");

            string target = Path.Combine(Path.GetTempPath(), "raf-ext-" + Guid.NewGuid().ToString("N"));
            _temp.Add(target);
            var report = await RecoveryWriter.RecoverAsync(part.FileSystem, disk.DiskNumber, part.OffsetBytes, part.SizeBytes,
                disk.LogicalSectorSize, new[] { aFile, passwords }, new RecoveryOptions { TargetFolder = target }, null, default);
            Assert.Equal(2, report.Succeeded);

            string text = File.ReadAllText(Path.Combine(target, "a_directory", "a_file"), Encoding.UTF8);
            Assert.Equal("This is a text file.\n\nWe should be able to parse it.\n", text);
            Assert.StartsWith("place,user,password\nbank,joesmith,superrich", File.ReadAllText(Path.Combine(target, "passwords.txt")));
        }
        finally { ImageDisk.Close(disk.DiskNumber); }
    }

    [Fact]
    public async Task Advanced_scan_of_a_linux_drive_skips_the_space_that_files_occupy()
    {
        var disk = ImageDisk.Open(Unpack("ext4.raw", _temp));
        try
        {
            var part = disk.Partitions.Single();
            var result = await VolumeScanner.ScanAsync(part.FileSystem, disk.DiskNumber, part.OffsetBytes, part.SizeBytes,
                disk.LogicalSectorSize, ScanMode.Advanced, includeExisting: false, TrimState.NotSupported, null, default,
                freeSpaceOnly: true);
            Assert.False(result.Cancelled);
            Assert.Contains(result.Warnings, w => w.Contains("נסרק רק המקום הפנוי"));
        }
        finally { ImageDisk.Close(disk.DiskNumber); }
    }
}
