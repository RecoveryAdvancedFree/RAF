using System.Buffers.Binary;
using RAF.Core.Carving;
using RAF.Core.Disks;
using RAF.Core.FileSystems.Ntfs;
using RAF.Core.Imaging;
using RAF.Core.Model;
using RAF.Core.Repair;
using Xunit;
using Xunit.Abstractions;

namespace RAF.Core.Tests;

/// <summary>
/// מחיצת NTFS ששני מגזרי האתחול שלה אבדו, וגם טבלת המחיצות — ונבנית מחדש מרשומות הקבצים.
///
/// ntfs-lost.raw.gz: דיסק של 64MB שנוצר ב-Windows (diskpart, format fs=ntfs quick): מחיצה אחת
/// בהיסט 64KB, אשכול 4KB; תיקיות תמונות\חתונה, תמונות\טיול ומסמכים; 40 תמונות JPEG ו-10 PNG
/// שנוצרו בסקריפט, ו-3 קבצים שנמחקו (טיול\צילום-01.jpg, חתונה\צילום-03.jpg, מסמכים\תרשים-05.png).
/// הבדיקה מוחקת בעותק את טבלת המחיצות ואת שני מגזרי האתחול.
/// </summary>
public class NtfsRebuildTests : IDisposable
{
    private const long PartitionOffset = 64 * 1024;
    private readonly List<string> _temp = new();
    private readonly ITestOutputHelper _out;

    public NtfsRebuildTests(ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        foreach (var f in _temp) try { File.Delete(f); } catch (IOException) { }
    }

    /// <summary>הדיסק, אחרי שכל מה שמצביע על המחיצה נמחק: טבלת המחיצות ושני מגזרי האתחול.</summary>
    private string WipedDisk(bool keepTable = false)
    {
        string path = LinuxDriveTests.Unpack("ntfs-lost.raw", _temp);
        using var f = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        byte[] mbr = new byte[512];
        f.ReadExactly(mbr);
        long sectors = BinaryPrimitives.ReadUInt32LittleEndian(mbr.AsSpan(446 + 12));
        Assert.Equal(PartitionOffset, BinaryPrimitives.ReadUInt32LittleEndian(mbr.AsSpan(446 + 8)) * 512L);

        byte[] zero = new byte[512];
        if (!keepTable) { f.Position = 446; f.Write(zero, 0, 64); }               // טבלת המחיצות
        f.Position = PartitionOffset; f.Write(zero);                               // מגזר האתחול
        f.Position = PartitionOffset + (sectors - 1) * 512; f.Write(zero);         // העותק שבסוף המחיצה
        return path;
    }

    [Fact]
    public async Task A_partition_without_any_boot_sector_is_rebuilt_with_names_and_folders()
    {
        var disk = ImageDisk.Open(WipedDisk());
        try
        {
            Assert.DoesNotContain(disk.Partitions, p => p.FileSystem == FileSystemKind.Ntfs);

            var hunt = await PartitionHunter.HuntAsync(disk, null, default);
            foreach (var f in hunt.Found)
                _out.WriteLine($"found {f.Offset} {f.Size} {f.FileSystem} damaged={f.BootSectorDamaged} rebuilt={f.Rebuilt?.ClusterSize}/{f.Rebuilt?.Confirmed}");
            var found = Assert.Single(hunt.Found);
            var rebuilt = Assert.IsType<RebuiltNtfs>(found.Rebuilt);
            _out.WriteLine($"offset {rebuilt.Offset}, cluster {rebuilt.ClusterSize}, MFT {rebuilt.MftCluster}, " +
                           $"size {rebuilt.Size}, records {rebuilt.Records}, confirmed {rebuilt.Confirmed}");

            Assert.Equal(PartitionOffset, found.Offset);
            Assert.Equal(4096, rebuilt.ClusterSize);
            Assert.True(rebuilt.Confirmed >= 20, $"רק {rebuilt.Confirmed} רשומות אושרו מול הקבצים");

            // המחיצה נקראת דרך מגזר האתחול שחושב — שמות, תיקיות וקבצים שנמחקו.
            VirtualRepair.ApplyRebuilt(disk.DiskNumber, rebuilt);
            try
            {
                var result = await NtfsScanner.ScanAsync(disk.DiskNumber, found.Offset, found.Size, 512,
                    ScanMode.Deep, includeExisting: true, TrimState.NotSupported, null, default);
                var files = result.Files.Where(f => !f.IsDirectory).ToList();
                foreach (var w in result.Warnings) _out.WriteLine("warning: " + w);
                _out.WriteLine($"{files.Count} files, {files.Count(f => f.IsDeleted)} deleted");

                Assert.Contains(files, f => f.Name == "צילום-00.jpg" && f.Path.EndsWith(@"תמונות\חתונה") && !f.IsDeleted);
                Assert.Contains(files, f => f.Name == "תרשים-00.png" && f.Path.EndsWith("מסמכים"));
                Assert.Contains(files, f => f.Name == "צילום-01.jpg" && f.Path.EndsWith(@"תמונות\טיול") && f.IsDeleted);
                Assert.Equal(40, files.Count(f => f.Extension == "jpg"));
                Assert.Equal(10, files.Count(f => f.Extension == "png"));
            }
            finally
            {
                VirtualRepair.Remove(disk.DiskNumber, found.Offset);
            }
        }
        finally
        {
            ImageDisk.Close(disk.DiskNumber);
        }
    }

    [Fact]
    public async Task A_listed_partition_that_windows_wants_to_format_is_rebuilt_in_place()
    {
        // טבלת המחיצות שלמה ורק שני מגזרי האתחול אבדו: Windows מציג RAW ומבקש לפרמט,
        // ואין עותק גיבוי לקרוא דרכו. המחיצה נבנית מחדש במקומה, ולא כמחיצה נוספת.
        var disk = ImageDisk.Open(WipedDisk(keepTable: true));
        try
        {
            var listed = Assert.Single(disk.Partitions);
            Assert.True(listed.FileSystem is FileSystemKind.Raw or FileSystemKind.Unknown);

            var hunt = await PartitionHunter.HuntAsync(disk, null, default);
            var found = Assert.Single(hunt.Found);
            Assert.True(found.OnExisting);
            Assert.Equal(listed.OffsetBytes, found.Offset);
            Assert.Equal(4096, found.Rebuilt!.ClusterSize);
        }
        finally
        {
            ImageDisk.Close(disk.DiskNumber);
        }
    }

    [Fact]
    public async Task An_advanced_scan_of_the_area_finds_the_file_table_too()
    {
        // הרעיון המקורי: הסריקה לפי חתימות, שמוצאת קבצים בלי שמות, מוצאת בדרך גם את טבלת
        // הקבצים — וממנה את המחיצה עצמה, עם השמות.
        var disk = ImageDisk.Open(WipedDisk());
        try
        {
            long size = disk.SizeBytes - PartitionOffset;
            var result = await FileCarver.ScanAsync(disk.DiskNumber, PartitionOffset, size, 512, null, default);
            _out.WriteLine($"{result.Files.Count} carved");

            var rebuilt = Assert.Single(result.RebuiltNtfs);
            Assert.Equal(0, rebuilt.Offset);                 // יחסית לאזור שנסרק — תחילתו
            Assert.Equal(4096, rebuilt.ClusterSize);
            Assert.True(rebuilt.Confirmed >= 20);
        }
        finally
        {
            ImageDisk.Close(disk.DiskNumber);
        }
    }

    [Fact]
    public async Task An_advanced_scan_of_a_readable_partition_reports_nothing_to_rebuild()
    {
        var disk = ImageDisk.Open(LinuxDriveTests.Unpack("ntfs-lost.raw", _temp));
        try
        {
            var part = Assert.Single(disk.Partitions);
            var result = await FileCarver.ScanAsync(disk.DiskNumber, part.OffsetBytes, part.SizeBytes, 512, null, default,
                fileSystem: part.FileSystem);
            Assert.Empty(result.RebuiltNtfs);
        }
        finally
        {
            ImageDisk.Close(disk.DiskNumber);
        }
    }

    [Fact]
    public async Task A_healthy_partition_is_not_reported_as_rebuilt()
    {
        // אותו דיסק בלי שום נזק: המחיצה רשומה בטבלה ונקראת, ואין מה לבנות.
        var disk = ImageDisk.Open(LinuxDriveTests.Unpack("ntfs-lost.raw", _temp));
        try
        {
            Assert.Contains(disk.Partitions, p => p.FileSystem == FileSystemKind.Ntfs);
            var hunt = await PartitionHunter.HuntAsync(disk, null, default);
            foreach (var p in disk.Partitions) _out.WriteLine($"listed {p.OffsetBytes} {p.SizeBytes} {p.FileSystem}");
            foreach (var f in hunt.Found) _out.WriteLine($"found {f.Offset} {f.Size} rebuilt={f.Rebuilt?.ClusterSize}/{f.Rebuilt?.Confirmed}");
            Assert.DoesNotContain(hunt.Found, f => f.Rebuilt is not null);
        }
        finally
        {
            ImageDisk.Close(disk.DiskNumber);
        }
    }
}
