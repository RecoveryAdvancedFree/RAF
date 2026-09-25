using System.Buffers.Binary;
using RAF.Core.Carving;
using RAF.Core.Repair;
using Xunit;

namespace RAF.Core.Tests;

/// <summary>
/// בניית תחילת JPEG שנהרסה בעזרת תמונה תקינה מאותה מצלמה (JpegTransplant).
/// "מצלמה" כאן היא המקודד של הבדיקות: כל התמונות שלו יוצאות עם אותן טבלאות,
/// כמו תמונות ממצלמה אחת. לפני הטבלאות נוסף מקטע פרטי צילום, כמו במצלמה אמיתית —
/// כך סימן הסריקה רחוק מתחילת הקובץ, ושורד כשהתחילה נדרסת.
/// </summary>
public class PhotoReferenceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _output;

    public PhotoReferenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"raf-photo-{Guid.NewGuid():N}");
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

    /// <summary>תמונת "מצלמה": מקטע פרטי צילום (עם תמונה מוקטנת, כשמבקשים) לפני הטבלאות.</summary>
    private static byte[] Camera(int width, int height, int seed, bool thumbnail = false, int restart = 0)
    {
        byte[] jpeg = JpegEncoder.Encode(width, height, seed, restartInterval: restart);
        var exif = new List<byte>("Exif\0\0"u8.ToArray());
        var random = new Random(seed + 1000);
        for (int i = 0; i < 3000; i++) exif.Add((byte)random.Next(0, 0xFF));    // בלי FF — כמו תוכן טקסטואלי
        if (thumbnail) exif.AddRange(JpegEncoder.Encode(16, 16, seed + 1));

        var app1 = new List<byte> { 0xFF, 0xE1, (byte)((exif.Count + 2) >> 8), (byte)(exif.Count + 2) };
        app1.AddRange(exif);
        return [.. jpeg.AsSpan(0, 2), .. app1, .. jpeg.AsSpan(2)];
    }

    private static int SosOf(byte[] jpeg)
    {
        int at = 2;
        while (jpeg[at + 1] != 0xDA) at += 2 + BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(at + 2));
        return at;
    }

    private static int DataStart(byte[] jpeg)
    {
        int sos = SosOf(jpeg);
        return sos + 2 + BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(sos + 2));
    }

    private static byte[] Overwrite(byte[] jpeg, int count, int seed)
    {
        byte[] damaged = (byte[])jpeg.Clone();
        new Random(seed).NextBytes(damaged.AsSpan(0, count));
        return damaged;
    }

    private static void AssertSameImage(byte[] original, string repairedPath)
    {
        byte[] repaired = File.ReadAllBytes(repairedPath);
        Assert.Equal(JpegVerdict.Complete, JpegDecoder.Check(JpegBytes.Of(repaired)).Verdict);
        Assert.Equal(original.AsSpan(DataStart(original)).ToArray(), repaired.AsSpan(DataStart(repaired)).ToArray());
    }

    [Fact]
    public void Start_overwritten_keeps_the_surviving_tables()
    {
        byte[] original = Camera(64, 48, 1);
        // תחילת הקובץ וחלק מפרטי הצילום נדרסו; הטבלאות שרדו.
        string broken = Write("broken.jpg", Overwrite(original, 1500, 5));
        string donor = Write("donor.jpg", Camera(64, 48, 2));

        var d = FileDoctor.Diagnose(broken);
        Assert.True(d.NeedsReferencePhoto);
        Assert.DoesNotContain(d.Issues, i => i.Kind == FileIssueKind.ImageDamaged);

        var r = FileDoctor.RepairPhoto(broken, donor, _output);
        Assert.True(r.Succeeded, r.Message);
        AssertSameImage(original, r.OutputPath!);
        Assert.Contains(r.Applied, a => a.Contains("מהחלקים ששרדו"));
    }

    [Fact]
    public void Whole_header_lost_takes_tables_and_size_from_the_reference()
    {
        byte[] original = Camera(64, 48, 3);
        int sos = SosOf(original);
        string broken = Write("broken.jpg", Overwrite(original, sos, 6));    // הכול עד סימן הסריקה
        string donor = Write("donor.jpg", Camera(64, 48, 4));

        Assert.True(FileDoctor.Diagnose(broken).NeedsReferencePhoto);

        var r = FileDoctor.RepairPhoto(broken, donor, _output);
        Assert.True(r.Succeeded, r.Message);
        AssertSameImage(original, r.OutputPath!);
        Assert.Contains(r.Applied, a => a.Contains("מידות התמונה"));
        Assert.Contains(r.Applied, a => a.Contains("פרטי הצילום"));
    }

    [Fact]
    public void Restart_interval_comes_from_the_reference_when_needed()
    {
        byte[] original = Camera(64, 48, 7, restart: 2);
        string broken = Write("broken.jpg", Overwrite(original, SosOf(original), 8));
        string donor = Write("donor.jpg", Camera(64, 48, 9, restart: 2));

        var r = FileDoctor.RepairPhoto(broken, donor, _output);
        Assert.True(r.Succeeded, r.Message);
        AssertSameImage(original, r.OutputPath!);
    }

    [Fact]
    public void Damaged_data_is_rebuilt_partially()
    {
        byte[] original = Camera(160, 160, 23);
        byte[] damaged = Overwrite(original, SosOf(original), 24);
        int middle = DataStart(original) + (original.Length - DataStart(original)) / 2;
        new Random(25).NextBytes(damaged.AsSpan(middle, 64));
        string broken = Write("broken.jpg", damaged);

        var r = FileDoctor.RepairPhoto(broken, Write("donor.jpg", Camera(160, 160, 26)), _output);
        Assert.False(r.Succeeded);
        Assert.NotNull(r.OutputPath);
        Assert.Contains(r.After!.Issues, i => i.Kind == FileIssueKind.ImageDamaged);
    }

    [Fact]
    public void Reference_with_other_settings_is_rejected_without_writing()
    {
        byte[] original = Camera(64, 48, 10);
        string broken = Write("broken.jpg", Overwrite(original, SosOf(original), 11));
        string donor = Write("donor.jpg", Camera(160, 96, 12));     // גודל תמונה אחר

        var r = FileDoctor.RepairPhoto(broken, donor, _output);
        Assert.False(r.Succeeded);
        Assert.Null(r.OutputPath);
        Assert.Contains("באותן הגדרות", r.Message);
        Assert.False(Directory.Exists(_output) && Directory.EnumerateFiles(_output).Any());
    }

    [Fact]
    public void Thumbnail_is_not_mistaken_for_the_photo()
    {
        // גם סימן הסריקה של התמונה הראשית נדרס — מה ששרד הוא רק התמונה המוקטנת,
        // שהיא תמונה שלמה בפני עצמה. אין כאן נתונים לבנות מהם.
        byte[] original = Camera(64, 48, 13, thumbnail: true);
        int sos = SosOf(original);
        int app1End = 4 + BinaryPrimitives.ReadUInt16BigEndian(original.AsSpan(4));
        byte[] damaged = (byte[])original.Clone();
        new Random(14).NextBytes(damaged.AsSpan(app1End, sos + 20 - app1End));
        for (int i = app1End; i < sos + 20; i++) if (damaged[i] == 0xFF) damaged[i] = 0;
        damaged[0] = 0; damaged[1] = 0;

        var d = FileDoctor.Diagnose(Write("broken.jpg", damaged));
        Assert.False(d.NeedsReferencePhoto);
    }

    [Fact]
    public void Photo_with_thumbnail_and_lost_main_tables_is_rebuilt()
    {
        byte[] original = Camera(64, 48, 15, thumbnail: true);
        int sos = SosOf(original);
        byte[] damaged = (byte[])original.Clone();
        // נדרס מהתחלה עד אחרי התמונה המוקטנת, כולל הטבלאות — סימן הסריקה שרד.
        new Random(16).NextBytes(damaged.AsSpan(0, sos));

        string broken = Write("broken.jpg", damaged);
        Assert.True(FileDoctor.Diagnose(broken).NeedsReferencePhoto);

        var r = FileDoctor.RepairPhoto(broken, Write("donor.jpg", Camera(64, 48, 17, thumbnail: true)), _output);
        Assert.True(r.Succeeded, r.Message);
        AssertSameImage(original, r.OutputPath!);
    }

    [Fact]
    public void Healthy_and_truncated_photos_do_not_ask_for_a_reference()
    {
        byte[] ok = Camera(64, 48, 18);
        Assert.False(FileDoctor.Diagnose(Write("ok.jpg", ok)).NeedsReferencePhoto);
        Assert.False(FileDoctor.Diagnose(Write("cut.jpg", ok.AsSpan(0, ok.Length - 200).ToArray())).NeedsReferencePhoto);
    }

    [Fact]
    public void Reference_is_checked_before_use()
    {
        Assert.Null(JpegTransplant.DescribeReference(Write("good.jpg", Camera(64, 48, 19))));
        Assert.NotNull(JpegTransplant.DescribeReference(Write("text.jpg", "hello world"u8.ToArray())));

        byte[] cut = Camera(64, 48, 20);
        Assert.NotNull(JpegTransplant.DescribeReference(Write("cut.jpg", cut.AsSpan(0, cut.Length - 100).ToArray())));

        string same = Write("same.jpg", Overwrite(Camera(64, 48, 21), 1500, 22));
        var r = FileDoctor.RepairPhoto(same, same, _output);
        Assert.Null(r.OutputPath);
    }
}
