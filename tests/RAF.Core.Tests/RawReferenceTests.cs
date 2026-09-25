using System.Buffers.Binary;
using RAF.Core.Repair;
using Xunit;

namespace RAF.Core.Tests;

/// <summary>
/// בניית תחילת קובץ RAW שנהרסה בעזרת קובץ תקין מאותה מצלמה (TiffTransplant).
/// הקבצים כאן בנויים כמו NEF: רשימה ראשונה עם שם המצלמה ותמונה ממוזערת, פרטי צילום
/// באורך משתנה (כמו בפועל — ומה שאחריהם זז), ושתי תת-רשימות: תצוגה מקדימה ב-JPEG
/// ונתוני החיישן.
/// </summary>
public class RawReferenceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _output;

    public RawReferenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"raf-raw-{Guid.NewGuid():N}");
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

    /// <summary>קובץ RAW: noteLength — אורך פרטי הצילום; seed — התוכן (תמונה אחרת מאותה מצלמה).</summary>
    private sealed class Raw
    {
        public byte[] Bytes = [];
        public int HeaderEnd, ThumbAt, PreviewAt, PreviewLength, SensorAt, SensorLength;
    }

    private static Raw Build(int seed, int noteLength, int width = 64, int height = 48, int bits = 14)
    {
        var random = new Random(seed);
        byte[] preview = JpegEncoder.Encode(64, 48, seed);
        byte[] sensor = new byte[width * height * 2];
        random.NextBytes(sensor);
        byte[] thumb = new byte[0x300];
        random.NextBytes(thumb);

        const int Ifd0 = 8, Ifd0Count = 8;
        int ifd0Ext = Ifd0 + 2 + 12 * Ifd0Count + 4;                  // Make, Model, SubIFDs
        int exif = ifd0Ext + 16 + 16 + 8;
        int exifExt = exif + 2 + 12 * 2 + 4;                           // תאריך ופרטי יצרן
        int thumbAt = (exifExt + 20 + noteLength + 3) / 4 * 4;
        int sub0 = thumbAt + thumb.Length;
        int sub1 = sub0 + 2 + 12 * 2 + 4;
        int previewAt = (sub1 + 2 + 12 * 6 + 4 + 0x1FF) / 0x200 * 0x200;
        int sensorAt = (previewAt + preview.Length + 0xFF) / 0x100 * 0x100;
        var d = new byte[sensorAt + sensor.Length];

        "II*\0"u8.CopyTo(d);
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(4), Ifd0);

        void Ifd(int at, (int Tag, int Type, int Count, int Value)[] entries, int next = 0)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(at), (ushort)entries.Length);
            for (int i = 0; i < entries.Length; i++)
            {
                int e = at + 2 + 12 * i;
                BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(e), (ushort)entries[i].Tag);
                BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(e + 2), (ushort)entries[i].Type);
                BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(e + 4), entries[i].Count);
                if (entries[i].Type == 3 && entries[i].Count == 1)
                    BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(e + 8), (ushort)entries[i].Value);
                else BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(e + 8), entries[i].Value);
            }
            BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(at + 2 + 12 * entries.Length), next);
        }

        "TestCam Corp\0"u8.CopyTo(d.AsSpan(ifd0Ext));
        "TestCam R1\0"u8.CopyTo(d.AsSpan(ifd0Ext + 16));
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(ifd0Ext + 32), sub0);
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(ifd0Ext + 36), sub1);
        Ifd(Ifd0,
        [
            (0x100, 3, 1, 32), (0x101, 3, 1, 12),
            (0x10F, 2, 13, ifd0Ext), (0x110, 2, 11, ifd0Ext + 16),
            (0x111, 4, 1, thumbAt), (0x117, 4, 1, thumb.Length),
            (0x14A, 4, 2, ifd0Ext + 32), (0x8769, 4, 1, exif),
        ]);

        "2026:09:25 08:00:00\0"u8.CopyTo(d.AsSpan(exifExt));
        random.NextBytes(d.AsSpan(exifExt + 20, noteLength));
        Ifd(exif, [(0x9003, 2, 20, exifExt), (0x927C, 7, noteLength, exifExt + 20)]);

        thumb.CopyTo(d, thumbAt);
        Ifd(sub0, [(0x201, 4, 1, previewAt), (0x202, 4, 1, preview.Length)]);
        Ifd(sub1,
        [
            (0x100, 4, 1, width), (0x101, 4, 1, height), (0x102, 3, 1, bits), (0x103, 3, 1, 1),
            (0x111, 4, 1, sensorAt), (0x117, 4, 1, sensor.Length),
        ]);
        preview.CopyTo(d, previewAt);
        sensor.CopyTo(d, sensorAt);

        return new Raw
        {
            Bytes = d, HeaderEnd = thumbAt, ThumbAt = thumbAt, PreviewAt = previewAt, PreviewLength = preview.Length,
            SensorAt = sensorAt, SensorLength = sensor.Length,
        };
    }

    private static byte[] Damage(byte[] data, int count, int seed)
    {
        byte[] damaged = (byte[])data.Clone();
        new Random(seed).NextBytes(damaged.AsSpan(0, count));
        return damaged;
    }

    /// <summary>התצוגה המקדימה ונתוני החיישן, כפי שהרשימות של הקובץ המתוקן מצביעות אליהם.</summary>
    private static (byte[] Preview, byte[] Sensor, string Model) Read(byte[] d)
    {
        int U16(int p) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p));
        int U32(int p) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p));
        Dictionary<int, int> Tags(int ifd) => Enumerable.Range(0, U16(ifd))
            .ToDictionary(i => U16(ifd + 2 + 12 * i), i => ifd + 2 + 12 * i);

        var ifd0 = Tags(U32(4));
        int subs = U32(ifd0[0x14A] + 8);
        var sub0 = Tags(U32(subs));
        var sub1 = Tags(U32(subs + 4));
        byte[] preview = d.AsSpan(U32(sub0[0x201] + 8), U32(sub0[0x202] + 8)).ToArray();
        byte[] sensor = d.AsSpan(U32(sub1[0x111] + 8), U32(sub1[0x117] + 8)).ToArray();
        string model = System.Text.Encoding.ASCII.GetString(d, U32(ifd0[0x110] + 8), 10);
        return (preview, sensor, model);
    }

    private static void AssertSameImage(Raw original, string repairedPath)
    {
        var (preview, sensor, model) = Read(File.ReadAllBytes(repairedPath));
        Assert.Equal(original.Bytes.AsSpan(original.PreviewAt, original.PreviewLength).ToArray(), preview);
        Assert.Equal(original.Bytes.AsSpan(original.SensorAt, original.SensorLength).ToArray(), sensor);
        Assert.Equal("TestCam R1", model);
    }

    [Fact]
    public void First_list_lost_keeps_everything_else()
    {
        var original = Build(1, 3000);
        string broken = Write("broken.nef", Damage(original.Bytes, 100, 2));
        string donor = Write("donor.nef", Build(3, 3000).Bytes);

        var d = FileDoctor.Diagnose(broken);
        Assert.True(d.NeedsReferencePhoto);

        var r = FileDoctor.RepairPhoto(broken, donor, _output);
        Assert.True(r.Succeeded, r.Message);
        // אותו מבנה בדיוק: רק הרשימה הראשונה הוחלפה, והמצביעים שבה זהים לשל המקור.
        Assert.Equal(original.Bytes, File.ReadAllBytes(r.OutputPath!));
        Assert.DoesNotContain(r.Applied, a => a.Contains("פרטי הצילום"));
    }

    [Fact]
    public void Whole_start_lost_and_shifted_layout_is_relocated()
    {
        // פרטי הצילום של התמונה ארוכים יותר מאלה של הדוגמה — כל מה שאחריהם זז.
        var original = Build(4, 3200);
        string broken = Write("broken.nef", Damage(original.Bytes, original.HeaderEnd, 5));
        string donor = Write("donor.nef", Build(6, 3000).Bytes);

        var r = FileDoctor.RepairPhoto(broken, donor, _output);
        Assert.True(r.Succeeded, r.Message);
        AssertSameImage(original, r.OutputPath!);
        Assert.Contains(r.Applied, a => a.Contains("פרטי הצילום"));
    }

    [Fact]
    public void Longer_reference_start_moves_what_does_not_fit_to_the_end()
    {
        // הכותרת של הדוגמה ארוכה מזו של התמונה: העתקה מלאה הייתה דורסת את התמונה הממוזערת.
        var original = Build(7, 3000);
        string broken = Write("broken.nef", Damage(original.Bytes, original.HeaderEnd, 8));
        string donor = Write("donor.nef", Build(9, 3400).Bytes);

        var r = FileDoctor.RepairPhoto(broken, donor, _output);
        Assert.True(r.Succeeded, r.Message);
        AssertSameImage(original, r.OutputPath!);
        byte[] repaired = File.ReadAllBytes(r.OutputPath!);
        Assert.Equal(original.Bytes.AsSpan(original.ThumbAt, 0x300).ToArray(), repaired.AsSpan(original.ThumbAt, 0x300).ToArray());
        Assert.True(repaired.Length > original.Bytes.Length);       // מה שלא נכנס — בסוף
    }

    [Fact]
    public void Reference_with_other_settings_is_rejected()
    {
        var original = Build(10, 3000);
        string broken = Write("broken.nef", Damage(original.Bytes, 100, 11));

        foreach (var (name, donor) in new[] { ("size.nef", Build(12, 3000, width: 96)), ("bits.nef", Build(13, 3000, bits: 12)) })
        {
            var r = FileDoctor.RepairPhoto(broken, Write(name, donor.Bytes), _output);
            Assert.False(r.Succeeded);
            Assert.Null(r.OutputPath);
            Assert.Contains("באותן הגדרות", r.Message);
        }
    }

    [Fact]
    public void Healthy_raw_and_signature_only_damage_do_not_ask_for_a_reference()
    {
        var raw = Build(14, 3000);
        Assert.False(FileDoctor.Diagnose(Write("ok.nef", raw.Bytes)).NeedsReferencePhoto);

        // רק החתימה נפגעה — הרשימה שאחריה שלמה, והתיקון הרגיל של החתימה מספיק.
        byte[] signature = (byte[])raw.Bytes.Clone();
        signature[0] = signature[1] = 0;
        var d = FileDoctor.Diagnose(Write("sig.nef", signature));
        Assert.False(d.NeedsReferencePhoto);
    }

    [Fact]
    public void Reference_is_checked_before_use()
    {
        Assert.Null(TiffTransplant.DescribeReference(Write("good.nef", Build(15, 3000).Bytes)));
        Assert.NotNull(TiffTransplant.DescribeReference(Write("photo.nef", JpegEncoder.Encode(64, 48, 1))));

        string same = Write("same.nef", Damage(Build(16, 3000).Bytes, 100, 17));
        Assert.Null(FileDoctor.RepairPhoto(same, same, _output).OutputPath);
    }
}
