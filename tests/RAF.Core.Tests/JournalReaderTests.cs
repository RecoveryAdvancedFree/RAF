using System.Buffers.Binary;
using System.Text;
using RAF.Core.FileSystems.Ntfs;
using Xunit;

namespace RAF.Core.Tests;

/// <summary>
/// בדיקות מפענחי היומנים.
///
/// שני היומנים מאתרים קבצים שרשומת ה-MFT שלהם כבר נדרסה. מכיוון שהזיהוי
/// ב-‎$LogFile הוא הֶיוּרִיסטִי — סריקה אחר מבנים בתוך נתונים בינאריים —
/// חשוב במיוחד לוודא שהוא אינו מייצר התאמות שווא, שכן כל התאמה כזו
/// מוצגת למשתמש כקובץ שהיה קיים.
/// </summary>
public class JournalReaderTests
{
    private static readonly DateTime Created = new(2024, 6, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Modified = new(2025, 2, 14, 17, 30, 0, DateTimeKind.Utc);

    // ==================================================== יומן השינויים

    /// <summary>בניית רשומת USN בגרסה 2, בפורמט הבינארי המדויק.</summary>
    private static byte[] UsnV2(string name, long fileRef, long parentRef, uint reason, uint attributes = 0)
    {
        byte[] nameBytes = Encoding.Unicode.GetBytes(name);
        int length = Align8(60 + nameBytes.Length);
        byte[] r = new byte[length];

        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(0), (uint)length);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(4), 2);   // גרסה ראשית
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(6), 0);
        BinaryPrimitives.WriteInt64LittleEndian(r.AsSpan(8), fileRef);
        BinaryPrimitives.WriteInt64LittleEndian(r.AsSpan(16), parentRef);
        BinaryPrimitives.WriteInt64LittleEndian(r.AsSpan(24), 0x1234);          // USN
        BinaryPrimitives.WriteInt64LittleEndian(r.AsSpan(32), Modified.ToFileTimeUtc());
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(40), reason);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(52), attributes);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(56), (ushort)nameBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(58), 60);
        nameBytes.CopyTo(r, 60);

        return r;
    }

    /// <summary>בניית רשומת USN בגרסה 3, עם מזהים בני 16 בתים.</summary>
    private static byte[] UsnV3(string name, long fileRef, long parentRef, uint reason)
    {
        byte[] nameBytes = Encoding.Unicode.GetBytes(name);
        int length = Align8(76 + nameBytes.Length);
        byte[] r = new byte[length];

        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(0), (uint)length);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(4), 3);
        BinaryPrimitives.WriteInt64LittleEndian(r.AsSpan(8), fileRef);
        BinaryPrimitives.WriteInt64LittleEndian(r.AsSpan(24), parentRef);
        BinaryPrimitives.WriteInt64LittleEndian(r.AsSpan(40), 0x5678);
        BinaryPrimitives.WriteInt64LittleEndian(r.AsSpan(48), Modified.ToFileTimeUtc());
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(56), reason);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(72), (ushort)nameBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(74), 76);
        nameBytes.CopyTo(r, 76);

        return r;
    }

    private static List<UsnEntry> ParseAll(byte[] buffer, out int consumed)
    {
        var entries = new List<UsnEntry>();
        long counter = 0;
        consumed = UsnJournalReader.Parse(buffer, e => entries.Add(e), ref counter);
        return entries;
    }

    [Fact]
    public void Usn_v2_record_yields_name_parent_and_reason()
    {
        // מזהה עם מונה גרסה בביטים העליונים, כפי ש-NTFS שומר אותו.
        long fileRef = 4242 | (7L << 48);
        long parentRef = 99 | (3L << 48);

        byte[] buffer = UsnV2("דוח.docx", fileRef, parentRef, UsnReason.FileDelete | UsnReason.Close);
        var entries = ParseAll(buffer, out _);

        var e = Assert.Single(entries);
        Assert.Equal("דוח.docx", e.FileName);
        Assert.Equal(4242, e.FileRecord);
        Assert.Equal(7, e.FileSequence);
        Assert.Equal(99, e.ParentRecord);
        Assert.Equal(3, e.ParentSequence);
        Assert.True(e.IsDelete);
        Assert.Equal(Modified.ToLocalTime(), e.Timestamp);
    }

    [Fact]
    public void Usn_v3_record_is_parsed_from_its_wider_layout()
    {
        byte[] buffer = UsnV3("תמונה.jpg", 555, 12, UsnReason.FileDelete);
        var entries = ParseAll(buffer, out _);

        var e = Assert.Single(entries);
        Assert.Equal("תמונה.jpg", e.FileName);
        Assert.Equal(555, e.FileRecord);
        Assert.Equal(12, e.ParentRecord);
        Assert.True(e.IsDelete);
    }

    [Fact]
    public void Non_delete_records_are_still_parsed_but_not_marked_as_deletes()
    {
        byte[] buffer = UsnV2("חדש.txt", 10, 5, UsnReason.FileCreate);
        var e = Assert.Single(ParseAll(buffer, out _));

        Assert.False(e.IsDelete);
    }

    [Fact]
    public void Directory_records_are_identified_by_their_attributes()
    {
        byte[] buffer = UsnV2("תיקייה", 20, 5, UsnReason.FileDelete, attributes: 0x10);
        var e = Assert.Single(ParseAll(buffer, out _));

        Assert.True(e.IsDirectory);
    }

    [Fact]
    public void Multiple_records_are_read_in_sequence()
    {
        byte[] a = UsnV2("ראשון.txt", 1, 5, UsnReason.FileDelete);
        byte[] b = UsnV2("שני.txt", 2, 5, UsnReason.FileDelete);

        byte[] buffer = new byte[a.Length + b.Length];
        a.CopyTo(buffer, 0);
        b.CopyTo(buffer, a.Length);

        var entries = ParseAll(buffer, out int consumed);

        Assert.Equal(2, entries.Count);
        Assert.Equal(buffer.Length, consumed);
    }

    [Fact]
    public void Zero_padding_between_pages_is_skipped()
    {
        byte[] record = UsnV2("אחרי_ריפוד.txt", 7, 5, UsnReason.FileDelete);
        byte[] buffer = new byte[64 + record.Length];  // 64 בתי אפס לפני הרשומה
        record.CopyTo(buffer, 64);

        var e = Assert.Single(ParseAll(buffer, out _));
        Assert.Equal("אחרי_ריפוד.txt", e.FileName);
    }

    [Fact]
    public void A_record_cut_at_the_block_boundary_is_left_for_the_next_block()
    {
        byte[] full = UsnV2("חתוך.txt", 8, 5, UsnReason.FileDelete);
        byte[] truncated = full.AsSpan(0, full.Length - 10).ToArray();

        var entries = ParseAll(truncated, out int consumed);

        // הרשומה אינה מפוענחת, והמפענח מדווח שלא צרך אותה —
        // כך היא תיגרר לבלוק הבא במקום ללכת לאיבוד.
        Assert.Empty(entries);
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void An_absurd_record_length_aborts_instead_of_looping()
    {
        byte[] buffer = new byte[256];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xFFFFFFF);

        var entries = ParseAll(buffer, out int consumed);

        Assert.Empty(entries);
        Assert.Equal(buffer.Length, consumed);
    }

    // ==================================================== יומן הטרנזקציות

    /// <summary>בניית גוף ‎$FILE_NAME, כפי שהוא מופיע בשריד רשומה ביומן.</summary>
    private static byte[] FileNameAttribute(
        string name, long parentRecord = 55, ushort parentSequence = 2,
        long realSize = 4096, long allocated = 4096, uint flags = 0,
        long? createdTicks = null, long? modifiedTicks = null)
    {
        byte[] nameBytes = Encoding.Unicode.GetBytes(name);
        byte[] a = new byte[66 + nameBytes.Length];

        long parentRef = (parentRecord & 0x0000FFFFFFFFFFFF) | ((long)parentSequence << 48);
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(0), parentRef);
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(8), createdTicks ?? Created.ToFileTimeUtc());
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(16), modifiedTicks ?? Modified.ToFileTimeUtc());
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(40), allocated);
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(48), realSize);
        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(56), flags);
        a[64] = (byte)name.Length;
        a[65] = 1; // מרחב שמות Win32
        nameBytes.CopyTo(a, 66);

        return a;
    }

    [Fact]
    public void Logfile_name_structure_is_recognised()
    {
        var entry = LogFileReader.TryParseFileName(FileNameAttribute("מסמך חשוב.pdf"));

        Assert.NotNull(entry);
        Assert.Equal("מסמך חשוב.pdf", entry!.Value.Name);
        Assert.Equal(55, entry.Value.ParentRecord);
        Assert.Equal(4096, entry.Value.RealSize);
        Assert.Equal(Created.ToLocalTime(), entry.Value.Created);
        Assert.False(entry.Value.IsDirectory);
    }

    [Fact]
    public void Logfile_directory_flag_is_read()
    {
        var entry = LogFileReader.TryParseFileName(
            FileNameAttribute("תיקייה", flags: 0x10000000));

        Assert.NotNull(entry);
        Assert.True(entry!.Value.IsDirectory);
    }

    [Fact]
    public void Random_bytes_are_not_mistaken_for_file_names()
    {
        // ההגנה המרכזית מפני התאמות שווא: נתונים אקראיים לא אמורים
        // לייצר ולו שם אחד, אחרת המשתמש יראה "קבצים" שמעולם לא היו.
        var random = new Random(20260923);
        int falsePositives = 0;

        for (int round = 0; round < 200; round++)
        {
            byte[] noise = new byte[4096];
            random.NextBytes(noise);
            falsePositives += LogFileReader.ScanPage(noise, _ => { });
        }

        Assert.Equal(0, falsePositives);
    }

    [Fact]
    public void Zero_filled_pages_produce_nothing()
        => Assert.Equal(0, LogFileReader.ScanPage(new byte[4096], _ => { }));

    [Theory]
    [InlineData("שם\u0001עם בקרה")]   // תו בקרה
    [InlineData("נתיב\\לא\\חוקי")]     // תו אסור בשם קובץ
    [InlineData("   ")]                // רווחים בלבד
    public void Implausible_names_are_rejected(string name)
        => Assert.Null(LogFileReader.TryParseFileName(FileNameAttribute(name)));

    [Fact]
    public void Impossible_timestamps_are_rejected()
        => Assert.Null(LogFileReader.TryParseFileName(
            FileNameAttribute("קובץ.txt", createdTicks: 5)));

    [Fact]
    public void A_size_larger_than_its_allocation_is_rejected()
        => Assert.Null(LogFileReader.TryParseFileName(
            FileNameAttribute("קובץ.txt", realSize: 900_000, allocated: 4096)));

    [Fact]
    public void A_zero_parent_reference_is_rejected()
        => Assert.Null(LogFileReader.TryParseFileName(
            FileNameAttribute("קובץ.txt", parentRecord: 0)));

    [Fact]
    public void A_real_structure_inside_a_page_is_found_by_the_scanner()
    {
        byte[] page = new byte[4096];
        byte[] attribute = FileNameAttribute("נמצא_ביומן.xlsx");
        attribute.CopyTo(page, 512);   // מיושר ל-8 בתים, כמו ביומן אמיתי

        var found = new List<LogNameEntry>();
        int count = LogFileReader.ScanPage(page, found.Add);

        Assert.Equal(1, count);
        Assert.Equal("נמצא_ביומן.xlsx", found[0].Name);
    }

    private static int Align8(int value) => (value + 7) & ~7;
}
