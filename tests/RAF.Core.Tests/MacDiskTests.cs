using RAF.Core.Disks;
using RAF.Core.Model;
using Xunit;

namespace RAF.Core.Tests;

/// <summary>
/// כוננים במק — רק בבדיקה האוטומטית במק (RAF_MAC_DEVICE_TESTS=1), שמחברת קודם תמונת דיסק
/// קטנה (64MB) עם מחיצת exFAT (hdiutil). רצה כמנהל, ולכן פותחת את הכונן ישירות;
/// RAF_MAC_AUTHOPEN=1 מכריח את הדרך של authopen, שבה משתמש משתמש רגיל.
/// </summary>
public class MacDiskTests
{
    private static bool Enabled => OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("RAF_MAC_DEVICE_TESTS") == "1";

    [Fact]
    public void An_attached_disk_is_listed_and_its_partition_is_read()
    {
        if (!Enabled) return;
        var disks = MacDisks.Enumerate();
        // שם המחיצה ב-Windows מגיע ממערכת ההפעלה; כאן מזהים את הכונן לפי הגודל ומערכת הקבצים.
        var test = disks.FirstOrDefault(d => d.SizeBytes == 64L * 1024 * 1024 && d.Partitions.Any(p => p.FileSystem == FileSystemKind.ExFat));
        Assert.True(test is not null, "disks: " + string.Join(" | ", disks.Select(d =>
            $"{d.Model} {d.BusType} {d.SizeBytes} raw={d.RawAccessible} {d.Problem} [{string.Join(",", d.Partitions.Select(p => p.FileSystem + ":" + p.Label))}]")));
        Assert.True(test!.RawAccessible);
        Assert.Contains(test.Partitions, p => p.FileSystem == FileSystemKind.ExFat);
    }
}
