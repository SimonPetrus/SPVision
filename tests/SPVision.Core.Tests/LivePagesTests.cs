using SPVision.Core;

namespace SPVision.Core.Tests;

public class LivePagesTests
{
    static readonly Guid Nvr = Guid.Parse("11111111-1111-1111-1111-111111111111");
    static readonly Guid Other = Guid.Parse("22222222-2222-2222-2222-222222222222");

    static CameraRef Cam(int channel, Guid? device = null) => new(device ?? Nvr, channel);

    /// <summary>Channels 1..count of the recorder, in list order.</summary>
    static List<CameraRef> Cameras(int count) => [.. Enumerable.Range(1, count).Select(c => Cam(c))];

    static LivePages Wall(int cameras, int pageSize, int page = 0)
    {
        var pages = new LivePages(pageSize);
        pages.Load([], Cameras(cameras), page, pageSize);
        return pages;
    }

    static int?[] Channels(IEnumerable<CameraRef?> slots) => [.. slots.Select(s => s?.Channel)];

    [Theory]
    [InlineData(9, 3)]
    [InlineData(16, 2)]
    [InlineData(25, 1)]
    [InlineData(36, 1)]
    [InlineData(4, 6)]
    [InlineData(1, 22)]
    [InlineData(22, 1)]
    [InlineData(13, 2)]
    [InlineData(6, 4)]
    public void TwentyTwoCamerasSplitIntoPagesOfTheLayout(int tiles, int expected)
    {
        Assert.Equal(expected, LivePages.CountPages(22, tiles));
        Assert.Equal(expected, Wall(22, tiles).PageCount);
    }

    [Fact]
    public void EmptyWallIsOnePage()
    {
        Assert.Equal(1, LivePages.CountPages(0, 9));
        Assert.Equal(1, Wall(0, 9).PageCount);
        Assert.All(Wall(0, 9).CurrentPage, Assert.Null);
    }

    [Fact]
    public void PagesShowConsecutiveSlotsAndTheLastIsPadded()
    {
        var wall = Wall(22, 9);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9], Channels(wall.CurrentPage));
        wall.GoTo(1);
        Assert.Equal([10, 11, 12, 13, 14, 15, 16, 17, 18], Channels(wall.CurrentPage));
        wall.GoTo(2);
        Assert.Equal([19, 20, 21, 22, null, null, null, null, null], Channels(wall.CurrentPage));
    }

    [Fact]
    public void SliceAlwaysHasPageSizeEntries()
    {
        var slots = new List<CameraRef?> { Cam(1), null, Cam(3) };
        Assert.Equal([1, null, 3, null], Channels(LivePages.Slice(slots, 0, 4)));
        Assert.Equal([null, null, null, null], Channels(LivePages.Slice(slots, 1, 4)));
    }

    [Theory]
    [InlineData(0, 1, 3, 1)]
    [InlineData(2, 1, 3, 0)] // last → first
    [InlineData(0, -1, 3, 2)] // first → last
    [InlineData(1, -1, 3, 0)]
    [InlineData(0, 1, 1, 0)]
    [InlineData(0, -1, 1, 0)]
    public void ArrowsWrapAround(int page, int delta, int count, int expected) =>
        Assert.Equal(expected, LivePages.Wrap(page, delta, count));

    [Fact]
    public void StepWrapsAndReportsChange()
    {
        var wall = Wall(22, 9);
        Assert.True(wall.Step(-1));
        Assert.Equal(2, wall.Page);
        Assert.True(wall.Step(1));
        Assert.Equal(0, wall.Page);
        Assert.False(Wall(22, 25).Step(1)); // one page: nothing to do
    }

    [Theory]
    [InlineData(-1, 3, 0)]
    [InlineData(5, 3, 2)]
    [InlineData(1, 3, 1)]
    [InlineData(4, 0, 0)]
    public void ClampKeepsThePageInRange(int page, int count, int expected) =>
        Assert.Equal(expected, LivePages.Clamp(page, count));

    [Theory]
    [InlineData(0, 9, 0)]
    [InlineData(8, 9, 0)]
    [InlineData(9, 9, 1)]
    [InlineData(21, 9, 2)]
    [InlineData(21, 16, 1)]
    public void PageContainingSlot(int slot, int size, int expected) =>
        Assert.Equal(expected, LivePages.PageContaining(slot, size));

    [Fact]
    public void LoadClampsTheSavedPage()
    {
        Assert.Equal(2, Wall(22, 9, page: 7).Page);
        Assert.Equal(0, Wall(22, 25, page: 2).Page);
        Assert.Equal(0, Wall(22, 9, page: -3).Page);
    }

    [Fact]
    public void LayoutChangeKeepsTheFirstCameraOfThePageVisible()
    {
        var wall = Wall(22, 9, page: 1); // shows 10..18
        wall.SetPageSize(16);
        Assert.Equal(0, wall.Page); // slot 9 (channel 10) is on page 1 of 16
        Assert.Contains(10, Channels(wall.CurrentPage));

        wall = Wall(22, 9, page: 2); // 19..22
        wall.SetPageSize(16);
        Assert.Equal(1, wall.Page);
        Assert.Equal(19, wall.CurrentPage[2]!.Channel);

        wall = Wall(22, 16, page: 1); // 17..22
        wall.SetPageSize(4);
        Assert.Equal(4, wall.Page); // slot 16 → page 4 of 4-tile pages
        Assert.Equal(17, wall.CurrentPage[0]!.Channel);

        wall = Wall(22, 9, page: 2);
        wall.SetPageSize(25);
        Assert.Equal(0, wall.Page);
    }

    [Fact]
    public void LayoutChangeSkipsLeadingHolesWhenLookingForTheFirstCamera()
    {
        var pages = new LivePages(4);
        pages.Load([Cam(1), Cam(2), Cam(3), Cam(4), null, null, Cam(7), Cam(8)], Cameras(4).Concat([Cam(7), Cam(8)]).ToList(), 1, 4);
        pages.SetPageSize(6); // anchor = slot 6 (channel 7) → page 1 of 6
        Assert.Equal(1, pages.Page);
        Assert.Equal(7, pages.CurrentPage[0]!.Channel);
    }

    [Fact]
    public void ReconcileKeepsSavedOrderAndAppendsNewCameras()
    {
        var pages = new LivePages(9);
        // A view saved before paging: 9 tiles of a 3×3 layout, in the user's own order.
        pages.Load([Cam(5), Cam(1), Cam(9), Cam(2), Cam(3), Cam(4), Cam(6), Cam(7), Cam(8)], Cameras(22), 0, 9);
        Assert.Equal(22, pages.Slots.Count);
        Assert.Equal([5, 1, 9, 2, 3, 4, 6, 7, 8], Channels(pages.Slots.Take(9)));
        Assert.Equal(Enumerable.Range(10, 13).Select(c => (int?)c), Channels(pages.Slots.Skip(9)));
        Assert.Equal(3, pages.PageCount);
    }

    [Fact]
    public void ReconcileFillsHolesFirstThenAppends()
    {
        var pages = new LivePages(4);
        pages.Load([Cam(1), null, Cam(3), null], Cameras(6), 0, 4);
        Assert.Equal([1, 2, 3, 4, 5, 6], Channels(pages.Slots));
    }

    [Fact]
    public void ReconcileDropsHiddenOrRemovedCamerasAndDuplicates()
    {
        var pages = new LivePages(4);
        var listed = new List<CameraRef> { Cam(1), Cam(3), Cam(4) }; // channel 2 hidden, other device removed
        pages.Load([Cam(1), Cam(2), Cam(1, Other), Cam(3), Cam(1), Cam(4)], listed, 0, 4);
        // Unlisted cameras leave the order (the others move up); a second copy leaves an empty tile.
        Assert.Equal([1, 3, null, 4], Channels(pages.Slots));
    }

    [Fact]
    public void ReconcileNeverEndsWithHoles()
    {
        var pages = new LivePages(4);
        pages.Load([Cam(1), null, null, null, null, null, Cam(9)], [Cam(1)], 1, 4);
        Assert.Equal([1], Channels(pages.Slots));
        Assert.Equal(1, pages.PageCount);
        Assert.Equal(0, pages.Page); // clamped
    }

    [Fact]
    public void ResetPutsTheDeviceFirstAndKeepsTheOthers()
    {
        var pages = Wall(3, 4);
        pages.GoTo(0);
        var listed = new List<CameraRef> { Cam(1), Cam(2), Cam(3), Cam(1, Other), Cam(2, Other) };
        pages.Reconcile(listed);
        pages.Reset([Cam(1, Other), Cam(2, Other)], listed);
        Assert.Equal(0, pages.Page);
        Assert.Equal([Cam(1, Other), Cam(2, Other), Cam(1), Cam(2), Cam(3)], pages.Slots);
    }

    [Fact]
    public void DroppingACameraFromAnotherPageTradesPlaces()
    {
        var wall = Wall(22, 9);
        wall.Place(2, Cam(15)); // channel 15 lives on page 2, slot 14
        Assert.Equal(15, wall.Slots[2]!.Channel);
        Assert.Equal(3, wall.Slots[14]!.Channel);
        Assert.Equal(22, wall.Slots.Count);
        Assert.Equal(22, wall.Slots.Distinct().Count());
    }

    [Fact]
    public void DropPersistsAcrossPagesAndLayouts()
    {
        var wall = Wall(22, 9);
        wall.GoTo(1);
        wall.Place(0, Cam(1)); // slot 9 ← channel 1, channel 10 → slot 0
        Assert.Equal(1, wall.CurrentPage[0]!.Channel);
        wall.GoTo(0);
        Assert.Equal(10, wall.CurrentPage[0]!.Channel);
        wall.SetPageSize(16);
        Assert.Equal([10, 2, 3, 4, 5, 6, 7, 8, 9, 1], Channels(wall.CurrentPage.Take(10)));
    }

    [Fact]
    public void DroppingACameraWithoutSlotMovesTheDisplacedOneToAHole()
    {
        var wall = Wall(6, 4);
        wall.Clear(1); // channel 2 closed → hole at slot 1
        wall.Place(3, Cam(2)); // back onto tile 4: channel 4 takes the hole
        Assert.Equal([1, 4, 3, 2, 5, 6], Channels(wall.Slots));
    }

    [Fact]
    public void DroppingACameraWithoutSlotOnAFullWallAppendsTheDisplacedOne()
    {
        var pages = new LivePages(4);
        pages.Load([Cam(1), Cam(2)], [Cam(1), Cam(2)], 0, 4);
        pages.Reconcile([Cam(1), Cam(2), Cam(3)]); // now 1, 2, 3
        pages.Clear(2);
        Assert.Equal([1, 2], Channels(pages.Slots));
        pages.Place(0, Cam(3));
        Assert.Equal([3, 2, 1], Channels(pages.Slots));
    }

    [Fact]
    public void DroppingOntoAnEmptyTileOfTheLastPage()
    {
        var wall = Wall(22, 9);
        wall.GoTo(2);
        wall.Place(6, Cam(1)); // empty tile 7 of page 3 = slot 24
        Assert.Null(wall.Slots[0]);
        Assert.Equal(1, wall.Slots[24]!.Channel);
        Assert.Equal(25, wall.Slots.Count);
        Assert.Equal(3, wall.PageCount);
    }

    [Fact]
    public void SwapTradesTwoTilesOfThePage()
    {
        var wall = Wall(22, 9);
        wall.GoTo(1);
        wall.Swap(0, 8);
        Assert.Equal(18, wall.Slots[9]!.Channel);
        Assert.Equal(10, wall.Slots[17]!.Channel);
    }

    [Fact]
    public void SwapOntoAnEmptyTileMovesTheCamera()
    {
        var wall = Wall(22, 9);
        wall.GoTo(2);
        wall.Swap(3, 8); // channel 22 → last tile
        Assert.Equal([19, 20, 21, null, null, null, null, null, 22], Channels(wall.CurrentPage));
    }

    [Fact]
    public void SwappingTheLastCameraBackShrinksTheList()
    {
        var wall = Wall(5, 4);
        wall.GoTo(1);
        wall.Swap(0, 3); // channel 5 → slot 7
        Assert.Equal(8, wall.Slots.Count);
        wall.Swap(3, 0);
        Assert.Equal(5, wall.Slots.Count);
    }

    [Fact]
    public void ClosingTheLastCamerasRemovesTheirPage()
    {
        var wall = Wall(10, 9);
        wall.GoTo(1);
        wall.Clear(0); // channel 10, the only camera of page 2
        Assert.Equal(1, wall.PageCount);
        Assert.Equal(0, wall.Page);
    }

    [Fact]
    public void ClosedCameraComesBackIntoItsHoleOnReconcile()
    {
        var wall = Wall(22, 9);
        wall.Clear(4);
        Assert.Null(wall.Slots[4]);
        wall.Reconcile(Cameras(22));
        Assert.Equal(5, wall.Slots[4]!.Channel);
    }

    [Fact]
    public void AssignTilesKeepsTilesThatAlreadyShowAWantedCamera()
    {
        // 9 → 16 tiles from page 2 (channels 10..18): channels 10..16 stay where they are streaming.
        var current = new CameraRef?[36];
        for (var i = 0; i < 9; i++)
        {
            current[i] = Cam(10 + i);
        }
        var wanted = LivePages.Slice(Cameras(22).Cast<CameraRef?>().ToList(), 0, 16);
        var order = LivePages.AssignTiles(current, wanted);

        Assert.Equal(36, order.Distinct().Count()); // a permutation of all tiles
        for (var i = 9; i < 16; i++)
        {
            Assert.Equal(wanted[i], current[order[i]]); // channel 10..16: no reconnect
        }
        var reconnects = Enumerable.Range(0, 16).Count(i => current[order[i]] != wanted[i]);
        Assert.Equal(9, reconnects); // only channels 1..9 open
    }

    [Fact]
    public void AssignTilesOnAPageChangeKeepsEveryTileInPlace()
    {
        var current = new CameraRef?[36];
        for (var i = 0; i < 9; i++)
        {
            current[i] = Cam(1 + i);
        }
        var wanted = LivePages.Slice(Cameras(22).Cast<CameraRef?>().ToList(), 1, 9);
        Assert.Equal(Enumerable.Range(0, 36), LivePages.AssignTiles(current, wanted));
    }

    [Fact]
    public void AssignTilesSwapMovesBothTiles()
    {
        var current = new CameraRef?[] { Cam(1), Cam(2), Cam(3), Cam(4), null };
        var wanted = new CameraRef?[] { Cam(4), Cam(2), Cam(3), Cam(1) };
        Assert.Equal([3, 1, 2, 0, 4], LivePages.AssignTiles(current, wanted));
    }

    [Fact]
    public void AssignTilesUnchangedPageIsTheIdentity()
    {
        var current = new CameraRef?[] { Cam(1), null, Cam(3), Cam(9) };
        var wanted = new CameraRef?[] { Cam(1), null, Cam(3) };
        Assert.Equal([0, 1, 2, 3], LivePages.AssignTiles(current, wanted));
    }

    [Fact]
    public void SavedViewKeepsThePage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"spvision-paging-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            store.Save(new AppSettings
            {
                LastView = new SavedView { Layout = 9, Page = 2, Tiles = [Cam(3), null, Cam(1)] },
            });
            var view = store.Load().LastView!;
            Assert.Equal(2, view.Page);
            Assert.Equal([3, null, 1], Channels(view.Tiles));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
