using RAF.Core.Model;
using RAF.Core.Recovery;
using Xunit;

namespace RAF.Core.Tests;

/// <summary>
/// כפילויות בתוצאות: מה נחשב כפול, איזה עותק נשאר, ומתי לא קוראים בכלל מהדיסק.
/// </summary>
public class DuplicatesTests
{
    private static RecoveredFile File_(long id, long size, long cluster,
        RecoveryQuality quality = RecoveryQuality.Excellent, DiscoverySource source = DiscoverySource.MftActive)
        => new()
        {
            Id = id, Name = $"f{id}.jpg", Size = size, Quality = quality, Source = source,
            Content = ContentCheck.HasData,
            Extents = new List<DataExtent> { new(cluster, 4, false) },
        };

    /// <summary>"דיסק" מדומה: התוכן נקבע לפי האשכול הראשון.</summary>
    private static Func<RecoveredFile, byte[]?> Disk(Dictionary<long, byte> contentAt, List<long>? reads = null)
        => f =>
        {
            reads?.Add(f.Id);
            byte fill = contentAt[f.Extents[0].StartCluster];
            return Enumerable.Repeat(fill, (int)Math.Min(f.Size, 1000)).ToArray();
        };

    [Fact]
    public void Same_content_in_two_places_keeps_the_best_copy()
    {
        var files = new[]
        {
            File_(1, 5000, 100, RecoveryQuality.Good),
            File_(2, 5000, 200, RecoveryQuality.Excellent),                    // זה נשאר — איכות טובה יותר
            File_(3, 5000, 300, RecoveryQuality.Excellent, DiscoverySource.Carving),
            File_(4, 5000, 400),                                               // אותו גודל, תוכן אחר
        };
        var disk = Disk(new() { [100] = 7, [200] = 7, [300] = 7, [400] = 9 });

        var hidden = Duplicates.Find(files, disk);

        Assert.Equal(2, hidden.Count);
        Assert.Equal(2, hidden[1]);
        Assert.Equal(2, hidden[3]);
        Assert.False(hidden.ContainsKey(4));
    }

    [Fact]
    public void Files_with_a_unique_size_are_never_read()
    {
        var reads = new List<long>();
        var files = new[] { File_(1, 1000, 10), File_(2, 2000, 20), File_(3, 3000, 30) };

        var hidden = Duplicates.Find(files, Disk(new() { [10] = 1, [20] = 1, [30] = 1 }, reads));

        Assert.Empty(hidden);
        Assert.Empty(reads);
    }

    [Fact]
    public void Same_place_on_disk_is_a_duplicate_even_without_reading()
    {
        // כונן לא מחובר: אי אפשר לקרוא — אבל שתי רשומות לאותו מקום הן אותו קובץ.
        var files = new[]
        {
            File_(1, 5000, 100, source: DiscoverySource.MftOrphan),
            File_(2, 5000, 100),
            File_(3, 5000, 900),
        };

        var hidden = Duplicates.Find(files, _ => null);

        Assert.Single(hidden);
        Assert.Equal(1, hidden[2]);                                            // בשוויון — זה שנמצא ראשון נשאר
    }

    [Fact]
    public void Unrecoverable_and_empty_files_are_not_compared()
    {
        var files = new[]
        {
            File_(1, 5000, 100),
            File_(2, 5000, 100, RecoveryQuality.Unrecoverable),
        };

        Assert.Empty(Duplicates.Find(files, _ => null));
    }
}
