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
}
