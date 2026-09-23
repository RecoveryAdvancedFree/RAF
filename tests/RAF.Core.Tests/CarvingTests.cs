using System.Buffers.Binary;
using System.Text;
using RAF.Core.Carving;
using RAF.Core.FileSystems;
using RAF.Core.Native;
using RAF.Core.Signatures;
using Xunit;

namespace RAF.Core.Tests;

/// <summary>
/// בדיקות הסריקה המתקדמת.
///
/// החלק המסוכן כאן הוא קביעת אורך הקובץ. אורך שגוי מייצר קובץ קטוע שלא
/// נפתח, או קובץ תפוח שבולע את שכניו — ובשני המקרים הסריקה נראית מוצלחת.
///
/// בסריקה על כונן אמיתי התברר שיש סכנה שלישית: חתימות קצרות מופיעות
/// בנתונים אקראיים מאות פעמים, וכל "קובץ" כזה שאורכו נוחש גרם לסורק
/// לדלג על הנתונים שאחריו — ולבלוע קבצים אמיתיים. הבדיקות בתחתית
/// הקובץ מכסות את זה במפורש.
/// </summary>
public class CarvingTests : IDisposable
{
    private const int SectorSize = 512;

    private readonly string _path;
    private readonly MemoryStream _image = new();
    private RawDevice? _device;

    public CarvingTests()
        => _path = Path.Combine(Path.GetTempPath(), $"raf-carve-{Guid.NewGuid():N}.img");

    public void Dispose()
    {
        _device?.Dispose();
        try { File.Delete(_path); } catch { /* ניקוי בלבד */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>הנחת קובץ בגבול סקטור, והחזרת ההיסט שבו הונח.</summary>
    private long Place(byte[] content)
    {
        long padding = (SectorSize - _image.Length % SectorSize) % SectorSize;
        _image.Write(new byte[padding]);

        long offset = _image.Length;
        _image.Write(content);
        return offset;
    }

    private RawVolume Open()
    {
        long padding = (SectorSize - _image.Length % SectorSize) % SectorSize;
        _image.Write(new byte[padding + SectorSize * 4]);

        File.WriteAllBytes(_path, _image.ToArray());

        _device = RawDevice.TryOpen(_path, SectorSize)
                  ?? throw new IOException($"cannot open image. Win32: {RawDevice.LastError}");

        return RawVolume.Open(VolumeReader.Wrap(_device, 0, _image.Length), SectorSize);
    }

    private ResolvedLength Resolve(byte[] content, string extension, byte[]? followedBy = null)
    {
        long offset = Place(content);
        if (followedBy is not null) _image.Write(followedBy);

        var volume = Open();
        var signature = FileSignatures.ForExtension(extension).First();
        return FileLength.Resolve(signature, volume, offset, _image.Length - offset);
    }

    private List<RAF.Core.Model.RecoveredFile> Sweep()
    {
        var volume = Open();
        return new FileCarver().Sweep(volume, _image.Length, SectorSize, null, CancellationToken.None).Files;
    }

    // ==================================================== אורך מתוך המבנה

    [Fact]
    public void Bmp_length_comes_from_its_header_field()
    {
        byte[] bmp = RealFormats.Bmp(40, 30, 1);
        var r = Resolve(bmp, "bmp");

        Assert.Equal(bmp.Length, r.Bytes);

        // שדה גודל יחיד אינו מוכיח שזה קובץ, ולכן הוא אינו מאפשר דילוג.
        Assert.Equal(LengthConfidence.Declared, r.Confidence);
    }

    [Fact]
    public void Riff_length_is_the_declared_size_plus_the_header()
    {
        byte[] wav = RealFormats.Wav(3008, 2);
        Assert.Equal(wav.Length, Resolve(wav, "wav").Bytes);
    }

    [Fact]
    public void Png_length_is_found_by_walking_its_chunks()
    {
        byte[] png = RealFormats.Png(900, 3);
        Assert.Equal(png.Length, Resolve(png, "png").Bytes);
    }

    [Fact]
    public void Sqlite_length_is_page_size_times_page_count()
    {
        byte[] db = RealFormats.Sqlite(4096, 3, 4);
        Assert.Equal(db.Length, Resolve(db, "sqlite").Bytes);
    }

    [Fact]
    public void Mp4_length_is_the_sum_of_its_top_level_boxes()
    {
        byte[] mp4 = RealFormats.Mp4(2000, 5);
        Assert.Equal(mp4.Length, Resolve(mp4, "mp4").Bytes);
    }

    [Fact]
    public void Exe_length_is_the_end_of_its_last_section()
    {
        byte[] exe = RealFormats.Pe(6);
        var r = Resolve(exe, "exe", followedBy: RealFormats.Random(20_000, 60));

        Assert.Equal(exe.Length, r.Bytes);
        Assert.Equal(LengthConfidence.Exact, r.Confidence);
    }

    [Fact]
    public void Ico_length_is_the_end_of_its_furthest_image()
    {
        byte[] ico = RealFormats.Ico(7);
        Assert.Equal(ico.Length, Resolve(ico, "ico").Bytes);
    }

    [Fact]
    public void Gif_length_is_found_by_walking_blocks_not_by_searching_for_3B()
    {
        // נתוני התמונה מלאים בבית 3B. חיפוש שלו היה עוצר בבית הראשון.
        byte[] gif = RealFormats.Gif();
        Assert.Equal(gif.Length, Resolve(gif, "gif").Bytes);
    }

    [Fact]
    public void Zip_length_ends_after_the_end_of_directory_record_and_comment()
    {
        byte[] zip = RealFormats.Zip(3000, 8);
        Assert.Equal(zip.Length, Resolve(zip, "zip").Bytes);
    }

    [Fact]
    public void Pdf_length_reaches_its_end_of_file_marker()
    {
        byte[] head = Encoding.ASCII.GetBytes("%PDF-1.4\n");
        byte[] tail = Encoding.ASCII.GetBytes("\n%%EOF");
        byte[] pdf = new byte[3000];

        head.CopyTo(pdf, 0);
        for (int i = head.Length; i < pdf.Length - tail.Length; i++) pdf[i] = (byte)'A';
        tail.CopyTo(pdf, pdf.Length - tail.Length);

        Assert.Equal(pdf.Length, Resolve(pdf, "pdf").Bytes);
    }

    /// <summary>PDF קטן: כותרת, גוף, ו-%%EOF — ואפשר להוסיף באמצע עוד תוכן (למשל עדכון מצטבר).</summary>
    private static byte[] Pdf(int body, string? update = null)
    {
        // בלי שורה חדשה אחרי הסימן האחרון: האורך נמדד עד סוף "%%EOF".
        string text = "%PDF-1.4\n" + new string('A', body) + "\n%%EOF" +
                      (update is null ? "" : "\n" + update + "\n%%EOF");
        return Encoding.ASCII.GetBytes(text);
    }

    /// <summary>עוטף מחיצה וסופר כמה בתים נקראו ממנה — כדי למדוד, ולא רק לנחש, את עלות החיפוש.</summary>
    private sealed class CountingVolume(RawVolume inner) : RAF.Core.FileSystems.IClusterVolume
    {
        public long BytesRead { get; private set; }
        public int BytesPerCluster => inner.BytesPerCluster;
        public long ClusterToOffset(long cluster) => inner.ClusterToOffset(cluster);
        public bool? IsClusterAllocated(long cluster) => inner.IsClusterAllocated(cluster);
        public void Dispose() { }
        public int ReadRaw(long offset, Span<byte> destination)
        {
            int n = inner.ReadRaw(offset, destination);
            BytesRead += Math.Max(0, n);
            return n;
        }
    }

    [Fact]
    public void A_small_pdf_does_not_make_the_scan_read_hundreds_of_megabytes()
    {
        // הבאג מהבדיקה על כונן F: PDF של 0.1MB, ואחריו רק נתונים שאינם PDF —
        // החיפוש אחר סימן סיום נוסף קרא 256MB. עכשיו: עד 16MB אחרי הסימן האחרון.
        byte[] pdf = Pdf(100_000);
        long offset = Place(pdf);
        _image.Write(new byte[40 * 1024 * 1024]);

        var counting = new CountingVolume(Open());
        var signature = FileSignatures.ForExtension("pdf").First();
        var r = FileLength.Resolve(signature, counting, offset, _image.Length - offset);

        Assert.Equal(pdf.Length, r.Bytes);
        Assert.True(counting.BytesRead < 20 * 1024 * 1024, $"read {counting.BytesRead / 1048576}MB");
    }

    [Fact]
    public void An_incremental_pdf_ends_at_its_last_end_marker()
    {
        byte[] pdf = Pdf(3000, update: "1 0 obj << /Updated true >> endobj");
        Assert.Equal(pdf.Length, Resolve(pdf, "pdf").Bytes);
    }

    [Fact]
    public void A_pdf_stops_where_another_file_begins_after_its_end_marker()
    {
        // אחרי ה-PDF יושבת תמונה, ואחריה — במקרה — עוד "%%EOF" בנתונים אחרים.
        // הסורק אינו רשאי לבלוע את התמונה בדרך לסימן הזה.
        byte[] pdf = Pdf(3000);
        long offset = Place(pdf);
        Place(RealFormats.Png(2000, 41));
        Place(Encoding.ASCII.GetBytes("zzz\n%%EOF\n"));

        var volume = Open();
        var r = FileLength.Resolve(FileSignatures.ForExtension("pdf").First(), volume, offset, _image.Length - offset);
        Assert.Equal(pdf.Length, r.Bytes);
    }

    [Fact]
    public void A_jpeg_inside_a_pdf_before_its_end_marker_does_not_cut_it()
    {
        // PDF עם תמונת JPEG שלמה בתוכו, שנופלת בדיוק על גבול סקטור — לפני ה-%%EOF.
        byte[] jpeg = JpegEncoder.Encode(64, 64, 42);
        byte[] head = Encoding.ASCII.GetBytes("%PDF-1.4\n");
        byte[] pad = new byte[SectorSize - head.Length];
        pad.AsSpan().Fill((byte)'A');
        byte[] pdf = [.. head, .. pad, .. jpeg, .. Encoding.ASCII.GetBytes("\nendstream\n%%EOF")];

        Assert.Equal(pdf.Length, Resolve(pdf, "pdf").Bytes);
    }

    // ==================================================== JPEG

    [Fact]
    public void Jpeg_length_comes_from_its_segment_structure()
    {
        byte[] jpeg = RealFormats.Jpeg(4000, 9);
        var r = Resolve(jpeg, "jpg");

        Assert.Equal(jpeg.Length, r.Bytes);
        Assert.Equal(LengthConfidence.Exact, r.Confidence);
    }

    [Fact]
    public void Jpeg_end_is_not_confused_with_its_exif_thumbnail()
    {
        // התמונה הממוזערת שבתוך Exif מסתיימת ב-FF D9 משלה.
        byte[] jpeg = RealFormats.Jpeg(4000, 10, withExifThumbnail: true);
        Assert.Equal(jpeg.Length, Resolve(jpeg, "jpg").Bytes);
    }

    [Fact]
    public void Jpeg_end_is_not_taken_from_random_data_after_the_file()
    {
        // הבאג שהתגלה על כונן אמיתי: חיפוש FF D9 האחרון מצא מופעים
        // אקראיים בנתונים שאחרי הקובץ, ויצר JPEG בגודל עשרות מגה.
        byte[] jpeg = RealFormats.Jpeg(4000, 11);
        byte[] after = RealFormats.Random(200_000, 110);

        Assert.Equal(jpeg.Length, Resolve(jpeg, "jpg", followedBy: after).Bytes);
    }

    [Fact]
    public void A_cut_jpeg_is_kept_even_when_the_foreign_data_after_it_looks_like_a_segment()
    {
        // הבאג: אחרי הנתונים הדחוסים מגיעים בתים של קובץ אחר, והראשונים שבהם
        // הם FF E1 — נראה כמו מקטע Exif. הקריאה ניסתה להמשיך "למקטע" הזה,
        // נכשלה, ופסלה את כל הקובץ. כך כ-8% מהקבצים המפוצלים לא נמצאו כלל.
        byte[] jpeg = JpegEncoder.Encode(320, 240, 36);
        int cut = jpeg.Length / 2;
        byte[] foreign = [0xFF, 0xE1, 0x12, 0x34, .. RealFormats.Random(5000, 37)];

        var r = Resolve([.. jpeg[..cut], .. foreign], "jpg");

        Assert.True(r.Bytes >= cut, $"הקובץ נפסל או קוצר מדי: {r.Bytes}");
    }

    // ==================================================== התאמות שווא

    [Fact]
    public void A_random_sector_starting_with_BM_is_not_a_bitmap()
    {
        byte[] fake = RealFormats.Random(4096, 12);
        fake[0] = (byte)'B'; fake[1] = (byte)'M';

        Assert.Equal(0, Resolve(fake, "bmp").Bytes);
    }

    [Fact]
    public void A_random_sector_starting_with_MZ_is_not_an_executable()
    {
        byte[] fake = RealFormats.Random(4096, 13);
        fake[0] = (byte)'M'; fake[1] = (byte)'Z';

        Assert.Equal(0, Resolve(fake, "exe").Bytes);
    }

    // ==================================================== JPEG: אימות וחיבור מקטעים

    [Fact]
    public void A_fragmented_jpeg_is_rebuilt_from_both_fragments_and_the_file_in_the_gap_is_found_too()
    {
        byte[] jpeg = JpegEncoder.Encode(640, 480, 31, restartInterval: 8);
        int split = 20 * SectorSize;

        long first = Place(jpeg[..split]);
        long png = Place(RealFormats.Png(3000, 32));          // קובץ אחר שיושב בפער
        long second = Place(jpeg[split..]);

        var files = Sweep();
        var image = _image.ToArray();

        var j = files.Single(f => f.Extension == "jpg");
        Assert.Equal(jpeg.Length, j.Size);
        Assert.Equal(RAF.Core.Model.RecoveryQuality.Good, j.Quality);
        Assert.Contains("מפוצל", j.QualityReason);
        Assert.Equal(2, j.Extents.Count);
        Assert.Equal(first / SectorSize, j.Extents[0].StartCluster);
        Assert.Equal(second / SectorSize, j.Extents[1].StartCluster);

        // התוכן לפי המקטעים זהה לקובץ המקורי, בית-בית.
        byte[] rebuilt = [.. image.AsSpan((int)first, split), .. image.AsSpan((int)second, jpeg.Length - split)];
        Assert.Equal(jpeg, rebuilt);

        // הפער לא "נבלע": ה-PNG שבתוכו נמצא.
        Assert.Contains(files, f => f.Extension == "png" && f.Extents[0].StartCluster == png / SectorSize);
    }

    [Fact]
    public void A_whole_jpeg_is_verified_by_decoding()
    {
        Place(JpegEncoder.Encode(320, 240, 33));

        var j = Assert.Single(Sweep());
        Assert.Equal(RAF.Core.Model.RecoveryQuality.Excellent, j.Quality);
        Assert.Contains("פוענחו", j.QualityReason);
    }

    [Fact]
    public void A_jpeg_whose_rest_was_overwritten_is_graded_poor_not_excellent()
    {
        byte[] jpeg = JpegEncoder.Encode(640, 480, 34);
        int cut = jpeg.Length / 2 / SectorSize * SectorSize;
        Place([.. jpeg[..cut], .. RealFormats.Random(jpeg.Length - cut - 2, 35), 0xFF, 0xD9]);

        var j = Assert.Single(Sweep());
        Assert.Equal(RAF.Core.Model.RecoveryQuality.Poor, j.Quality);
        Assert.Contains("משתבשים", j.QualityReason);
    }

    [Fact]
    public void Long_scan_hands_out_checkpoints_that_are_independent_copies()
    {
        // שני קבצים עם 9MB ביניהם — יותר מבלוק קריאה אחד, כדי שתהיה נקודת ביניים באמצע.
        Place(RealFormats.Png(900, 3));
        Place(new byte[9 * 1024 * 1024]);
        Place(RealFormats.Png(700, 4));

        var checkpoints = new List<RAF.Core.Model.ScanResult>();
        var saved = FileCarver.CheckpointEvery;
        FileCarver.CheckpointEvery = TimeSpan.Zero;
        try
        {
            var volume = Open();
            var final = new FileCarver().Sweep(volume, _image.Length, SectorSize, null, CancellationToken.None,
                                               checkpoint: checkpoints.Add);

            Assert.Equal(2, final.Files.Count);
            Assert.NotEmpty(checkpoints);

            // הנקודה הראשונה נלקחה לפני שהקובץ השני נמצא — ונשארה כך גם אחרי שהסריקה המשיכה.
            var first = checkpoints[0];
            Assert.Single(first.Files);
            Assert.NotSame(final.Files, first.Files);
            Assert.True(first.Cancelled, "נקודת ביניים מסומנת כסריקה שלא הושלמה");
            Assert.Contains("נקודת ביניים", Assert.Single(first.Warnings));
        }
        finally
        {
            FileCarver.CheckpointEvery = saved;
        }
    }

    [Fact]
    public void Random_data_does_not_produce_false_files()
    {
        // 4MB אקראיים = 8,192 גבולות סקטור. לפני האימות המבני זה ייצר
        // התאמות BM, MZ ו-ICO; עכשיו לא אמור לצאת כמעט דבר.
        Place(RealFormats.Random(4 * 1024 * 1024, 31337));

        var files = Sweep();
        Assert.True(files.Count <= 2, $"false files: {files.Count} — {string.Join(", ", files.Select(f => f.Path))}");
    }

    // ==================================================== סריקה מלאה

    [Fact]
    public void The_sweep_finds_every_real_file_at_its_true_offset()
    {
        var planted = new List<(long Offset, byte[] Content)>
        {
            (Place(RealFormats.Bmp(64, 48, 20)), null!),
            (Place(RealFormats.Jpeg(6000, 21)), null!),
            (Place(RealFormats.Png(3000, 22)), null!),
            (Place(RealFormats.Wav(5000, 23)), null!),
            (Place(RealFormats.Sqlite(4096, 2, 24)), null!),
            (Place(RealFormats.Mp4(4000, 25)), null!),
            (Place(RealFormats.Pe(26)), null!),
        };

        var files = Sweep();

        Assert.Equal(planted.Count, files.Count);
        for (int i = 0; i < planted.Count; i++)
            Assert.Equal(planted[i].Offset / SectorSize, files[i].Extents[0].StartCluster);
    }

    [Fact]
    public void A_file_of_unknown_length_does_not_hide_the_files_after_it()
    {
        // GZIP אינו נושא את אורכו, ולכן אורכו משוער. בעבר הסורק דילג על
        // כל האזור המשוער — ו-BMP אמיתי שיושב אחריו נעלם.
        byte[] gzip = RealFormats.Random(8192, 40);
        gzip[0] = 0x1F; gzip[1] = 0x8B; gzip[2] = 0x08; gzip[3] = 0x00;

        Place(gzip);
        long bmpOffset = Place(RealFormats.Bmp(32, 32, 41));

        var files = Sweep();

        Assert.Contains(files, f => f.Extents[0].StartCluster == bmpOffset / SectorSize && f.Name.EndsWith(".bmp"));
    }

    [Fact]
    public void Content_embedded_inside_a_verified_file_is_not_reported_as_a_separate_file()
    {
        // PNG שבנתוניו מופיעה חתימת PDF בגבול סקטור. מבנה ה-PNG נקרא
        // עד IEND, ולכן גבולו ודאי והסורק מדלג על תוכנו.
        byte[] png = RealFormats.Png(8000, 42);
        Encoding.ASCII.GetBytes("%PDF-1.4").CopyTo(png, 1024);

        Place(png);

        var single = Assert.Single(Sweep());
        Assert.Equal(png.Length, single.Size);
    }

    /// <summary>
    /// תחילת טבלת האותיות הגדולות שכל מחיצת exFAT כותבת: מיפוי זהות
    /// של תווי UTF-16 — 0000, 0001, 0002... בסדר little-endian.
    /// </summary>
    private static byte[] ExFatUpcaseTable(int entries)
    {
        byte[] table = new byte[entries * 2];
        for (int i = 0; i < entries; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(i * 2), (ushort)i);
        return table;
    }

    [Fact]
    public void The_exfat_upcase_table_is_not_mistaken_for_an_icon()
    {
        // הבאג שהתגלה על כונן אמיתי: הטבלה פותחת ב-00 00 01 00 — חתימת ICO —
        // והמשכה נקרא כספרייה עם אורך 2,228,256 בתים, שבלע את כל הקבצים.
        byte[] upcase = ExFatUpcaseTable(3000);
        Assert.Equal(0, Resolve(upcase, "ico").Bytes);
    }

    [Fact]
    public void Files_written_right_after_the_exfat_upcase_table_are_found()
    {
        // שחזור התרחיש המלא: הטבלה, ומיד אחריה הקבצים הראשונים שנכתבו.
        Place(ExFatUpcaseTable(3000));
        long png = Place(RealFormats.Png(3000, 50));
        long jpeg = Place(RealFormats.Jpeg(4000, 51));
        long bmp = Place(RealFormats.Bmp(40, 40, 52));

        var files = Sweep();

        Assert.DoesNotContain(files, f => f.Name.EndsWith(".ico"));
        Assert.Contains(files, f => f.Extents[0].StartCluster == png / SectorSize);
        Assert.Contains(files, f => f.Extents[0].StartCluster == jpeg / SectorSize);
        Assert.Contains(files, f => f.Extents[0].StartCluster == bmp / SectorSize);
    }

    [Fact]
    public void A_header_that_declares_a_large_size_does_not_hide_what_follows()
    {
        // BMP שכותרתו תקינה אך מצהירה על גודל שמכסה קבצים אחרים. גם אם
        // מדובר בצירוף מקרים ולא בקובץ, אסור לו להעלים את מה שאחריו.
        byte[] bmp = RealFormats.Bmp(40, 40, 60);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(2), 200_000);

        Place(bmp);
        long png = Place(RealFormats.Png(3000, 61));

        var files = Sweep();
        Assert.Contains(files, f => f.Extents[0].StartCluster == png / SectorSize && f.Name.EndsWith(".png"));
    }

    [Fact]
    public void An_empty_image_yields_nothing()
    {
        Place(new byte[16384]);
        Assert.Empty(Sweep());
    }
}
