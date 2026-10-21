namespace SPVision.Core;

/// <summary>
/// Kayıt Oynatma state kept across restarts (<see cref="AppSettings.LastPlaybackView"/>): the layout and the checked
/// cameras in tile order. The day and the time are not kept; a restored view waits for the user (pick a day or play).
/// </summary>
public sealed record PlaybackRestore(GridLayout Layout, IReadOnlyList<CameraRef> Cameras)
{
    /// <summary>
    /// What to show on startup. "Açılışta son görünümü geri yükle" off or nothing saved: the default layout, no
    /// cameras. A saved 7 opens as 8 (<see cref="GridLayout.ForPlaybackView"/>). Cameras that no longer exist or are
    /// hidden ("Listede göster" off) are dropped, duplicates too; at most as many as the layout has tiles.
    /// </summary>
    public static PlaybackRestore From(AppSettings settings)
    {
        var view = settings.RestoreLastView ? settings.LastPlaybackView : null;
        var layout = GridLayout.ForPlaybackView(view);
        var listed = settings.Devices
            .SelectMany(d => d.ListedCameras().Select(c => new CameraRef(d.Id, c.Channel)))
            .ToHashSet();
        var cameras = (view?.Tiles ?? [])
            .OfType<CameraRef>()
            .Where(listed.Contains)
            .Distinct()
            .Take(layout.TileCount)
            .ToList();
        return new PlaybackRestore(layout, cameras);
    }

    /// <summary>The view to save: layout plus the checked cameras in tile order (empty tiles left out).</summary>
    public static SavedView Save(GridLayout layout, IEnumerable<CameraRef?> slots) => new()
    {
        Name = "Son oynatma",
        Layout = layout.TileCount,
        Tiles = [.. slots.OfType<CameraRef>()],
    };
}
