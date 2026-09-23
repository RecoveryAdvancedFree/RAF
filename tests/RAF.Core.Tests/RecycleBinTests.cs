using System.Buffers.Binary;
using System.Text;
using RAF.Core.FileSystems;
using RAF.Core.Model;
using Xunit;

namespace RAF.Core.Tests;

/// <summary>
/// שמות מקוריים מסל המחזור. כשמרוקנים את הסל, הקבצים שבו נראים בסריקה
/// כ-‎$RX3F9K2.jpg — שם שאומר למשתמש כלום. הקובץ הקטן ‎$I שלצדו שומר את
/// השם, התיקייה וזמן המחיקה, בשני פורמטים: של Vista עד Windows 8, ושל Windows 10.
/// </summary>
public class RecycleBinTests
{
    private const string Bin = @"$Recycle.Bin\S-1-5-21-1004336348-1177238915-682003330-1001";
    private static readonly DateTime DeletedAt = new(2026, 9, 20, 14, 30, 0, DateTimeKind.Local);

    /// <summary>‎$I בפורמט של Windows 10: אורך הנתיב ואחריו הנתיב.</summary>
    private static byte[] InfoV2(string originalPath, long size = 12345)
    {
        byte[] path = Encoding.Unicode.GetBytes(originalPath + "\0");
        byte[] data = new byte[28 + path.Length];
        BinaryPrimitives.WriteInt64LittleEndian(data, 2);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(8), size);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(16), DeletedAt.ToFileTime());
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24), path.Length / 2);
        path.CopyTo(data, 28);
        return data;
    }

    /// <summary>‎$I בפורמט של Vista עד Windows 8: נתיב באורך קבוע של 260 תווים.</summary>
    private static byte[] InfoV1(string originalPath)
    {
        byte[] data = new byte[24 + 520];
        BinaryPrimitives.WriteInt64LittleEndian(data, 1);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(16), DeletedAt.ToFileTime());
        Encoding.Unicode.GetBytes(originalPath).CopyTo(data, 24);
        return data;
    }

    private static RecoveredFile Info(long id, string name, byte[] data, DateTime? modified = null) => new()
    {
        Id = id, Name = name, Path = Bin, Size = data.Length, IsDeleted = true,
        ResidentData = data, Modified = modified,
    };

    private static RecoveredFile Content(long id, string name, string path = Bin, bool directory = false) => new()
    {
        Id = id, Name = name, Path = path, Size = directory ? 0 : 5000, IsDeleted = true, IsDirectory = directory,
    };

    private static int Apply(List<RecoveredFile> files) => RecycleBinNames.Apply(files, f => f.ResidentData!);

    [Fact]
    public void A_file_emptied_from_the_bin_gets_its_name_and_folder_back()
    {
        var photo = Content(10, "$RX3F9K2.jpg");
        var files = new List<RecoveredFile>
        {
            Info(11, "$IX3F9K2.jpg", InfoV2(@"C:\Users\יוסי\Pictures\חתונה\כלה.jpg")),
            photo,
        };

        Assert.Equal(1, Apply(files));
        Assert.Equal("כלה.jpg", photo.Name);
        Assert.Equal(@"Users\יוסי\Pictures\חתונה", photo.Path);
        Assert.Equal(DeletedAt, photo.RecycledAt);
    }

    [Fact]
    public void The_older_windows_format_is_read_too()
    {
        var doc = Content(20, "$RAB12CD.docx");
        Apply(new List<RecoveredFile> { Info(21, "$IAB12CD.docx", InfoV1(@"D:\עבודה\דוח.docx")), doc });

        Assert.Equal("דוח.docx", doc.Name);
        Assert.Equal("עבודה", doc.Path);
    }

    [Fact]
    public void A_folder_deleted_to_the_bin_takes_the_files_inside_it_back_to_their_path()
    {
        var folder = Content(30, "$R7QW2ZT", directory: true);
        var inside = Content(31, "IMG_0001.jpg", Bin + @"\$R7QW2ZT\טיול");

        var files = new List<RecoveredFile>
        {
            Info(32, "$I7QW2ZT", InfoV2(@"C:\Users\יוסי\Desktop\תמונות 2025")),
            folder, inside,
        };

        Assert.Equal(1, Apply(files));
        Assert.Equal("תמונות 2025", folder.Name);
        Assert.Equal(@"Users\יוסי\Desktop", folder.Path);
        Assert.Equal(@"Users\יוסי\Desktop\תמונות 2025\טיול", inside.Path);
        Assert.Equal("IMG_0001.jpg", inside.Name);
        Assert.Equal(DeletedAt, inside.RecycledAt);
    }

    [Fact]
    public void When_the_same_name_was_used_twice_the_newest_record_wins()
    {
        var file = Content(40, "$R111111.txt");
        var files = new List<RecoveredFile>
        {
            Info(41, "$I111111.txt", InfoV2(@"C:\ישן.txt"), modified: new DateTime(2025, 1, 1)),
            Info(42, "$I111111.txt", InfoV2(@"C:\חדש.txt"), modified: new DateTime(2026, 9, 1)),
            file,
        };

        Apply(files);
        Assert.Equal("חדש.txt", file.Name);
    }

    [Fact]
    public void Files_outside_the_bin_and_bytes_that_are_not_a_record_are_left_alone()
    {
        var elsewhere = Content(50, "$Rfile.txt", @"Users\יוסי");
        var random = new byte[544];
        new Random(3).NextBytes(random);
        var inBin = Content(51, "$R222222.txt");

        var files = new List<RecoveredFile>
        {
            new() { Id = 52, Name = "$Ifile.txt", Path = @"Users\יוסי", Size = 100, ResidentData = InfoV2(@"C:\x.txt") },
            Info(53, "$I222222.txt", random),
            elsewhere, inBin,
        };

        Assert.Equal(0, Apply(files));
        Assert.Equal("$Rfile.txt", elsewhere.Name);
        Assert.Equal("$R222222.txt", inBin.Name);
        Assert.Null(RecycleBinNames.Parse(random));
    }
}
