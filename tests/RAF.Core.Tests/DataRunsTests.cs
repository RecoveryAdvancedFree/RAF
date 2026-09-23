using RAF.Core.FileSystems.Ntfs;
using Xunit;

namespace RAF.Core.Tests;

/// <summary>
/// בדיקות פענוח רשימות ה-data runs. קידוד שגוי כאן מתבטא בקבצים
/// שמשוחזרים מהמקום הלא נכון על הדיסק — תקלה שקטה במיוחד.
/// </summary>
public class DataRunsTests
{
    [Fact]
    public void Decodes_single_run()
    {
        // 0x21 = אורך בבית אחד, היסט בשני בתים.
        // אורך 0x18 = 24 אשכולות, היסט 0x0234 = 564.
        byte[] runs = { 0x21, 0x18, 0x34, 0x02, 0x00 };

        var extents = DataRuns.Decode(runs);

        Assert.Single(extents);
        Assert.Equal(564, extents[0].StartCluster);
        Assert.Equal(24, extents[0].ClusterCount);
        Assert.False(extents[0].IsSparse);
    }

    [Fact]
    public void Offsets_are_relative_to_previous_run()
    {
        // שתי ריצות: הראשונה באשכול 100, השנייה בהיסט יחסי +50 ממנה.
        byte[] runs =
        {
            0x11, 0x0A, 0x64,   // אורך 10, היסט +100  → מתחיל ב-100
            0x11, 0x05, 0x32,   // אורך 5,  היסט +50   → מתחיל ב-150
            0x00,
        };

        var extents = DataRuns.Decode(runs);

        Assert.Equal(2, extents.Count);
        Assert.Equal(100, extents[0].StartCluster);
        Assert.Equal(150, extents[1].StartCluster);
    }

    [Fact]
    public void Negative_offset_moves_backwards()
    {
        // 0xF6 כבית בודד עם סימן הוא -10.
        byte[] runs =
        {
            0x11, 0x08, 0x64,   // מתחיל ב-100
            0x11, 0x04, 0xF6,   // היסט -10 → מתחיל ב-90
            0x00,
        };

        var extents = DataRuns.Decode(runs);

        Assert.Equal(2, extents.Count);
        Assert.Equal(100, extents[0].StartCluster);
        Assert.Equal(90, extents[1].StartCluster);
    }

    [Fact]
    public void Zero_offset_length_marks_sparse_run()
    {
        // 0x01 = אורך בבית אחד, ללא בתי היסט → מקטע דליל.
        byte[] runs = { 0x01, 0x20, 0x00 };

        var extents = DataRuns.Decode(runs);

        Assert.Single(extents);
        Assert.True(extents[0].IsSparse);
        Assert.Equal(32, extents[0].ClusterCount);
    }

    [Fact]
    public void Truncated_list_stops_cleanly_instead_of_throwing()
    {
        // הכותרת מבטיחה שני בתי היסט אך הרשימה נגמרת — קלט פגום טיפוסי.
        byte[] runs = { 0x21, 0x18, 0x34 };

        var extents = DataRuns.Decode(runs);

        Assert.Empty(extents);
    }

    [Fact]
    public void Multi_byte_lengths_decode_as_unsigned()
    {
        // 0x22 = אורך בשני בתים (0x0100 = 256), היסט בשני בתים (0x0001 = 256).
        byte[] runs = { 0x22, 0x00, 0x01, 0x00, 0x01, 0x00 };

        var extents = DataRuns.Decode(runs);

        Assert.Single(extents);
        Assert.Equal(256, extents[0].ClusterCount);
        Assert.Equal(256, extents[0].StartCluster);
    }

    [Fact]
    public void Allocated_clusters_excludes_sparse_runs()
    {
        byte[] runs =
        {
            0x11, 0x0A, 0x64,   // 10 אשכולות אמיתיים
            0x01, 0x20,         // 32 אשכולות דלילים
            0x11, 0x05, 0x10,   // 5 אשכולות אמיתיים
            0x00,
        };

        var extents = DataRuns.Decode(runs);

        Assert.Equal(15, DataRuns.AllocatedClusters(extents));
    }
}
