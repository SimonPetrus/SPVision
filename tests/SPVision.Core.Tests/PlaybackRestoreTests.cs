using SPVision.Core;

namespace SPVision.Core.Tests;

public class PlaybackRestoreTests
{
    static readonly Guid DeviceId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    static CameraRef Cam(int channel) => new(DeviceId, channel);

    /// <summary>One recorder with cameras 1–20; <paramref name="hidden"/> are switched off under "Listede göster".</summary>
    static AppSettings Settings(SavedView? playback, bool restore = true, params int[] hidden) => new()
    {
        RestoreLastView = restore,
        LastPlaybackView = playback,
        Devices =
        [
            new Device
            {
                Id = DeviceId,
                Cameras = [.. Enumerable.Range(1, 20).Select(c => new Camera { Channel = c, Hidden = hidden.Contains(c) })],
            },
        ],
    };

    [Fact]
    public void RestoresLayoutAndCamerasInOrder()
    {
        var restore = PlaybackRestore.From(Settings(new SavedView { Layout = 9, Tiles = [Cam(5), Cam(2), Cam(7)] }));
        Assert.Same(GridLayout.Nine, restore.Layout);
        Assert.Equal([Cam(5), Cam(2), Cam(7)], restore.Cameras);
    }

    [Fact]
    public void SavedSevenOpensAsEight()
    {
        var restore = PlaybackRestore.From(Settings(new SavedView { Layout = 7, Tiles = [Cam(1), Cam(2), Cam(3), Cam(4), Cam(5), Cam(6), Cam(7)] }));
        Assert.Same(GridLayout.Eight, restore.Layout);
        Assert.Equal(7, restore.Cameras.Count);
    }

    [Fact]
    public void DropsMissingHiddenAndDuplicateCameras()
    {
        var other = new CameraRef(Guid.NewGuid(), 1); // device removed
        var restore = PlaybackRestore.From(Settings(
            new SavedView { Layout = 4, Tiles = [Cam(3), other, Cam(21), null, Cam(4), Cam(3), Cam(6)] },
            hidden: 4));
        Assert.Equal([Cam(3), Cam(6)], restore.Cameras);
    }

    [Fact]
    public void ClampsToTheLayoutsTileCount()
    {
        var restore = PlaybackRestore.From(Settings(new SavedView { Layout = 4, Tiles = [.. Enumerable.Range(1, 9).Select(c => (CameraRef?)Cam(c))] }));
        Assert.Equal([Cam(1), Cam(2), Cam(3), Cam(4)], restore.Cameras);
    }

    [Fact]
    public void RestoreOffOrNothingSavedGivesTheDefault()
    {
        foreach (var settings in new[]
        {
            Settings(new SavedView { Layout = 9, Tiles = [Cam(1)] }, restore: false),
            Settings(null),
        })
        {
            var restore = PlaybackRestore.From(settings);
            Assert.Same(GridLayout.PlaybackDefault, restore.Layout);
            Assert.Empty(restore.Cameras);
        }
    }

    [Fact]
    public void SaveKeepsTileOrderWithoutGaps()
    {
        var view = PlaybackRestore.Save(GridLayout.Ten, [null, Cam(8), null, Cam(2)]);
        Assert.Equal(10, view.Layout);
        Assert.Equal([Cam(8), Cam(2)], view.Tiles);
    }

    [Fact]
    public void SavedPlaybackSurvivesTheSettingsFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"spvision-playback-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            var settings = Settings(PlaybackRestore.Save(GridLayout.Thirteen, [Cam(9), null, Cam(1)]));
            store.Save(settings);
            var restore = PlaybackRestore.From(store.Load());
            Assert.Same(GridLayout.Thirteen, restore.Layout);
            Assert.Equal([Cam(9), Cam(1)], restore.Cameras);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OldSettingsWithoutPlaybackViewLoad()
    {
        var path = Path.Combine(Path.GetTempPath(), $"spvision-playback-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{ "LastView": { "Name": "Son görünüm", "Layout": 25 } }""");
            var loaded = new SettingsStore(path).Load();
            Assert.Null(loaded.LastPlaybackView);
            Assert.Same(GridLayout.PlaybackDefault, PlaybackRestore.From(loaded).Layout);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
