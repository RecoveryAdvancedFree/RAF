using RAF.Core.Model;
using Xunit;

namespace RAF.Core.Tests;

public class SectorMapTests
{
    [Fact]
    public void Every_byte_belongs_to_exactly_one_cell_and_the_cells_cover_the_whole_area()
    {
        var map = new SectorMap(1_000_003, cells: 7);

        Assert.Equal(0, map.CellStart(0));
        Assert.Equal(map.Length, map.CellStart(map.Cells));
        for (int c = 0; c < map.Cells; c++)
        {
            Assert.Equal(c, map.CellOf(map.CellStart(c)));
            Assert.Equal(c, map.CellOf(map.CellStart(c + 1) - 1));
        }
    }

    [Fact]
    public void A_cell_shows_its_most_important_state()
    {
        var map = new SectorMap(1000, cells: 10);
        map.Add(0, 1000, SectorState.Read);
        map.Mark(250, SectorState.Found);
        map.Add(510, 5, SectorState.Bad);

        // ריבועים של 100 בתים: נמצא קובץ בריבוע 2, וסקטור שלא נקרא בריבוע 5.
        Assert.Equal("2232252222", map.Snapshot());
    }

    [Fact]
    public void A_retry_that_succeeds_leaves_the_cell_read_and_one_that_fails_leaves_it_bad()
    {
        var map = new SectorMap(1000, cells: 10);
        map.Add(0, 100, SectorState.Read);
        map.Add(100, 200, SectorState.Retry);
        Assert.Equal(SectorState.Retry, map.StateOf(1));

        // הניסיון החוזר: האשכול הראשון הצליח, מהשני סקטור אחד לא נקרא.
        map.Remove(100, 100, SectorState.Retry);
        map.Add(100, 100, SectorState.Read);
        map.Remove(200, 100, SectorState.Retry);
        map.Add(200, 90, SectorState.Read);
        map.Add(290, 10, SectorState.Bad);

        Assert.Equal(SectorState.Read, map.StateOf(1));
        Assert.Equal(SectorState.Bad, map.StateOf(2));
        Assert.Equal(SectorState.Pending, map.StateOf(3));
    }

    [Fact]
    public void A_tiny_area_has_no_more_cells_than_bytes()
    {
        var map = new SectorMap(3);
        Assert.Equal(3, map.Cells);
        Assert.Equal(3, map.Snapshot().Length);
    }
}
