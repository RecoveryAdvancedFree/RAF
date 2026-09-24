using System.Buffers.Binary;
using RAF.Core.Imaging;
using RAF.Core.Native;
using Xunit;

namespace RAF.Core.Tests;

/// <summary>
/// כונן וירטואלי של Windows (VHD / VHDX) נפתח ישירות מהקובץ. הקריאה נבדקה גם מול
/// Windows עצמו, על שלושה כוננים שנוצרו ב-diskpart — זהה בכל סקטור; כאן בונים
/// כוננים קטנים בקוד ובודקים את התרגום: בלוק קיים, בלוק שלא הוקצה (אפסים), וכתיבה חסומה.
/// </summary>
public class VirtualDiskTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"raf-vd-{Guid.NewGuid():N}");

    public void Dispose()
    {
        foreach (string ext in new[] { ".vhd", ".vhdx" })
            try { File.Delete(_path + ext); } catch { }
    }

    private static byte[] Pattern(int length, int seed) => RealFormats.Random(length, seed);

    private static byte[] Footer(long size, uint type, long dataOffset)
    {
        byte[] f = new byte[512];
        "conectix"u8.CopyTo(f);
        BinaryPrimitives.WriteInt64BigEndian(f.AsSpan(16), dataOffset);
        BinaryPrimitives.WriteInt64BigEndian(f.AsSpan(40), size);
        BinaryPrimitives.WriteInt64BigEndian(f.AsSpan(48), size);
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(60), type);
        return f;
    }

    private RawDevice Open(string ext, byte[] file)
    {
        File.WriteAllBytes(_path + ext, file);
        return RawDevice.TryOpen(_path + ext) ?? throw new IOException("open failed");
    }

    [Fact]
    public void A_fixed_vhd_is_the_data_itself_without_the_footer()
    {
        byte[] data = Pattern(64 * 1024, 1);
        using var device = Open(".vhd", [.. data, .. Footer(data.Length, 2, -1)]);

        Assert.Equal("VHD", device.Virtual?.Format);
        Assert.Equal(data.Length, device.Virtual!.Size);
        Assert.Equal(data[1000..3000], device.ReadBlock(1000, 2000));
    }

    /// <summary>VHD דינמי: בלוק 0 קיים, בלוק 1 לא הוקצה ונקרא כאפסים, בלוק 2 קיים.</summary>
    [Fact]
    public void A_dynamic_vhd_maps_blocks_and_reads_unallocated_ones_as_zeros()
    {
        const int block = 4096;
        byte[] first = Pattern(block, 2), third = Pattern(block, 3);

        var file = new MemoryStream();
        file.Write(Footer(3 * block, 3, 512));                                  // עותק הכותרת בהתחלה
        byte[] header = new byte[1024];
        "cxsparse"u8.CopyTo(header);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(8), -1);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(16), 1536);         // טבלת הבלוקים
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(28), 3);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(32), block);
        file.Write(header);
        byte[] table = new byte[512];
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(0), 4);             // בלוק 0 בסקטור 4
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(4), 0xFFFFFFFF);    // בלוק 1 — לא הוקצה
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(8), 13);            // בלוק 2 בסקטור 13
        file.Write(table);
        file.Write(new byte[512]); file.Write(first);                            // מפת סקטורים + נתונים (סקטור 4)
        file.Write(new byte[512]); file.Write(third);                            // סקטור 13
        file.Write(Footer(3 * block, 3, 512));

        using var device = Open(".vhd", file.ToArray());

        Assert.Equal(3 * block, device.Virtual!.Size);
        byte[] all = device.ReadBlock(0, 3 * block);
        Assert.Equal(first, all[..block]);
        Assert.All(all[block..(2 * block)], b => Assert.Equal(0, b));
        Assert.Equal(third, all[(2 * block)..]);
    }

    [Fact]
    public void A_differencing_vhd_is_refused_with_an_explanation()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Open(".vhd", [.. new byte[4096], .. Footer(4096, 4, 0)]));
        Assert.Contains("הפרשים", ex.Message);
    }

    /// <summary>VHDX מינימלי: בלוקים של 1MB, הראשון קיים והשני לא.</summary>
    [Fact]
    public void A_vhdx_maps_its_blocks_through_the_bat()
    {
        const int MB = 1024 * 1024;
        byte[] file = new byte[4 * MB];
        "vhdxfile"u8.CopyTo(file);
        "head"u8.CopyTo(file.AsSpan(64 * 1024));
        BinaryPrimitives.WriteInt64LittleEndian(file.AsSpan(64 * 1024 + 8), 1);

        // טבלת האזורים: טבלת הבלוקים ב-1MB, המידע ב-2MB.
        int regions = 192 * 1024;
        "regi"u8.CopyTo(file.AsSpan(regions));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(regions + 8), 2);
        void Region(int i, string guid, long at, int length)
        {
            new Guid(guid).ToByteArray().CopyTo(file, regions + 16 + i * 32);
            BinaryPrimitives.WriteInt64LittleEndian(file.AsSpan(regions + 16 + i * 32 + 16), at);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(regions + 16 + i * 32 + 24), (uint)length);
        }
        Region(0, "2DC27766-F623-4200-9D64-115E9BFD4A08", MB, MB);
        Region(1, "8B7CA206-4790-4B9A-B8FE-575F050F886E", 2 * MB, MB);

        int meta = 2 * MB;
        "metadata"u8.CopyTo(file.AsSpan(meta));
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(meta + 10), 3);
        void Item(int i, string guid, int offset)
        {
            new Guid(guid).ToByteArray().CopyTo(file, meta + 32 + i * 32);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(meta + 32 + i * 32 + 16), (uint)offset);
        }
        Item(0, "CAA16737-FA36-4D43-B3B6-33F0AA44E76B", 64 * 1024);
        Item(1, "2FA54224-CD1B-4876-B211-5DBED83BF4B8", 64 * 1024 + 8);
        Item(2, "8141BF1D-A96F-4709-BA47-F233A8FAAB5F", 64 * 1024 + 16);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(meta + 64 * 1024), MB);             // גודל בלוק
        BinaryPrimitives.WriteInt64LittleEndian(file.AsSpan(meta + 64 * 1024 + 8), 2 * MB);      // גודל הדיסק
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(meta + 64 * 1024 + 16), 512);

        // טבלת הבלוקים: בלוק 0 קיים ב-3MB (מצב 6), בלוק 1 לא קיים.
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(MB), (3UL << 20) | 6);
        byte[] data = Pattern(MB, 5);
        data.CopyTo(file, 3 * MB);

        using var device = Open(".vhdx", file);

        Assert.Equal("VHDX", device.Virtual?.Format);
        Assert.Equal(2 * MB, device.Virtual!.Size);
        Assert.Equal(data[..4096], device.ReadBlock(0, 4096));
        Assert.All(device.ReadBlock(MB, 4096), b => Assert.Equal(0, b));
    }

    [Fact]
    public void Writing_to_a_virtual_disk_file_is_refused()
    {
        byte[] data = Pattern(64 * 1024, 1);
        File.WriteAllBytes(_path + ".vhd", [.. data, .. Footer(data.Length, 2, -1)]);

        var ex = Assert.Throws<InvalidOperationException>(() => RawWriter.TryOpen(_path + ".vhd", 512));
        Assert.Contains("לקריאה בלבד", ex.Message);
    }

    [Fact]
    public void An_opened_vhd_shows_the_size_of_the_disk_inside_it()
    {
        byte[] data = Pattern(64 * 1024, 1);
        File.WriteAllBytes(_path + ".vhd", [.. data, .. Footer(data.Length, 2, -1)]);

        var disk = ImageDisk.Open(_path + ".vhd");
        try
        {
            Assert.Equal(data.Length, disk.SizeBytes);
            Assert.Contains("VHD", disk.ImageNote);
        }
        finally { ImageDisk.Close(disk.DiskNumber); }
    }
}
