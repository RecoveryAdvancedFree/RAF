using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using RAF.Core.Carving;
using RAF.Core.FileSystems;
using RAF.Core.Native;
using RAF.Core.Signatures;
using Xunit;

namespace RAF.Core.Tests;

/// <summary>
/// שמות לקבצים מסריקה מתקדמת — מתוך המידע ששמור בהם — וסוגי הווידאו והשמע
/// שנוספו: מצלמות וידאו ביתיות, DVD, Windows Media, הקלטות AMR ו-OGG.
/// </summary>
public class CarvedMetadataTests : IDisposable
{
    private const int SectorSize = 512;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"raf-meta-{Guid.NewGuid():N}.img");
    private readonly MemoryStream _image = new();
    private RawDevice? _device;

    public void Dispose()
    {
        _device?.Dispose();
        try { File.Delete(_path); } catch { }
    }

    private long Place(byte[] content)
    {
        _image.Write(new byte[(SectorSize - _image.Length % SectorSize) % SectorSize]);
        long offset = _image.Length;
        _image.Write(content);
        return offset;
    }

    private RawVolume Open()
    {
        _image.Write(new byte[(SectorSize - _image.Length % SectorSize) % SectorSize + SectorSize * 4]);
        File.WriteAllBytes(_path, _image.ToArray());
        _device = RawDevice.TryOpen(_path, SectorSize) ?? throw new IOException("cannot open image");
        return RawVolume.Open(VolumeReader.Wrap(_device, 0, _image.Length), SectorSize);
    }

    private static CarvedInfo? Info(byte[] file, string structure)
    {
        var signature = FileSignatures.All.First(s => s.Structure == structure && s.Matches(file));
        return CarvedMetadata.Read(signature, file.Length, (at, count) =>
            at >= file.Length ? [] : file.AsSpan((int)at, (int)Math.Min(count, file.Length - at)).ToArray());
    }

    // ============================================================ בוני קבצים

    /// <summary>JPEG אמיתי עם מקטע Exif: יצרן, דגם ותאריך צילום.</summary>
    private static byte[] PhotoWithExif(string make, string model, string taken, int seed = 1)
    {
        byte[] Ascii(string s) => [.. Encoding.ASCII.GetBytes(s), 0];
        byte[] makeBytes = Ascii(make), modelBytes = Ascii(model), dateBytes = Ascii(taken);

        // מבנה TIFF: מדור ראשי (3 רשומות), מדור Exif (רשומה אחת), ואחריהם הנתונים.
        const int ifd0 = 8, exifIfd = ifd0 + 2 + 3 * 12 + 4;
        int data = exifIfd + 2 + 12 + 4;
        var tiff = new byte[data + makeBytes.Length + modelBytes.Length + dateBytes.Length];
        "II*\0"u8.CopyTo(tiff);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), ifd0);

        void Entry(int at, ushort tag, ushort type, int count, int value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(at), tag);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(at + 2), type);
            BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(at + 4), (uint)count);
            BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(at + 8), (uint)value);
        }

        int makeAt = data, modelAt = makeAt + makeBytes.Length, dateAt = modelAt + modelBytes.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(ifd0), 3);
        Entry(ifd0 + 2, 0x010F, 2, makeBytes.Length, makeAt);
        Entry(ifd0 + 14, 0x0110, 2, modelBytes.Length, modelAt);
        Entry(ifd0 + 26, 0x8769, 4, 1, exifIfd);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(exifIfd), 1);
        Entry(exifIfd + 2, 0x9003, 2, dateBytes.Length, dateAt);
        makeBytes.CopyTo(tiff, makeAt);
        modelBytes.CopyTo(tiff, modelAt);
        dateBytes.CopyTo(tiff, dateAt);

        byte[] jpeg = JpegEncoder.Encode(16, 16, seed);
        byte[] app1 = [.. "Exif\0\0"u8, .. tiff];
        byte[] segment = [0xFF, 0xE1, (byte)((app1.Length + 2) >> 8), (byte)(app1.Length + 2), .. app1];
        return [.. jpeg[..2], .. segment, .. jpeg[2..]];
    }

    /// <summary>מסמך Word אמיתי (ZIP) עם כותרת ותאריך ב-docProps/core.xml.</summary>
    private static byte[] WordDocument(string title)
    {
        using var s = new MemoryStream();
        using (var zip = new ZipArchive(s, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string text)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                w.Write(text);
            }
            Add("[Content_Types].xml", "<Types/>");
            Add("word/document.xml", "<w:document>" + new string('x', 3000) + "</w:document>");
            Add("docProps/core.xml",
                "<cp:coreProperties xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\">" +
                $"<dc:title>{title}</dc:title><dcterms:modified xsi:type=\"dcterms:W3CDTF\">2025-03-10T08:00:00Z</dcterms:modified>" +
                "</cp:coreProperties>");
        }
        return s.ToArray();
    }

    /// <summary>MP3 עם תג ID3v2.3: שם שיר ומבצע, בקידוד שנבחר.</summary>
    private static byte[] Song(string artist, string title, int encoding)
    {
        byte[] Text(string value) => encoding switch
        {
            1 => [1, 0xFF, 0xFE, .. Encoding.Unicode.GetBytes(value)],
            3 => [3, .. Encoding.UTF8.GetBytes(value)],
            _ => [0, .. CodePage1255().GetBytes(value)],
        };
        byte[] Frame(string id, byte[] body)
        {
            byte[] f = new byte[10 + body.Length];
            Encoding.ASCII.GetBytes(id).CopyTo(f, 0);
            BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(4), (uint)body.Length);
            body.CopyTo(f, 10);
            return f;
        }
        byte[] frames = [.. Frame("TIT2", Text(title)), .. Frame("TPE1", Text(artist))];
        int size = frames.Length;
        byte[] header = [.. "ID3"u8, 3, 0, 0, (byte)((size >> 21) & 0x7F), (byte)((size >> 14) & 0x7F), (byte)((size >> 7) & 0x7F), (byte)(size & 0x7F)];
        return [.. header, .. frames, 0xFF, 0xFB, 0x90, 0x00, .. RealFormats.Random(4000, 3)];
    }

    private static Encoding CodePage1255()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1255);
    }

    // ============================================================= שמות

    [Fact]
    public void A_photo_is_named_by_when_it_was_taken_and_with_which_camera()
    {
        var info = Info(PhotoWithExif("Canon", "Canon EOS 80D", "2024:07:14 18:05:33"), "jpg");

        Assert.Equal("2024-07-14 18-05-33 Canon EOS 80D", info?.Name);
        Assert.Equal(new DateTime(2024, 7, 14, 18, 5, 33), info?.Date);
    }

    [Fact]
    public void Brand_is_added_only_when_the_model_does_not_already_say_it()
    {
        Assert.Equal("2023-01-02 03-04-05 samsung SM-G991B",
            Info(PhotoWithExif("samsung", "SM-G991B", "2023:01:02 03:04:05"), "jpg")?.Name);
    }

    [Fact]
    public void A_word_document_found_as_zip_gets_its_real_type_and_its_title()
    {
        var info = Info(WordDocument("סיכום שנתי 2025"), "zip");

        Assert.Equal("docx", info?.Extension);
        Assert.Equal("מסמך Word", info?.Folder);
        Assert.Equal("סיכום שנתי 2025", info?.Name);
        Assert.Equal(2025, info?.Date?.Year);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(0)]   // קידוד "Latin-1" שהוא בפועל Windows-1255 — שירים עבריים ישנים
    public void A_song_is_named_artist_dash_title(int encoding)
        => Assert.Equal("אריק איינשטיין - אני ואתה", Info(Song("אריק איינשטיין", "אני ואתה", encoding), "mp3")?.Name);

    [Fact]
    public void A_pdf_title_comes_from_its_info_dictionary_even_in_utf16()
    {
        byte[] title = [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("חוזה שכירות")];
        string hex = Convert.ToHexString(title);
        string pdf = "%PDF-1.4\n1 0 obj\n<< /Type /Catalog >>\nendobj\n" +
                     $"2 0 obj\n<< /Title <{hex}> /CreationDate (D:20220315120000+02'00') >>\nendobj\n" +
                     "trailer\n<< /Root 1 0 R /Info 2 0 R >>\n%%EOF\n";

        var info = Info(Encoding.Latin1.GetBytes(pdf), "pdf");

        Assert.Equal("חוזה שכירות", info?.Name);
        Assert.Equal(new DateTime(2022, 3, 15, 12, 0, 0), info?.Date);
    }

    [Theory]
    [InlineData("Microsoft Word - דוח רבעוני.docx", "דוח רבעוני")]
    [InlineData("a/b:c*d?", "a b c d")]
    [InlineData("   ", null)]
    [InlineData("Untitled", null)]
    public void Names_are_cleaned_for_the_file_system(string raw, string? expected)
    {
        string title = "FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(raw));
        string pdf = $"%PDF-1.4\n2 0 obj\n<< /Title <{title}> >>\nendobj\ntrailer\n<< /Info 2 0 R >>\n%%EOF\n";
        Assert.Equal(expected, Info(Encoding.Latin1.GetBytes(pdf), "pdf")?.Name);
    }

    [Fact]
    public void An_old_word_document_is_told_apart_from_excel_by_its_stream_names()
    {
        byte[] ole = new byte[2048];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(ole, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(ole.AsSpan(0x1E), 9);             // סקטורים של 512
        BinaryPrimitives.WriteUInt32LittleEndian(ole.AsSpan(0x30), 0);             // הספרייה בסקטור 0 (אחרי הכותרת)
        void Name(int entry, string name)
        {
            byte[] n = Encoding.Unicode.GetBytes(name + "\0");
            n.CopyTo(ole, 512 + entry * 128);
            BinaryPrimitives.WriteUInt16LittleEndian(ole.AsSpan(512 + entry * 128 + 64), (ushort)n.Length);
        }
        Name(0, "Root Entry");
        Name(1, "WordDocument");

        Assert.Equal("doc", Info(ole, "doc")?.Extension);
        Name(1, "Workbook");
        Assert.Equal("xls", Info(ole, "doc")?.Extension);
    }

    [Fact]
    public void A_video_is_named_by_its_recording_time_and_iphone_video_becomes_mov()
    {
        var recorded = new DateTime(2021, 5, 1, 9, 30, 0, DateTimeKind.Utc);
        uint seconds = (uint)(recorded - new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        byte[] mvhd = new byte[108];
        BinaryPrimitives.WriteUInt32BigEndian(mvhd, 108);
        "mvhd"u8.CopyTo(mvhd.AsSpan(4));
        BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(12), seconds);

        byte[] Box(string type, byte[] body)
        {
            byte[] b = new byte[8 + body.Length];
            BinaryPrimitives.WriteUInt32BigEndian(b, (uint)b.Length);
            Encoding.ASCII.GetBytes(type).CopyTo(b, 4);
            body.CopyTo(b, 8);
            return b;
        }
        byte[] movie = [.. Box("ftyp", [.. "qt  "u8, 0, 0, 0, 0, .. "qt  "u8]), .. Box("mdat", new byte[1000]), .. Box("moov", mvhd)];

        var info = Info(movie, "mp4");

        Assert.Equal("mov", info?.Extension);
        Assert.Equal(recorded.ToLocalTime(), info?.Date);
        Assert.Equal(recorded.ToLocalTime().ToString("yyyy-MM-dd HH-mm-ss"), info?.Name);
    }

    [Fact]
    public void The_advanced_scan_names_what_it_finds_and_sorts_it_into_real_types()
    {
        Place(PhotoWithExif("Apple", "iPhone 12", "2024:12:25 10:00:00"));
        Place(WordDocument("מכתב לסבתא"));
        Place(RealFormats.Png(2000, 5));                                          // בלי מידע — מספר

        var files = new FileCarver().Sweep(Open(), _image.Length, SectorSize, null, CancellationToken.None).Files;

        var photo = files.Single(f => f.Extension == "jpg");
        Assert.Equal("2024-12-25 10-00-00 Apple iPhone 12.jpg", photo.Name);
        Assert.Equal(new DateTime(2024, 12, 25, 10, 0, 0), photo.Modified);

        var doc = files.Single(f => f.Extension == "docx");
        Assert.Equal("מכתב לסבתא.docx", doc.Name);
        Assert.Equal("מסמך Word", doc.Path);

        Assert.Matches(@"^\d{6}_[0-9A-F]+\.png$", files.Single(f => f.Extension == "png").Name);
    }

    // ===================================================== סוגים חדשים — אורך

    private ResolvedLength Resolve(byte[] content, string structure, byte[]? followedBy = null)
    {
        long offset = Place(content);
        _image.Write(followedBy ?? RealFormats.Random(8192, 99));
        var volume = Open();
        var signature = FileSignatures.All.First(s => s.Structure == structure && s.Matches(content));
        return FileLength.Resolve(signature, volume, offset, _image.Length - offset);
    }

    /// <summary>זרם TS: חבילות עם 0x47 במקומו, והראשונה היא טבלת התוכניות.</summary>
    private static byte[] TransportStream(int packets, int packetSize)
    {
        int sync = packetSize - 188;
        byte[] data = RealFormats.Random(packets * packetSize, 7);
        for (int k = 0; k < packets; k++) data[k * packetSize + sync] = 0x47;
        new byte[] { 0x47, 0x40, 0x00, 0x10, 0x00, 0x00, 0xB0 }.CopyTo(data, sync);
        return data;
    }

    [Theory]
    [InlineData(192, "m2ts", 20_000)]     // AVCHD — כ-3.8MB, כולל דגימה של מגה-בייט
    [InlineData(188, "ts", 300)]
    public void A_transport_stream_ends_where_its_packets_stop(int packetSize, string structure, int packets)
    {
        byte[] stream = TransportStream(packets, packetSize);
        var r = Resolve(stream, structure);

        Assert.Equal(stream.Length, r.Bytes);
        Assert.Equal(LengthConfidence.Exact, r.Confidence);
    }

    [Fact]
    public void A_dvd_program_stream_ends_at_its_end_code()
    {
        using var s = new MemoryStream();
        for (int k = 0; k < 40; k++)
        {
            byte[] pack = new byte[2048];
            new byte[] { 0, 0, 1, 0xBA, 0x44, 0, 4, 0, 4, 1, 1, 0x89, 0xC3, 0xF8 }.CopyTo(pack, 0);
            int at = 14;
            if (k == 0)
            {
                new byte[] { 0, 0, 1, 0xBB, 0, 12 }.CopyTo(pack, at);                 // כותרת המערכת
                at += 6 + 12;
            }
            int length = 2048 - at - 6;
            new byte[] { 0, 0, 1, 0xE0, (byte)(length >> 8), (byte)length }.CopyTo(pack, at);
            s.Write(pack);
        }
        s.Write([0, 0, 1, 0xB9]);

        var r = Resolve(s.ToArray(), "mpg");
        Assert.Equal(s.Length, r.Bytes);
    }

    [Fact]
    public void Windows_media_length_comes_from_its_file_properties()
    {
        byte[] header = new byte[30 + 104];
        new byte[] { 0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C }.CopyTo(header, 0);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16), header.Length);
        new byte[] { 0xA1, 0xDC, 0xAB, 0x8C, 0x47, 0xA9, 0xCF, 0x11, 0x8E, 0xE4, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65 }.CopyTo(header, 30);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(46), 104);
        byte[] data = new byte[5000];
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(16), data.Length);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(30 + 40), header.Length + data.Length);

        Assert.Equal(header.Length + data.Length, Resolve([.. header, .. data], "asf").Bytes);
        Assert.Equal("wma", Info([.. header, .. data], "asf")?.Extension);           // אין זרם וידאו
    }

    [Fact]
    public void An_amr_recording_ends_at_its_last_valid_frame()
    {
        using var s = new MemoryStream();
        s.Write("#!AMR\n"u8);
        for (int i = 0; i < 200; i++)
        {
            s.WriteByte(7 << 3 | 4);                                                  // מצב 12.2 — 31 בתים
            s.Write(RealFormats.Random(31, i));
        }

        var r = Resolve(s.ToArray(), "amr", followedBy: Enumerable.Repeat((byte)0xFF, 4096).ToArray());
        Assert.Equal(s.Length, r.Bytes);
    }

    /// <summary>
    /// נמצא בבדיקה על קבצים של ffmpeg: החבילה הראשונה בזרם היא טבלת השירותים (SDT,
    /// מזהה 0x11) ולא טבלת התוכניות — והזרם לא זוהה בכלל.
    /// </summary>
    [Fact]
    public void A_transport_stream_that_opens_with_another_table_is_still_found()
    {
        byte[] stream = TransportStream(100, 188);
        stream[2] = 0x11;                                                           // SDT

        Assert.Equal(stream.Length, Resolve(stream, "ts").Bytes);
    }

    /// <summary>נמצא בבדיקה: אפסים שאחרי הקלטת AMR נקראו כמסגרות, והקובץ בלע את הבא אחריו.</summary>
    [Fact]
    public void Zeros_after_an_amr_recording_are_not_frames()
    {
        using var s = new MemoryStream();
        s.Write("#!AMR\n"u8);
        for (int i = 0; i < 50; i++) { s.WriteByte(7 << 3 | 4); s.Write(RealFormats.Random(31, i)); }

        Assert.Equal(s.Length, Resolve(s.ToArray(), "amr", followedBy: new byte[8192]).Bytes);
    }

    /// <summary>
    /// נמצא בבדיקה: דגימה בגבולות 2048 המשיכה לתוך סרט MPEG אחר שישב אחרי הקובץ.
    /// הסרט הראשון נגמר בקוד הסיום שלו.
    /// </summary>
    [Fact]
    public void A_program_stream_does_not_run_into_the_next_one()
    {
        byte[] Movie(int packs)
        {
            using var s = new MemoryStream();
            for (int k = 0; k < packs; k++)
            {
                byte[] pack = new byte[2048];
                new byte[] { 0, 0, 1, 0xBA, 0x44, 0, 4, 0, 4, 1, 1, 0x89, 0xC3, 0xF8, 0, 0, 1, 0xBB, 0, 12 }.CopyTo(pack, 0);
                int length = 2048 - 32 - 6;
                new byte[] { 0, 0, 1, 0xE0, (byte)(length >> 8), (byte)length }.CopyTo(pack, 32);
                s.Write(pack);
            }
            s.Write([0, 0, 1, 0xB9]);
            return s.ToArray();
        }
        byte[] first = Movie(600);                                                  // יותר ממגה-בייט
        byte[] padding = new byte[(2048 - first.Length % 2048) % 2048];

        Assert.Equal(first.Length, Resolve(first, "mpg", followedBy: [.. padding, .. Movie(600)]).Bytes);
    }

    [Fact]
    public void An_ogg_file_ends_at_the_page_marked_last()
    {
        byte[] Page(int flags, byte[] body)
        {
            byte[] p = new byte[27 + 1 + body.Length];
            "OggS"u8.CopyTo(p);
            p[5] = (byte)flags;
            BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(14), 1234);
            p[26] = 1;
            p[27] = (byte)body.Length;
            body.CopyTo(p, 28);
            return p;
        }
        byte[] ogg = [.. Page(2, [.. "OpusHead"u8, .. new byte[11]]), .. Page(0, new byte[200]), .. Page(4, new byte[100])];

        Assert.Equal(ogg.Length, Resolve(ogg, "ogg", followedBy: [.. "OggS"u8, .. new byte[100]]).Bytes);
        Assert.Equal("opus", Info(ogg, "ogg")?.Extension);
    }
}
