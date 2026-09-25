using System.Security.Cryptography;
using RAF.Core.FileSystems;
using RAF.Core.Imaging;
using RAF.Core.Model;
using RAF.Core.Recovery;
using Xunit;
using Xunit.Abstractions;

namespace RAF.Core.Tests;

/// <summary>
/// מאגרים לוגיים שנבנו בכלי המאגרים של לינוקס אמיתי (make.sh בתיקייה C:\lvmtest-raf, דרך WSL):
/// מאגר על שני כוננים (אזור רציף שעובר מכונן לכונן, ואזור לסירוגין), מאגר מעל מערך עם בקרה —
/// כמו בשרת אחסון ביתי — ואזור דליל. בלי התיקייה הבדיקה מדלגת.
/// </summary>
public class LvmRealTests : IDisposable
{
    private const string Folder = @"C:\lvmtest-raf";
    private readonly ITestOutputHelper _out;
    private readonly List<int> _open = new();
    private readonly string _target = Path.Combine(Path.GetTempPath(), "raf-lvm-" + Guid.NewGuid().ToString("N"));

    public LvmRealTests(ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        foreach (int n in Enumerable.Reverse(_open)) ImageDisk.Close(n);
        if (Directory.Exists(_target)) Directory.Delete(_target, true);
    }

    private List<PhysicalDiskInfo> Open(string name, params int[] members)
    {
        var disks = members.Select(i => ImageDisk.Open(Path.Combine(Folder, $"{name}-{i}.img"))).ToList();
        _open.AddRange(disks.Select(d => d.DiskNumber));
        return disks;
    }

    private async Task Verify(PhysicalDiskInfo volume, string name)
    {
        _out.WriteLine(volume.ImageNote);
        var part = volume.Partitions.Single();
        Assert.Equal(FileSystemKind.Ext, part.FileSystem);
        var result = await VolumeScanner.ScanAsync(part.FileSystem, volume.DiskNumber, part.OffsetBytes, part.SizeBytes,
            volume.LogicalSectorSize, ScanMode.Quick, includeExisting: true, TrimState.NotSupported, null, default);

        foreach (var p in File.ReadAllLines(Path.Combine(Folder, "manifest.txt")).Select(l => l.Split('|')).Where(p => p[0] == name))
        {
            string fileName = p[2][(p[2].LastIndexOf('/') + 1)..];
            bool deleted = p[1] == "deleted";
            var file = result.Files.FirstOrDefault(f => f.Name == fileName && f.IsDeleted == deleted && f.Quality != RecoveryQuality.Unrecoverable);
            _out.WriteLine($"{p[1]} {p[2]}: {(file is null ? "not found" : file.Quality.ToString())}");
            Assert.True(file is not null, $"{p[2]} not found");

            string dir = Path.Combine(_target, Guid.NewGuid().ToString("N"));
            var report = await RecoveryWriter.RecoverAsync(part.FileSystem, volume.DiskNumber, part.OffsetBytes, part.SizeBytes,
                volume.LogicalSectorSize, new[] { file }, new RecoveryOptions { TargetFolder = dir, PreservePaths = false }, null, default);
            Assert.Equal(1, report.Succeeded);
            string got = Directory.GetFiles(dir, fileName, SearchOption.AllDirectories).Single();
            Assert.Equal(p[3], Convert.ToHexString(MD5.HashData(File.ReadAllBytes(got))).ToLowerInvariant());
        }
    }

    [Theory]
    [InlineData("data")]   // רציף, עובר מכונן לכונן באמצע
    [InlineData("fast")]   // לסירוגין על שני הכוננים
    public async Task Volume_spread_over_two_drives_is_opened_and_recovered(string volumeName)
    {
        if (!File.Exists(Path.Combine(Folder, "plain-0.img"))) return;
        var disks = Open("plain", 0, 1);
        Assert.All(disks, d => Assert.Equal(FileSystemKind.Lvm, d.Partitions.Single().FileSystem));

        var group = Assert.Single(LvmDisk.Find(disks));
        Assert.Equal("raftplain", group.Name);
        Assert.Empty(group.Missing);
        Assert.Equal(new[] { "data", "fast" }, group.Volumes.Select(v => v.Name).OrderBy(n => n));

        var volume = LvmDisk.Open(group, volumeName);
        _open.Add(volume.DiskNumber);
        await Verify(volume, volumeName);
    }

    [Fact]
    public void A_missing_drive_is_reported_and_its_volumes_are_not_opened()
    {
        if (!File.Exists(Path.Combine(Folder, "plain-0.img"))) return;
        var group = Assert.Single(LvmDisk.Find(Open("plain", 0)));
        Assert.Single(group.Missing);
        Assert.All(group.Volumes, v => Assert.NotNull(v.Problem));
        Assert.Throws<InvalidOperationException>(() => LvmDisk.Open(group, "data"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]   // גם כשחסר כונן במערך שמתחת
    public async Task Home_storage_server_layout_raid_then_volume_is_opened_and_recovered(int leaveOut)
    {
        if (!File.Exists(Path.Combine(Folder, "nas-0.img"))) return;
        var members = Open("nas", new[] { 0, 1, 2 }.Where(i => i != leaveOut).ToArray());
        var array = RaidDisk.Assemble(Assert.Single(RaidDisk.Find(members)));
        _open.Add(array.DiskNumber);
        Assert.Equal(FileSystemKind.Lvm, array.Partitions.Single().FileSystem);

        var group = Assert.Single(LvmDisk.Find(members.Append(array)));
        Assert.Equal("raftnas", group.Name);
        var volume = LvmDisk.Open(group, "volume1");
        _open.Add(volume.DiskNumber);
        await Verify(volume, "volume1");

        // סגירת כונן במערך סוגרת גם את המערך וגם את האזור שבנוי עליו.
        Assert.Equal(new[] { array.DiskNumber, volume.DiskNumber },
            RAF.Core.Native.DevicePaths.DependentsOf(members[0].DiskNumber));
    }

    [Fact]
    public void Thin_volume_is_reported_as_not_supported_yet()
    {
        if (!File.Exists(Path.Combine(Folder, "thin-0.img"))) return;
        var group = Assert.Single(LvmDisk.Find(Open("thin", 0)));
        var thin = Assert.Single(group.Volumes, v => v.Name == "thinvol");
        _out.WriteLine(string.Join(" | ", group.Volumes.Select(v => $"{v.Name}: {v.Problem}")));
        Assert.Contains("דליל", thin.Problem);
    }
}
