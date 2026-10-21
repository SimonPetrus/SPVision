using SPVision.Core;

namespace SPVision.Core.Tests;

public class GridLayoutTests
{
    [Fact]
    public void PickerSectionsHaveExpectedTileCounts()
    {
        Assert.Equal(new[] { 1, 4, 9, 16, 25, 36 }, GridLayout.Standard.Select(l => l.TileCount));
        Assert.Equal(new[] { 6, 7, 8, 10, 13, 22 }, GridLayout.BigTile.Select(l => l.TileCount));
        Assert.Equal(new[] { 1, 4, 6, 7, 8, 9, 10, 13, 16, 22, 25, 36 }, GridLayout.All.Select(l => l.TileCount));
        Assert.Equal(36, GridLayout.MaxTiles);
        Assert.Same(GridLayout.TwentyFive, GridLayout.Default);
    }

    [Fact]
    public void PlaybackOffersLayoutsUpToSixteenTiles()
    {
        Assert.Equal(new[] { 1, 4, 9, 16 }, GridLayout.PlaybackStandard.Select(l => l.TileCount));
        Assert.Equal(new[] { 6, 7, 8, 10, 13 }, GridLayout.PlaybackBigTile.Select(l => l.TileCount));
        Assert.Same(GridLayout.Four, GridLayout.PlaybackDefault);
    }

    [Fact]
    public void EveryLayoutIsSquare()
    {
        Assert.All(GridLayout.All, l => Assert.Equal(l.Columns, l.Rows));
        Assert.All(GridLayout.All, l => Assert.All(l.Cells, c => Assert.Equal(c.RowSpan, c.ColumnSpan)));
        Assert.DoesNotContain(GridLayout.All, l => l.TileCount == 32);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 2)]
    [InlineData(9, 3)]
    [InlineData(16, 4)]
    [InlineData(25, 5)]
    [InlineData(36, 6)]
    public void StandardLayoutsAreUniformGridsInReadingOrder(int tileCount, int size)
    {
        var layout = GridLayout.WithTileCount(tileCount)!;

        Assert.True(layout.IsUniform);
        Assert.Equal((size, size), (layout.Columns, layout.Rows));
        Assert.Equal(
            Enumerable.Range(0, size * size).Select(i => new GridCell(i / size, i % size)),
            layout.Cells);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(13)]
    [InlineData(22)]
    [InlineData(36)]
    public void TilesCoverEveryCellExactlyOnce(int tileCount)
    {
        var layout = GridLayout.WithTileCount(tileCount)!;
        var covered = new int[layout.Rows, layout.Columns];
        foreach (var cell in layout.Cells)
        {
            for (var r = cell.Row; r < cell.Row + cell.RowSpan; r++)
            {
                for (var c = cell.Column; c < cell.Column + cell.ColumnSpan; c++)
                {
                    covered[r, c]++;
                }
            }
        }

        Assert.All(covered.Cast<int>(), n => Assert.Equal(1, n));
        Assert.Equal(tileCount, layout.TileCount);
    }

    [Fact]
    public void BigTileLayoutsPutBigTilesFirstThenReadingOrder()
    {
        Assert.Equal(
            new GridCell[] { new(0, 0, 2, 2), new(0, 2), new(1, 2), new(2, 0), new(2, 1), new(2, 2) },
            GridLayout.Six.Cells);
        Assert.Equal(
            new GridCell[] { new(0, 0, 3, 3), new(0, 3), new(1, 3), new(2, 3), new(3, 0), new(3, 1), new(3, 2), new(3, 3) },
            GridLayout.Eight.Cells);
        Assert.Equal(
            new GridCell[] { new(0, 0, 2, 2), new(0, 2, 2, 2), new(2, 0, 2, 2), new(2, 2), new(2, 3), new(3, 2), new(3, 3) },
            GridLayout.Seven.Cells);
        Assert.Equal(
            new GridCell[] { new(0, 0, 2, 2), new(0, 2, 2, 2), new(2, 0), new(2, 1), new(2, 2), new(2, 3), new(3, 0), new(3, 1), new(3, 2), new(3, 3) },
            GridLayout.Ten.Cells);
        Assert.Equal(new GridCell(1, 1, 2, 2), GridLayout.Thirteen.Cells[0]);
        Assert.Equal(
            new GridCell[] { new(0, 0), new(0, 1), new(0, 2), new(0, 3), new(1, 0), new(1, 3) },
            GridLayout.Thirteen.Cells.Skip(1).Take(6));
        Assert.Equal(new GridCell(0, 0, 2, 2), GridLayout.TwentyTwo.Cells[0]);
        Assert.Equal(new GridCell(0, 2), GridLayout.TwentyTwo.Cells[1]);
        Assert.Equal(new GridCell(4, 4), GridLayout.TwentyTwo.Cells[^1]);
    }

    [Theory]
    [InlineData(1, 6)]   // old 1×1
    [InlineData(2, 6)]   // old 2×2 (4 tiles)
    [InlineData(3, 25)]  // old 3×3 (9 tiles)
    [InlineData(4, 25)]  // old 4×4 (16 tiles)
    public void LegacyColumnsKeepTheirMapping(int columns, int expected)
    {
        Assert.Equal(expected, GridLayout.ForSavedView(new SavedView { Columns = columns }).TileCount);
    }

    [Theory]
    [InlineData(6, 6)]
    [InlineData(8, 8)]
    [InlineData(25, 25)]
    [InlineData(32, 36)] // retired 8×4: squeezed tiles
    [InlineData(1, 1)]
    [InlineData(9, 9)]
    [InlineData(13, 13)]
    [InlineData(22, 22)]
    [InlineData(36, 36)]
    [InlineData(5, 6)]   // unknown: smallest layout that holds the tiles
    [InlineData(20, 22)]
    [InlineData(40, 36)]
    public void SavedLayoutIsKeptMigratedOrFitted(int layout, int expected)
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

    [Fact]
    public void SavedThirtyTwoBecomesThirtySixAndKeepsAllCamerasInOrder()
    {
        var path = Path.Combine(Path.GetTempPath(), $"spvision-{Guid.NewGuid()}.json");
        var deviceId = Guid.NewGuid();
        try
        {
            var tiles = string.Join(", ", Enumerable.Range(1, 32).Select(i => i % 5 == 0 ? "null" : $$"""{ "DeviceId": "{{deviceId}}", "Channel": {{i}} }"""));
            File.WriteAllText(path, $$"""{ "LastView": { "Name": "Son görünüm", "Layout": 32, "Tiles": [ {{tiles}} ] } }""");

            var view = new SettingsStore(path).Load().LastView!;
            var layout = GridLayout.ForSavedView(view);

            Assert.Same(GridLayout.ThirtySix, layout);
            Assert.Equal(32, view.Tiles.Count);
            Assert.True(view.Tiles.Count <= layout.TileCount); // every saved tile still has a place
            Assert.Equal(
                Enumerable.Range(1, 32).Select(i => i % 5 == 0 ? null : new CameraRef(deviceId, i)),
                view.Tiles);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
