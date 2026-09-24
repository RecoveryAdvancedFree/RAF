using System.Buffers.Binary;
using RAF.Core.Disks;
using Xunit;

namespace RAF.Core.Tests;

public class DriveHealthTests
{
    private static byte[] NvmeLog(byte critical = 0, int celsius = 40, byte spare = 100, byte spareThreshold = 10,
        byte used = 3, long hours = 1234, long mediaErrors = 0)
    {
        var log = new byte[512];
        log[0] = critical;
        BinaryPrimitives.WriteUInt16LittleEndian(log.AsSpan(1), (ushort)(celsius + 273));
        log[3] = spare;
        log[4] = spareThreshold;
        log[5] = used;
        BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(128), (ulong)hours);
        BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(160), (ulong)mediaErrors);
        return log;
    }

    [Fact]
    public void A_healthy_nvme_drive_is_good_and_its_numbers_are_read()
    {
        var h = DriveHealth.FromNvme(NvmeLog());

        Assert.Equal(HealthLevel.Good, h.Level);
        Assert.Empty(h.Problems);
        Assert.Equal(40, h.TemperatureC);
        Assert.Equal(1234, h.PowerOnHours);
        Assert.Equal(3, h.PercentUsed);
        Assert.Equal(100, h.SpareLeft);
    }

    [Fact]
    public void An_nvme_drive_out_of_spare_or_read_only_is_bad()
    {
        Assert.Equal(HealthLevel.Bad, DriveHealth.FromNvme(NvmeLog(spare: 5)).Level);
        Assert.Equal(HealthLevel.Bad, DriveHealth.FromNvme(NvmeLog(critical: 0x08)).Level);
    }

    [Fact]
    public void A_worn_nvme_drive_or_one_with_media_errors_is_a_caution()
    {
        Assert.Equal(HealthLevel.Caution, DriveHealth.FromNvme(NvmeLog(used: 104)).Level);
        var h = DriveHealth.FromNvme(NvmeLog(mediaErrors: 7));
        Assert.Equal(HealthLevel.Caution, h.Level);
        Assert.Contains(h.Problems, p => p.Contains('7'));
    }

    /// <summary>טבלת SMART: גרסה (2) ו-30 רשומות של 12 בתים — מזהה, דגלים, נוכחי, גרוע ביותר, גולמי.</summary>
    private static byte[] Attributes(params (byte Id, byte Current, long Raw)[] rows)
    {
        var data = new byte[512];
        for (int i = 0; i < rows.Length; i++)
        {
            var at = data.AsSpan(2 + i * 12, 12);
            at[0] = rows[i].Id;
            at[3] = rows[i].Current;
            at[4] = rows[i].Current;
            BinaryPrimitives.WriteUInt32LittleEndian(at[5..], (uint)rows[i].Raw);
        }
        return data;
    }

    private static byte[] Thresholds(params (byte Id, byte Limit)[] rows)
    {
        var data = new byte[512];
        for (int i = 0; i < rows.Length; i++)
        {
            data[2 + i * 12] = rows[i].Id;
            data[2 + i * 12 + 1] = rows[i].Limit;
        }
        return data;
    }

    [Fact]
    public void A_healthy_sata_drive_is_good()
    {
        var h = DriveHealth.FromAta(
            Attributes((5, 100, 0), (9, 97, 15000), (194, 64, 36), (197, 100, 0), (198, 100, 0)),
            Thresholds((5, 36), (197, 0)), predictFailure: null);

        Assert.Equal(HealthLevel.Good, h.Level);
        Assert.Equal(15000, h.PowerOnHours);
        Assert.Equal(36, h.TemperatureC);
        Assert.Equal(0, h.Reallocated);
    }

    [Fact]
    public void Unreadable_sectors_make_a_sata_drive_bad_and_reallocated_ones_a_caution()
    {
        var pending = DriveHealth.FromAta(Attributes((5, 100, 0), (197, 100, 12)), default, null);
        Assert.Equal(HealthLevel.Bad, pending.Level);
        Assert.Equal(12, pending.Pending);

        var reallocated = DriveHealth.FromAta(Attributes((5, 98, 40)), default, null);
        Assert.Equal(HealthLevel.Caution, reallocated.Level);
        Assert.Equal(40, reallocated.Reallocated);
    }

    [Fact]
    public void An_attribute_at_its_failure_threshold_makes_the_drive_bad()
    {
        var h = DriveHealth.FromAta(Attributes((1, 6, 0)), Thresholds((1, 6)), predictFailure: null);
        Assert.Equal(HealthLevel.Bad, h.Level);

        Assert.Equal(HealthLevel.Bad, DriveHealth.FromAta(Attributes((9, 100, 1)), default, predictFailure: true).Level);
    }
}
