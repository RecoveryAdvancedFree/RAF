using RAF.Core.Crypto;
using RAF.Core.Disks;
using RAF.Core.Model;
using RAF.Core.Native;
using Xunit;

namespace RAF.Core.Tests;

/// <summary>
/// מחיצת BitLocker: מזוהה ככזו (ולא כ"לא מזוהה", שהיה שולח לאבחון ולתיקון),
/// ונפתחת רק לקריאה דרך האות שלה.
/// </summary>
public class BitLockerTests
{
    [Fact]
    public void Boot_sector_with_the_FVE_signature_is_bitlocker()
    {
        byte[] head = new byte[2048];
        head[0] = 0xEB; head[1] = 0x58; head[2] = 0x90;
        "-FVE-FS-"u8.CopyTo(head.AsSpan(3));
        head[510] = 0x55; head[511] = 0xAA;

        var result = FileSystemIdentifier.Identify(head);

        Assert.Equal(FileSystemKind.BitLocker, result.Kind);
        Assert.Equal("BitLocker", FileSystemIdentifier.DisplayName(result.Kind));
        Assert.False(FileSystemIdentifier.IsSupported(result.Kind));
    }

    [Theory]
    [InlineData("E", @"\\.\E:")]
    [InlineData("e:", @"\\.\E:")]
    [InlineData(@"F:\", @"\\.\F:")]
    public void Volume_path_is_built_from_the_drive_letter(string letter, string expected)
    {
        Assert.Equal(expected, DevicePaths.VolumePathOf(letter));
        Assert.True(DevicePaths.IsVolumePath(expected));
    }

    [Theory]
    [InlineData(@"\\.\PhysicalDrive1")]
    [InlineData(@"C:\images\disk.img")]
    [InlineData(@"\\.\E:\")]
    public void Other_paths_are_not_volume_paths(string path)
        => Assert.False(DevicePaths.IsVolumePath(path));

    [Fact]
    public void Writing_to_a_volume_opened_through_windows_is_refused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => RawWriter.TryOpen(@"\\.\E:", 512));
        Assert.Contains("לקריאה בלבד", ex.Message);
    }

    // ============================================================== פתיחה במפתח

    /// <summary>
    /// קטעים מכוננים שהוצפנו ב-BitLocker של Windows 11 (ראו BitLockerRealTests): הכותרת,
    /// העותק הראשון של אזור הניהול, ותחילת המחיצה המקורית — מוצפנת, במקום שאליו הועברה.
    /// כל השאר אפסים. הסיסמה של שניהם Raf-Test-123.
    /// </summary>
    private static (long Size, RawRead Read) Fixture(string name)
    {
        using var input = new BinaryReader(File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Media", $"bitlocker-{name}.bin")));
        long size = input.ReadInt64();
        var regions = new List<(long At, byte[] Data)>();
        while (input.BaseStream.Position < input.BaseStream.Length)
        {
            long at = input.ReadInt64();
            regions.Add((at, input.ReadBytes(input.ReadInt32())));
        }

        return (size, (offset, destination) =>
        {
            destination.Clear();
            foreach (var (at, data) in regions)
            {
                long a = Math.Max(offset, at), b = Math.Min(offset + destination.Length, at + data.Length);
                if (b > a) data.AsSpan((int)(a - at), (int)(b - a)).CopyTo(destination[(int)(a - offset)..]);
            }
            return destination.Length;
        });
    }

    private static byte[] Block(RawRead read, long at, int length)
    {
        byte[] buffer = new byte[length];
        read(at, buffer);
        return buffer;
    }

    [Theory]
    [InlineData("ntfs-xts128", "605803-310222-298342-343541-344839-125180-203225-258423", FileSystemKind.Ntfs, "AES-XTS 128")]
    [InlineData("exfat-cbc128", "060896 081070 131175 104104 653312 179575 521719 296747", FileSystemKind.ExFat, "AES-CBC 128")]
    [InlineData("ntfs-xts128", "Raf-Test-123", FileSystemKind.Ntfs, "AES-XTS 128")]
    public void A_real_bitlocker_volume_opens_with_the_recovery_key_or_the_password(
        string name, string secret, FileSystemKind inside, string method)
    {
        var (size, read) = Fixture(name);
        var metadata = BitLockerMetadata.Read((at, n) => Block(read, at, n), size);

        Assert.Contains(metadata.Protectors, p => p.Kind == BitLockerMetadata.ProtectorKind.RecoveryPassword);
        Assert.Contains(metadata.Protectors, p => p.Kind == BitLockerMetadata.ProtectorKind.Password);

        var cipher = metadata.Unlock(secret);
        Assert.NotNull(cipher);
        Assert.Equal(method, cipher!.Name);

        // תחילת המחיצה, מפוענחת מהמקום שאליו הועברה — מערכת הקבצים שבתוכה.
        var volume = BitLockerVolume.Open(metadata, cipher, size, read);
        Assert.Equal(inside, volume.Inner.Kind);

        // אזור הניהול נקרא כאפסים: הוא אינו חלק מהקבצים.
        byte[] managed = new byte[4096];
        Assert.Equal(managed.Length, volume.Read(metadata.BlockOffsets[0], managed, read));
        Assert.All(managed, b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData("Raf-Test-124")]
    [InlineData("605803-310222-298342-343541-344839-125180-203225-258434")]   // מפתח חוקי אחר
    [InlineData("")]
    public void A_wrong_key_never_opens_the_volume(string secret)
    {
        var (size, read) = Fixture("ntfs-xts128");
        var metadata = BitLockerMetadata.Read((at, n) => Block(read, at, n), size);
        Assert.Null(metadata.Unlock(secret));
    }

    [Theory]
    [InlineData("605803-310222-298342-343541-344839-125180-203225-258423", true)]
    [InlineData("605803310222298342343541344839125180203225258423", true)]
    [InlineData("605803-310222-298342-343541-344839-125180-203225-258424", false)]  // אינו מתחלק ב-11
    [InlineData("605803-310222-298342-343541-344839-125180-203225", false)]         // 7 קבוצות
    [InlineData("999999-310222-298342-343541-344839-125180-203225-258423", false)]  // המנה גדולה מ-65535
    public void Recovery_key_is_eight_groups_divisible_by_eleven(string text, bool valid)
        => Assert.Equal(valid, BitLockerMetadata.RecoveryKey(text) is not null);

    [Fact]
    public void BitLocker_to_go_is_recognised_behind_its_fat32_disguise()
    {
        byte[] head = new byte[2048];
        head[0] = 0xEB; head[1] = 0x58; head[2] = 0x90;
        "MSWIN4.1"u8.CopyTo(head.AsSpan(3));
        "FAT32   "u8.CopyTo(head.AsSpan(82));
        new Guid("4967d63b-2e29-4ad8-8399-f6a339e3d001").TryWriteBytes(head.AsSpan(0x1A8));
        head[510] = 0x55; head[511] = 0xAA;

        Assert.Equal(FileSystemKind.BitLocker, FileSystemIdentifier.Identify(head).Kind);

        // בלי המזהה — FAT32 רגיל.
        head.AsSpan(0x1A8, 16).Clear();
        Assert.Equal(FileSystemKind.Fat32, FileSystemIdentifier.Identify(head).Kind);
    }

    [Fact]
    public void Writing_to_a_volume_decrypted_by_the_program_is_refused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => RawWriter.TryOpen("bitlocker:3@1048576", 512));
        Assert.Contains("לקריאה בלבד", ex.Message);
    }
}
