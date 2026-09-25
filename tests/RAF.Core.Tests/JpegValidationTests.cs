#if WINDOWS
using System.Drawing;
using System.Drawing.Imaging;
#endif
using RAF.Core.Carving;
using Xunit;

namespace RAF.Core.Tests;

/// <summary>
/// אימות JPEG בפענוח, וחיבור JPEG מפוצל.
///
/// הכשל שנבדק כאן: קובץ שחתימת הסיום שלו נמצאה, ולכן קיבל "מצוין" — אבל
/// מחציתו נתונים של קובץ אחר, כי במקור הוא היה מפוצל. בכיוון ההפוך, הכשל
/// המסוכן לא פחות: תמונה תקינה לגמרי שהמפענח טועה בה ומסמן אותה "חלש".
/// </summary>
public class JpegValidationTests
{
    private static JpegCheck Check(byte[] data) => JpegDecoder.Check(JpegBytes.Of(data));

    private static byte[] Noise(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>JPEG אמיתי מהמקודד של Windows — כדי לא לבדוק את המפענח רק מול המקודד שלנו.</summary>
    private static byte[] WindowsJpeg(int width, int height, int seed)
    {
#if !WINDOWS
        return JpegEncoder.Encode(width, height, seed);      // אין מקודד של Windows — המקודד שלנו
#else
        using var bitmap = new Bitmap(width, height);
        var random = new Random(seed);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.White);
            for (int i = 0; i < 300; i++)
            {
                using var brush = new SolidBrush(Color.FromArgb(random.Next(256), random.Next(256), random.Next(256)));
                g.FillEllipse(brush, random.Next(width), random.Next(height), random.Next(10, 120), random.Next(10, 120));
            }
        }

        using var output = new MemoryStream();
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, 90L);
        bitmap.Save(output, codec, parameters);
        return output.ToArray();
#endif
    }

    // ------------------------------------------------------------ קבצים תקינים

    [Theory]
    [InlineData(640, 480, 2, 2, 3, 0)]      // 4:2:0, בלי RST
    [InlineData(640, 480, 2, 2, 3, 7)]      // 4:2:0, RST כל 7 יחידות — מחזור מלא של D0–D7 ועוד
    [InlineData(101, 37, 1, 1, 3, 3)]       // 4:4:4, ממדים שאינם כפולה של 8
    [InlineData(333, 250, 2, 1, 3, 0)]      // 4:2:2
    [InlineData(200, 200, 1, 1, 1, 5)]      // גווני אפור
    public void A_valid_jpeg_decodes_completely(int w, int h, int sh, int sv, int components, int restart)
    {
        byte[] jpeg = JpegEncoder.Encode(w, h, seed: w + h, (sh, sv), components, restart);
        var check = Check(jpeg);

        Assert.Equal(JpegVerdict.Complete, check.Verdict);
        Assert.Equal(jpeg.Length, check.Offset);
        Assert.Equal(check.McusTotal, check.McusDecoded);
    }

    [Fact]
    public void A_jpeg_from_the_windows_encoder_decodes_completely()
    {
        byte[] jpeg = WindowsJpeg(800, 600, 1);
        var check = Check(jpeg);

        Assert.Equal(JpegVerdict.Complete, check.Verdict);
        Assert.Equal(jpeg.Length, check.Offset);
    }

    // ------------------------------------------------------------ קבצים פגומים

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Foreign_data_is_detected_soon_after_it_starts(int restart)
    {
        byte[] jpeg = JpegEncoder.Encode(640, 480, 3, restartInterval: restart);
        int cut = jpeg.Length / 2 / 512 * 512;
        Noise(jpeg.Length - cut, 99).CopyTo(jpeg, cut);

        var check = Check(jpeg);
        Assert.Equal(JpegVerdict.Corrupt, check.Verdict);
        Assert.InRange(check.Offset, cut, cut + JpegFragments.SplitWindow);
        Assert.InRange(check.Fraction, 0.3, 0.7);
    }

    [Fact]
    public void Foreign_data_is_detected_in_a_windows_jpeg()
    {
        byte[] jpeg = WindowsJpeg(800, 600, 2);
        int cut = jpeg.Length * 2 / 3 / 512 * 512;
        Noise(jpeg.Length - cut, 7).CopyTo(jpeg, cut);

        var check = Check(jpeg);
        Assert.Equal(JpegVerdict.Corrupt, check.Verdict);
        Assert.InRange(check.Offset, cut, cut + JpegFragments.SplitWindow);
    }

    [Fact]
    public void A_truncated_jpeg_is_not_complete()
    {
        byte[] jpeg = JpegEncoder.Encode(320, 240, 4);
        Assert.Equal(JpegVerdict.Corrupt, Check(jpeg[..(jpeg.Length - 700)]).Verdict);
    }

    [Fact]
    public void Restart_markers_out_of_order_are_detected()
    {
        byte[] jpeg = JpegEncoder.Encode(320, 240, 5, restartInterval: 4);
        int first = IndexOf(jpeg, 0xFF, 0xD0);
        jpeg[first + 1] = 0xD3;
        Assert.Equal(JpegVerdict.Corrupt, Check(jpeg).Verdict);
    }

    [Fact]
    public void Formats_outside_baseline_are_left_undecided_not_called_broken()
    {
        // פרוגרסיבי: אותו קובץ, עם SOF2 במקום SOF0.
        byte[] progressive = JpegEncoder.Encode(64, 64, 6);
        progressive[IndexOf(progressive, 0xFF, 0xC0) + 1] = 0xC2;
        Assert.Equal(JpegVerdict.Unsupported, Check(progressive).Verdict);

        // בלי טבלאות הופמן (כמו פריים של MJPEG).
        Assert.Equal(JpegVerdict.Unsupported, Check(RealFormats.Jpeg(4000, 9)).Verdict);
    }

    private static int IndexOf(byte[] data, byte a, byte b)
    {
        for (int i = 0; i < data.Length - 1; i++)
            if (data[i] == a && data[i + 1] == b) return i;
        throw new InvalidOperationException("marker not found");
    }

    // ------------------------------------------------------------ חיבור מקטעים

    /// <summary>הקובץ כפי שהוא יושב על הדיסק: מקטע, פער של נתונים זרים, מקטע, ועוד נתונים.</summary>
    private static byte[] OnDisk(byte[] jpeg, int split, int gap, int seed)
        => [.. jpeg[..split], .. Noise(gap, seed), .. jpeg[split..], .. Noise(8192, seed + 1)];

    private static JpegFragmentPair? Find(byte[] disk)
    {
        var (check, decoder) = JpegDecoder.CheckWithCheckpoints(JpegBytes.Of(disk));
        return JpegFragments.Find(disk, check, decoder, 512, TimeSpan.FromSeconds(20));
    }

    [Theory]
    [InlineData(0, 40, 3)]
    [InlineData(6, 40, 57)]
    [InlineData(0, 25, 300)]
    public void A_two_fragment_jpeg_is_reassembled_exactly(int restart, int splitSectors, int gapSectors)
    {
        byte[] jpeg = JpegEncoder.Encode(640, 480, 8, restartInterval: restart);
        int split = splitSectors * 512, gap = gapSectors * 512;

        var pair = Find(OnDisk(jpeg, split, gap, 11));

        Assert.NotNull(pair);
        Assert.Equal((split, (long)gap, (long)jpeg.Length), (pair!.Value.Split, pair.Value.Gap, pair.Value.Length));
    }

    [Fact]
    public void A_two_fragment_windows_jpeg_is_reassembled_exactly()
    {
        byte[] jpeg = WindowsJpeg(800, 600, 3);
        int split = jpeg.Length / 3 / 512 * 512, gap = 90 * 512;

        var pair = Find(OnDisk(jpeg, split, gap, 13));

        Assert.NotNull(pair);
        Assert.Equal((split, (long)gap, (long)jpeg.Length), (pair!.Value.Split, pair.Value.Gap, pair.Value.Length));
    }

    [Fact]
    public void Without_a_real_second_fragment_nothing_is_invented()
    {
        byte[] jpeg = JpegEncoder.Encode(640, 480, 9);
        int split = 30 * 512;
        byte[] disk = [.. jpeg[..split], .. Noise(200_000, 12)];   // המשך הקובץ נדרס

        Assert.Null(Find(disk));
    }
}
