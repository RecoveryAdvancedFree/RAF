using System.Text;
using RAF.Core.FileSystems;
using RAF.Core.Imaging;
using RAF.Core.Model;
using RAF.Core.Native;
using RAF.Core.Recovery;
using RAF.Core.Repair;
using Xunit;

namespace RAF.Core.Tests;

/// <summary>
/// קריאת מחיצה ש-Windows מבקש לפרמט, דרך עותק הגיבוי של מגזר האתחול —
/// בזיכרון בלבד. הבדיקות מוכיחות שני דברים שאסור להתפשר על אף אחד מהם:
/// הקבצים חוזרים עם שמותיהם, ועל הכונן לא נכתב אף בית.
/// </summary>
public sealed class VirtualRepairTests : IDisposable
{
    private const int Sector = NtfsImageBuilder.BytesPerSector;
    private const long FileRecord = 30;   // הטבלה בתמונת הבדיקה מכילה 32 רשומות

    private static readonly byte[] Content = Encoding.UTF8.GetBytes("חשבונית מס 2031 — תוכן שחייב לחזור בדיוק");

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"raf-virtual-{Guid.NewGuid():N}.img");

    public void Dispose()
    {
        foreach (var p in new[] { _path, ImageMap.PathFor(_path) })
            try { File.Delete(p); } catch { }
    }

    /// <summary>
    /// מחיצת NTFS עם קובץ שנמחק, שמגזר האתחול שלה נמחק — בדיוק המצב שבו
    /// Windows מציג "יש לפרמט את הדיסק". העותק בסקטור האחרון שלם.
    /// </summary>
    private byte[] BrokenNtfsPartition(bool keepBackup = true)
    {
        using var builder = new NtfsImageBuilder();

        builder.WriteRecord(FileRecord, new MftRecordBuilder(NtfsImageBuilder.MftRecordSize, Sector)
        {
            RecordNumber = FileRecord,
            InUse = false,
        }
        .WithFileName("חשבונית.txt", parentRecord: 5, realSize: Content.Length)
        .WithResidentData(Content)
        .Build());

        byte[] image = builder.ToArray();

        if (keepBackup)
            image.AsSpan(0, Sector).CopyTo(image.AsSpan(image.Length - Sector));

        Array.Clear(image, 0, Sector);          // מגזר האתחול הראשי — נמחק
        return image;
    }

    private PhysicalDiskInfo Open(byte[] image)
    {
        File.WriteAllBytes(_path, image);
        new ImageMap { Kind = "partition", Size = image.Length, Complete = true }.Save(ImageMap.PathFor(_path));
        return ImageDisk.Open(_path);
    }

    [Fact]
    public void A_partition_that_needs_formatting_gives_back_its_files_by_name_without_writing()
    {
        byte[] image = BrokenNtfsPartition();
        var disk = Open(image);
        var part = Assert.Single(disk.Partitions);

        try
        {
            // לפני: המחיצה אינה מזוהה — כמו ב-Windows.
            Assert.Equal(FileSystemKind.Raw, part.FileSystem);

            var diagnosis = VirtualRepair.Apply(disk.DiskNumber, part.OffsetBytes, part.SizeBytes, Sector);
            Assert.True(diagnosis.CanRepair);
            Assert.Equal(FileSystemKind.Ntfs, diagnosis.DetectedFileSystem);
            Assert.True(VirtualRepair.IsActive(disk.DiskNumber, part.OffsetBytes));

            var result = VolumeScanner.ScanAsync(
                FileSystemKind.Ntfs, disk.DiskNumber, part.OffsetBytes, part.SizeBytes, Sector,
                ScanMode.Quick, includeExisting: false, disk.Trim, null, CancellationToken.None)
                .GetAwaiter().GetResult();

            // הקובץ חזר עם שמו המקורי ועם התוכן המדויק.
            var file = Assert.Single(result.Files, f => f.Name == "חשבונית.txt");
            byte[] head = FileContentReader.ReadHead(
                FileSystemKind.Ntfs, disk.DiskNumber, part.OffsetBytes, part.SizeBytes, Sector, file, 4096);
            Assert.Equal(Content, head);

            // ועל הקובץ — כלומר על "הכונן" — לא נכתב דבר.
            Assert.Equal(image, File.ReadAllBytes(_path));
        }
        finally
        {
            VirtualRepair.Remove(disk.DiskNumber, part.OffsetBytes);
            ImageDisk.Close(disk.DiskNumber);
        }
    }

    [Fact]
    public void Diagnosis_repair_and_imaging_still_see_the_real_damaged_sector()
    {
        var disk = Open(BrokenNtfsPartition());
        var part = disk.Partitions[0];

        try
        {
            VirtualRepair.Apply(disk.DiskNumber, part.OffsetBytes, part.SizeBytes, Sector);

            // קוראי המנועים רואים את העותק...
            using (var patched = VolumeReader.TryOpen(disk.DiskNumber, part.OffsetBytes, part.SizeBytes, Sector)!)
                Assert.Equal((byte)'N', patched.ReadBlock(3, 1)[0]);

            // ...אבל האבחון עדיין רואה את הפגם. אחרת תיקון אמיתי היה שומר
            // בקובץ הביטול את העותק במקום את מה שבאמת היה על הדיסק.
            var again = PartitionDiagnosis.Diagnose(disk.DiskNumber, part.OffsetBytes, part.SizeBytes, Sector);
            Assert.Equal(RepairOutlook.BackupFound, again.Outlook);

            using var raw = VolumeReader.TryOpen(
                disk.DiskNumber, part.OffsetBytes, part.SizeBytes, Sector, applyOverlay: false)!;
            Assert.All(raw.ReadBlock(0, Sector), b => Assert.Equal(0, b));
        }
        finally
        {
            VirtualRepair.Remove(disk.DiskNumber, part.OffsetBytes);
            ImageDisk.Close(disk.DiskNumber);
        }
    }

    [Fact]
    public void Without_a_backup_copy_nothing_is_applied()
    {
        var disk = Open(BrokenNtfsPartition(keepBackup: false));
        var part = disk.Partitions[0];

        try
        {
            var diagnosis = VirtualRepair.Apply(disk.DiskNumber, part.OffsetBytes, part.SizeBytes, Sector);
            Assert.False(diagnosis.CanRepair);
            Assert.False(VirtualRepair.IsActive(disk.DiskNumber, part.OffsetBytes));
        }
        finally
        {
            ImageDisk.Close(disk.DiskNumber);
        }
    }

    [Fact]
    public void Removing_it_restores_the_plain_view()
    {
        var disk = Open(BrokenNtfsPartition());
        var part = disk.Partitions[0];

        try
        {
            VirtualRepair.Apply(disk.DiskNumber, part.OffsetBytes, part.SizeBytes, Sector);
            VirtualRepair.Remove(disk.DiskNumber, part.OffsetBytes);

            using var reader = VolumeReader.TryOpen(disk.DiskNumber, part.OffsetBytes, part.SizeBytes, Sector)!;
            Assert.All(reader.ReadBlock(0, Sector), b => Assert.Equal(0, b));
        }
        finally
        {
            ImageDisk.Close(disk.DiskNumber);
        }
    }
}
