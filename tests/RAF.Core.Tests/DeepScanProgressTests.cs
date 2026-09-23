using System.Buffers.Binary;
using RAF.Core.FileSystems;
using RAF.Core.Imaging;
using RAF.Core.Model;
using Xunit;

namespace RAF.Core.Tests;

/// <summary>
/// דיווח התקדמות בסריקה עמוקה של FAT.
///
/// הבאג שנמצא על כונן אמיתי: המעבר על כל האשכולות דיווח התקדמות רק אחרי
/// התנאים שמדלגים על אשכול שאינו ספרייה — כלומר כמעט אף פעם. הסריקה רצה,
/// אבל המסך נשאר על "קורא את ספריית השורש" עם 0:00 ונראה תקוע.
/// </summary>
public class DeepScanProgressTests : IDisposable
{
    private const int Sector = 512;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"raf-deep-{Guid.NewGuid():N}.img");

    public void Dispose()
    {
        try { File.Delete(_path); File.Delete(ImageMap.PathFor(_path)); } catch { /* ניקוי בלבד */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>מחיצת FAT16 ריקה: מגזר אתחול, שתי טבלאות FAT וספריית שורש ריקה.</summary>
    private static byte[] EmptyFat16()
    {
        const uint totalSectors = 100_000;
        byte[] image = new byte[totalSectors * Sector];
        FatTests.BootSector(totalSectors: totalSectors).CopyTo(image, 0);

        // שני הערכים הראשונים בכל טבלת FAT שמורים: סוג המדיה וסימן סוף.
        for (int fat = 0; fat < 2; fat++)
        {
            int at = (1 + fat * 200) * Sector;
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(at), 0xFFF8);
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(at + 2), 0xFFFF);
        }

        return image;
    }

    /// <summary>אוסף דיווחים באופן סינכרוני — Progress&lt;T&gt; היה מעביר אותם לתהליכון אחר.</summary>
    private sealed class Collect : IProgress<ScanProgress>
    {
        public readonly List<ScanProgress> Reports = new();
        public void Report(ScanProgress value) { lock (Reports) Reports.Add(value); }
    }

    [Fact]
    public void The_sweep_over_all_clusters_reports_progress_even_when_it_finds_nothing()
    {
        File.WriteAllBytes(_path, EmptyFat16());
        new ImageMap { Kind = "partition", Size = new FileInfo(_path).Length, Complete = true }
            .Save(ImageMap.PathFor(_path));

        var disk = ImageDisk.Open(_path);
        var part = Assert.Single(disk.Partitions);
        var progress = new Collect();

        try
        {
            VolumeScanner.ScanAsync(
                FileSystemKind.Fat16, disk.DiskNumber, part.OffsetBytes, part.SizeBytes, Sector,
                ScanMode.Deep, includeExisting: false, disk.Trim, progress, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        finally
        {
            ImageDisk.Close(disk.DiskNumber);
        }

        var sweep = progress.Reports.Where(r => r.Percent is > 0).ToList();

        // בערך 200 דיווחים — אחד לכל חצי אחוז — ולא אפס.
        Assert.True(sweep.Count >= 100, $"only {sweep.Count} progress reports during the sweep");
        Assert.True(sweep[^1].Percent > 95);
        Assert.True(sweep.Zip(sweep.Skip(1)).All(p => p.Second.Percent >= p.First.Percent));
    }
}
