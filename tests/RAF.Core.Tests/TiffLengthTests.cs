using System.Buffers.Binary;
using RAF.Core.Carving;
using RAF.Core.FileSystems;
using RAF.Core.Native;
using RAF.Core.Repair;
using Xunit;

namespace RAF.Core.Tests;

/// <summary>
/// אורך של TIFF ושל קובצי RAW שנשמרים כמוהו. אין בהם שדה "הקובץ נגמר כאן" —
/// הסוף הוא הנתון הרחוק ביותר שהתגיות מצביעות אליו, גם בתוך תת-רשימה.
///
/// על קבצים אמיתיים (7 מהמחשב ו-7 שנוצרו: רב-עמודי, אריחים, LZW, JPEG, EXIF,
/// וקובץ Ghostscript של 43MB) האורך יצא זהה לגודל הקובץ. קובצי RAW אמיתיים לא
/// היו זמינים, ולכן המבנה שלהם נבנה כאן: תמונת ה-RAW בתת-רשימה, כמו ב-NEF וב-ARW.
/// </summary>
public class TiffLengthTests : IDisposable
{
    private const int Sector = 512;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"raf-tiff-{Guid.NewGuid():N}.img");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"raf-tiff-{Guid.NewGuid():N}");
    private RawDevice? _device;

    public TiffLengthTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _device?.Dispose();
        try { File.Delete(_path); } catch { }
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// בונה TIFF מרשימות: כל רשימה היא רצף תגיות, ותגית עם נתונים מחוץ לרשימה
    /// מקבלת אותם בהיסט שנקבע כאן. bigEndian — סדר הבתים של "MM".
    /// </summary>
    private sealed class Builder(bool bigEndian = false, ushort magic = 42)
    {
        private readonly List<byte> _data = new(new byte[8]);

        public int Here => _data.Count;

        public int Blob(int length, byte fill)
        {
            int at = _data.Count;
            _data.AddRange(Enumerable.Repeat(fill, length));
            return at;
        }

        /// <summary>רשימה בהיסט הנוכחי. entries: (תגית, סוג, כמות, ערך או היסט).</summary>
        public int Ifd((ushort Tag, ushort Type, uint Count, uint Value)[] entries, uint next = 0)
        {
            int at = _data.Count;
            var bytes = new byte[2 + entries.Length * 12 + 4];
            U16(bytes, 0, (ushort)entries.Length);
            for (int i = 0; i < entries.Length; i++)
            {
                var (tag, type, count, value) = entries[i];
                U16(bytes, 2 + i * 12, tag);
                U16(bytes, 4 + i * 12, type);
                U32(bytes, 6 + i * 12, count);
                if (type == 3 && count == 1) U16(bytes, 10 + i * 12, (ushort)value);
                else U32(bytes, 10 + i * 12, value);
            }
            U32(bytes, bytes.Length - 4, next);
            _data.AddRange(bytes);
            return at;
        }

        public byte[] Finish(int firstIfd)
        {
            byte[] result = _data.ToArray();
            result[0] = result[1] = (byte)(bigEndian ? 'M' : 'I');
            U16(result, 2, magic);
            U32(result, 4, (uint)firstIfd);
            return result;
        }

        public void Patch(int at, uint value)
        {
            byte[] b = new byte[4];
            U32(b, 0, value);
            for (int i = 0; i < 4; i++) _data[at + i] = b[i];
        }

        private void U16(byte[] b, int at, ushort v)
        {
            if (bigEndian) BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(at), v);
            else BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), v);
        }

        private void U32(byte[] b, int at, uint v)
        {
            if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(at), v);
            else BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), v);
        }
    }

    private long Length(byte[] file)
    {
        byte[] image = new byte[(file.Length / Sector + 2) * Sector];
        file.CopyTo(image, 0);
        File.WriteAllBytes(_path, image);
        _device = RawDevice.TryOpen(_path, Sector)!;
        var volume = RawVolume.Open(VolumeReader.Wrap(_device, 0, image.Length), Sector);
        return StructureCheck.ReadTiff(new WindowReader(volume, 0, image.Length));
    }

    /// <summary>
    /// מבנה של RAW מודרני (NEF, ARW): ברשימה הראשית תמונה ממוזערת ורשימת EXIF;
    /// תמונת ה-RAW עצמה — הנתון הגדול והרחוק — רק בתת-רשימה.
    /// </summary>
    private static byte[] CameraRaw(bool bigEndian = false, ushort magic = 42)
    {
        // הסדר כמו בקובץ אמיתי: הרשימה הראשית בהתחלה, ותמונת ה-RAW — הנתון הגדול —
        // בסוף הקובץ, כשרק תת-הרשימה מצביעה אליה. ההיסטים נכתבים אחרי שידועים.
        var b = new Builder(bigEndian, magic);
        int main = b.Ifd(new (ushort, ushort, uint, uint)[]
        {
            (0x110, 2, 40, 0),                                          // שם המצלמה — נתון מחוץ לרשימה
            (0x201, 4, 1, 0), (0x202, 4, 1, 3000),                      // תמונה ממוזערת
            (0x14A, 4, 1, 0),                                           // תת-רשימה
            (0x8769, 4, 1, 0),                                          // רשימת EXIF
        });
        int model = b.Blob(40, 0x41);
        int thumb = b.Blob(3000, 0x11);
        int exifIfd = b.Ifd(new (ushort, ushort, uint, uint)[] { (0x9003, 2, 20, (uint)model) });
        int subIfd = b.Ifd(new (ushort, ushort, uint, uint)[] { (0x111, 4, 1, 0), (0x117, 4, 1, 50_000) });
        int raw = b.Blob(50_000, 0x22);

        b.Patch(main + 2 + 0 * 12 + 8, (uint)model);
        b.Patch(main + 2 + 1 * 12 + 8, (uint)thumb);
        b.Patch(main + 2 + 3 * 12 + 8, (uint)subIfd);
        b.Patch(main + 2 + 4 * 12 + 8, (uint)exifIfd);
        b.Patch(subIfd + 2 + 0 * 12 + 8, (uint)raw);
        return b.Finish(main);
    }

    [Fact]
    public void The_raw_image_in_a_sub_ifd_is_the_end_of_a_camera_raw_file()
    {
        byte[] file = CameraRaw();
        Assert.Equal(file.Length, Length(file));
    }

    [Fact]
    public void Without_following_the_sub_ifd_the_raw_image_would_be_cut_off()
    {
        // אותו קובץ, כשתת-הרשימה אינה מסומנת כתת-רשימה: הקורא לא מגיע לתמונה,
        // והאורך נגמר לפניה. מוכיח שהבדיקה הקודמת תלויה במעקב אחרי תת-הרשימה.
        byte[] file = CameraRaw();
        var main = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4));
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan((int)main + 2 + 3 * 12), 0x9999);
        Assert.True(Length(file) < file.Length - 40_000);
    }

    [Fact]
    public void Motorola_byte_order_is_read_too()
    {
        byte[] file = CameraRaw(bigEndian: true);
        Assert.Equal(file.Length, Length(file));
    }

    [Fact]
    public void Strips_and_tiles_across_several_pages_set_the_end()
    {
        var b = new Builder();
        int strip1 = b.Blob(7000, 1), strip2 = b.Blob(5000, 2);
        int tile = b.Blob(9000, 3);
        int second = b.Ifd(new (ushort, ushort, uint, uint)[] { (0x144, 4, 1, (uint)tile), (0x145, 4, 1, 9000) });

        int offsets = b.Blob(8, 0), counts = b.Blob(8, 0);
        b.Patch(offsets, (uint)strip1); b.Patch(offsets + 4, (uint)strip2);
        b.Patch(counts, 7000); b.Patch(counts + 4, 5000);
        int first = b.Ifd(new (ushort, ushort, uint, uint)[] { (0x111, 4, 2, (uint)offsets), (0x117, 4, 2, (uint)counts) },
            next: (uint)second);

        byte[] file = b.Finish(first);
        Assert.Equal(file.Length, Length(file));
    }

    [Fact]
    public void A_truncated_raw_file_is_diagnosed_as_missing_data_and_not_offered_a_fix()
    {
        // נחתך באמצע תמונת ה-RAW: התגיות מצביעות למקום שאינו קיים עוד.
        byte[] cut = CameraRaw().AsSpan(0, 20_000).ToArray();
        string path = Path.Combine(_dir, "cut.nef");
        File.WriteAllBytes(path, cut);

        var issue = Assert.Single(FileDoctor.Diagnose(path).Issues);
        Assert.Equal(FileIssueKind.Truncated, issue.Kind);
        Assert.False(issue.Fixable);
    }

    [Fact]
    public void Panasonic_rw2_is_left_to_an_estimate()
        => Assert.Equal(0, Length(CameraRaw(magic: 0x55)));

    [Fact]
    public void The_doctor_never_offers_to_cut_a_raw_file_shorter()
    {
        // נתונים אחרי הסוף שהתגיות מכירות — אולי נתון שמצלמה שומרת בלי תגית רגילה.
        byte[] file = [.. CameraRaw(), .. Enumerable.Repeat((byte)0x5A, 4000)];
        string path = Path.Combine(_dir, "photo.nef");
        File.WriteAllBytes(path, file);

        Assert.DoesNotContain(FileDoctor.Diagnose(path).Issues, i => i.Kind == FileIssueKind.TrailingData);
    }
}
