using SPVision.Core;

namespace SPVision.Core.Tests;

public class GridLayoutTests
{
    [Fact]
    public void LayoutsHaveExpectedShapes()
    {
        Assert.Equal(new[] { 6, 8, 25, 32 }, GridLayout.All.Select(l => l.TileCount));
        Assert.Equal((3, 3), (GridLayout.Six.Columns, GridLayout.Six.Rows));
        Assert.Equal((4, 4), (GridLayout.Eight.Columns, GridLayout.Eight.Rows));
        Assert.Equal((5, 5), (GridLayout.TwentyFive.Columns, GridLayout.TwentyFive.Rows));
        Assert.Equal((8, 4), (GridLayout.ThirtyTwo.Columns, GridLayout.ThirtyTwo.Rows));
        Assert.Equal(32, GridLayout.MaxTiles);
        Assert.Same(GridLayout.TwentyFive, GridLayout.Default);
    }

    [Fact]
    public void BigTileLayoutsFillRightColumnThenBottomRow()
    {
        Assert.Equal(
            new GridCell[] { new(0, 0, 2, 2), new(0, 2), new(1, 2), new(2, 0), new(2, 1), new(2, 2) },
            GridLayout.Six.Cells);
        Assert.Equal(
            new GridCell[] { new(0, 0, 3, 3), new(0, 3), new(1, 3), new(2, 3), new(3, 0), new(3, 1), new(3, 2), new(3, 3) },
            GridLayout.Eight.Cells);
    }

    [Theory]
    [InlineData(25)]
    [InlineData(32)]
    public void UniformLayoutsCoverEveryCellOnce(int tileCount)
    {
        var layout = GridLayout.Fitting(tileCount);

        Assert.All(layout.Cells, c => Assert.Equal((1, 1), (c.RowSpan, c.ColumnSpan)));
        Assert.Equal(layout.Columns * layout.Rows, layout.Cells.Distinct().Count());
        Assert.Equal(new GridCell(0, 1), layout.Cells[1]); // row-major order
    }

    [Theory]
    [InlineData(1, 6)]   // old 1×1
    [InlineData(2, 6)]   // old 2×2 (4 tiles)
    [InlineData(3, 25)]  // old 3×3 (9 tiles)
    [InlineData(4, 25)]  // old 4×4 (16 tiles)
    public void LegacyColumnsMapToSmallestLayoutThatHoldsTheTiles(int columns, int expected)
    {
        Assert.Equal(expected, GridLayout.ForSavedView(new SavedView { Columns = columns }).TileCount);
    }

    [Theory]
    [InlineData(6, 6)]
    [InlineData(8, 8)]
    [InlineData(25, 25)]
    [InlineData(32, 32)]
    [InlineData(9, 25)]
    [InlineData(16, 25)]
    [InlineData(40, 32)]
    public void SavedLayoutIsKeptOrFitted(int layout, int expected)
    {
        Assert.Equal(expected, GridLayout.ForSavedView(new SavedView { Layout = layout, Columns = 2 }).TileCount);
    }

    [Fact]
    public void MissingOrInvalidViewUsesDefault()
    {
        Assert.Same(GridLayout.Default, GridLayout.ForSavedView(null));
        Assert.Same(GridLayout.Default, GridLayout.ForSavedView(new SavedView()));
        Assert.Same(GridLayout.Default, GridLayout.ForSavedView(new SavedView { Layout = 0, Columns = 7 }));
    }

    [Fact]
    public void LegacySettingsFileKeepsTileAssignmentsInOrder()
    {
        var path = Path.Combine(Path.GetTempPath(), $"spvision-{Guid.NewGuid()}.json");
        var deviceId = Guid.NewGuid();
        try
        {
            File.WriteAllText(path, $$"""
                {
                  "LastView": {
                    "Name": "Son görünüm",
                    "Columns": 3,
                    "Tiles": [ { "DeviceId": "{{deviceId}}", "Channel": 5 }, null, { "DeviceId": "{{deviceId}}", "Channel": 2 } ]
                  }
                }
                """);

            var view = new SettingsStore(path).Load().LastView!;

            Assert.Null(view.Layout);
            Assert.Equal(25, GridLayout.ForSavedView(view).TileCount);
            Assert.Equal(new CameraRef?[] { new(deviceId, 5), null, new(deviceId, 2) }, view.Tiles);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
