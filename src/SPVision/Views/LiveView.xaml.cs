using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SPVision.Controls;
using SPVision.Core;

namespace SPVision.Views;

public partial class LiveView : UserControl
{
    /// <summary>All tiles in position order: the first <c>_layout.TileCount</c> are on screen, the rest are collapsed and empty.</summary>
    readonly List<VideoTile> _tiles = [];
    readonly DispatcherTimer _qualityTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    GridLayout _layout = GridLayout.Default;

    /// <summary>Global camera order over all pages; the tiles show <see cref="LivePages.CurrentPage"/>.</summary>
    readonly LivePages _pages = new(GridLayout.Default.TileCount);
    VideoTile? _selected;
    VideoTile? _maximized;
    bool _batchUpdate; // tiles are being set from the page: no single-tile handling, save once at the end
    bool _stateChangePending;

    public LiveView()
    {
        InitializeComponent();
        for (var i = 0; i < GridLayout.MaxTiles; i++)
        {
            var tile = new VideoTile { Visibility = Visibility.Collapsed };
            tile.Activated += Select;
            tile.DoubleClicked += ToggleMaximize;
            tile.CameraDropped += DropCamera;
            tile.TileDropped += SwapTiles;
            tile.Changed += t =>
            {
                if (!_batchUpdate)
                {
                    TileClosed(t);
                }
                RaiseStateChanged();
            };
            tile.StateChanged += _ => RaiseStateChanged();
            tile.Notice += (_, text) => ShowToast(text);
            tile.StopAllRequested += StopAll;
            tile.OpenFoldersRequested += OpenFolders;
            _tiles.Add(tile);
            // Always children: layout changes, paging and maximize only move/hide them. Removing a tile from the tree
            // unloads it, which releases its stream and video window — restoring it then had to reopen the
            // stream and wait for the next keyframe (black tiles after un-maximizing).
            TileGrid.Children.Add(tile);
        }
        _qualityTimer.Tick += (_, _) =>
        {
            _qualityTimer.Stop();
            UpdateAutoQuality();
        };
        Loaded += (_, _) => RestoreLayout();
    }

    /// <summary>Short feedback for the user ("Kaydedildi: …"); the main window shows it in the status bar.</summary>
    public event Action<string>? Message;

    /// <summary>
    /// Layout, page, quality, selection or any tile state changed. Coalesced: raised once per dispatcher
    /// pass, so the top bar, camera list and status bar refresh cheaply.
    /// </summary>
    public event Action? StateChanged;

    /// <summary>Current layout (any of <see cref="GridLayout.All"/>).</summary>
    public GridLayout Layout => _layout;

    /// <summary>Zero-based page shown.</summary>
    public int Page => _pages.Page;

    /// <summary>Pages of the current layout (at least 1).</summary>
    public int PageCount => _pages.PageCount;

    /// <summary>null = automatic per tile size, otherwise forced for every tile.</summary>
    public StreamQuality? GlobalQuality => App.Settings.LiveQuality;

    public bool SelectedIsRecording => _selected?.IsRecording == true;

    public Device? SelectedDevice => _selected?.Device;

    /// <summary>Tiles of the current page (empty ones included).</summary>
    public IReadOnlyList<VideoTile> VisibleTiles => _tiles.Take(_layout.TileCount).ToList();

    /// <summary>1-based tile number showing the camera on the current page, or null when it is not on screen.</summary>
    public int? TileNumberOf(CameraRef source)
    {
        for (var i = 0; i < _layout.TileCount; i++)
        {
            if (_tiles[i].Source == source)
            {
                return i + 1;
            }
        }
        return null;
    }

    /// <summary>Tile showing the camera on the current page, if any.</summary>
    public VideoTile? TileOf(CameraRef source) => _tiles.Take(_layout.TileCount).FirstOrDefault(t => t.Source == source);

    /// <summary>
    /// Sidebar double-click. A camera that is on a page already is shown there (its page opens, its tile is
    /// selected); a camera without a slot (its tile was closed) goes into the selected tile if that is empty,
    /// else the first empty tile, else the selected one.
    /// </summary>
    public void ShowCamera(CameraRef source)
    {
        var slot = _pages.IndexOf(source);
        if (slot >= 0)
        {
            var page = LivePages.PageContaining(slot, _layout.TileCount);
            var position = slot - page * _layout.TileCount;
            if (page != _pages.Page || (_maximized is not null && _tiles.IndexOf(_maximized) != position))
            {
                RestoreMaximized();
                _pages.GoTo(page);
                Sync();
            }
            Select(_tiles[position]);
            return;
        }
        var target = _selected is not null && _selected.Camera is null && _tiles.IndexOf(_selected) < _layout.TileCount
            ? _selected
            : _tiles.Take(_layout.TileCount).FirstOrDefault(t => t.Camera is null) ?? _selected ?? _tiles[0];
        DropCamera(target, source);
    }

    /// <summary>Device row double-click: its cameras first, in channel order, from the first page (the layout stays).</summary>
    public void ShowDevice(Device device)
    {
        // Cameras hidden in Settings are left out, like in the sidebar list.
        var cameras = device.ListedCameras().Where(c => c.Online).Select(c => new CameraRef(device.Id, c.Channel)).ToList();
        if (cameras.Count == 0)
        {
            return;
        }
        RestoreMaximized();
        _pages.Reset(cameras, ListedCameras());
        Sync();
        Select(_tiles[0]);
    }

    public void StopAll()
    {
        foreach (var tile in _tiles)
        {
            tile.Stop();
        }
    }

    /// <summary>Previous/next page (status bar arrows, PageUp/PageDown), wrapping around. A maximized tile is restored first.</summary>
    public void StepPage(int delta)
    {
        if (_pages.PageCount <= 1)
        {
            return;
        }
        RestoreMaximized();
        _pages.Step(delta);
        Sync();
    }

    /// <summary>Re-resolves devices after edits (password/key changes, camera list) and reconnects affected tiles.</summary>
    public void ReloadDevice(Device device)
    {
        Batch(() =>
        {
            foreach (var tile in _tiles.Take(_layout.TileCount).Where(t => t.Device?.Id == device.Id))
            {
                // Cameras that are gone or hidden now are handled by the order update below.
                if (device.Cameras.FirstOrDefault(c => c.Channel == tile.Camera!.Channel) is { Hidden: false } camera)
                {
                    tile.Show(device, camera);
                }
            }
        });
        UpdateCameraOrder();
    }

    /// <summary>Camera list of a device was refreshed in place: rename the tiles that keep their camera, update the order.</summary>
    public void RefreshDeviceCameras(Device device)
    {
        Batch(() =>
        {
            foreach (var tile in _tiles.Take(_layout.TileCount).Where(t => t.Device?.Id == device.Id))
            {
                if (device.Cameras.FirstOrDefault(c => c.Channel == tile.Camera!.Channel) is not { Hidden: false } camera)
                {
                    continue;
                }
                if (!ReferenceEquals(camera, tile.Camera) || !ReferenceEquals(device, tile.Device))
                {
                    tile.Show(device, camera);
                }
                else
                {
                    tile.RefreshCameraInfo();
                }
            }
        });
        UpdateCameraOrder();
    }

    /// <summary>Shows or hides the recorder's motion boxes on every SDK tile.</summary>
    public void ApplyMotionBoxes(bool show)
    {
        foreach (var tile in _tiles)
        {
            tile.ApplyMotionBoxes(show);
        }
    }

    /// <summary>The device is already gone from the settings: its cameras leave the order.</summary>
    public void RemoveDevice(Guid deviceId)
    {
        Debug.Assert(App.Settings.Devices.All(d => d.Id != deviceId));
        UpdateCameraOrder(); // its tiles show other cameras or become empty, releasing their streams
    }

    /// <summary>Top bar: layout picker.</summary>
    public void SelectLayout(GridLayout layout) => SetLayout(layout);

    /// <summary>Top bar: Oto/HD/SD segmented group.</summary>
    public void SelectQuality(StreamQuality? quality) => SetGlobalQuality(quality);

    /// <summary>Every camera of the sidebar list (not hidden in Settings), in list order: what the pages are made of.</summary>
    static List<CameraRef> ListedCameras() =>
        [.. App.Settings.Devices.SelectMany(d => d.ListedCameras().Select(c => new CameraRef(d.Id, c.Channel)))];

    /// <summary>Cameras were added, removed or hidden: the order follows the list, the tiles follow the order.</summary>
    void UpdateCameraOrder()
    {
        _pages.Reconcile(ListedCameras());
        Sync();
    }

    /// <summary>A camera from the sidebar was dropped onto a tile (or put there by a double-click).</summary>
    void DropCamera(VideoTile tile, CameraRef source)
    {
        var position = _tiles.IndexOf(tile);
        if (position < 0 || position >= _layout.TileCount || Resolve(source) is null)
        {
            return;
        }
        _pages.Place(position, source);
        Sync();
        Select(_tiles[position]);
    }

    /// <summary>Dragging a tile onto another swaps their cameras; onto an empty tile it moves the camera.</summary>
    void SwapTiles(VideoTile from, VideoTile to)
    {
        var a = _tiles.IndexOf(from);
        var b = _tiles.IndexOf(to);
        if (a == b || from.Source is null || a >= _layout.TileCount || b >= _layout.TileCount)
        {
            return;
        }
        _pages.Swap(a, b);
        Sync(); // both tiles only move: no reconnect
        Select(_tiles[b]);
    }

    /// <summary>"Kareyi kapat" in a tile's menu: its slot becomes empty.</summary>
    void TileClosed(VideoTile tile)
    {
        var position = _tiles.IndexOf(tile);
        if (position < 0 || position >= _layout.TileCount || tile.Source is not null)
        {
            return;
        }
        _pages.Clear(position);
        Sync(); // the last page may be gone now
    }

    static (Device Device, Camera Camera)? Resolve(CameraRef source)
    {
        var device = App.Settings.Devices.FirstOrDefault(d => d.Id == source.DeviceId);
        var camera = device?.Cameras.FirstOrDefault(c => c.Channel == source.Channel);
        return device is null || camera is null ? null : (device, camera);
    }

    /// <summary>
    /// Brings the tiles in line with the current page. A tile that already shows a camera of the page moves to
    /// that camera's position (no reconnect); only tiles whose camera changes open a new stream, and tiles of
    /// cameras that left the screen release theirs. Saves the view once at the end.
    /// </summary>
    void Sync()
    {
        var wanted = _pages.CurrentPage;
        var maximizedAt = _maximized is null ? -1 : _tiles.IndexOf(_maximized);
        var order = LivePages.AssignTiles(_tiles.Select(t => t.Source).ToList(), wanted);
        var reordered = order.Select(i => _tiles[i]).ToList();
        _tiles.Clear();
        _tiles.AddRange(reordered);
        Batch(() =>
        {
            for (var i = 0; i < _tiles.Count; i++)
            {
                var tile = _tiles[i];
                var camera = i < wanted.Count ? wanted[i] : null;
                if (tile.Source == camera)
                {
                    continue;
                }
                if (camera is not null && Resolve(camera) is { } found)
                {
                    tile.Show(found.Device, found.Camera);
                }
                else if (tile.Camera is not null)
                {
                    tile.Clear(); // also tiles beyond the layout: they release their streams (Flyleaf and SDK alike)
                }
            }
        });
        if (maximizedAt >= 0)
        {
            _maximized = _tiles[maximizedAt];
            ShowMaximized();
        }
        else
        {
            ArrangeTiles();
        }
        if (_selected is null || _tiles.IndexOf(_selected) >= _layout.TileCount)
        {
            Select(_tiles[0]);
        }
        _qualityTimer.Start();
        RaiseStateChanged();
    }

    /// <summary>Runs a change that touches many tiles and saves the layout once afterwards.</summary>
    void Batch(Action change)
    {
        _batchUpdate = true;
        try
        {
            change();
        }
        finally
        {
            _batchUpdate = false;
        }
        SaveLayout();
    }

    void Select(VideoTile tile)
    {
        if (_selected is not null)
        {
            _selected.IsSelected = false;
        }
        _selected = tile;
        tile.IsSelected = true;
        RaiseStateChanged();
    }

    void ToggleMaximize(VideoTile tile)
    {
        if (_maximized is null)
        {
            _maximized = tile;
            ShowMaximized();
        }
        else
        {
            _maximized = null;
            ArrangeTiles();
        }
        _qualityTimer.Start();
    }

    /// <summary>
    /// The other tiles stay in the grid, only hidden: their streams and video windows (SDK HWND, Flyleaf
    /// surface) keep running, so restoring shows their live picture at once — no reopen, no keyframe wait.
    /// A hidden tile also hides its video window, so nothing covers the maximized one (airspace).
    /// </summary>
    void ShowMaximized()
    {
        for (var i = 0; i < _tiles.Count; i++)
        {
            var other = _tiles[i];
            other.Visibility = other == _maximized ? Visibility.Visible
                : i < _layout.TileCount ? Visibility.Hidden
                : Visibility.Collapsed; // Sync may have moved a tile out of the layout
        }
        _maximized!.IsCompact = false;
        Place(_maximized, new GridCell(0, 0, _layout.Rows, _layout.Columns));
    }

    /// <summary>Back to the grid before the page changes.</summary>
    void RestoreMaximized()
    {
        if (_maximized is not null)
        {
            ToggleMaximize(_maximized);
        }
    }

    void SetLayout(GridLayout layout)
    {
        _layout = layout;
        _maximized = null;
        // The first camera of the page stays on screen: the new page is the one that contains it.
        _pages.SetPageSize(layout.TileCount);
        Sync();
    }

    void ArrangeTiles()
    {
        TileGrid.ColumnDefinitions.Clear();
        TileGrid.RowDefinitions.Clear();
        for (var i = 0; i < _layout.Columns; i++)
        {
            TileGrid.ColumnDefinitions.Add(new ColumnDefinition());
        }
        for (var i = 0; i < _layout.Rows; i++)
        {
            TileGrid.RowDefinitions.Add(new RowDefinition());
        }
        // From 16 tiles on they are small: slimmer 22 px header strip; fewer tiles use 26 px. One height per layout,
        // so the headers of neighbouring tiles always line up.
        var compact = _layout.TileCount >= 16;
        for (var i = 0; i < _tiles.Count; i++)
        {
            var tile = _tiles[i];
            if (i < _layout.TileCount)
            {
                tile.IsCompact = compact;
                Place(tile, _layout.Cells[i]);
                tile.Visibility = Visibility.Visible;
            }
            else
            {
                tile.Visibility = Visibility.Collapsed; // already cleared by Sync
            }
        }
    }

    static void Place(VideoTile tile, GridCell cell)
    {
        Grid.SetRow(tile, cell.Row);
        Grid.SetColumn(tile, cell.Column);
        Grid.SetRowSpan(tile, cell.RowSpan);
        Grid.SetColumnSpan(tile, cell.ColumnSpan);
    }

    /// <summary>
    /// Small tiles use the sub stream, large ones the main stream — decided per tile from the height of
    /// its video area (the header strip is not video), so the big tiles of the "büyük kareli" layouts can get HD
    /// while its neighbours stay SD. The old client always pulled main streams, which is what kept a CPU
    /// core busy. While a tile is maximized only that one is re-evaluated: the hidden ones keep the stream they
    /// had, so restoring the grid shows them at once (a quality switch reopens the stream).
    /// </summary>
    void UpdateAutoQuality()
    {
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleY;
        foreach (var tile in _tiles.Take(_layout.TileCount))
        {
            if (_maximized is not null && _maximized != tile)
            {
                continue;
            }
            var pixels = tile.VideoHeight * dpi;
            var quality = pixels >= App.Settings.MainStreamMinTileHeight ? StreamQuality.Main : StreamQuality.Sub;
            tile.SetAutoQuality(quality);
        }
    }

    void Grid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _qualityTimer.Stop();
        _qualityTimer.Start();
    }

    /// <summary>
    /// Saved order and page, brought up to date with the camera list (new or unhidden cameras join, closed tiles
    /// are filled again). "Açılışta son görünümü geri yükle" off: default layout, list order, first page.
    /// </summary>
    void RestoreLayout()
    {
        SetGlobalQuality(App.Settings.LiveQuality);
        var view = App.Settings.RestoreLastView ? App.Settings.LastView : null;
        // A saved 32 opens as 36; settings from the 1/4/9/16 era keep their old mapping (GridLayout.ForSavedView).
        _layout = GridLayout.ForSavedView(view);
        _maximized = null;
        _pages.Load(view?.Tiles ?? [], ListedCameras(), view?.Page ?? 0, _layout.TileCount);
        Sync();
    }

    void SaveLayout()
    {
        if (!IsLoaded)
        {
            return;
        }
        App.Settings.LastView = new SavedView
        {
            Name = "Son görünüm",
            Layout = _layout.TileCount,
            Tiles = [.. _pages.Slots],
            Page = _pages.Page,
        };
        App.SaveSettings();
    }

    void RaiseStateChanged()
    {
        if (_stateChangePending)
        {
            return;
        }
        _stateChangePending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _stateChangePending = false;
            StateChanged?.Invoke();
        });
    }

    void ShowToast(string text) => Message?.Invoke(text);

    /// <summary>null = automatic per tile size, otherwise forced for every tile.</summary>
    void SetGlobalQuality(StreamQuality? quality)
    {
        App.Settings.LiveQuality = quality;
        App.SaveSettings();
        foreach (var tile in _tiles)
        {
            if (quality is { } forced)
            {
                tile.SetManualQuality(forced);
            }
            else
            {
                tile.EnableAutoQuality();
            }
        }
        UpdateAutoQuality();
        RaiseStateChanged();
    }

    /// <summary>Top bar: snapshot of the selected tile.</summary>
    public async void Snapshot()
    {
        if (_selected?.Camera is null)
        {
            ShowToast("Önce bir kare seçin.");
            return;
        }
        var path = await _selected.TakeSnapshotAsync();
        if (path is null)
        {
            ShowToast("Anlık görüntü alınamadı.");
            return;
        }
        _selected.Flash();
        ShowToast($"Kaydedildi: {path}");
    }

    /// <summary>Top bar: start/stop the MP4 recording of the selected tile.</summary>
    public void ToggleRecording()
    {
        if (_selected?.Camera is null)
        {
            ShowToast("Önce bir kare seçin.");
            return;
        }
        var file = _selected.ToggleRecording();
        ShowToast(file is null ? "Kayıt durduruldu." : $"Kayıt: {file}");
        RaiseStateChanged();
    }

    void OpenFolders()
    {
        var folder = App.Settings.SnapshotFolder;
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(App.Settings.RecordingFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        if (!string.Equals(folder, App.Settings.RecordingFolder, StringComparison.OrdinalIgnoreCase))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{App.Settings.RecordingFolder}\"") { UseShellExecute = true });
        }
    }
}
