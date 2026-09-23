using System.IO.Compression;
using RAF.Core.Model;
using RAF.Core.Recovery;
using Xunit;

namespace RAF.Core.Tests;

/// <summary>
/// בדיקות שמירת הסריקה לקובץ ופתיחתה.
///
/// קובץ סריקה הוא מה שמאפשר לשחזר אחרי שהתוכנה נסגרה — ולכן כל שדה
/// שהשחזור נשען עליו (מיקום התוכן, תוכן רזידנטי, דחיסה) חייב לחזור בדיוק.
/// שדה שאבד בדרך אינו שגיאה גלויה: השחזור פשוט יכתוב קובץ שגוי.
/// </summary>
public class ScanArchiveTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "raf-archive-" + Guid.NewGuid().ToString("N"));

    public ScanArchiveTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* ניקוי בלבד */ }
    }

    private string PathOf(string name) => Path.Combine(_folder, name + ScanArchive.Extension);

    private static ScanArchive Sample() => new()
    {
        Header = new ScanArchiveHeader
        {
            SavedAt = new DateTime(2026, 9, 23, 14, 30, 0),
            AppVersion = "0.2.0",
            Disk = new DiskIdentity("SanDisk 3.2Gen1", "4C530001", 28_700_000_000, null),
            PartitionTitle = "כונן E",
            Mode = ScanMode.Deep,
            FileCount = 2,
            RecoverableCount = 1,
        },
        PartitionOffset = 1_048_576,
        PartitionSize = 28_600_000_000,
        SectorSize = 4096,
        FileSystem = FileSystemKind.Ntfs,
        ReadThrough = true,
        Selected = new List<long> { 7 },
        Result = new ScanResult
        {
            Mode = ScanMode.Deep,
            Duration = TimeSpan.FromMinutes(42),
            Cancelled = true,
            FileSystem = "NTFS",
            RecordsExamined = 123_456,
            BytesRead = 9_876_543_210,
            Warnings = new List<string> { "אזהרה בעברית" },
            Files = new List<RecoveredFile>
            {
                new()
                {
                    Id = 7, ParentId = 5, Name = "דוח שנתי.pdf", Path = @"מסמכים\2025", Size = 70_000,
                    IsDeleted = true, Source = DiscoverySource.MftOrphan,
                    Created = new DateTime(2025, 1, 2, 3, 4, 5), Modified = new DateTime(2025, 6, 7, 8, 9, 10),
                    Quality = RecoveryQuality.Good, QualityReason = "נימוק", Content = ContentCheck.HasData,
                    Extents = new List<DataExtent> { new(100, 4, false), new(0, 8, true), new(900, 2, false) },
                    IsCompressed = true, CompressionUnitClusters = 16, NameIsPartial = true,
                },
                new()
                {
                    Id = 8, Name = "small.txt", Path = "", Size = 5,
                    ResidentData = new byte[] { 0, 1, 2, 255, 10 },
                    Content = ContentCheck.Empty,
                },
            },
        },
    };

    [Fact]
    public void Round_trip_keeps_every_field_recovery_depends_on()
    {
        string path = PathOf("full");
        ScanArchive.Save(Sample(), path);
        var loaded = ScanArchive.Load(path);

        Assert.Equal(1_048_576, loaded.PartitionOffset);
        Assert.Equal(28_600_000_000, loaded.PartitionSize);
        Assert.Equal(4096, loaded.SectorSize);
        Assert.Equal(FileSystemKind.Ntfs, loaded.FileSystem);
        Assert.True(loaded.ReadThrough);
        Assert.Equal(new[] { 7L }, loaded.Selected);

        var r = loaded.Result;
        Assert.Equal(ScanMode.Deep, r.Mode);
        Assert.Equal(TimeSpan.FromMinutes(42), r.Duration);
        Assert.True(r.Cancelled);
        Assert.Equal(123_456, r.RecordsExamined);
        Assert.Equal(9_876_543_210, r.BytesRead);
        Assert.Equal("אזהרה בעברית", Assert.Single(r.Warnings));

        var f = r.Files[0];
        Assert.Equal((7L, 5L, "דוח שנתי.pdf", @"מסמכים\2025", 70_000L), (f.Id, f.ParentId, f.Name, f.Path, f.Size));
        Assert.True(f.IsDeleted);
        Assert.Equal(DiscoverySource.MftOrphan, f.Source);
        Assert.Equal(new DateTime(2025, 1, 2, 3, 4, 5), f.Created);
        Assert.Equal(new DateTime(2025, 6, 7, 8, 9, 10), f.Modified);
        Assert.Null(f.Accessed);
        Assert.Equal(RecoveryQuality.Good, f.Quality);
        Assert.Equal("נימוק", f.QualityReason);
        Assert.Equal(ContentCheck.HasData, f.Content);
        Assert.Equal(new[] { new DataExtent(100, 4, false), new DataExtent(0, 8, true), new DataExtent(900, 2, false) },
                     f.Extents);
        Assert.True(f.IsCompressed);
        Assert.Equal(16, f.CompressionUnitClusters);
        Assert.True(f.NameIsPartial);

        // תוכן רזידנטי: הבתים עצמם, כולל אפסים ו-255.
        Assert.Equal(new byte[] { 0, 1, 2, 255, 10 }, r.Files[1].ResidentData);
        Assert.Equal(ContentCheck.Empty, r.Files[1].Content);

        // המאפיינים המחושבים נגזרים מחדש מהנתונים שחזרו.
        Assert.True(f.IsWorthRecovering);
        Assert.False(r.Files[1].IsWorthRecovering);
        Assert.Equal("pdf", f.Extension);
    }

    [Fact]
    public void Header_is_readable_without_the_body()
    {
        string path = PathOf("header");
        ScanArchive.Save(Sample(), path);

        var header = ScanArchive.ReadHeader(path);
        Assert.Equal("כונן E", header.PartitionTitle);
        Assert.Equal(2, header.FileCount);
        Assert.Equal("4C530001", header.Disk.SerialNumber);
        Assert.Equal(ScanMode.Deep, header.Mode);
    }

    [Fact]
    public void Computed_properties_are_not_stored()
    {
        string path = PathOf("lean");
        ScanArchive.Save(Sample(), path);

        using var gzip = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        string json = new StreamReader(gzip).ReadToEnd();

        Assert.DoesNotContain("IsWorthRecovering", json);
        Assert.DoesNotContain("HasContent", json);
        Assert.DoesNotContain("DeletedCount", json);
    }

    [Fact]
    public void Save_replaces_the_file_atomically_and_leaves_no_temp_file()
    {
        string path = PathOf("atomic");
        ScanArchive.Save(Sample(), path);
        ScanArchive.Save(Sample(), path);   // שמירה שנייה דורסת את הראשונה

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal(2, ScanArchive.Load(path).Result.Files.Count);
    }

    [Fact]
    public void A_file_that_is_not_a_scan_is_rejected_with_a_clear_message()
    {
        string path = PathOf("garbage");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5 });
        Assert.Contains("אינו קובץ סריקה", Assert.Throws<InvalidDataException>(() => ScanArchive.Load(path)).Message);

        // קובץ סריקה שנקטע באמצע — למשל העתקה שלא הושלמה.
        string whole = PathOf("whole");
        ScanArchive.Save(Sample(), whole);
        byte[] bytes = File.ReadAllBytes(whole);
        string cut = PathOf("cut");
        File.WriteAllBytes(cut, bytes[..(bytes.Length / 2)]);
        Assert.Contains("אינו קובץ סריקה", Assert.Throws<InvalidDataException>(() => ScanArchive.Load(cut)).Message);

        string gz = PathOf("gz-not-json");
        using (var gzip = new GZipStream(File.Create(gz), CompressionLevel.Fastest))
            gzip.Write("hello\nworld"u8);
        Assert.Contains("אינו קובץ סריקה", Assert.Throws<InvalidDataException>(() => ScanArchive.Load(gz)).Message);
    }

    [Fact]
    public void A_file_from_a_newer_version_is_refused_instead_of_misread()
    {
        string path = PathOf("future");
        var sample = Sample();
        ScanArchive.Save(new ScanArchive
        {
            Header = sample.Header with { FormatVersion = ScanArchive.CurrentFormat + 1 },
            Result = sample.Result,
        }, path);

        Assert.Contains("גרסה חדשה", Assert.Throws<InvalidDataException>(() => ScanArchive.Load(path)).Message);
    }

    // ------------------------------------------------------------ זיהוי הכונן

    private static PhysicalDiskInfo Disk(string model, string serial, long size, string? image = null)
        => new() { Model = model, SerialNumber = serial, SizeBytes = size, ImagePath = image };

    [Fact]
    public void Disk_is_matched_by_serial_and_size_not_by_number()
    {
        var id = new DiskIdentity("SanDisk", "ABC123", 1000, null);

        Assert.True(id.Matches(Disk("SanDisk", " abc123 ", 1000)));
        Assert.False(id.Matches(Disk("SanDisk", "ABC124", 1000)));   // כונן אחר מאותו דגם
        Assert.False(id.Matches(Disk("SanDisk", "ABC123", 999)));    // גודל שונה
    }

    [Fact]
    public void Disk_without_serial_is_matched_by_model_and_size()
    {
        var id = new DiskIdentity("Generic Card Reader", "", 32_000, null);

        Assert.True(id.Matches(Disk("Generic Card Reader", "", 32_000)));
        Assert.False(id.Matches(Disk("Other Reader", "", 32_000)));
    }

    [Fact]
    public void Image_is_matched_by_path_and_never_by_a_physical_disk()
    {
        string image = Path.Combine(_folder, "disk.img");
        var id = new DiskIdentity("", "", 1000, image);

        Assert.True(id.Matches(Disk("", "", 5, Path.Combine(_folder, ".", "DISK.IMG"))));
        Assert.False(id.Matches(Disk("", "", 1000)));
        Assert.False(new DiskIdentity("X", "S", 1000, null).Matches(Disk("X", "S", 1000, image)));
    }
}
