using System.Buffers.Binary;
using RAF.Core.Carving;
using RAF.Core.Repair;
using Xunit;

namespace RAF.Core.Tests;

/// <summary>
/// התמונות המוקטנות שבתוך תמונת מצלמה: התמונה הממוזערת של EXIF, והתצוגה המקדימה
/// הגדולה של MPF. על 400 תמונות אמיתיות מהמחשב: ב-393 נמצאה תמונה מוקטנת שלמה,
/// וב-298 מהן היא 1440×1080. כשהתמונה עצמה פגומה — זה מה שנשאר ממנה.
/// </summary>
public class JpegPreviewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"raf-preview-{Guid.NewGuid():N}");

    public JpegPreviewTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static readonly byte[] Thumb = JpegEncoder.Encode(160, 120, 2);
    private static readonly byte[] Large = JpegEncoder.Encode(320, 240, 3);

    private static byte[] Segment(byte marker, byte[] payload)
    {
        byte[] s = new byte[4 + payload.Length];
        s[0] = 0xFF; s[1] = marker;
        BinaryPrimitives.WriteUInt16BigEndian(s.AsSpan(2), (ushort)(payload.Length + 2));
        payload.CopyTo(s, 4);
        return s;
    }

    /// <summary>
    /// תמונת מצלמה: EXIF עם תמונה ממוזערת (IFD1), MPF שמצביע לתצוגה מקדימה גדולה
    /// שנכתבה אחרי התמונה עצמה — כמו שמצלמות כותבות.
    /// </summary>
    private static byte[] CameraPhoto(byte[]? thumb = null)
    {
        thumb ??= Thumb;
        byte[] main = JpegEncoder.Encode(640, 480, 1);

        // EXIF: כותרת TIFF, IFD0 ריק שמצביע ל-IFD1, ו-IFD1 עם מיקום התמונה הממוזערת.
        byte[] tiff = new byte[44 + thumb.Length];
        "II*\0"u8.CopyTo(tiff);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(10), 14);                // IFD0: 0 תגיות, הבאה ב-14
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(14), 2);
        Entry(tiff, 16, 0x201, 4, 1, 44);
        Entry(tiff, 28, 0x202, 4, 1, (uint)thumb.Length);
        thumb.CopyTo(tiff, 44);
        byte[] app1 = Segment(0xE1, [.. "Exif\0\0"u8, .. tiff]);

        // MPF: שתי רשומות — התמונה עצמה, והתצוגה המקדימה שאחריה.
        byte[] mp = new byte[26 + 32];
        "II*\0"u8.CopyTo(mp);
        BinaryPrimitives.WriteUInt32LittleEndian(mp.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(mp.AsSpan(8), 1);
        Entry(mp, 10, 0xB002, 7, 32, 26);
        byte[] app2 = Segment(0xE2, [.. "MPF\0"u8, .. mp]);

        int mpfHeader = 2 + app1.Length + 4 + 4;
        int largeAt = 2 + app1.Length + app2.Length + (main.Length - 2);
        byte[] file = [0xFF, 0xD8, .. app1, .. app2, .. main.AsSpan(2), .. Large];
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(mpfHeader + 26 + 16 + 4), (uint)Large.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(mpfHeader + 26 + 16 + 8), (uint)(largeAt - mpfHeader));
        return file;
    }

    private static void Entry(byte[] b, int at, ushort tag, ushort type, uint count, uint value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), tag);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at + 2), type);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 4), count);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 8), value);
    }

    private string Write(string name, byte[] data)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    [Fact]
    public void The_largest_intact_preview_is_chosen()
    {
        var best = JpegPreviews.Best(CameraPhoto());
        Assert.NotNull(best);
        Assert.Equal((320, 240), (best!.Width, best.Height));
        Assert.Equal(Large, best.Data);
    }

    [Fact]
    public void A_damaged_preview_is_not_offered()
    {
        byte[] brokenThumb = (byte[])Thumb.Clone();
        brokenThumb.AsSpan(brokenThumb.Length / 2, 40).Fill(0xFF);      // נתונים שאינם הופמן תקין
        byte[] photo = CameraPhoto(brokenThumb);
        byte[] cut = photo.AsSpan(0, photo.Length - Large.Length - 100).ToArray();   // גם הגדולה אבדה

        Assert.Null(JpegPreviews.Best(cut));
    }

    [Fact]
    public void A_cut_photo_gives_back_its_thumbnail_as_a_separate_file()
    {
        // נקטעה באמצע התמונה עצמה: התצוגה הגדולה (בסוף) אבדה, הממוזערת (בהתחלה) שרדה.
        byte[] photo = CameraPhoto();
        byte[] cut = photo.AsSpan(0, photo.Length - Large.Length - 3000).ToArray();

        var d = FileDoctor.Diagnose(Write("wedding.jpg", cut));
        Assert.Contains(d.Issues, i => i.Kind == FileIssueKind.ImageDamaged && !i.Fixable);
        Assert.Contains(d.Issues, i => i.Kind == FileIssueKind.PreviewAvailable && i.Fixable);

        var r = FileDoctor.Repair(Write("wedding.jpg", cut), Path.Combine(_dir, "out"));
        Assert.NotNull(r.PreviewPath);
        Assert.Equal(Thumb, File.ReadAllBytes(r.PreviewPath!));
        Assert.True(r.Succeeded, r.Message);
    }

    [Fact]
    public void A_healthy_photo_is_not_offered_its_preview()
        => Assert.True(FileDoctor.Diagnose(Write("ok.jpg", CameraPhoto())).IsHealthy);

    /// <summary>
    /// מה שטלפונים כותבים אחרי סוף ה-JPEG הוא חלק מהקובץ: בלוק המידע של Samsung
    /// (נמצא בשתי תמונות אמיתיות במחשב), וסרטון של "תמונה נעה". הצעה "להסיר" אותם
    /// הייתה מוחקת מידע אמיתי.
    /// </summary>
    [Theory]
    [InlineData("samsung")]
    [InlineData("motion")]
    public void What_phones_write_after_the_image_is_not_extra_data(string kind)
    {
        byte[] photo = JpegEncoder.Encode(64, 48, 5);
        byte[] trailer = kind == "samsung"
            ? [.. new byte[60], .. "Image_UTC_Data"u8, .. new byte[20], .. "SEFT"u8]
            : [0, 0, 0, 24, .. "ftypmp42"u8, .. new byte[200]];

        var d = FileDoctor.Diagnose(Write($"{kind}.jpg", [.. photo, .. trailer]));
        Assert.DoesNotContain(d.Issues, i => i.Kind == FileIssueKind.TrailingData);
    }

    [Fact]
    public void Junk_after_the_image_is_still_reported()
    {
        byte[] junk = Enumerable.Range(0, 3000).Select(i => (byte)(i * 37 % 251 + 1)).ToArray();
        var d = FileDoctor.Diagnose(Write("junk.jpg", [.. JpegEncoder.Encode(64, 48, 5), .. junk]));
        Assert.Contains(d.Issues, i => i.Kind == FileIssueKind.TrailingData && i.Fixable);
    }

    /// <summary>שחזור: תמונה שחזרה פגומה — התמונה המוקטנת שלה נשמרת לצדה אוטומטית.</summary>
    [Fact]
    public async Task Recovery_saves_the_preview_next_to_a_photo_that_came_back_damaged()
    {
        const int sector = 512;
        byte[] photo = CameraPhoto();
        byte[] cut = photo.AsSpan(0, photo.Length - Large.Length - 3000).ToArray();

        int sectors = (cut.Length + sector - 1) / sector;
        byte[] image = new byte[(sectors + 8) * sector];
        cut.CopyTo(image, 4 * sector);

        string imagePath = Path.Combine(_dir, "card.img");
        File.WriteAllBytes(imagePath, image);
        new RAF.Core.Imaging.ImageMap { Kind = "partition", Size = image.Length, Complete = true }
            .Save(RAF.Core.Imaging.ImageMap.PathFor(imagePath));
        var disk = RAF.Core.Imaging.ImageDisk.Open(imagePath);

        var file = new RAF.Core.Model.RecoveredFile
        {
            Id = 1, Name = "IMG_0003.JPG", Path = "DCIM", Size = cut.Length,
            Quality = RAF.Core.Model.RecoveryQuality.Poor, Content = RAF.Core.Model.ContentCheck.HasData,
            Extents = new List<RAF.Core.Model.DataExtent> { new(4, sectors, false) },
        };

        string target = Path.Combine(_dir, "restored");
        try
        {
            var report = await RAF.Core.Recovery.RecoveryWriter.RecoverAsync(
                RAF.Core.Model.FileSystemKind.Raw, disk.DiskNumber, 0, image.Length, sector, new[] { file },
                new RAF.Core.Recovery.RecoveryOptions { TargetFolder = target, PreservePaths = true },
                progress: null, CancellationToken.None);

            Assert.Equal(1, report.PreviewsSaved);
            string preview = Directory.GetFiles(target, "*(תמונה מוקטנת).jpg", SearchOption.AllDirectories).Single();
            Assert.Equal(Thumb, File.ReadAllBytes(preview));
        }
        finally
        {
            RAF.Core.Imaging.ImageDisk.Close(disk.DiskNumber);
        }
    }
}
