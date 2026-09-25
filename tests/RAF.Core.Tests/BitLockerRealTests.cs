using System.Security.Cryptography;
using RAF.Core.FileSystems;
using RAF.Core.Imaging;
using RAF.Core.Model;
using RAF.Core.Recovery;
using Xunit;
using Xunit.Abstractions;

namespace RAF.Core.Tests;

/// <summary>
/// כוננים וירטואליים שהוצפנו ב-BitLocker של Windows עצמו (make.ps1 בתיקייה C:\bltest-raf):
/// NTFS ב-XTS 128/256 וב-CBC, exFAT ו-FAT32. בכל אחד קובץ קיים וקובץ שנמחק, ומפתח שחזור
/// שנרשם ביומן. הבדיקה פותחת, סורקת ומשחזרת — ומשווה טביעות אצבע. בלי התיקייה היא מדלגת.
/// </summary>
public class BitLockerRealTests
{
    private const string Folder = @"C:\bltest-raf";
    private readonly ITestOutputHelper _out;

    public BitLockerRealTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData("ntfs-xts128", false)]
    [InlineData("ntfs-cbc128", false)]
    [InlineData("ntfs-xts256", false)]
    [InlineData("exfat-cbc128", false)]
    [InlineData("fat32-xts128", false)]
    [InlineData("ntfs-xts128", true)]
    public async Task Real_bitlocker_volume_opens_scans_and_recovers(string name, bool usePassword)
    {
        string vhd = Path.Combine(Folder, name + ".vhd");
        if (!File.Exists(vhd)) return;

        var log = File.ReadAllLines(Path.Combine(Folder, "make.log"));
        string Value(string key) => log.First(l => l.StartsWith($"{name} {key}=")).Split('=')[1].Trim();

        var disk = ImageDisk.Open(vhd);
        PhysicalDiskInfo? open = null;
        string target = Path.Combine(Path.GetTempPath(), "raf-bl-" + Guid.NewGuid().ToString("N"));
        try
        {
            var part = disk.Partitions.Single();
            Assert.Equal(FileSystemKind.BitLocker, part.FileSystem);

            var info = BitLockerDisk.Inspect(disk.DiskNumber, part.OffsetBytes, part.SizeBytes, disk.LogicalSectorSize);
            _out.WriteLine($"protectors: {string.Join(", ", info.Protectors)}; typed={info.TypedKey}; problem={info.Problem}");
            Assert.True(info.TypedKey);

            Assert.Throws<InvalidOperationException>(() => BitLockerDisk.Unlock(
                disk.DiskNumber, part.OffsetBytes, part.SizeBytes, disk.LogicalSectorSize, "wrong-password", "t"));

            open = BitLockerDisk.Unlock(disk.DiskNumber, part.OffsetBytes, part.SizeBytes, disk.LogicalSectorSize,
                usePassword ? "Raf-Test-123" : Value("recovery"), "t");
            var inner = open.Partitions.Single();
            _out.WriteLine($"{open.ImageNote} · {inner.FileSystem}");
            Assert.True(VolumeScanner.IsSupported(inner.FileSystem), inner.FileSystem.ToString());

            var result = await VolumeScanner.ScanAsync(inner.FileSystem, open.DiskNumber, 0, inner.SizeBytes,
                open.LogicalSectorSize, ScanMode.Quick, includeExisting: true, TrimState.NotSupported, null, default);

            // ב-FAT32 המחיקה מוחקת את האות הראשונה של השם.
            var files = result.Files.Where(f => f.Name is "keep.bin" or "hello.txt" || (f.IsDeleted && f.Name.EndsWith("one.bin"))).ToList();
            _out.WriteLine(string.Join(", ", files.Select(f => $"{f.Name}{(f.IsDeleted ? " (deleted)" : "")} {f.Quality}")));
            Assert.Contains(files, f => f.Name == "keep.bin" && !f.IsDeleted);
            Assert.Contains(files, f => f.IsDeleted && f.Quality == RecoveryQuality.Excellent);

            var report = await RecoveryWriter.RecoverAsync(inner.FileSystem, open.DiskNumber, 0, inner.SizeBytes,
                open.LogicalSectorSize, files, new RecoveryOptions { TargetFolder = target, PreservePaths = false }, null, default);
            _out.WriteLine($"recovered {report.Succeeded}, failures {report.Failures.Count}");

            string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(
                Directory.GetFiles(target, file, SearchOption.AllDirectories).Single())));
            Assert.Equal(Value("keep"), Hash("keep.bin"));
            Assert.Equal(Value("gone"), Hash("*one.bin"));
        }
        finally
        {
            if (open is not null) ImageDisk.Close(open.DiskNumber);
            ImageDisk.Close(disk.DiskNumber);
            if (Directory.Exists(target)) Directory.Delete(target, true);
        }
    }
}
