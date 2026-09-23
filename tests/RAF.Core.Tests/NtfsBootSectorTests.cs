using System.Buffers.Binary;
using System.Text;
using RAF.Core.FileSystems.Ntfs;
using Xunit;

namespace RAF.Core.Tests;

/// <summary>בדיקות ניתוח מגזר האתחול, כולל דחיית מגזרים פגומים.</summary>
public class NtfsBootSectorTests
{
    /// <summary>בניית מגזר אתחול NTFS תקין עם פרמטרים נתונים.</summary>
    private static byte[] Build(
        ushort bytesPerSector = 512, byte sectorsPerCluster = 8,
        long totalSectors = 2_097_152, long mftCluster = 786_432,
        sbyte recordSizeField = -10, sbyte indexSizeField = 1)
    {
        byte[] sector = new byte[512];
        Encoding.ASCII.GetBytes("NTFS    ").CopyTo(sector, 3);

        BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(11), bytesPerSector);
        sector[13] = sectorsPerCluster;
        BinaryPrimitives.WriteInt64LittleEndian(sector.AsSpan(40), totalSectors);
        BinaryPrimitives.WriteInt64LittleEndian(sector.AsSpan(48), mftCluster);
        BinaryPrimitives.WriteInt64LittleEndian(sector.AsSpan(56), totalSectors / 2);
        sector[64] = (byte)recordSizeField;
        sector[68] = (byte)indexSizeField;
        BinaryPrimitives.WriteUInt64LittleEndian(sector.AsSpan(72), 0x1234_5678_9ABC_DEF0);

        sector[510] = 0x55;
        sector[511] = 0xAA;
        return sector;
    }

    [Fact]
    public void Parses_a_standard_volume()
    {
        var boot = NtfsBootSector.Parse(Build());

        Assert.NotNull(boot);
        Assert.Equal(512, boot!.BytesPerSector);
        Assert.Equal(8, boot.SectorsPerCluster);
        Assert.Equal(4096, boot.BytesPerCluster);
        Assert.Equal(786_432L * 4096, boot.MftOffset);
        Assert.Equal(0x1234_5678_9ABC_DEF0UL, boot.VolumeSerial);
    }

    [Fact]
    public void Negative_size_field_means_a_power_of_two_in_bytes()
    {
        // ‎-10 מקודד 2^10 = 1024 בתים לרשומה. זהו המצב הרגיל ב-NTFS.
        var boot = NtfsBootSector.Parse(Build(recordSizeField: -10));
        Assert.Equal(1024, boot!.MftRecordSize);
    }

    [Fact]
    public void Positive_size_field_means_a_cluster_count()
    {
        // ערך 1 פירושו אשכול שלם לרשומה — כאן 4096 בתים.
        var boot = NtfsBootSector.Parse(Build(recordSizeField: 1));
        Assert.Equal(4096, boot!.MftRecordSize);
    }

    [Fact]
    public void Large_cluster_encoding_is_decoded_as_a_power_of_two()
    {
        // ערך מעל 0x80 מקודד חזקה של 2 בהשלמה ל-2: 0xF4 → 2^12 = 4096 סקטורים.
        var boot = NtfsBootSector.Parse(Build(sectorsPerCluster: 0xF4));
        Assert.Equal(4096, boot!.SectorsPerCluster);
    }

    [Fact]
    public void Non_ntfs_sector_is_rejected()
    {
        byte[] sector = Build();
        Encoding.ASCII.GetBytes("MSDOS5.0").CopyTo(sector, 3);

        Assert.Null(NtfsBootSector.Parse(sector));
    }

    [Theory]
    [InlineData(0)]      // גודל סקטור אפס
    [InlineData(100)]    // אינו חזקת 2
    [InlineData(16384)]  // מעבר לטווח הסביר
    public void Impossible_sector_sizes_are_rejected(ushort bytesPerSector)
        => Assert.Null(NtfsBootSector.Parse(Build(bytesPerSector: bytesPerSector)));

    [Fact]
    public void Zero_mft_cluster_is_rejected()
        => Assert.Null(NtfsBootSector.Parse(Build(mftCluster: 0)));

    [Fact]
    public void Truncated_sector_is_rejected()
        => Assert.Null(NtfsBootSector.Parse(new byte[128]));
}
