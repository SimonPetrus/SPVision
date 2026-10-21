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
    readonly List<VideoTile> _tiles = [];
    readonly DispatcherTimer _qualityTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    GridLayout _layout = GridLayout.Default;
    VideoTile? _selected;
    VideoTile? _maximized;
    bool _batchUpdate; // many tiles change at once: save the layout once at the end
    bool _stateChangePending;

    public LiveView()
    {
        InitializeComponent();
        for (var i = 0; i < GridLayout.MaxTiles; i++)
        {
            var tile = new VideoTile { Visibility = Visibility.Collapsed };
            tile.Activated += Select;
            tile.DoubleClicked += ToggleMaximize;
            tile.CameraDropped += (t, source) => ShowCamera(t, source);
            tile.TileDropped += SwapTiles;
            tile.Changed += _ =>
            {
                if (!_batchUpdate)
                {
                    SaveLayout();
                }
                RaiseStateChanged();
            };
            tile.StateChanged += _ => RaiseStateChanged();
            tile.Notice += (_, text) => ShowToast(text);
            tile.StopAllRequested += StopAll;
            tile.OpenFoldersRequested += OpenFolders;
            _tiles.Add(tile);
            // Always children: layout changes and maximize only move/hide them. Removing a tile from the tree
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
    /// Layout, quality, selection or any tile state changed. Coalesced: raised once per dispatcher
    /// pass, so the top bar, camera list and status bar refresh cheaply.
    /// </summary>
    public event Action? StateChanged;

    /// <summary>Current layout (any of <see cref="GridLayout.All"/>).</summary>
    public GridLayout Layout => _layout;

    /// <summary>null = automatic per tile size, otherwise forced for every tile.</summary>
    public StreamQuality? GlobalQuality => App.Settings.LiveQuality;

    public bool SelectedIsRecording => _selected?.IsRecording == true;

    public Device? SelectedDevice => _selected?.Device;

    /// <summary>Tiles of the current layout (empty ones included).</summary>
    public IReadOnlyList<VideoTile> VisibleTiles => _tiles.Take(_layout.TileCount).ToList();

    /// <summary>1-based tile number showing the camera, or null when it is not on screen.</summary>
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

    /// <summary>Tile showing the camera in the current layout, if any.</summary>
    public VideoTile? TileOf(CameraRef source) => _tiles.Take(_layout.TileCount).FirstOrDefault(t => t.Source == source);

    /// <summary>Puts a camera into the selected tile, or the first empty one.</summary>
    public void ShowCamera(CameraRef source)
    {
        var target = _selected is not null && _selected.Camera is null
            ? _selected
            : _tiles.Take(_layout.TileCount).FirstOrDefault(t => t.Camera is null) ?? _selected ?? _tiles[0];
        ShowCamera(target, source);
        Select(target);
    }

    /// <summary>Opens every camera of a device into the grid, growing the layout if needed.</summary>
    public void ShowDevice(Device device)
    {
        // Cameras hidden in Settings are left out, like in the sidebar list.
        var cameras = device.ListedCameras().Where(c => c.Online).Take(GridLayout.MaxTiles).ToList();
        if (cameras.Count == 0)
        {
            return;
        }
        SetLayout(GridLayout.Fitting(Math.Max(cameras.Count, _layout.TileCount)));
        Batch(() =>
        {
            for (var i = 0; i < _tiles.Count; i++)
            {
                if (i < cameras.Count)
                {
                    _tiles[i].Show(device, cameras[i]);
                }
                else if (i < _layout.TileCount)
                {
                    _tiles[i].Clear();
                }
            }
        });
    }

    public void StopAll()
    {
        foreach (var tile in _tiles)
        {
            tile.Stop();
        }
    }

    /// <summary>Re-resolves devices after edits (password/key changes) and reconnects affected tiles.</summary>
    public void ReloadDevice(Device device)
    {
        foreach (var tile in _tiles.Where(t => t.Device?.Id == device.Id))
        {
            var camera = device.Cameras.FirstOrDefault(c => c.Channel == tile.Camera!.Channel);
            if (camera is null)
            {
                tile.Clear();
            }
            else
            {
                tile.Show(device, camera);
            }
        }
    }

    /// <summary>Camera list of a device was refreshed in place: clear tiles whose camera is gone, rename the rest.</summary>
    public void RefreshDeviceCameras(Device device)
    {
        foreach (var tile in _tiles.Where(t => t.Device?.Id == device.Id))
        {
            var camera = device.Cameras.FirstOrDefault(c => c.Channel == tile.Camera!.Channel);
            if (camera is null)
            {
                tile.Clear();
            }
            else if (!ReferenceEquals(camera, tile.Camera) || !ReferenceEquals(device, tile.Device))
            {
                tile.Show(device, camera);
            }
            else
            {
                tile.RefreshCameraInfo();
            }
        }
        RaiseStateChanged();
    }

    /// <summary>Shows or hides the recorder's motion boxes on every SDK tile.</summary>
    public void ApplyMotionBoxes(bool show)
    {
        foreach (var tile in _tiles)
        {
            tile.ApplyMotionBoxes(show);
        }
    }

    public void RemoveDevice(Guid deviceId)
    {
        foreach (var tile in _tiles.Where(t => t.Device?.Id == deviceId))
        {
            tile.Clear();
        }
    }

    /// <summary>Top bar: layout picker.</summary>
    public void SelectLayout(GridLayout layout) => SetLayout(layout);

    /// <summary>Top bar: Oto/HD/SD segmented group.</summary>
    public void SelectQuality(StreamQuality? quality) => SetGlobalQuality(quality);

    void ShowCamera(VideoTile tile, CameraRef source)
    {
        var device = App.Settings.Devices.FirstOrDefault(d => d.Id == source.DeviceId);
        var camera = device?.Cameras.FirstOrDefault(c => c.Channel == source.Channel);
        if (device is null || camera is null)
        {
            return;
        }
        tile.Show(device, camera);
        _qualityTimer.Start();
    }

    /// <summary>Dragging a tile onto another swaps their cameras; onto an empty tile it moves the camera.</summary>
    void SwapTiles(VideoTile from, VideoTile to)
    {
        if (from == to || from.Source is not { } moved)
        {
            return;
        }
        var replaced = to.Source;
        Batch(() =>
        {
            if (replaced is null)
            {
                from.Clear();
            }
            else
            {
                ShowCamera(from, replaced);
            }
            ShowCamera(to, moved);
        });
        Select(to);
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
            // The other tiles stay in the grid, only hidden: their streams and video windows (SDK HWND, Flyleaf
            // surface) keep running, so restoring shows their live picture at once — no reopen, no keyframe wait.
            // A hidden tile also hides its video window, so nothing covers the maximized one (airspace).
            _maximized = tile;
            foreach (var other in _tiles.Take(_layout.TileCount))
            {
                other.Visibility = other == tile ? Visibility.Visible : Visibility.Hidden;
            }
            tile.IsCompact = false;
            Place(tile, new GridCell(0, 0, _layout.Rows, _layout.Columns));
        }
        else
        {
            _maximized = null;
            ArrangeTiles();
        }
        _qualityTimer.Start();
    }

    void SetLayout(GridLayout layout)
    {
        _layout = layout;
        _maximized = null;
        // Tiles that drop out of the layout release their streams (Flyleaf and SDK alike).
        _batchUpdate = true;
        try
        {
            for (var i = layout.TileCount; i < _tiles.Count; i++)
            {
                _tiles[i].Clear();
            }
        }
        finally
        {
            _batchUpdate = false;
        }
        ArrangeTiles();
        if (_selected is null || _tiles.IndexOf(_selected) >= layout.TileCount)
        {
            Select(_tiles[0]);
        }
        _qualityTimer.Start();
        SaveLayout();
        RaiseStateChanged();
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
                tile.Visibility = Visibility.Collapsed; // already cleared by SetLayout
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

    void RestoreLayout()
    {
        SetGlobalQuality(App.Settings.LiveQuality);
        // "Açılışta son görünümü geri yükle" off: start with the default layout and empty tiles.
        var view = App.Settings.RestoreLastView ? App.Settings.LastView : null;
        // A saved 32 opens as 36; settings from the 1/4/9/16 era keep their old mapping (GridLayout.ForSavedView).
        SetLayout(GridLayout.ForSavedView(view));
        if (view is null)
        {
            return;
        }
        Batch(() =>
        {
            for (var i = 0; i < Math.Min(view.Tiles.Count, _layout.TileCount); i++)
            {
                if (view.Tiles[i] is { } source)
                {
                    ShowCamera(_tiles[i], source);
                }
            }
        });
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
            Tiles = _tiles.Take(_layout.TileCount).Select(t => t.Source).ToList(),
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
