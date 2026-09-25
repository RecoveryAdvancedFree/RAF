using System.Security.Cryptography;
using RAF.Core.FileSystems;
using RAF.Core.Imaging;
using RAF.Core.Model;
using RAF.Core.Recovery;
using Xunit;
using Xunit.Abstractions;

namespace RAF.Core.Tests;

/// <summary>
/// כונני btrfs שנבנו בלינוקס אמיתי (make.sh בתיקייה C:\btrfstest-raf, דרך WSL): כונן אחד, ומבנה של
/// Synology — מערך מראה, עליו מאגר לוגי, ובתוכו btrfs. בכל אחד: קובץ זעיר, קובץ מפוצל, קובץ דחוס,
/// "תיקייה משותפת", וקבצים שנמחקו. בלי התיקייה הבדיקה מדלגת.
/// </summary>
public class BtrfsRealTests : IDisposable
{
    private const string Folder = @"C:\btrfstest-raf";
    private readonly ITestOutputHelper _out;
    private readonly List<int> _open = new();
    private readonly string _target = Path.Combine(Path.GetTempPath(), "raf-btrfs-" + Guid.NewGuid().ToString("N"));

    public BtrfsRealTests(ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        foreach (int n in Enumerable.Reverse(_open)) ImageDisk.Close(n);
        if (Directory.Exists(_target)) Directory.Delete(_target, true);
    }

    private PhysicalDiskInfo Open(string file)
    {
        var disk = ImageDisk.Open(Path.Combine(Folder, file));
        _open.Add(disk.DiskNumber);
        return disk;
    }

    /// <summary>סריקה והשוואה. מחזיר כמה קבצים שנמחקו חזרו זהים.</summary>
    private async Task<int> Verify(PhysicalDiskInfo disk, string name)
    {
        var part = disk.Partitions.Single();
        Assert.Equal(FileSystemKind.Btrfs, part.FileSystem);
        Assert.Equal(name, part.Label);
        var result = await VolumeScanner.ScanAsync(part.FileSystem, disk.DiskNumber, part.OffsetBytes, part.SizeBytes,
            disk.LogicalSectorSize, ScanMode.Quick, includeExisting: true, TrimState.NotSupported, null, default);
        _out.WriteLine($"{result.Files.Count} files · {string.Join(" | ", result.Warnings)}");

        int deletedOk = 0;
        foreach (var p in File.ReadAllLines(Path.Combine(Folder, "manifest.txt")).Select(l => l.Split('|')).Where(p => p[0] == name))
        {
            string fileName = p[2][(p[2].LastIndexOf('/') + 1)..];
            string folder = p[2].Contains('/') ? p[2][..p[2].LastIndexOf('/')].Replace('/', '\\') : "";
            bool deleted = p[1] == "deleted";
            if (p[1] == "zstd")
            {
                // דחיסה שעוד לא נתמכת: הקובץ מוצג, עם הסבר — ולא כאילו אפשר לשחזר אותו.
                var zstd = Assert.Single(result.Files, f => f.Name == fileName && f.Path == folder);
                Assert.Equal(RecoveryQuality.Unrecoverable, zstd.Quality);
                Assert.Contains("zstd", zstd.QualityReason);
                continue;
            }
            var file = result.Files.FirstOrDefault(f => f.Name == fileName && f.Path == folder && f.IsDeleted == deleted && f.Quality != RecoveryQuality.Unrecoverable);
            _out.WriteLine($"{p[1]} {p[2]}: {(file is null ? "not found" : $"{file.Quality} '{file.Path}'")}");
            Assert.True(file is not null, $"{p[2]} not found");
            Assert.Equal(folder, file.Path);

            string dir = Path.Combine(_target, Guid.NewGuid().ToString("N"));
            var report = await RecoveryWriter.RecoverAsync(part.FileSystem, disk.DiskNumber, part.OffsetBytes, part.SizeBytes,
                disk.LogicalSectorSize, new[] { file }, new RecoveryOptions { TargetFolder = dir, PreservePaths = false }, null, default);
            Assert.True(report.Succeeded == 1, $"{report.Entries[0].Status}: {report.Entries[0].Reason}");
            string got = Directory.GetFiles(dir, fileName, SearchOption.AllDirectories).Single();
            Assert.Equal(p[3], Convert.ToHexString(MD5.HashData(File.ReadAllBytes(got))).ToLowerInvariant());
            if (deleted) deletedOk++;
        }
        return deletedOk;
    }

    [Fact]
    public async Task Single_drive_btrfs_is_read_with_names_folders_and_content()
    {
        if (!File.Exists(Path.Combine(Folder, "plain.img"))) return;
        await Verify(Open("plain.img"), "plain");
    }

    [Fact]
    public async Task Synology_layout_mirror_then_pool_then_btrfs_is_read()
    {
        if (!File.Exists(Path.Combine(Folder, "syno-0.img"))) return;
        var members = new[] { Open("syno-0.img"), Open("syno-1.img") };
        var array = RaidDisk.Assemble(Assert.Single(RaidDisk.Find(members)));
        _open.Add(array.DiskNumber);
        var group = Assert.Single(LvmDisk.Find(members.Append(array)));
        var volume = LvmDisk.Open(group, "lv");
        _open.Add(volume.DiskNumber);
        await Verify(volume, "syno");
    }
}
