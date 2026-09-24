using RAF.Core.FileSystems;
using RAF.Core.Model;
using Xunit;

namespace RAF.Core.Tests;

/// <summary>
/// קבצים שבדיקת הדיסק של Windows השאירה ב-FOUND.000 בשם FILE0000.CHK. הם מקבלים
/// בחזרה את הסוג לפי התוכן, והאורך האמיתי — בדיקת הדיסק שומרת עד סוף האשכול האחרון,
/// ולפעמים גם אשכולות של קובץ אחר.
/// </summary>
public class ChkFilesTests
{
    private const int Cluster = 512;
    private const string Found = "FOUND.000";

    /// <summary>מחיצה בזיכרון: אשכול הוא 512 בתים.</summary>
    private sealed class MemoryVolume(byte[] data) : IClusterVolume
    {
        public int BytesPerCluster => Cluster;
        public long ClusterToOffset(long cluster) => cluster * Cluster;
        public bool? IsClusterAllocated(long cluster) => true;
        public void Dispose() { }

        public int ReadRaw(long offset, Span<byte> destination)
        {
            if (offset >= data.Length) return 0;
            int take = (int)Math.Min(destination.Length, data.Length - offset);
            data.AsSpan((int)offset, take).CopyTo(destination);
            return take;
        }
    }

    /// <summary>
    /// הנתונים מפוזרים בשני מקטעים עם רווח ביניהם, ואחריהם אשכול של "קובץ אחר" —
    /// בדיוק מה שבדיקת הדיסק שומרת לשרשרת אשכולות אבודה.
    /// </summary>
    private static (RecoveredFile File, MemoryVolume Volume) Chk(byte[] content, string name = "FILE0003.CHK", string path = Found)
    {
        int clusters = (content.Length + Cluster - 1) / Cluster + 1;           // אשכול זבל אחד בסוף
        int first = clusters / 2;
        int second = clusters - first;

        byte[] disk = RealFormats.Random((10 + clusters + 20) * Cluster, seed: 99);
        byte[] padded = new byte[clusters * Cluster];
        RealFormats.Random(padded.Length, seed: 7).CopyTo(padded, 0);
        content.CopyTo(padded, 0);

        padded.AsSpan(0, first * Cluster).CopyTo(disk.AsSpan(10 * Cluster));
        padded.AsSpan(first * Cluster).CopyTo(disk.AsSpan((10 + first + 5) * Cluster));

        var file = new RecoveredFile
        {
            Id = 1, Name = name, Path = path, Size = padded.Length,
            Extents = { new DataExtent(10, first, false), new DataExtent(10 + first + 5, second, false) },
        };
        return (file, new MemoryVolume(disk));
    }

    [Fact]
    public void A_photo_gets_its_type_back_and_its_real_length()
    {
        byte[] jpeg = RealFormats.Jpeg(20_000, seed: 3);
        var (file, volume) = Chk(jpeg);

        Assert.True(ChkFiles.Identify(file, volume));

        Assert.EndsWith(".jpg", file.Name);
        Assert.StartsWith("FILE0003", file.Name);
        Assert.Equal(jpeg.Length, file.Size);
        Assert.Contains("בדיקת הדיסק", file.QualityReason);
    }

    [Fact]
    public void A_png_is_recognised_as_a_png()
    {
        byte[] png = RealFormats.Png(5000, seed: 4);
        var (file, volume) = Chk(png, "file0012.chk");

        Assert.True(ChkFiles.Identify(file, volume));

        Assert.Equal("file0012.png", file.Name);
        Assert.Equal(png.Length, file.Size);
    }

    [Fact]
    public void Unrecognised_content_keeps_its_name_and_size()
    {
        var (file, volume) = Chk(RealFormats.Random(3000, seed: 5));
        long size = file.Size;

        Assert.False(ChkFiles.Identify(file, volume));

        Assert.Equal("FILE0003.CHK", file.Name);
        Assert.Equal(size, file.Size);
    }

    [Fact]
    public void A_small_file_kept_inside_its_record_is_identified_too()
    {
        byte[] png = RealFormats.Png(300, seed: 6);
        var file = new RecoveredFile { Id = 2, Name = "FILE0000.CHK", Path = Found, Size = png.Length, ResidentData = png };

        Assert.True(ChkFiles.Identify(file, new MemoryVolume([])));
        Assert.Equal("FILE0000.png", file.Name);
    }

    [Fact]
    public void Only_chk_files_inside_a_found_folder_are_touched()
    {
        var extents = new List<DataExtent> { new(10, 1, false) };
        RecoveredFile Make(string name, string path) => new()
        {
            Name = name, Path = path, Size = 100, Extents = extents,
        };

        var inFound = Make("FILE0001.CHK", "FOUND.002");
        var nested = Make("FILE0002.CHK", @"FOUND.000\DIR0000.CHK");
        var elsewhere = Make("FILE0001.CHK", "Documents");
        var ordinary = Make("report.chk", "FOUND.000");
        var notFound = Make("FILE0001.CHK", "FOUND.ABC");

        var picked = ChkFiles.Candidates([inFound, nested, elsewhere, ordinary, notFound]);

        Assert.Equal([inFound, nested], picked);
    }
}
