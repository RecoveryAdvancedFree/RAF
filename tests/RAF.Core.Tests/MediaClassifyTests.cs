using RAF.Core.Disks;
using RAF.Core.Model;
using RAF.Core.Native;
using Xunit;

namespace RAF.Core.Tests;

/// <summary>
/// סיווג סוג המדיה. כרטיס זיכרון בקורא כרטיסים מדווח על פס USB, ולכן
/// רק שם ההתקן מבדיל בינו לבין דיסק-און-קי.
/// </summary>
public class MediaClassifyTests
{
    [Theory]
    [InlineData("Generic- SD/MMC")]
    [InlineData("Generic STORAGE DEVICE USB3.0 CRW -SD")]
    [InlineData("Multi-Card Reader")]
    [InlineData("Transcend microSD")]
    [InlineData("Generic- SDXC")]
    [InlineData("Generic- MS/MS-Pro")]
    [InlineData("Generic- Compact Flash")]
    public void A_card_in_a_usb_reader_is_a_memory_card(string name)
        => Assert.Equal(MediaKind.MemoryCard,
            StorageQuery.Classify(Win32.StorageBusType.Usb, false, true, name));

    [Theory]
    [InlineData("USB SanDisk 3.2Gen1")]
    [InlineData("Kingston DataTraveler 3.0")]
    [InlineData("Samsung Flash Drive FIT")]
    [InlineData("")]
    public void A_flash_drive_stays_a_flash_drive(string name)
        => Assert.Equal(MediaKind.UsbFlash,
            StorageQuery.Classify(Win32.StorageBusType.Usb, false, true, name));

    [Fact]
    public void A_usb_disk_with_seek_penalty_is_a_hard_disk_even_when_named_like_a_reader()
        => Assert.Equal(MediaKind.HardDisk,
            StorageQuery.Classify(Win32.StorageBusType.Usb, true, false, "Card Reader"));
}
