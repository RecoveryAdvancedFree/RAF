using System.Buffers.Binary;
using System.Text;
using RAF.Core.FileSystems;
using RAF.Core.Imaging;
using RAF.Core.Model;
using RAF.Core.Native;
using Xunit;

namespace RAF.Core.Tests;

/// <summary>
/// בדיקות יצירת תמונת דיסק ועבודה מתוכה.
///
/// הכשל המסוכן כאן שקט: תמונה שנראית שלמה אך סקטור פגום הוחלף בה באפסים
/// בלי תיעוד, או סקטור טוב שלא נוסה שוב ואבד. לכן המקור המדומה מחזיר
/// כישלון בסקטורים מסוימים בדיוק כמו דיסק אמיתי — והבדיקות משוות את
/// התמונה ואת המפה בית אחר בית.
/// </summary>
public sealed class ImagingTests : IDisposable
{
    private const int Sector = 512;
    private readonly List<string> _temp = new();

    private string TempPath(string extension = ".img")
    {
        string path = Path.Combine(Path.GetTempPath(), $"raf-imaging-{Guid.NewGuid():N}{extension}");
        _temp.Add(path);
        _temp.Add(ImageMap.PathFor(path));
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _temp)
            try { File.Delete(path); } catch { }
    }

    /// <summary>
    /// מקור שמתנהג כמו דיסק עם סקטורים פגומים: כל קריאה שנוגעת בסקטור
    /// פגום נכשלת כולה (כך מתנהג ReadFile מול דיסק אמיתי), ונרשמת ביומן.
    /// </summary>
    private sealed class FaultySource : ISectorSource
    {
        private readonly byte[] _data;
        private readonly HashSet<long> _bad;
        public List<(long Offset, int Length)> Reads { get; } = new();

        public FaultySource(byte[] data, IEnumerable<long> badSectors)
        {
            _data = data;
            _bad = badSectors.ToHashSet();
        }

        public long Length => _data.Length;
        public int SectorSize => Sector;
        public Action<long>? OnRead { get; set; }

        public int Read(long offset, Span<byte> destination)
        {
            Reads.Add((offset, destination.Length));
            OnRead?.Invoke(offset);

            for (long s = offset / Sector; s * Sector < offset + destination.Length; s++)
                if (_bad.Contains(s)) return 0;

            int n = (int)Math.Min(destination.Length, _data.Length - offset);
            _data.AsSpan((int)offset, n).CopyTo(destination);
            return n;
        }
    }

    private static byte[] Pattern(int length, int seed)
    {
        byte[] data = new byte[length];
        new Random(seed).NextBytes(data);
        // ללא אפסים — כדי שסקטור שמולא באפסים יהיה מובחן מנתון אמיתי.
        for (int i = 0; i < data.Length; i++) if (data[i] == 0) data[i] = 1;
        return data;
    }

    private ImagingResult Image(ISectorSource source, out string path, CancellationToken token = default)
    {
        path = TempPath();
        return DiskImager.Create(source, "disk", "בדיקה", path, null, token);
    }

    // ------------------------------------------------------------ מקור תקין

    [Fact]
    public void A_healthy_source_produces_an_identical_image_and_a_clean_map()
    {
        byte[] data = Pattern(3 * DiskImager.ChunkSize + 7 * Sector, 1);
        var result = Image(new FaultySource(data, Array.Empty<long>()), out string path);

        Assert.True(result.Complete);
        Assert.Equal(0, result.UnreadableBytes);
        Assert.Equal(data, File.ReadAllBytes(path));

        var map = ImageMap.TryLoad(result.MapPath)!;
        Assert.True(map.Complete);
        Assert.Empty(map.Unreadable);
        Assert.Empty(map.NotCopied);
        Assert.Equal(data.Length, map.Size);
    }

    // ------------------------------------------------------------ סקטורים פגומים

    [Fact]
    public void Only_the_bad_sectors_are_lost_and_the_map_names_exactly_them()
    {
        byte[] data = Pattern(4 * DiskImager.ChunkSize, 2);

        // סקטור בודד, שניים צמודים, ועוד אחד בבלוק אחר.
        long[] bad = { 100, 2050, 2051, 5000 };
        var result = Image(new FaultySource(data, bad), out string path);

        byte[] image = File.ReadAllBytes(path);
        byte[] expected = (byte[])data.Clone();
        foreach (long s in bad) Array.Clear(expected, (int)(s * Sector), Sector);

        // כל סקטור טוב — גם אלה שישבו באותו בלוק עם סקטור פגום — הועתק.
        Assert.Equal(expected, image);

        var map = ImageMap.TryLoad(result.MapPath)!;
        Assert.True(map.Complete);
        Assert.Equal(
            new[]
            {
                new ByteRange(100 * Sector, Sector),
                new ByteRange(2050 * Sector, 2 * Sector),   // סקטורים צמודים מאוחדים לטווח אחד
                new ByteRange(5000 * Sector, Sector),
            },
            map.Unreadable);
        Assert.Equal(4 * Sector, result.UnreadableBytes);
    }

    [Fact]
    public void A_damaged_zone_is_skipped_in_pass_one_so_good_data_after_it_is_read_first()
    {
        // אזור פגום של 6MB באמצע, ונתונים טובים אחריו.
        int chunk = DiskImager.ChunkSize;
        byte[] data = Pattern(16 * chunk, 3);
        long badFrom = 4L * chunk / Sector, badTo = 10L * chunk / Sector;
        var bad = Enumerable.Range((int)badFrom, (int)(badTo - badFrom)).Select(i => (long)i);

        var source = new FaultySource(data, bad);
        var result = Image(source, out _);

        // הקריאה הראשונה של הנתונים הטובים שאחרי האזור הפגום קודמת
        // לכל קריאה ברמת סקטור בתוך האזור — הנתונים הטובים נאספים קודם.
        int firstAfter = source.Reads.FindIndex(r => r.Offset >= 10L * chunk);
        int firstSectorRetry = source.Reads.FindIndex(r => r.Length == Sector);
        Assert.True(firstAfter >= 0 && firstSectorRetry >= 0);
        Assert.True(firstAfter < firstSectorRetry);

        // הדילוג גדל: מעבר 1 אינו מנסה כל בלוק באזור הפגום.
        int passOneAttemptsInZone = source.Reads
            .Take(firstSectorRetry)
            .Count(r => r.Length == chunk && r.Offset >= 4L * chunk && r.Offset < 10L * chunk);
        Assert.True(passOneAttemptsInZone < 6, $"pass one tried {passOneAttemptsInZone} blocks in the bad zone");

        Assert.Equal(6L * chunk, result.UnreadableBytes);
        Assert.Single(ImageMap.TryLoad(result.MapPath)!.Unreadable);
    }

    // ------------------------------------------------------------ עצירה

    [Fact]
    public void Stopping_midway_records_what_was_not_copied()
    {
        int chunk = DiskImager.ChunkSize;
        byte[] data = Pattern(8 * chunk, 4);
        using var cancel = new CancellationTokenSource();

        var source = new FaultySource(data, Array.Empty<long>())
        {
            OnRead = offset => { if (offset >= 3L * chunk) cancel.Cancel(); },
        };

        var result = Image(source, out string path, cancel.Token);

        Assert.False(result.Complete);
        Assert.True(result.Cancelled);

        var map = ImageMap.TryLoad(result.MapPath)!;
        Assert.False(map.Complete);
        Assert.Equal(new[] { new ByteRange(4L * chunk, 4L * chunk) }, map.NotCopied);

        // מה שהועתק — הועתק נכון.
        byte[] image = File.ReadAllBytes(path);
        Assert.Equal(data.AsSpan(0, 4 * chunk).ToArray(), image.AsSpan(0, 4 * chunk).ToArray());
    }

    // ------------------------------------------------------------ מפה

    [Fact]
    public void The_map_survives_a_round_trip()
    {
        string path = TempPath(".map");
        var map = new ImageMap
        {
            Source = "Samsung SSD · מחיצה C:",
            Kind = "partition",
            Size = 123_456_789,
            SectorSize = 4096,
            Complete = false,
            Unreadable = { new ByteRange(4096, 8192), new ByteRange(1_000_000, 4096) },
            NotCopied = { new ByteRange(50_000_000, 73_456_789) },
        };

        map.Save(path);
        var loaded = ImageMap.TryLoad(path)!;

        Assert.Equal(map.Source, loaded.Source);
        Assert.Equal("partition", loaded.Kind);
        Assert.Equal(map.Size, loaded.Size);
        Assert.Equal(4096, loaded.SectorSize);
        Assert.False(loaded.Complete);
        Assert.Equal(map.Unreadable, loaded.Unreadable);
        Assert.Equal(map.NotCopied, loaded.NotCopied);
    }

    [Fact]
    public void A_file_that_is_not_a_map_is_ignored()
    {
        string path = TempPath(".map");
        File.WriteAllText(path, "size=5\nunreadable=0+5\n");
        Assert.Null(ImageMap.TryLoad(path));
    }

    // ------------------------------------------------------------ פתיחת תמונה

    private static byte[] NtfsBootSector()
    {
        byte[] sector = new byte[Sector];
        sector[0] = 0xEB; sector[1] = 0x52; sector[2] = 0x90;
        Encoding.ASCII.GetBytes("NTFS    ").CopyTo(sector, 3);
        BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(11), Sector);
        sector[13] = 8;
        BinaryPrimitives.WriteInt64LittleEndian(sector.AsSpan(40), 2047);
        BinaryPrimitives.WriteInt64LittleEndian(sector.AsSpan(48), 4);
        BinaryPrimitives.WriteInt64LittleEndian(sector.AsSpan(56), 100);
        sector[64] = unchecked((byte)-10);
        sector[68] = 1;
        // קוד אתחול: בתים שבקריאה כטבלת MBR היו נראים כרשומות מחיצה.
        for (int i = 446; i < 510; i++) sector[i] = (byte)(i * 7);
        sector[510] = 0x55; sector[511] = 0xAA;
        return sector;
    }

    private string WriteImage(byte[] content, string? kind)
    {
        string path = TempPath();
        File.WriteAllBytes(path, content);
        if (kind is not null)
            new ImageMap { Kind = kind, Size = content.Length, Complete = true }.Save(ImageMap.PathFor(path));
        return path;
    }

    private static void WithImage(string path, Action<PhysicalDiskInfo> test)
    {
        var disk = ImageDisk.Open(path);
        try { test(disk); }
        finally { ImageDisk.Close(disk.DiskNumber); }
    }

    [Fact]
    public void A_partition_image_opens_as_one_partition_even_though_its_boot_code_looks_like_an_mbr()
    {
        byte[] content = new byte[1024 * 1024];
        NtfsBootSector().CopyTo(content, 0);

        WithImage(WriteImage(content, "partition"), disk =>
        {
            Assert.True(DevicePaths.IsImage(disk.DiskNumber));
            Assert.Equal(MediaKind.Image, disk.Media);

            var part = Assert.Single(disk.Partitions);
            Assert.Equal(FileSystemKind.Ntfs, part.FileSystem);
            Assert.Equal(0, part.OffsetBytes);
            Assert.Equal(content.Length, part.SizeBytes);
        });
    }

    [Fact]
    public void A_whole_disk_image_exposes_the_partitions_in_its_table()
    {
        const long partStart = 2048;
        byte[] content = new byte[4 * 1024 * 1024];

        // MBR עם רשומה אחת מסוג NTFS.
        int e = 446;
        content[e + 4] = 0x07;
        BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(e + 8), (uint)partStart);
        BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(e + 12), 4096);
        content[510] = 0x55; content[511] = 0xAA;
        NtfsBootSector().CopyTo(content, (int)(partStart * Sector));

        WithImage(WriteImage(content, "disk"), disk =>
        {
            Assert.Equal(PartitionScheme.Mbr, disk.Scheme);
            var part = Assert.Single(disk.Partitions);
            Assert.Equal(partStart * Sector, part.OffsetBytes);
            Assert.Equal(FileSystemKind.Ntfs, part.FileSystem);

            // הקריאה דרך מספר הדיסק הווירטואלי מגיעה לקובץ — כמו כל מנוע בתוכנה.
            using var reader = VolumeReader.TryOpen(disk.DiskNumber, part.OffsetBytes, part.SizeBytes, Sector)!;
            Assert.Equal(NtfsBootSector(), reader.ReadBlock(0, Sector));
        });
    }

    [Fact]
    public void An_unreadable_partition_image_is_still_offered_for_diagnosis_and_carving()
    {
        byte[] content = Pattern(1024 * 1024, 5);   // מגזר אתחול הרוס

        WithImage(WriteImage(content, "partition"), disk =>
        {
            var part = Assert.Single(disk.Partitions);
            Assert.Equal(FileSystemKind.Raw, part.FileSystem);
        });
    }

    [Fact]
    public void A_damaged_image_carries_a_warning()
    {
        byte[] content = new byte[1024 * 1024];
        NtfsBootSector().CopyTo(content, 0);
        string path = WriteImage(content, null);
        new ImageMap
        {
            Kind = "partition", Size = content.Length, Complete = true,
            Unreadable = { new ByteRange(8192, 1024) },
        }.Save(ImageMap.PathFor(path));

        WithImage(path, disk =>
        {
            Assert.True(disk.ImageDamaged);
            Assert.Contains("1 KB", disk.ImageNote); // כמה לא נקרא — בגודל, לא במספר סקטורים
            Assert.NotNull(RecoveryProfile.For(disk, ScanMode.Quick).Warning);
        });
    }

    [Fact]
    public void Files_are_carved_from_an_image_exactly_as_from_a_drive()
    {
        // קבצים אמיתיים בתוך תמונה, והסריקה המתקדמת רצה דרך הנתיב הרגיל.
        byte[] png = RealFormats.Png(5000, 70);
        byte[] jpeg = RealFormats.Jpeg(6000, 71);
        byte[] content = new byte[2 * 1024 * 1024];
        png.CopyTo(content, 64 * Sector);
        jpeg.CopyTo(content, 512 * Sector);

        WithImage(WriteImage(content, "partition"), disk =>
        {
            var part = disk.Partitions[0];
            var result = VolumeScanner.ScanAsync(
                part.FileSystem, disk.DiskNumber, part.OffsetBytes, part.SizeBytes, Sector,
                ScanMode.Advanced, false, disk.Trim, null, CancellationToken.None).GetAwaiter().GetResult();

            Assert.Contains(result.Files, f => f.Extents[0].StartCluster == 64 && f.Size == png.Length);
            Assert.Contains(result.Files, f => f.Extents[0].StartCluster == 512 && f.Size == jpeg.Length);
        });
    }

    [Fact]
    public void An_unregistered_image_number_never_falls_back_to_a_physical_drive()
    {
        Assert.Null(DevicePaths.PathOf(DevicePaths.FirstImageNumber + 999));
        Assert.Null(VolumeReader.TryOpen(DevicePaths.FirstImageNumber + 999, 0, 0, Sector));
    }

    // ------------------------------------------------------------ המשך תמונה

    [Fact]
    public void A_stopped_image_resumes_from_the_map_without_reading_what_was_already_copied()
    {
        int chunk = DiskImager.ChunkSize;
        byte[] data = Pattern(6 * chunk, 21);

        // עצירה אחרי שלושה בלוקים.
        using var cancel = new CancellationTokenSource();
        var first = new FaultySource(data, Array.Empty<long>())
        {
            OnRead = offset => { if (offset >= 3L * chunk) cancel.Cancel(); },
        };
        var stopped = Image(first, out string path, cancel.Token);
        Assert.True(stopped.Cancelled);
        long copied = data.Length - stopped.NotCopiedBytes;
        Assert.True(copied > 0);

        // בינתיים האזור שכבר הועתק "נהרס" במקור: אם ההמשך היה קורא אותו
        // שוב, הוא היה נכשל שם. ההמשך אמור לא לגעת בו בכלל.
        var badNow = Enumerable.Range(0, (int)(copied / Sector)).Select(s => (long)s);
        var second = new FaultySource(data, badNow);
        var resumed = DiskImager.Resume(second, "disk", "בדיקה", path, retryUnreadable: false, null, CancellationToken.None);

        Assert.True(resumed.Complete);
        Assert.Equal(0, resumed.UnreadableBytes);
        Assert.Equal(data, File.ReadAllBytes(path));
        Assert.All(second.Reads, r => Assert.True(r.Offset >= copied, $"קריאה חוזרת של אזור שכבר הועתק: {r.Offset}"));

        var map = ImageMap.TryLoad(ImageMap.PathFor(path))!;
        Assert.True(map.Complete);
        Assert.Empty(map.NotCopied);
    }

    [Fact]
    public void Retrying_unreadable_sectors_recovers_the_ones_that_now_read_and_keeps_the_rest()
    {
        byte[] data = Pattern(2 * DiskImager.ChunkSize, 22);
        var first = Image(new FaultySource(data, new long[] { 10, 11, 900 }), out string path);
        Assert.Equal(3 * Sector, first.UnreadableBytes);

        // הכונן "התאושש" חלקית: סקטורים 10 ו-11 נקראים עכשיו, 900 עדיין לא.
        var second = new FaultySource(data, new long[] { 900 });
        var retried = DiskImager.Resume(second, "disk", "בדיקה", path, retryUnreadable: true, null, CancellationToken.None);

        Assert.Equal(Sector, retried.UnreadableBytes);
        byte[] image = File.ReadAllBytes(path);
        Assert.Equal(data.AsSpan(10 * Sector, 2 * Sector).ToArray(), image.AsSpan(10 * Sector, 2 * Sector).ToArray());
        Assert.Equal(new ByteRange(900L * Sector, Sector), Assert.Single(ImageMap.TryLoad(ImageMap.PathFor(path))!.Unreadable));

        // הניסיון החוזר קרא רק את הסקטורים הפגומים — לא את כל הכונן: 10–11 כבלוק אחד,
        // ו-900 שלוש פעמים (כבלוק, כסקטור בודד אחרי שהבלוק נכשל, ושוב במעבר ההפוך).
        Assert.Equal(5 * Sector, second.Reads.Sum(r => (long)r.Length));
        Assert.All(second.Reads, r => Assert.Contains(r.Offset / Sector, new long[] { 10, 900 }));
    }

    [Fact]
    public void An_image_of_a_different_source_cannot_be_resumed()
    {
        byte[] data = Pattern(DiskImager.ChunkSize, 23);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Image(new FaultySource(data, Array.Empty<long>()), out string path, cancel.Token);

        var other = DiskImager.Inspect(path, data.Length * 2L, "disk")!;
        Assert.False(other.CanResume);
        Assert.Contains("מקור אחר", other.Reason);

        Assert.True(DiskImager.Inspect(path, data.Length, "disk")!.CanResume);
        Assert.False(DiskImager.Inspect(path, data.Length, "partition")!.CanResume);
        Assert.Null(DiskImager.Inspect(TempPath(), data.Length, "disk"));

        Assert.Throws<InvalidOperationException>(() => DiskImager.Resume(
            new FaultySource(Pattern(2 * DiskImager.ChunkSize, 1), Array.Empty<long>()),
            "disk", "בדיקה", path, false, null, CancellationToken.None));
    }

    [Fact]
    public void Ranges_from_before_and_after_a_resume_are_merged_in_order()
    {
        var merged = DiskImager.Normalize(new[]
        {
            new ByteRange(5000, 100), new ByteRange(0, 512), new ByteRange(512, 512), new ByteRange(5050, 100),
        });

        Assert.Equal(new[] { new ByteRange(0, 1024), new ByteRange(5000, 150) }, merged);
    }

    // ------------------------------------------------------------ מעבר 3 — מהכיוון ההפוך

    /// <summary>
    /// כונן שסקטורים מסוימים בו נקראים רק כשמגיעים אליהם מלמעלה — כמו ראש קריאה
    /// שנתקע בקצה של אזור פגום כשהוא מגיע אליו מתחילתו.
    /// </summary>
    private sealed class DirectionalSource(byte[] data, HashSet<long> onlyFromAbove, HashSet<long> dead) : ISectorSource
    {
        private long _last = -1;
        public List<long> Reads { get; } = new();
        public long Length => data.Length;
        public int SectorSize => Sector;

        public int Read(long offset, Span<byte> destination)
        {
            bool fromAbove = offset < _last;
            _last = offset;
            Reads.Add(offset / Sector);

            for (long s = offset / Sector; s * Sector < offset + destination.Length; s++)
                if (dead.Contains(s) || (onlyFromAbove.Contains(s) && !fromAbove)) return 0;

            data.AsSpan((int)offset, destination.Length).CopyTo(destination);
            return destination.Length;
        }
    }

    [Fact]
    public void Reading_backwards_recovers_sectors_that_fail_when_reached_from_the_start()
    {
        byte[] data = Pattern(2 * DiskImager.ChunkSize, 31);

        // אזור פגום 100–140: הפנים מת, והקצה העליון (131–140) נקרא רק כשמגיעים מלמעלה.
        var edge = Enumerable.Range(131, 10).Select(i => (long)i).ToHashSet();
        var dead = Enumerable.Range(100, 31).Select(i => (long)i).ToHashSet();
        var result = Image(new DirectionalSource(data, edge, dead), out string path);

        Assert.Equal(31 * Sector, result.UnreadableBytes);
        Assert.Equal(new ByteRange(100L * Sector, 31 * Sector), Assert.Single(ImageMap.TryLoad(ImageMap.PathFor(path))!.Unreadable));

        byte[] image = File.ReadAllBytes(path);
        Assert.Equal(data.AsSpan(131 * Sector, 10 * Sector).ToArray(), image.AsSpan(131 * Sector, 10 * Sector).ToArray());
        Assert.True(image.AsSpan(100 * Sector, 31 * Sector).IndexOfAnyExcept((byte)0) < 0);
    }

    [Fact]
    public void A_completely_dead_zone_is_not_ground_sector_by_sector_a_third_time()
    {
        byte[] data = Pattern(2 * DiskImager.ChunkSize, 32);
        var dead = Enumerable.Range(1000, 1000).Select(i => (long)i).ToHashSet();
        var source = new DirectionalSource(data, [], dead);

        var result = Image(source, out _);
        Assert.Equal(1000 * Sector, result.UnreadableBytes);

        // המעבר השני ניסה כל סקטור; המעבר ההפוך מוותר אחרי רצף הכישלונות.
        // רק המעבר ההפוך יורד סקטור אחר סקטור — כל קריאה שלו (מלבד הראשונה) נמוכה באחד מקודמתה.
        int reverseReads = 1 + source.Reads.Zip(source.Reads.Skip(1)).Count(p => p.Second == p.First - 1);
        Assert.InRange(reverseReads, DiskImager.ReverseGiveUp, DiskImager.ReverseGiveUp + 1);
    }

    [Fact]
    public void Stopping_during_the_backwards_pass_keeps_the_image_complete()
    {
        byte[] data = Pattern(2 * DiskImager.ChunkSize, 33);
        var dead = Enumerable.Range(500, 40).Select(i => (long)i).ToHashSet();
        using var stop = new CancellationTokenSource();

        var progress = new SyncProgress<ImagingProgress>(p => { if (p.Pass == 3) stop.Cancel(); });
        string path = TempPath();

        var result = DiskImager.Create(new DirectionalSource(data, [], dead), "disk", "בדיקה", path, progress, stop.Token);

        Assert.True(result.Complete);
        Assert.Equal(0, result.NotCopiedBytes);
        Assert.Equal(40 * Sector, result.UnreadableBytes);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
