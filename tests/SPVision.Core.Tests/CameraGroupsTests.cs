using SPVision.Core;

namespace SPVision.Core.Tests;

/// <summary>Canlı İzleme › GRUPLAR: name rules, capturing the screen, the tiles a group shows, and the live pages of a group.</summary>
public class CameraGroupsTests
{
    static readonly Guid Nvr = Guid.Parse("11111111-1111-1111-1111-111111111111");
    static readonly Guid Other = Guid.Parse("22222222-2222-2222-2222-222222222222");

    static CameraRef Cam(int channel, Guid? device = null) => new(device ?? Nvr, channel);

    static List<CameraRef> Cameras(int count) => [.. Enumerable.Range(1, count).Select(c => Cam(c))];

    static int?[] Channels(IEnumerable<CameraRef?> slots) => [.. slots.Select(s => s?.Channel)];

    static CameraGroup Group(string name, int layout = 4, params CameraRef?[] tiles) =>
        new() { Name = name, Layout = layout, Tiles = [.. tiles] };

    // ---- Names ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void EmptyNameIsRefused(string? name) =>
        Assert.Equal("Grup adı boş olamaz.", CameraGroups.CheckName(name, []));

    [Fact]
    public void LongNameAndControlCharactersAreRefused()
    {
        Assert.Null(CameraGroups.CheckName(new string('a', CameraGroups.MaxNameLength), []));
        Assert.Equal("Grup adı en çok 40 karakter olabilir.", CameraGroups.CheckName(new string('a', CameraGroups.MaxNameLength + 1), []));
        Assert.Equal("Grup adında geçersiz karakter var.", CameraGroups.CheckName("Giriş\tKapı", []));
    }

    [Theory]
    [InlineData("Giriş", "giriş")]
    [InlineData("Giriş", "GİRİŞ")]
    [InlineData("Giriş", "GIRIŞ")]
    [InlineData("Atölye", " atölye ")]
    [InlineData("Satış", "SATIŞ")]
    public void DuplicateNamesIgnoreCase(string existing, string typed)
    {
        var groups = new[] { Group(existing) };
        Assert.Equal("Bu adda bir grup zaten var.", CameraGroups.CheckName(typed, groups));
    }

    [Theory]
    [InlineData("Tüm kameralar")]
    [InlineData("TÜM KAMERALAR")]
    public void BuiltInNameIsTaken(string typed) =>
        Assert.Equal("Bu adda bir grup zaten var.", CameraGroups.CheckName(typed, []));

    [Fact]
    public void RenamingAGroupMayKeepItsNameOrChangeItsCase()
    {
        var giris = Group("Giriş");
        var groups = new[] { giris, Group("Atölye") };
        Assert.Null(CameraGroups.CheckName("Giriş", groups, giris.Id));
        Assert.Null(CameraGroups.CheckName("GİRİŞ", groups, giris.Id));
        Assert.Equal("Bu adda bir grup zaten var.", CameraGroups.CheckName("atölye", groups, giris.Id));
        Assert.Null(CameraGroups.CheckName("Satış", groups));
    }

    [Fact]
    public void DefaultNameCountsUpWhenTaken()
    {
        Assert.Equal("Yeni grup", CameraGroups.UniqueName([]));
        Assert.Equal("Yeni grup 2", CameraGroups.UniqueName([Group("yeni grup")]));
        Assert.Equal("Yeni grup 3", CameraGroups.UniqueName([Group("Yeni grup"), Group("Yeni grup 2")]));
        Assert.Equal("Yeni grup 2", CameraGroups.UniqueName([Group("Yeni grup"), Group("Yeni grup 3")]));
    }

    // ---- Capture / overwrite ----

    [Fact]
    public void CaptureStoresTheLayoutAndEveryTileWithItsHoles()
    {
        var group = CameraGroups.Capture("  Giriş ", GridLayout.Four, [Cam(2), null, Cam(7), Cam(1)]);
        Assert.Equal("Giriş", group.Name);
        Assert.Equal(4, group.Layout);
        Assert.Equal([2, null, 7, 1], Channels(group.Tiles));
    }

    [Fact]
    public void CaptureHasOneEntryPerTileAndNoRepeats()
    {
        Assert.Equal([1, 2, null, null, null, null], Channels(CameraGroups.Capture("a", GridLayout.Six, [Cam(1), Cam(2)]).Tiles));
        Assert.Equal([1], Channels(CameraGroups.Capture("a", GridLayout.One, [Cam(1), Cam(2)]).Tiles));
        Assert.Equal([1, null, 3, null], Channels(CameraGroups.Capture("a", GridLayout.Four, [Cam(1), Cam(1), Cam(3), null]).Tiles));
    }

    [Fact]
    public void OverwriteKeepsIdAndName()
    {
        var group = Group("Giriş", 4, Cam(1), Cam(2), Cam(3), Cam(4));
        var id = group.Id;
        CameraGroups.Overwrite(group, GridLayout.Nine, [Cam(9), null, Cam(5)]);
        Assert.Equal(id, group.Id);
        Assert.Equal("Giriş", group.Name);
        Assert.Equal(9, group.Layout);
        Assert.Equal([9, null, 5, null, null, null, null, null, null], Channels(group.Tiles));
    }

    // ---- Applying ----

    [Fact]
    public void SlotsPutEveryCameraBackInItsTile()
    {
        var group = Group("Giriş", 4, Cam(4), null, Cam(2), Cam(1, Other));
        var slots = CameraGroups.Slots(group, [.. Cameras(22), Cam(1, Other)]);
        Assert.Equal([4, null, 2, 1], Channels(slots));
        Assert.Equal(Other, slots[3]!.DeviceId);
    }

    [Fact]
    public void MissingCamerasLeaveTheirTileEmptyAndNothingMoves()
    {
        // The other recorder was removed and channel 2 is hidden ("Listede göster" off).
        var group = Group("Giriş", 4, Cam(1, Other), Cam(2), Cam(3), Cam(4));
        var listed = Cameras(22).Where(c => c.Channel != 2).ToList();
        Assert.Equal([null, null, 3, 4], Channels(CameraGroups.Slots(group, listed)));
        Assert.Equal(2, CameraGroups.CameraCount(group, listed));
        Assert.Equal([null, null, null, null], Channels(CameraGroups.Slots(group, [])));
    }

    [Fact]
    public void SlotsFollowTheLayoutTileCount()
    {
        // Edited settings: more tiles than the layout has, a repeated camera, a missing list.
        var group = Group("a", 4, Cam(1), Cam(1), Cam(3), Cam(4), Cam(5));
        Assert.Equal([1, null, 3, 4], Channels(CameraGroups.Slots(group, Cameras(9))));
        Assert.Equal(new CameraRef?[4], CameraGroups.Slots(new CameraGroup { Layout = 4, Tiles = null! }, Cameras(9)));
    }

    [Theory]
    [InlineData(32, 36)]
    [InlineData(0, 25)]
    [InlineData(5, 6)]
    [InlineData(22, 22)]
    public void LayoutOfARetiredOrUnknownCountMapsLikeASavedView(int stored, int expected)
    {
        var group = Group("a", stored);
        Assert.Equal(expected, CameraGroups.LayoutOf(group).TileCount);
        Assert.Equal(expected, CameraGroups.Slots(group, Cameras(4)).Length);
    }

    [Fact]
    public void FindByIdSurvivesRenameAndDelete()
    {
        var giris = Group("Giriş");
        var groups = new List<CameraGroup> { Group("Atölye"), giris };
        Assert.Same(giris, CameraGroups.Find(groups, giris.Id));
        giris.Name = "Ön kapı";
        Assert.Same(giris, CameraGroups.Find(groups, giris.Id));
        groups.Remove(giris);
        Assert.Null(CameraGroups.Find(groups, giris.Id));
        Assert.Null(CameraGroups.Find(groups, null));
    }

    [Fact]
    public void Texts()
    {
        Assert.Equal("Ekrandaki 4 kareli ızgara ve 3 kameranın yeri kaydedilir. Grubu seçince aynen geri gelir.",
            CameraGroups.SaveSummary(GridLayout.Four, 3));
        Assert.Equal("Grup: Giriş", CameraGroups.StatusText(Group("Giriş")));
        Assert.Equal("Grup: Tüm kameralar", CameraGroups.StatusText(null));
    }

    // ---- Live pages of a group ----

    static LivePages GroupWall(CameraGroup group, IReadOnlyList<CameraRef> listed)
    {
        var pages = new LivePages(1);
        pages.Load(CameraGroups.Slots(group, listed), listed, 0, CameraGroups.LayoutOf(group).TileCount, group: true);
        return pages;
    }

    [Fact]
    public void GroupShowsOnlyItsCamerasWithItsHoles()
    {
        var pages = GroupWall(Group("Giriş", 4, null, Cam(5), null, Cam(2)), Cameras(22));
        Assert.True(pages.IsGroup);
        Assert.Equal(1, pages.PageCount);
        Assert.Equal([null, 5, null, 2], Channels(pages.CurrentPage));
    }

    [Fact]
    public void GroupWithTrailingHolesStillShowsEveryTile()
    {
        var pages = GroupWall(Group("a", 9, Cam(3)), Cameras(22));
        Assert.Equal([3], Channels(pages.Slots));
        Assert.Equal([3, null, null, null, null, null, null, null, null], Channels(pages.CurrentPage));
    }

    [Fact]
    public void RemovedCameraLeavesAHoleInAGroupButShiftsTheNormalWall()
    {
        var group = GroupWall(Group("a", 4, Cam(1), Cam(2), Cam(3), Cam(4)), Cameras(22));
        var normal = new LivePages(4);
        normal.Load([Cam(1), Cam(2), Cam(3), Cam(4)], Cameras(22), 0, 4);
        var without2 = Cameras(22).Where(c => c.Channel != 2).ToList();

        group.Reconcile(without2);
        normal.Reconcile(without2);

        Assert.Equal([1, null, 3, 4], Channels(group.CurrentPage));
        Assert.Equal([1, 3, 4, 5], Channels(normal.CurrentPage));
    }

    [Fact]
    public void NewCamerasDoNotJoinAGroup()
    {
        var pages = GroupWall(Group("a", 4, Cam(1), null), Cameras(4));
        pages.Reconcile(Cameras(30));
        Assert.Equal([1, null, null, null], Channels(pages.CurrentPage));
    }

    [Fact]
    public void ClosedTileOfAGroupStaysEmpty()
    {
        var pages = GroupWall(Group("a", 4, Cam(1), Cam(2), Cam(3)), Cameras(22));
        pages.Clear(0);
        pages.Reconcile(Cameras(22));
        Assert.Equal([null, 2, 3, null], Channels(pages.CurrentPage));
    }

    [Fact]
    public void DroppingACameraIntoAGroupTileReplacesIt()
    {
        var pages = GroupWall(Group("a", 4, Cam(1), Cam(2), null, Cam(4)), Cameras(22));
        pages.Place(0, Cam(9)); // camera 1 goes to the empty tile
        Assert.Equal([9, 2, 1, 4], Channels(pages.CurrentPage));
        pages.Swap(1, 3);
        Assert.Equal([9, 4, 1, 2], Channels(pages.CurrentPage));
    }

    [Fact]
    public void ResetAndPlainLoadLeaveTheGroup()
    {
        var pages = GroupWall(Group("a", 4, Cam(3)), Cameras(6));
        pages.Reset([Cam(5)], Cameras(6));
        Assert.False(pages.IsGroup);
        Assert.Equal([5, 1, 2, 3, 4, 6], Channels(pages.Slots));

        pages = GroupWall(Group("a", 4, Cam(3)), Cameras(6));
        pages.Load([], Cameras(6), 0, 4);
        Assert.False(pages.IsGroup);
        Assert.Equal([1, 2, 3, 4, 5, 6], Channels(pages.Slots));
    }

    [Fact]
    public void ChangingTheLayoutOfAGroupKeepsItsCameras()
    {
        var pages = GroupWall(Group("a", 4, Cam(1), Cam(2), Cam(3), Cam(4)), Cameras(22));
        pages.SetPageSize(1);
        Assert.Equal(4, pages.PageCount);
        pages.SetPageSize(9);
        Assert.Equal([1, 2, 3, 4, null, null, null, null, null], Channels(pages.CurrentPage));
        Assert.True(pages.IsGroup);
    }

    // ---- Settings ----

    [Fact]
    public void GroupsAndTheActiveOneAreSaved()
    {
        var path = Path.Combine(Path.GetTempPath(), $"spvision-groups-{Guid.NewGuid():N}.json");
        try
        {
            var giris = Group("Giriş", 6, Cam(2), null, Cam(1, Other));
            new SettingsStore(path).Save(new AppSettings { Groups = [giris, Group("Atölye", 9)], ActiveGroup = giris.Id });
            var loaded = new SettingsStore(path).Load();
            Assert.Equal(["Giriş", "Atölye"], loaded.Groups.Select(g => g.Name));
            Assert.Equal(giris.Id, loaded.ActiveGroup);
            var group = CameraGroups.Find(loaded.Groups, loaded.ActiveGroup)!;
            Assert.Equal(6, group.Layout);
            Assert.Equal([Cam(2), null, Cam(1, Other)], group.Tiles);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OldSettingsHaveNoGroups()
    {
        var path = Path.Combine(Path.GetTempPath(), $"spvision-groups-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{ "Devices": [], "RestoreLastView": true }""");
            var loaded = new SettingsStore(path).Load();
            Assert.Empty(loaded.Groups);
            Assert.Null(loaded.ActiveGroup);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
