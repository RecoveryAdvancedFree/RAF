using System.Buffers.Binary;
using System.Text;
using RAF.Core.FileSystems.Fat;
using RAF.Core.Model;
using Xunit;

namespace RAF.Core.Tests;

/// <summary>
/// בדיקות מפענחי FAT12 / FAT16 / FAT32.
///
/// ההבחנה בין שלוש הווריאציות נקבעת לפי מספר האשכולות בלבד, ולא לפי
/// המחרוזת שכתובה במגזר האתחול — טעות נפוצה שמובילה לפענוח שגוי של
/// טבלת ההקצאה. כמו כן נבדקת הרכבת השמות הארוכים, ששמורים בערכים
/// נפרדים ובסדר הפוך.
/// </summary>
public class FatTests
{
    // ==================================================== מגזר האתחול

    internal static byte[] BootSector(
        ushort bytesPerSector = 512, byte sectorsPerCluster = 8,
        ushort reserved = 1, byte fats = 2, ushort rootEntries = 512,
        uint totalSectors = 100_000, ushort sectorsPerFat16 = 200,
        uint sectorsPerFat32 = 0, uint rootCluster = 0)
    {
        byte[] s = new byte[512];
        s[0] = 0xEB; s[1] = 0x3C; s[2] = 0x90;
        Encoding.ASCII.GetBytes("MSDOS5.0").CopyTo(s, 3);

        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(11), bytesPerSector);
        s[13] = sectorsPerCluster;
        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(14), reserved);
        s[16] = fats;
        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(17), rootEntries);
        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(22), sectorsPerFat16);
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(32), totalSectors);

        if (sectorsPerFat16 == 0)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(36), sectorsPerFat32);
            BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(44), rootCluster);
        }

        s[510] = 0x55; s[511] = 0xAA;
        return s;
    }

    [Fact]
    public void Fat16_is_identified_by_its_cluster_count()
    {
        // 100,000 סקטורים ב-8 סקטורים לאשכול → כ-12,400 אשכולות → FAT16.
        var boot = FatBootSector.Parse(BootSector());

        Assert.NotNull(boot);
        Assert.Equal(FileSystemKind.Fat16, boot!.Kind);
        Assert.Equal(4096, boot.BytesPerCluster);
    }

    [Fact]
    public void Fat12_is_identified_when_there_are_few_clusters()
    {
        // תצורת תקליטון: 2,880 סקטורים, סקטור אחד לאשכול.
        var boot = FatBootSector.Parse(BootSector(
            sectorsPerCluster: 1, reserved: 1, fats: 2,
            rootEntries: 224, totalSectors: 2880, sectorsPerFat16: 9));

        Assert.NotNull(boot);
        Assert.Equal(FileSystemKind.Fat12, boot!.Kind);
    }

    [Fact]
    public void Fat32_is_identified_and_uses_a_cluster_based_root()
    {
        var boot = FatBootSector.Parse(BootSector(
            sectorsPerCluster: 8, reserved: 32, rootEntries: 0,
            totalSectors: 8_000_000, sectorsPerFat16: 0,
            sectorsPerFat32: 8000, rootCluster: 2));

        Assert.NotNull(boot);
        Assert.Equal(FileSystemKind.Fat32, boot!.Kind);
        Assert.Equal(2, boot.RootCluster);
        Assert.Equal(0, boot.RootDirSectors);
    }

    [Theory]
    [InlineData(0)]     // גודל סקטור אפס
    [InlineData(300)]   // אינו חזקת 2
    public void Impossible_sector_sizes_are_rejected(ushort bytesPerSector)
        => Assert.Null(FatBootSector.Parse(BootSector(bytesPerSector: bytesPerSector)));

    [Fact]
    public void A_non_power_of_two_cluster_size_is_rejected()
        => Assert.Null(FatBootSector.Parse(BootSector(sectorsPerCluster: 3)));

    [Fact]
    public void A_sector_without_the_jump_instruction_is_rejected()
    {
        byte[] s = BootSector();
        s[0] = 0x00;
        Assert.Null(FatBootSector.Parse(s));
    }

    [Fact]
    public void An_ntfs_boot_sector_is_not_mistaken_for_fat()
    {
        byte[] s = BootSector();
        Encoding.ASCII.GetBytes("NTFS    ").CopyTo(s, 3);
        s[13] = 8;
        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(22), 0); // NTFS אינו משתמש בשדה זה
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(36), 0);

        Assert.Null(FatBootSector.Parse(s));
    }

    // ==================================================== ערכי ספרייה

    /// <summary>בניית ערך 8.3 בפורמט הבינארי.</summary>
    private static byte[] ShortEntry(
        string name83, byte attributes, uint firstCluster, uint size, bool deleted = false)
    {
        byte[] e = new byte[32];
        Encoding.ASCII.GetBytes(name83.PadRight(11)).CopyTo(e, 0);
        if (deleted) e[0] = 0xE5;

        e[11] = attributes;
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(20), (ushort)(firstCluster >> 16));
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(26), (ushort)(firstCluster & 0xFFFF));
        BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(28), size);

        // 2024-06-01 09:00
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(24), (ushort)(((2024 - 1980) << 9) | (6 << 5) | 1));
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(22), (ushort)((9 << 11) | (0 << 5)));
        return e;
    }

    /// <summary>בניית ערך שם ארוך הנושא עד 13 תווים.</summary>
    private static byte[] LongNameEntry(string part, byte ordinal, bool deleted = false)
    {
        byte[] e = new byte[32];
        e[0] = deleted ? (byte)0xE5 : ordinal;
        e[11] = 0x0F;

        var chars = part.PadRight(13, '￿').ToCharArray();
        int index = 0;

        void Put(int at, int count)
        {
            for (int i = 0; i < count; i++)
                BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(at + i * 2), chars[index++]);
        }

        Put(1, 5);
        Put(14, 6);
        Put(28, 2);
        return e;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        byte[] all = new byte[parts.Sum(p => p.Length) + 32]; // ערך מסיים ריק
        int at = 0;
        foreach (var p in parts) { p.CopyTo(all, at); at += p.Length; }
        return all;
    }

    [Fact]
    public void A_short_name_entry_is_parsed()
    {
        var entries = FatDirectory.Parse(Concat(ShortEntry("REPORT  TXT", 0x20, 5, 1234)));

        var e = Assert.Single(entries);
        Assert.Equal("REPORT.TXT", e.Name);
        Assert.Equal(5, e.FirstCluster);
        Assert.Equal(1234, e.Size);
        Assert.False(e.IsDeleted);
        Assert.False(e.IsDirectory);
    }

    [Fact]
    public void A_deleted_entry_is_flagged_and_its_first_letter_is_replaced()
    {
        var entries = FatDirectory.Parse(Concat(ShortEntry("REPORT  TXT", 0x20, 5, 1234, deleted: true)));

        var e = Assert.Single(entries);
        Assert.True(e.IsDeleted);

        // האות הראשונה נדרסה בסימון המחיקה ואינה ניתנת לשחזור.
        Assert.Equal("_EPORT.TXT", e.Name);
    }

    [Fact]
    public void Long_name_entries_are_assembled_in_reverse_order()
    {
        // השם מפוצל לשני ערכים, השמורים על הדיסק בסדר הפוך.
        var entries = FatDirectory.Parse(Concat(
            LongNameEntry("ארוך.txt", 0x42),
            LongNameEntry("שם קובץ מאוד ", 0x01),
            ShortEntry("SHMKOV~1TXT", 0x20, 9, 500)));

        var e = Assert.Single(entries);
        Assert.Equal("שם קובץ מאוד ארוך.txt", e.Name);
        Assert.Equal(9, e.FirstCluster);
    }

    [Fact]
    public void A_deleted_file_keeps_its_long_name()
    {
        // ערכי השם הארוך מסומנים גם הם כמחוקים, אך התווים עצמם שורדים.
        var entries = FatDirectory.Parse(Concat(
            LongNameEntry("מסמך חשוב.doc", 0x41, deleted: true),
            ShortEntry("MSMKCH~1DOC", 0x20, 12, 2048, deleted: true)));

        var e = Assert.Single(entries);
        Assert.Equal("מסמך חשוב.doc", e.Name);
        Assert.True(e.IsDeleted);
    }

    [Fact]
    public void Directories_and_dot_entries_are_identified()
    {
        var entries = FatDirectory.Parse(Concat(
            ShortEntry(".          ", 0x10, 7, 0),
            ShortEntry("..         ", 0x10, 0, 0),
            ShortEntry("SUBDIR     ", 0x10, 20, 0)));

        Assert.Equal(3, entries.Count);
        Assert.True(entries[0].IsDotEntry);
        Assert.True(entries[1].IsDotEntry);
        Assert.False(entries[2].IsDotEntry);
        Assert.True(entries[2].IsDirectory);
    }

    [Fact]
    public void The_volume_label_entry_is_not_reported_as_a_file()
    {
        var entries = FatDirectory.Parse(Concat(
            ShortEntry("MY VOLUME  ", 0x08, 0, 0),
            ShortEntry("DATA    BIN", 0x20, 4, 99)));

        var e = Assert.Single(entries);
        Assert.Equal("DATA.BIN", e.Name);
    }

    [Fact]
    public void A_32bit_first_cluster_is_reassembled_from_both_halves()
    {
        var entries = FatDirectory.Parse(Concat(ShortEntry("BIG     BIN", 0x20, 0x0012_3456, 1 << 20)));

        var e = Assert.Single(entries);
        Assert.Equal(0x0012_3456, e.FirstCluster);
    }

    [Fact]
    public void Timestamps_are_decoded_from_the_packed_fat_format()
    {
        var entries = FatDirectory.Parse(Concat(ShortEntry("FILE    TXT", 0x20, 2, 10)));

        var e = Assert.Single(entries);
        Assert.NotNull(e.Modified);
        Assert.Equal(new DateTime(2024, 6, 1, 9, 0, 0), e.Modified!.Value);
    }

    [Fact]
    public void Random_bytes_do_not_produce_plausible_entries()
    {
        var random = new Random(4242);
        int falsePositives = 0;

        for (int round = 0; round < 200; round++)
        {
            byte[] noise = new byte[4096];
            random.NextBytes(noise);

            // שמות עם תווי בקרה או תווים אסורים חייבים להיפסל.
            falsePositives += FatDirectory.Parse(noise, stopAtEnd: false).Count;
        }

        // רעש אקראי עשוי לייצר התאמה נדירה; הסף מוודא שזה לא נפוץ.
        Assert.True(falsePositives < 20, $"too many false positives: {falsePositives}");
    }
}
