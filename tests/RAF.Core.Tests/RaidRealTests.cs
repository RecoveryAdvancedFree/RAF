using System.Security.Cryptography;
using RAF.Core.FileSystems;
using RAF.Core.Imaging;
using RAF.Core.Model;
using RAF.Core.Recovery;
using Xunit;
using Xunit.Abstractions;

namespace RAF.Core.Tests;

/// <summary>
/// מערכי RAID שנבנו ב-mdadm של לינוקס אמיתי (make.sh בתיקייה C:\raidtest-raf, דרך WSL): כל כונן
/// בקובץ נפרד, ובכל מערך ext4 עם קבצים וקובץ שנמחק. הבדיקה פותחת את הכוננים, מוצאת את המערך,
/// מרכיבה, סורקת ומשווה טביעות אצבע — גם כשכונן אחד חסר. בלי התיקייה היא מדלגת.
/// </summary>
public class RaidRealTests
{
    private const string Folder = @"C:\raidtest-raf";
    private readonly ITestOutputHelper _out;

    public RaidRealTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData("raid0", 3, "RAID 0", -1)]
    [InlineData("raid1", 2, "RAID 1", -1)]
    [InlineData("raid1", 2, "RAID 1", 0)]
    [InlineData("raid5", 4, "RAID 5", -1)]
    [InlineData("raid5", 4, "RAID 5", 1)]
    [InlineData("raid5la", 3, "RAID 5", -1)]
    [InlineData("raid5la", 3, "RAID 5", 2)]
    [InlineData("raid6", 4, "RAID 6", -1)]
    [InlineData("raid6", 4, "RAID 6", 0)]
    [InlineData("raid6", 4, "RAID 6", 3)]
    [InlineData("raid10", 4, "RAID 10", -1)]
    [InlineData("raid10", 4, "RAID 10", 2)]
    [InlineData("linear", 2, "שרשור (JBOD)", -1)]
    [InlineData("raid5v090", 3, "RAID 5", -1)]
    [InlineData("raid5v090", 3, "RAID 5", 0)]
    [InlineData("raid1v10", 2, "RAID 1", -1)]
    public async Task Real_md_array_is_found_assembled_and_recovered(string name, int members, string level, int leaveOut)
    {
        if (!File.Exists(Path.Combine(Folder, $"{name}-0.img"))) return;
        var expected = File.ReadAllLines(Path.Combine(Folder, "manifest.txt"))
            .Select(l => l.Split('|')).Where(p => p[0] == name).ToList();

        var disks = Enumerable.Range(0, members).Where(i => i != leaveOut)
            .Select(i => ImageDisk.Open(Path.Combine(Folder, $"{name}-{i}.img"))).ToList();
        PhysicalDiskInfo? array = null;
        string target = Path.Combine(Path.GetTempPath(), "raf-raid-" + Guid.NewGuid().ToString("N"));
        try
        {
            // כונן עם כותרת 1.2 (ברירת המחדל) מסומן ברשימה כחלק ממערך. בכותרות שבסוף הכונן
            // (0.90, 1.0) תחילת הכונן היא נתונים רגילים — רק החיפוש מגלה אותן.
            if (!name.Contains('v'))
                Assert.All(disks, d => Assert.Equal(FileSystemKind.LinuxRaid, d.Partitions.Single().FileSystem));

            var found = Assert.Single(RaidDisk.Find(disks));
            _out.WriteLine($"{found.Level} · {found.Disks} disks · chunk {found.Chunk} · {found.Size:N0} bytes · missing [{string.Join(",", found.MissingRoles)}] · {found.Problem}");
            Assert.Equal(level, found.Level);
            Assert.Equal(members, found.Disks);
            Assert.Equal(disks.Count, found.Members.Count);
            Assert.Null(found.Problem);

            array = RaidDisk.Assemble(found);
            _out.WriteLine(array.ImageNote);
            var part = array.Partitions.Single();
            Assert.Equal(FileSystemKind.Ext, part.FileSystem);

            var result = await VolumeScanner.ScanAsync(part.FileSystem, array.DiskNumber, part.OffsetBytes, part.SizeBytes,
                array.LogicalSectorSize, ScanMode.Quick, includeExisting: true, TrimState.NotSupported, null, default);

            foreach (var p in expected)
            {
                string fileName = p[2][(p[2].LastIndexOf('/') + 1)..];
                bool deleted = p[1] == "deleted";
                var file = result.Files.FirstOrDefault(f => f.Name == fileName && f.IsDeleted == deleted && f.Quality != RecoveryQuality.Unrecoverable);
                _out.WriteLine($"{p[1]} {p[2]}: {(file is null ? "not found" : file.Quality.ToString())}");
                if (file is null) { Assert.True(deleted, $"{p[2]} not found"); continue; }

                string dir = Path.Combine(target, Guid.NewGuid().ToString("N"));
                var report = await RecoveryWriter.RecoverAsync(part.FileSystem, array.DiskNumber, part.OffsetBytes, part.SizeBytes,
                    array.LogicalSectorSize, new[] { file }, new RecoveryOptions { TargetFolder = dir, PreservePaths = false }, null, default);
                Assert.Equal(1, report.Succeeded);
                string got = Directory.GetFiles(dir, fileName, SearchOption.AllDirectories).Single();
                Assert.Equal(p[3], Convert.ToHexString(MD5.HashData(File.ReadAllBytes(got))).ToLowerInvariant());
            }
        }
        finally
        {
            if (array is not null) ImageDisk.Close(array.DiskNumber);
            foreach (var d in disks) ImageDisk.Close(d.DiskNumber);
            if (Directory.Exists(target)) Directory.Delete(target, true);
        }
    }

    [Theory]
    [InlineData("raid0", 3, 1)]
    [InlineData("linear", 2, 0)]
    public void Array_without_redundancy_is_not_assembled_with_a_missing_disk(string name, int members, int leaveOut)
    {
        if (!File.Exists(Path.Combine(Folder, $"{name}-0.img"))) return;
        var disks = Enumerable.Range(0, members).Where(i => i != leaveOut)
            .Select(i => ImageDisk.Open(Path.Combine(Folder, $"{name}-{i}.img"))).ToList();
        try
        {
            var found = Assert.Single(RaidDisk.Find(disks));
            Assert.Equal(new[] { leaveOut }, found.MissingRoles);
            Assert.NotNull(found.Problem);
            Assert.Throws<InvalidOperationException>(() => RaidDisk.Assemble(found));
        }
        finally
        {
            foreach (var d in disks) ImageDisk.Close(d.DiskNumber);
        }
    }
}
