using System.Buffers.Binary;
using System.Text;
using RAF.Core.Disks;
using RAF.Core.Imaging;
using RAF.Core.Model;
using RAF.Core.Native;
using Xunit;

namespace RAF.Core.Tests;

/// <summary>
/// סריקת כונן לאיתור מחיצות אבודות, והחזרתן לטבלת המחיצות.
///
/// שני הכשלים המסוכנים: להציג כמחיצה משהו שאינו מחיצה (ואז לרשום אותו
/// בטבלה), ולכתוב טבלה שהורסת מחיצה קיימת. לכן כל מועמד מאומת מול המבנה
/// שאחריו, והכתיבה נבדקת בית אחר בית — כולל הביטול.
/// </summary>
public sealed class PartitionHuntTests : IDisposable
{
    private const int Sector = 512;
    private const long Mb = 1024 * 1024;

    private readonly List<string> _temp = new();

    public void Dispose()
    {
        foreach (var p in _temp) try { File.Delete(p); } catch { }
        foreach (var d in _dirs) try { Directory.Delete(d, true); } catch { }
    }

    private readonly List<string> _dirs = new();

    /// <summary>כונן מדומה ענק שרובו אפסים — רק הסקטורים שנכתבו שמורים בזיכרון.</summary>
    private sealed class SparseSource : ISectorSource
    {
        private readonly Dictionary<long, byte[]> _sectors = new();

        public SparseSource(long length) => Length = length;

        public long Length { get; }
        public int SectorSize => Sector;

        public void Put(long offset, byte[] data)
        {
            for (int i = 0; i < data.Length; i += Sector)
            {
                byte[] s = new byte[Sector];
                data.AsSpan(i, Math.Min(Sector, data.Length - i)).CopyTo(s);
                _sectors[(offset + i) / Sector] = s;
            }
        }

        public int Read(long offset, Span<byte> destination)
        {
            destination.Clear();
            long first = offset / Sector, last = (offset + destination.Length - 1) / Sector;

            foreach (var (index, data) in _sectors)
            {
                if (index < first || index > last) continue;
                long at = index * Sector - offset;
                data.AsSpan().CopyTo(destination[(int)at..]);
            }

            return (int)Math.Min(destination.Length, Length - offset);
        }
    }

    /// <summary>מחיצת NTFS קטנה ואמיתית במבנה: מגזר אתחול וטבלת MFT.</summary>
    private static byte[] Ntfs()
    {
        using var builder = new NtfsImageBuilder();
        return builder.ToArray();
    }

    private static long NtfsBackupOffset(byte[] ntfs)
        => BinaryPrimitives.ReadInt64LittleEndian(ntfs.AsSpan(40)) * Sector;

    private static HuntResult Hunt(SparseSource source, params (long, long)[] existing)
        => PartitionHunter.Hunt(source, existing.ToList(), null, CancellationToken.None);

    // ------------------------------------------------------------ איתור

    [Fact]
    public void A_deleted_ntfs_partition_is_found_where_it_was()
    {
        var disk = new SparseSource(8 * Mb);
        disk.Put(Mb, Ntfs());

        var found = Assert.Single(Hunt(disk).Found);
        Assert.Equal(Mb, found.Offset);
        Assert.Equal(FileSystemKind.Ntfs, found.FileSystem);
        Assert.False(found.BootSectorDamaged);
    }

    [Fact]
    public void A_partition_whose_start_was_destroyed_is_found_through_its_backup()
    {
        byte[] ntfs = Ntfs();
        var disk = new SparseSource(8 * Mb);

        // מגזר האתחול נמחק; העותק בסקטור האחרון שלם.
        byte[] damaged = (byte[])ntfs.Clone();
        Array.Clear(damaged, 0, Sector);
        disk.Put(Mb, damaged);
        disk.Put(Mb + NtfsBackupOffset(ntfs), ntfs.AsSpan(0, Sector).ToArray());

        var found = Assert.Single(Hunt(disk).Found);
        Assert.Equal(Mb, found.Offset);
        Assert.True(found.BootSectorDamaged);
    }

    [Fact]
    public void The_backup_copy_of_a_healthy_partition_is_not_reported_as_a_second_one()
    {
        byte[] ntfs = Ntfs();
        var disk = new SparseSource(8 * Mb);
        disk.Put(Mb, ntfs);
        disk.Put(Mb + NtfsBackupOffset(ntfs), ntfs.AsSpan(0, Sector).ToArray());

        Assert.Single(Hunt(disk).Found);
    }

    [Fact]
    public void A_stray_boot_sector_with_nothing_behind_it_is_not_a_partition()
    {
        // שריד של מגזר אתחול — בלי טבלת MFT אחריו. אסור להציג אותו כמחיצה.
        var disk = new SparseSource(8 * Mb);
        disk.Put(3 * Mb, Ntfs().AsSpan(0, Sector).ToArray());

        Assert.Empty(Hunt(disk).Found);
    }

    [Fact]
    public void A_partition_already_in_the_table_is_not_reported_as_lost()
    {
        byte[] ntfs = Ntfs();
        var disk = new SparseSource(8 * Mb);
        disk.Put(Mb, ntfs);

        Assert.Empty(Hunt(disk, (Mb, ntfs.Length)).Found);
    }

    [Fact]
    public void A_fat32_partition_is_found_even_when_only_its_backup_survived()
    {
        byte[] boot = FatTests.BootSector(
            sectorsPerCluster: 8, reserved: 32, fats: 2, rootEntries: 0,
            totalSectors: 600_000, sectorsPerFat16: 0, sectorsPerFat32: 600, rootCluster: 2);
        boot[0x32] = 6;                               // מיקום העותק

        var disk = new SparseSource(400 * Mb);
        long start = 2 * Mb;

        disk.Put(start + 6 * Sector, boot);            // רק העותק — הראשי נמחק
        disk.Put(start + 32 * Sector, new byte[] { 0xF8, 0xFF, 0xFF, 0x0F });   // תחילת טבלת ה-FAT

        var found = Assert.Single(Hunt(disk).Found);
        Assert.Equal(start, found.Offset);
        Assert.Equal(FileSystemKind.Fat32, found.FileSystem);
        Assert.True(found.BootSectorDamaged);
    }

    [Fact]
    public void A_partition_partly_overlapping_an_existing_one_is_flagged()
    {
        var disk = new SparseSource(8 * Mb);
        disk.Put(2 * Mb, Ntfs());

        // המחיצה הקיימת נגמרת באמצע המחיצה שנמצאה.
        var found = Assert.Single(Hunt(disk, (Mb, Mb + 100 * Sector)).Found);
        Assert.True(found.OverlapsExisting);
    }

    [Fact]
    public void Leftovers_inside_another_partition_are_counted_but_not_listed()
    {
        // כמו שנמצא על כונן אמיתי: מחיצת NTFS גדולה, ובתוכה שרידי FAT12 קטנים
        // (אזורי אתחול של קובצי ISO שנשמרו עליה בעבר).
        var disk = new SparseSource(400 * Mb);
        byte[] ntfs = Ntfs();
        BinaryPrimitives.WriteInt64LittleEndian(ntfs.AsSpan(40), 300 * Mb / Sector - 1);   // מחיצה של 300MB
        disk.Put(Mb, ntfs);

        byte[] floppy = FatTests.BootSector(sectorsPerCluster: 1, reserved: 1, fats: 2, rootEntries: 224,
            totalSectors: 2880, sectorsPerFat16: 9);
        long leftover = 50 * Mb;
        disk.Put(leftover, floppy);
        disk.Put(leftover + Sector, new byte[] { 0xF0, 0xFF, 0xFF });

        var result = Hunt(disk);
        var only = Assert.Single(result.Found);
        Assert.Equal(Mb, only.Offset);
        Assert.Equal(1, result.HiddenInside);

        // וגם בתוך מחיצה קיימת בטבלה.
        var inTable = new SparseSource(400 * Mb);
        inTable.Put(leftover, floppy);
        inTable.Put(leftover + Sector, new byte[] { 0xF0, 0xFF, 0xFF });
        var r2 = Hunt(inTable, (Mb, 300 * Mb));
        Assert.Empty(r2.Found);
        Assert.Equal(1, r2.HiddenInside);
    }

    // ------------------------------------------------------------ החזרה לטבלה

    private (PhysicalDiskInfo Disk, string Path) ImageWith(byte[] content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"raf-hunt-{Guid.NewGuid():N}.img");
        _temp.Add(path);
        _temp.Add(ImageMap.PathFor(path));
        File.WriteAllBytes(path, content);
        new ImageMap { Kind = "disk", Size = content.Length, Complete = true }.Save(ImageMap.PathFor(path));
        return (ImageDisk.Open(path), path);
    }

    private string UndoFolder()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"raf-undo-{Guid.NewGuid():N}");
        _dirs.Add(dir);
        return dir;
    }

    private static FoundPartition HuntImage(PhysicalDiskInfo disk)
        => Assert.Single(PartitionHunter.HuntAsync(disk, null, CancellationToken.None).GetAwaiter().GetResult().Found);

    [Fact]
    public void A_found_partition_is_written_into_an_empty_mbr_and_reads_back()
    {
        byte[] content = new byte[8 * Mb];
        content[510] = 0x55; content[511] = 0xAA;           // MBR ריק
        Ntfs().CopyTo(content, (int)Mb);

        var (disk, path) = ImageWith(content);
        try
        {
            var found = HuntImage(disk);
            var plan = PartitionTableWriter.Plan(disk, found);
            Assert.True(plan.CanRestore, plan.Explanation);

            var result = PartitionTableWriter.Restore(disk, found, UndoFolder());
            Assert.True(result.Succeeded, result.Message);

            // קריאה מחדש של הכונן: המחיצה מופיעה בטבלה, והקבצים לא נגעו.
            var reopened = ImageDisk.Open(path);
            var part = Assert.Single(reopened.Partitions);
            Assert.Equal(Mb, part.OffsetBytes);
            Assert.Equal(FileSystemKind.Ntfs, part.FileSystem);

            byte[] after = File.ReadAllBytes(path);
            Assert.Equal(content.AsSpan(Sector).ToArray(), after.AsSpan(Sector).ToArray());

            // והביטול מחזיר את הכונן בדיוק למה שהיה.
            Assert.True(PartitionTableWriter.Undo(result.UndoFile!, Sector));
            Assert.Equal(content, File.ReadAllBytes(path));
        }
        finally
        {
            ImageDisk.Close(disk.DiskNumber);
        }
    }

    [Fact]
    public void A_disk_that_lost_its_table_entirely_gets_a_new_one()
    {
        byte[] content = new byte[8 * Mb];                 // אין טבלה בכלל
        Ntfs().CopyTo(content, (int)Mb);

        var (disk, path) = ImageWith(content);
        try
        {
            var found = HuntImage(disk);
            var result = PartitionTableWriter.Restore(disk, found, UndoFolder());
            Assert.True(result.Succeeded, result.Message);

            var reopened = ImageDisk.Open(path);
            Assert.Equal(PartitionScheme.Mbr, reopened.Scheme);
            Assert.Equal(Mb, Assert.Single(reopened.Partitions).OffsetBytes);
        }
        finally
        {
            ImageDisk.Close(disk.DiskNumber);
        }
    }

    [Fact]
    public void Nothing_is_written_over_an_existing_partition()
    {
        byte[] content = new byte[8 * Mb];
        // MBR עם מחיצה קיימת שמכסה את 1MB–5MB.
        content[446 + 4] = 0x07;
        BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(446 + 8), 2048);
        BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(446 + 12), 8192);
        content[510] = 0x55; content[511] = 0xAA;
        Ntfs().CopyTo(content, (int)(2 * Mb));             // מחיצה ישנה בתוכה

        var (disk, path) = ImageWith(content);
        try
        {
            var found = new FoundPartition { Offset = 2 * Mb, Size = 513 * Sector, FileSystem = FileSystemKind.Ntfs };
            var plan = PartitionTableWriter.Plan(disk, found);
            Assert.False(plan.CanRestore);

            var result = PartitionTableWriter.Restore(disk, found, UndoFolder());
            Assert.False(result.Succeeded);
            Assert.Equal(content, File.ReadAllBytes(path));
        }
        finally
        {
            ImageDisk.Close(disk.DiskNumber);
        }
    }

    // ------------------------------------------------------------ GPT

    private static readonly Guid BasicData = new("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");

    /// <summary>דיסק GPT תקני: MBR מגן, כותרת ראשית ומשנית, 128 רשומות.</summary>
    private static byte[] GptDisk(long size, params (long FirstLba, long LastLba)[] parts)
    {
        byte[] d = new byte[size];
        long lastLba = size / Sector - 1;

        d[446 + 4] = 0xEE;
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(446 + 8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(446 + 12), (uint)Math.Min(lastLba, uint.MaxValue));
        d[510] = 0x55; d[511] = 0xAA;

        byte[] entries = new byte[128 * 128];
        for (int i = 0; i < parts.Length; i++)
        {
            BasicData.TryWriteBytes(entries.AsSpan(i * 128));
            Guid.NewGuid().TryWriteBytes(entries.AsSpan(i * 128 + 16));
            BinaryPrimitives.WriteInt64LittleEndian(entries.AsSpan(i * 128 + 32), parts[i].FirstLba);
            BinaryPrimitives.WriteInt64LittleEndian(entries.AsSpan(i * 128 + 40), parts[i].LastLba);
        }

        uint arrayCrc = Crc32.Compute(entries);

        byte[] Header(long my, long alt, long entriesLba)
        {
            byte[] h = new byte[Sector];
            Encoding.ASCII.GetBytes("EFI PART").CopyTo(h, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8), 0x00010000);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(12), 92);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(24), my);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(32), alt);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(40), 34);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(48), lastLba - 33);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(72), entriesLba);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(80), 128);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(84), 128);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(88), arrayCrc);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), Crc32.Compute(h.AsSpan(0, 92)));
            return h;
        }

        Header(1, lastLba, 2).CopyTo(d, Sector);
        entries.CopyTo(d, 2 * Sector);
        entries.CopyTo(d, (lastLba - 32) * Sector);
        Header(lastLba, 1, lastLba - 32).CopyTo(d, lastLba * Sector);
        return d;
    }

    private static void AssertHeaderValid(byte[] disk, long lba)
    {
        byte[] h = disk.AsSpan((int)(lba * Sector), Sector).ToArray();
        Assert.Equal("EFI PART", Encoding.ASCII.GetString(h, 0, 8));

        uint stored = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(16));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), 0);
        Assert.Equal(stored, Crc32.Compute(h.AsSpan(0, 92)));

        long entriesLba = BinaryPrimitives.ReadInt64LittleEndian(h.AsSpan(72));
        uint arrayCrc = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(88));
        Assert.Equal(arrayCrc, Crc32.Compute(disk.AsSpan((int)(entriesLba * Sector), 128 * 128)));
    }

    [Fact]
    public void A_found_partition_is_added_to_a_gpt_disk_with_both_copies_kept_valid()
    {
        // מחיצה קיימת ב-1MB–2MB, ומחיצה שנמחקה ב-3MB.
        byte[] content = GptDisk(8 * Mb, (2048, 4095));
        Ntfs().CopyTo(content, (int)(3 * Mb));

        var (disk, path) = ImageWith(content);
        try
        {
            var found = HuntImage(disk);
            Assert.Equal(3 * Mb, found.Offset);

            var result = PartitionTableWriter.Restore(disk, found, UndoFolder());
            Assert.True(result.Succeeded, result.Message);

            byte[] after = File.ReadAllBytes(path);
            AssertHeaderValid(after, 1);
            AssertHeaderValid(after, after.Length / Sector - 1);

            var reopened = ImageDisk.Open(path);
            Assert.Equal(PartitionScheme.Gpt, reopened.Scheme);
            Assert.Equal(2, reopened.Partitions.Count);
            Assert.Contains(reopened.Partitions, p => p.OffsetBytes == 3 * Mb && p.FileSystem == FileSystemKind.Ntfs);
            Assert.Contains(reopened.Partitions, p => p.OffsetBytes == Mb);

            Assert.True(PartitionTableWriter.Undo(result.UndoFile!, Sector));
            Assert.Equal(content, File.ReadAllBytes(path));
        }
        finally
        {
            ImageDisk.Close(disk.DiskNumber);
        }
    }
}
