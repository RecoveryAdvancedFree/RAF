using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using RAF.Core.Repair;
using Xunit;

namespace RAF.Core.Tests;

/// <summary>
/// בדיקות אבחון ותיקון קבצים.
///
/// שני כללים נבדקים כאן מעבר לתיקונים עצמם: הקובץ המקורי לעולם אינו
/// משתנה, והרופא מסרב לתקן כשאין לו ביטחון — כתיבת חתימה שגויה לקובץ
/// שאינו מהסוג הזה הייתה מזיקה יותר מהשארתו כמות שהוא.
/// </summary>
public class FileDoctorTests : IDisposable
{
    private readonly string _dir;
    private readonly string _output;

    public FileDoctorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"raf-doctor-{Guid.NewGuid():N}");
        _output = Path.Combine(_dir, "out");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* ניקוי בלבד */ }
        GC.SuppressFinalize(this);
    }

    private string Write(string name, byte[] content)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static byte[] Body(int size, int seed)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);

        // תוכן שאינו מכיל בטעות חתימות פתיחה או סיום.
        for (int i = 0; i < data.Length; i++)
            if (data[i] == 0xFF || data[i] == 0x25) data[i] = 0x41;

        return data;
    }

    /// <summary>
    /// JPEG אמיתי: הרופא מפענח כל תמונה עד סופה, ולכן "JPEG" של בתים אקראיים
    /// בין חתימת פתיחה לסיום מאובחן — בצדק — כתמונה פגומה.
    /// </summary>
    private static byte[] Jpeg() => JpegEncoder.Encode(64, 48, 7);

    private static byte[] Bmp(int size = 6000)
    {
        byte[] b = Body(size, 9);
        b[0] = (byte)'B'; b[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(2), (uint)size);
        return b;
    }

    private static byte[] Png()
    {
        using var s = new MemoryStream();
        s.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        void Chunk(string type, int length)
        {
            byte[] h = new byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(h, (uint)length);
            Encoding.ASCII.GetBytes(type).CopyTo(h, 4);
            s.Write(h);
            s.Write(Body(length, length));
            s.Write(new byte[4]);
        }

        Chunk("IHDR", 13);
        Chunk("IDAT", 900);
        Chunk("IEND", 0);
        return s.ToArray();
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    // ==================================================== אבחון

    [Fact]
    public void A_healthy_file_has_no_issues()
    {
        var d = FileDoctor.Diagnose(Write("ok.jpg", Jpeg()));

        Assert.True(d.IsHealthy);
        Assert.Equal("תמונת JPEG", d.DetectedFormat);
    }

    [Fact]
    public void A_zeroed_header_is_diagnosed_as_damage()
    {
        byte[] jpeg = Jpeg();
        jpeg[0] = 0; jpeg[1] = 0; jpeg[2] = 0; jpeg[3] = 0;

        var d = FileDoctor.Diagnose(Write("damaged.jpg", jpeg));

        Assert.Contains(d.Issues, i => i.Kind == FileIssueKind.HeaderDamaged && i.Fixable);
    }

    [Fact]
    public void A_wrong_extension_is_detected_and_the_real_one_suggested()
    {
        var d = FileDoctor.Diagnose(Write("image.jpg", Png()));

        var issue = Assert.Single(d.Issues);
        Assert.Equal(FileIssueKind.ExtensionMismatch, issue.Kind);
        Assert.Equal("png", d.SuggestedExtension);
    }

    /// <summary>
    /// ניקון, סוני ו-DNG כותבים TIFF רגיל. בעבר הרופא דיווח עליהם "סיומת
    /// שגויה" והציע לשנות את שם תמונת ה-RAW ל-.tif.
    /// </summary>
    [Theory]
    [InlineData("photo.nef")]
    [InlineData("photo.arw")]
    [InlineData("photo.dng")]
    [InlineData("photo.tif")]
    public void Camera_raw_files_stored_as_tiff_keep_their_extension(string name)
    {
        byte[] raw = Body(4000, 3);
        byte[] header = [0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00];
        header.CopyTo(raw, 0);

        var d = FileDoctor.Diagnose(Write(name, raw));

        Assert.DoesNotContain(d.Issues, i => i.Kind == FileIssueKind.ExtensionMismatch);
    }

    [Fact]
    public void A_canon_raw_is_identified_as_such_and_not_as_tiff()
    {
        byte[] cr2 = Body(4000, 4);
        byte[] header = [0x49, 0x49, 0x2A, 0x00, 0x10, 0x00, 0x00, 0x00, 0x43, 0x52];
        header.CopyTo(cr2, 0);

        var d = FileDoctor.Diagnose(Write("photo.cr2", cr2));

        Assert.True(d.IsHealthy);
        Assert.Equal("תמונת RAW של Canon", d.DetectedFormat);
    }

    [Theory]
    [InlineData("3gp4", "clip.3gp")]
    [InlineData("crx ", "photo.cr3")]
    [InlineData("avif", "photo.avif")]
    [InlineData("heix", "photo.heic")]
    [InlineData("mif1", "photo.avif")]
    public void Formats_built_like_mp4_are_not_renamed_to_mp4(string brand, string name)
    {
        var d = FileDoctor.Diagnose(Write(name, RealFormats.Mp4(3000, 5, brand)));

        Assert.True(d.IsHealthy, string.Join(" ", d.Issues.Select(i => i.Description)));
    }

    /// <summary>
    /// MKV שנקטע: מאובחן ומוסבר, אבל לא "מתוקן". סימון האורך כ"לא ידוע" (כמו במשיב)
    /// נבדק ב-Edge, ב-ffmpeg ובמנוע של Windows ולא שינה את הניגון באף אחד מהם.
    /// </summary>
    [Fact]
    public void A_truncated_mkv_is_explained_but_not_offered_a_repair_that_changes_nothing()
    {
        byte[] full = RealFormats.Mkv(20_000, 4);
        Assert.True(FileDoctor.Diagnose(Write("full.mkv", full)).IsHealthy);

        var d = FileDoctor.Diagnose(Write("cut.mkv", full.AsSpan(0, 12_000).ToArray()));
        var issue = Assert.Single(d.Issues);
        Assert.Equal(FileIssueKind.Truncated, issue.Kind);
        Assert.False(issue.Fixable);
        Assert.Contains("%", issue.Description);
    }

    [Fact]
    public void A_live_recording_mkv_with_unknown_length_is_healthy()
        => Assert.True(FileDoctor.Diagnose(Write("live.mkv", RealFormats.Mkv(8000, 5, unknownSize: true))).IsHealthy);

    [Fact]
    public void Repairing_trailing_data_on_an_mp4_copies_the_body_exactly()
    {
        // גוף גדול מהמאגר של ההעתקה בזרימה, כדי שיעבור בכמה סבבים.
        byte[] mp4 = RealFormats.Mp4(3 * 1024 * 1024 + 123, 11);
        byte[] padded = [.. mp4, .. Body(5000, 12)];

        var result = FileDoctor.Repair(Write("clip.mp4", padded), _output);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(mp4, File.ReadAllBytes(result.OutputPath!));
    }

    [Fact]
    public void Data_after_the_declared_end_is_detected()
    {
        byte[] bmp = Bmp(6000);
        byte[] withGarbage = [.. bmp, .. Body(3000, 99)];

        var d = FileDoctor.Diagnose(Write("padded.bmp", withGarbage));

        Assert.Contains(d.Issues, i => i.Kind == FileIssueKind.TrailingData && i.Fixable);
    }

    [Fact]
    public void A_missing_end_marker_is_detected()
    {
        byte[] jpeg = Jpeg();
        byte[] cut = jpeg.AsSpan(0, jpeg.Length - 2).ToArray();

        var d = FileDoctor.Diagnose(Write("cut.jpg", cut));

        Assert.Contains(d.Issues, i => i.Kind == FileIssueKind.FooterMissing && i.Fixable);
    }

    [Fact]
    public void A_file_shorter_than_it_declares_is_truncated_and_not_fixable()
    {
        byte[] wav = Body(4000, 3);
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(4), 50_000); // מצהיר הרבה יותר
        Encoding.ASCII.GetBytes("WAVE").CopyTo(wav, 8);

        var d = FileDoctor.Diagnose(Write("short.wav", wav));

        var issue = Assert.Single(d.Issues);
        Assert.Equal(FileIssueKind.Truncated, issue.Kind);
        Assert.False(issue.Fixable); // נתונים חסרים אינם ניתנים להמצאה
    }

    [Fact]
    public void An_all_zero_file_is_empty_and_not_fixable()
    {
        var d = FileDoctor.Diagnose(Write("zeros.jpg", new byte[8192]));

        var issue = Assert.Single(d.Issues);
        Assert.Equal(FileIssueKind.Empty, issue.Kind);
        Assert.False(d.CanRepair);
    }

    [Fact]
    public void Unrelated_content_is_not_forced_into_the_expected_format()
    {
        // תוכן אקראי עם סיומת PDF: החתימה אינה חלקית ואינה מאופסת —
        // זה פשוט לא PDF. כתיבת חתימת PDF הייתה משקרת למשתמש.
        var d = FileDoctor.Diagnose(Write("notreally.pdf", Body(5000, 1)));

        var issue = Assert.Single(d.Issues);
        Assert.Equal(FileIssueKind.Unrecognized, issue.Kind);
        Assert.False(d.CanRepair);
    }

    // ==================================================== תיקון

    [Fact]
    public void Repairing_a_damaged_header_restores_it_and_nothing_else()
    {
        byte[] original = Jpeg();
        byte[] damaged = (byte[])original.Clone();
        damaged[0] = 0; damaged[1] = 0; damaged[2] = 0; // שלושת בתי החתימה

        var result = FileDoctor.Repair(Write("damaged.jpg", damaged), _output);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.After!.IsHealthy);

        // החתימה שוחזרה ושאר הקובץ זהה בית-בית למקור.
        Assert.Equal(original, File.ReadAllBytes(result.OutputPath!));
    }

    /// <summary>JPEG עם מקטע JFIF, כפי שמצלמות ותוכנות עריכה שומרות.</summary>
    /// <summary>JPEG שמקטע ה-JFIF שלו מזהה את סמן המקטע הראשון — כמו כל מה שהמקודד כותב.</summary>
    private static byte[] JfifJpeg() => Jpeg();

    [Fact]
    public void A_lost_jpeg_marker_is_restored_when_the_segment_id_survived()
    {
        byte[] original = JfifJpeg();
        byte[] damaged = (byte[])original.Clone();
        for (int i = 0; i < 4; i++) damaged[i] = 0;    // חתימה וסמן

        var result = FileDoctor.Repair(Write("jfif.jpg", damaged), _output);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.After!.IsHealthy);
        Assert.Equal(original, File.ReadAllBytes(result.OutputPath!));
    }

    [Fact]
    public void A_lost_jpeg_marker_without_evidence_is_reported_not_invented()
    {
        // בלי מזהה מקטע אין דרך לדעת את הסמן. הרופא משחזר את החתימה,
        // אך מודה שהקובץ עדיין אינו שלם — במקום לדווח עליו כתקין.
        byte[] damaged = Jpeg();
        for (int i = 0; i < 4; i++) damaged[i] = 0;
        for (int i = 6; i < 11; i++) damaged[i] = 0;   // גם המזהה "JFIF" אבד — אין ממה לשחזר את הסמן

        var result = FileDoctor.Repair(Write("nojfif.jpg", damaged), _output);

        Assert.NotNull(result.After);
        Assert.False(result.After!.IsHealthy);
        Assert.Contains(result.After.Issues, i => i.Kind == FileIssueKind.HeaderDamaged && !i.Fixable);
    }

    [Fact]
    public void Repairing_trailing_data_trims_to_the_exact_original()
    {
        byte[] bmp = Bmp(6000);
        byte[] padded = [.. bmp, .. Body(3000, 42)];

        var result = FileDoctor.Repair(Write("padded.bmp", padded), _output);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(bmp, File.ReadAllBytes(result.OutputPath!));
    }

    [Fact]
    public void Repairing_a_wrong_extension_renames_the_copy()
    {
        byte[] png = Png();
        var result = FileDoctor.Repair(Write("image.jpg", png), _output);

        Assert.True(result.Succeeded, result.Message);
        Assert.EndsWith(".png", result.OutputPath);
        Assert.Equal(png, File.ReadAllBytes(result.OutputPath!));
    }

    [Fact]
    public void Repairing_a_missing_footer_appends_it()
    {
        byte[] jpeg = Jpeg();
        byte[] cut = jpeg.AsSpan(0, jpeg.Length - 2).ToArray();

        var result = FileDoctor.Repair(Write("cut.jpg", cut), _output);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(jpeg, File.ReadAllBytes(result.OutputPath!));
    }

    [Fact]
    public void A_zeroed_bmp_header_gets_its_size_field_back_too()
    {
        byte[] bmp = Bmp(6000);
        byte[] damaged = (byte[])bmp.Clone();
        for (int i = 0; i < 6; i++) damaged[i] = 0; // חתימה ושדה גודל

        var result = FileDoctor.Repair(Write("damaged.bmp", damaged), _output);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(bmp, File.ReadAllBytes(result.OutputPath!));
    }

    [Fact]
    public void The_original_file_is_never_modified()
    {
        byte[] damaged = Jpeg();
        damaged[0] = 0; damaged[1] = 0;
        string path = Write("keep.jpg", damaged);
        string before = Hash(path);

        var result = FileDoctor.Repair(path, _output);

        Assert.Equal(before, Hash(path));
        Assert.NotEqual(Path.GetFullPath(path), Path.GetFullPath(result.OutputPath!));
    }

    [Fact]
    public void Repair_refuses_when_nothing_can_be_fixed_safely()
    {
        var result = FileDoctor.Repair(Write("notreally.pdf", Body(5000, 1)), _output);

        Assert.False(result.Succeeded);
        Assert.Null(result.OutputPath);
        Assert.False(Directory.Exists(_output) && Directory.GetFiles(_output).Length > 0);
    }
}
