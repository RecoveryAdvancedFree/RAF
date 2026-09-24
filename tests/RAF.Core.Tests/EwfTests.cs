using System.Security.Cryptography;
using System.Text;
using RAF.Core.Imaging;
using RAF.Core.Model;
using RAF.Core.Native;
using Xunit;

namespace RAF.Core.Tests;

/// <summary>
/// תמונות E01 של כלי חקירה. הקובץ לבדיקה (Media\ext2.E01) נוצר בכלי אמיתי, והכלי
/// שמר בו את טביעת האצבע של הכונן המקורי — כך נבדק שכל בית נקרא נכון, לא רק שהמבנה מתפענח.
/// נבדק גם מול הכונן המקורי עצמו ומול אותה תמונה מחולקת לשני קבצים (זהים בכל בית).
/// </summary>
public sealed class EwfTests : IDisposable
{
    private static readonly string Sample = Path.Combine(AppContext.BaseDirectory, "Media", "ext2.E01");
    private const string KnownMd5 = "196066ADD11FB71C4C49CF1BB50D6D24";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"raf-ewf-{Guid.NewGuid():N}");

    public EwfTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Every_byte_matches_the_fingerprint_the_imaging_tool_stored()
    {
        using var device = RawDevice.TryOpen(Sample)!;
        var disk = device.Virtual!;
        Assert.Equal("E01", disk.Format);
        Assert.Equal(4 * 1024 * 1024, disk.Size);

        byte[] all = device.ReadBlock(0, (int)disk.Size);
        Assert.Equal(KnownMd5, Convert.ToHexString(MD5.HashData(all)));
        Assert.Equal(KnownMd5, Convert.ToHexString(disk.Md5!));
    }

    [Fact]
    public void Reads_that_cross_chunk_boundaries_return_the_same_bytes()
    {
        using var device = RawDevice.TryOpen(Sample)!;
        byte[] all = device.ReadBlock(0, (int)device.Virtual!.Size);

        var random = new Random(3);
        for (int i = 0; i < 200; i++)
        {
            int at = random.Next(all.Length - 70_000);
            int length = random.Next(1, 70_000);                                 // עד שני חלקים ויותר
            Assert.Equal(all.AsSpan(at, length).ToArray(), device.ReadBlock(at, length));
        }
    }

    [Fact]
    public void An_e01_opens_as_a_disk_with_the_file_system_inside_it()
    {
        var disk = ImageDisk.Open(Sample);
        try
        {
            Assert.Equal(FileSystemKind.Ext, Assert.Single(disk.Partitions).FileSystem);
            Assert.Contains("E01", disk.ImageNote);
        }
        finally { ImageDisk.Close(disk.DiskNumber); }
    }

    [Fact]
    public void A_missing_segment_is_named()
    {
        // התמונה "ממשיכה" לקובץ הבא — שלא קיים. סימן הסיום (done) הופך לסימן המשך (next).
        byte[] data = File.ReadAllBytes(Sample);
        int done = LastIndexOf(data, Encoding.ASCII.GetBytes("done\0"));
        Assert.True(done > 0);
        Encoding.ASCII.GetBytes("next").CopyTo(data, done);

        string path = Path.Combine(_dir, "evidence.E01");
        File.WriteAllBytes(path, data);

        var ex = Assert.Throws<InvalidOperationException>(() => RawDevice.TryOpen(path));
        Assert.Contains("evidence.E02", ex.Message);
    }

    [Fact]
    public void The_newer_ex01_format_is_refused_with_an_explanation()
    {
        string path = Path.Combine(_dir, "evidence.Ex01");
        File.WriteAllBytes(path, [.. "EVF2\r\n"u8, 0x81, 0x00, .. new byte[4096]]);

        Assert.Contains("Ex01", Assert.Throws<InvalidOperationException>(() => RawDevice.TryOpen(path)).Message);
    }

    [Theory]
    [InlineData("case.E01", 2, "case.E02")]
    [InlineData("case.E01", 99, "case.E99")]
    [InlineData("case.E01", 100, "case.EAA")]
    [InlineData("case.E01", 101, "case.EAB")]
    [InlineData("case.E01", 126, "case.EBA")]
    [InlineData("case.E01", 776, "case.FAA")]
    [InlineData("case.e01", 100, "case.eaa")]
    public void Segment_files_are_named_like_the_imaging_tools_name_them(string first, int n, string expected)
        => Assert.Equal(expected, EwfImage.SegmentPath(first, n));

    private static int LastIndexOf(byte[] data, byte[] needle)
    {
        for (int i = data.Length - needle.Length; i >= 0; i--)
            if (data.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }
}
