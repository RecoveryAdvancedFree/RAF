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
        foreach (string ext in new[] { ".vhd", ".vhdx", ".vmdk" })
            try { File.Delete(_path + ext); } catch { }
        foreach (string dir in _dirs)
            try { Directory.Delete(dir, true); } catch { }
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

    // ============================================================== VMDK

    private readonly List<string> _dirs = new();

    private string Folder()
    {
        string dir = _path + "-vmdk";
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    /// <summary>
    /// VMDK שגדל לפי הצורך: 8 גרגרים של 4KB, ארבעה בכל טבלה. גרגר 0 ו-2 כתובים,
    /// גרגר 1 לא נכתב, גרגר 3 סומן כמאופס, והטבלה השנייה (גרגרים 4–7) לא קיימת כלל.
    /// </summary>
    private static byte[] SparseVmdk(byte[] first, byte[] third, uint flags = 3, string? parentCid = null)
    {
        byte[] header = new byte[512];
        "KDMV"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), flags);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(12), 64);        // 64 סקטורים = 32KB
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(20), 8);         // גרגר של 8 סקטורים
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(44), 4);        // 4 רשומות בטבלה
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(56), 1);         // הספרייה בסקטור 1

        byte[] descriptor = [];
        if (parentCid is not null)
        {
            descriptor = new byte[512];
            System.Text.Encoding.ASCII.GetBytes($"# Disk DescriptorFile\nparentCID={parentCid}\n").CopyTo(descriptor, 0);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(28), 19);    // התיאור אחרי הגרגרים
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(36), 1);
        }

        byte[] directory = new byte[512];
        BinaryPrimitives.WriteUInt32LittleEndian(directory, 2);                // טבלה 0 בסקטור 2; טבלה 1 — אין

        byte[] table = new byte[512];
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(0), 3);          // גרגר 0 — סקטור 3
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(8), 11);         // גרגר 2 — סקטור 11
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(12), 1);         // גרגר 3 — מאופס

        return [.. header, .. directory, .. table, .. first, .. third, .. descriptor];
    }

    [Fact]
    public void A_growing_vmdk_maps_grains_and_reads_missing_ones_as_zeros()
    {
        byte[] first = Pattern(4096, 11), third = Pattern(4096, 12);
        using var device = Open(".vmdk", SparseVmdk(first, third));

        Assert.Equal("VMDK", device.Virtual?.Format);
        Assert.Equal(32 * 1024, device.Virtual!.Size);

        byte[] all = device.ReadBlock(0, 32 * 1024);
        Assert.Equal(first, all[..4096]);
        Assert.All(all[4096..8192], b => Assert.Equal(0, b));
        Assert.Equal(third, all[8192..12288]);
        Assert.All(all[12288..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void A_vmdk_made_of_several_files_reads_across_them_in_order()
    {
        string dir = Folder();
        byte[] flat = Pattern(8192, 13);
        byte[] first = Pattern(4096, 14), third = Pattern(4096, 15);
        File.WriteAllBytes(Path.Combine(dir, "disk-flat.vmdk"), flat);
        File.WriteAllBytes(Path.Combine(dir, "disk-s001.vmdk"), SparseVmdk(first, third));

        // חלק בגודל מלא שמתחיל באמצע הקובץ שלו, חלק של אפסים, וחלק שגדל לפי הצורך.
        string descriptor = Path.Combine(dir, "disk.vmdk");
        File.WriteAllText(descriptor,
            "# Disk DescriptorFile\nversion=1\nCID=fffffffe\nparentCID=ffffffff\ncreateType=\"twoGbMaxExtentSparse\"\n\n" +
            "RW 8 FLAT \"disk-flat.vmdk\" 2\nRW 8 ZERO\nRW 64 SPARSE \"disk-s001.vmdk\"\n");

        using var device = RawDevice.TryOpen(descriptor)!;
        Assert.Equal(4096 + 4096 + 32 * 1024, device.Virtual!.Size);

        byte[] all = device.ReadBlock(0, (int)device.Virtual.Size);
        Assert.Equal(flat[1024..5120], all[..4096]);
        Assert.All(all[4096..8192], b => Assert.Equal(0, b));
        Assert.Equal(first, all[8192..12288]);
        Assert.Equal(third, all[16384..20480]);

        // קריאה אחת שחוצה את הגבול בין שני קבצים.
        Assert.Equal(all[4000..8300], device.ReadBlock(4000, 4300));

        var disk = ImageDisk.Open(descriptor);
        try { Assert.Contains("VMDK", disk.ImageNote); }
        finally { ImageDisk.Close(disk.DiskNumber); }
    }

    [Fact]
    public void A_vmdk_whose_part_is_missing_names_the_missing_file()
    {
        string descriptor = Path.Combine(Folder(), "disk.vmdk");
        File.WriteAllText(descriptor, "# Disk DescriptorFile\nparentCID=ffffffff\nRW 64 SPARSE \"disk-s002.vmdk\"\n");

        var ex = Assert.Throws<InvalidOperationException>(() => RawDevice.TryOpen(descriptor));
        Assert.Contains("disk-s002.vmdk", ex.Message);
    }

    [Fact]
    public void A_snapshot_vmdk_is_refused_with_an_explanation()
    {
        string descriptor = Path.Combine(Folder(), "disk-000001.vmdk");
        File.WriteAllText(descriptor, "# Disk DescriptorFile\nparentCID=6a1b2c3d\nRW 64 SPARSE \"disk-000001-s001.vmdk\"\n");
        Assert.Contains("הפרשים", Assert.Throws<InvalidOperationException>(() => RawDevice.TryOpen(descriptor)).Message);

        // גם כשהתיאור שמור בתוך קובץ יחיד.
        byte[] single = SparseVmdk(Pattern(4096, 16), Pattern(4096, 17), parentCid: "6a1b2c3d");
        Assert.Contains("הפרשים", Assert.Throws<InvalidOperationException>(() => Open(".vmdk", single)).Message);
    }

    [Fact]
    public void A_compressed_exported_vmdk_is_refused_with_an_explanation()
    {
        byte[] compressed = SparseVmdk(Pattern(4096, 18), Pattern(4096, 19), flags: 3 | (1 << 16));
        Assert.Contains("דחוס", Assert.Throws<InvalidOperationException>(() => Open(".vmdk", compressed)).Message);
    }
}
