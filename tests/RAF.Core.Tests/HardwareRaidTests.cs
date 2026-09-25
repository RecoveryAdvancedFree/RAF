using System.Security.Cryptography;
using RAF.Core.FileSystems;
using RAF.Core.Imaging;
using RAF.Core.Model;
using RAF.Core.Recovery;
using Xunit;
using Xunit.Abstractions;

namespace RAF.Core.Tests;

/// <summary>
/// מערכים בלי כותרת, כמו של כרטיס RAID (make.sh בתיקייה C:\hwraidtest-raf: נבנו בלינוקס והכותרת נמחקה).
/// הבדיקה נותנת לתוכנה את הכוננים בסדר אלפביתי — לא בסדר האמיתי — ובודקת שהיא מזהה סוג, רצועה,
/// סידור וסדר, מרכיבה, ושהקבצים חוזרים זהים. בלי התיקייה היא מדלגת.
/// </summary>
public class HardwareRaidTests : IDisposable
{
    private const string Folder = @"C:\hwraidtest-raf";
    private readonly ITestOutputHelper _out;
    private readonly List<int> _open = new();
    private readonly string _target = Path.Combine(Path.GetTempPath(), "raf-hw-" + Guid.NewGuid().ToString("N"));

    public HardwareRaidTests(ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        foreach (int n in Enumerable.Reverse(_open)) ImageDisk.Close(n);
        if (Directory.Exists(_target)) Directory.Delete(_target, true);
    }

    [Theory]
    [InlineData("r0ntfs")]
    [InlineData("r5ntfs")]
    [InlineData("r5ext")]
    [InlineData("r0ext")]
    [InlineData("r1ntfs")]
    public async Task Array_without_a_header_is_detected_assembled_and_recovered(string name)
    {
        if (!File.Exists(Path.Combine(Folder, "order.txt"))) return;
        var truth = File.ReadAllLines(Path.Combine(Folder, "order.txt")).Select(l => l.Split('|')).Single(p => p[0] == name);
        string[] letters = truth[4].Split(' ');

        var disks = letters.OrderBy(l => l).Select(l =>
        {
            var d = ImageDisk.Open(Path.Combine(Folder, $"{name}-disk-{l}.img"));
            _open.Add(d.DiskNumber);
            return (Letter: l, Disk: d);
        }).ToList();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var found = RaidDisk.Detect(disks.Select(d => d.Disk).ToList());
        _out.WriteLine($"{clock.ElapsedMilliseconds} ms");
        foreach (var f in found)
            _out.WriteLine($"{f.Level} layout {f.Layout} chunk {f.Chunk / 1024}K order {string.Join(",", f.Order.Select(n => disks.Single(d => d.Disk.DiskNumber == n).Letter))} {f.FileSystem} confident={f.Confident}");

        var best = found.First();
        Assert.True(best.Confident);
        Assert.Equal(int.Parse(truth[1]), best.LevelNumber);
        if (best.LevelNumber != 1)
        {
            Assert.Equal(long.Parse(truth[2]) * 1024, best.Chunk);
            Assert.Equal(letters, best.Order.Select(n => disks.Single(d => d.Disk.DiskNumber == n).Letter));
        }

        var array = RaidDisk.Assemble(best, disks.Select(d => d.Disk).ToList());
        _open.Add(array.DiskNumber);
        var part = array.Partitions.Single();
        var result = await VolumeScanner.ScanAsync(part.FileSystem, array.DiskNumber, part.OffsetBytes, part.SizeBytes,
            array.LogicalSectorSize, ScanMode.Quick, includeExisting: true, TrimState.NotSupported, null, default);

        foreach (var p in File.ReadAllLines(Path.Combine(Folder, "manifest.txt")).Select(l => l.Split('|')).Where(p => p[0] == name))
        {
            string fileName = p[2][(p[2].LastIndexOf('/') + 1)..];
            var file = result.Files.First(f => f.Name == fileName && !f.IsDeleted);
            string dir = Path.Combine(_target, Guid.NewGuid().ToString("N"));
            var report = await RecoveryWriter.RecoverAsync(part.FileSystem, array.DiskNumber, part.OffsetBytes, part.SizeBytes,
                array.LogicalSectorSize, new[] { file }, new RecoveryOptions { TargetFolder = dir, PreservePaths = false }, null, default);
            Assert.Equal(1, report.Succeeded);
            string got = Directory.GetFiles(dir, fileName, SearchOption.AllDirectories).Single();
            Assert.Equal(p[3], Convert.ToHexString(MD5.HashData(File.ReadAllBytes(got))).ToLowerInvariant());
        }
    }
}
