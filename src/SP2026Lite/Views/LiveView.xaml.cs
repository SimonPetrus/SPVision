using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using SP2026Lite.Controls;
using SP2026Lite.Core;

namespace SP2026Lite.Views;

public partial class LiveView : UserControl
{
    readonly List<VideoTile> _tiles = [];
    readonly DispatcherTimer _qualityTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    GridLayout _layout = GridLayout.Default;
    VideoTile? _selected;
    VideoTile? _maximized;
    bool _batchUpdate; // many tiles change at once: save the layout once at the end

    public LiveView()
    {
        InitializeComponent();
        for (var i = 0; i < GridLayout.MaxTiles; i++)
        {
            var tile = new VideoTile();
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
            };
            _tiles.Add(tile);
        }
        _qualityTimer.Tick += (_, _) =>
        {
            _qualityTimer.Stop();
            UpdateAutoQuality();
        };
        Loaded += (_, _) => RestoreLayout();
    }

    public event Action? FullScreenRequested;

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
        var cameras = device.Cameras.Where(c => c.Online).Take(GridLayout.MaxTiles).ToList();
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

    public void RemoveDevice(Guid deviceId)
    {
        foreach (var tile in _tiles.Where(t => t.Device?.Id == deviceId))
        {
            tile.Clear();
        }
    }

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
        UpdateRecordButton();
    }

    void ToggleMaximize(VideoTile tile)
    {
        if (_maximized is null)
        {
            _maximized = tile;
            ResetGrid(1, 1);
            Place(tile, new GridCell(0, 0));
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
        foreach (var button in new[] { Layout6, Layout8, Layout25, Layout32 })
        {
            button.IsChecked = (string)button.Tag == layout.TileCount.ToString();
        }
        if (_selected is null || _tiles.IndexOf(_selected) >= layout.TileCount)
        {
            Select(_tiles[0]);
        }
        _qualityTimer.Start();
        SaveLayout();
    }

    void ArrangeTiles()
    {
        ResetGrid(_layout.Columns, _layout.Rows);
        for (var i = 0; i < _layout.TileCount; i++)
        {
            Place(_tiles[i], _layout.Cells[i]);
        }
    }

    void ResetGrid(int columns, int rows)
    {
        TileGrid.Children.Clear();
        TileGrid.ColumnDefinitions.Clear();
        TileGrid.RowDefinitions.Clear();
        for (var i = 0; i < columns; i++)
        {
            TileGrid.ColumnDefinitions.Add(new ColumnDefinition());
        }
        for (var i = 0; i < rows; i++)
        {
            TileGrid.RowDefinitions.Add(new RowDefinition());
        }
    }

    void Place(VideoTile tile, GridCell cell)
    {
        Grid.SetRow(tile, cell.Row);
        Grid.SetColumn(tile, cell.Column);
        Grid.SetRowSpan(tile, cell.RowSpan);
        Grid.SetColumnSpan(tile, cell.ColumnSpan);
        TileGrid.Children.Add(tile);
    }

    /// <summary>
    /// Small tiles use the sub stream, large ones the main stream — decided per tile from its own height,
    /// so the large tile of the 6/8 layouts can get HD while its neighbours stay SD. The old client always
    /// pulled main streams, which is what kept a CPU core busy.
    /// </summary>
    void UpdateAutoQuality()
    {
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleY;
        foreach (var tile in _tiles.Take(_layout.TileCount))
        {
            var visible = _maximized is null || _maximized == tile;
            var pixels = tile.ActualHeight * dpi;
            var quality = visible && pixels >= App.Settings.MainStreamMinTileHeight ? StreamQuality.Main : StreamQuality.Sub;
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
        var view = App.Settings.LastView;
        // Settings from the 1/4/9/16 era map to the smallest layout that holds as many tiles.
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

    void ShowToast(string text)
    {
        ToastText.Text = text;
        Toast.Visibility = Visibility.Visible;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (ToastText.Text == text)
            {
                Toast.Visibility = Visibility.Collapsed;
            }
        };
        timer.Start();
    }

    void UpdateRecordButton() =>
        RecordButton.Content = _selected?.IsRecording == true ? "⏹ Kaydı durdur" : "⏺ Kayıt";

    void Layout_Click(object sender, RoutedEventArgs e) =>
        SetLayout(GridLayout.Fitting(int.Parse((string)((ToggleButton)sender).Tag)));

    /// <summary>null = automatic per tile size, otherwise forced for every tile.</summary>
    void SetGlobalQuality(StreamQuality? quality)
    {
        App.Settings.LiveQuality = quality;
        App.SaveSettings();
        QualityAuto.IsChecked = quality is null;
        QualityHd.IsChecked = quality == StreamQuality.Main;
        QualitySd.IsChecked = quality == StreamQuality.Sub;
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
    }

    void QualityAuto_Click(object sender, RoutedEventArgs e) => SetGlobalQuality(null);

    void QualityHd_Click(object sender, RoutedEventArgs e) => SetGlobalQuality(StreamQuality.Main);

    void QualitySd_Click(object sender, RoutedEventArgs e) => SetGlobalQuality(StreamQuality.Sub);

    async void Snapshot_Click(object sender, RoutedEventArgs e)
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

    void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_selected?.Camera is null)
        {
            ShowToast("Önce bir kare seçin.");
            return;
        }
        var file = _selected.ToggleRecording();
        ShowToast(file is null ? "Kayıt durduruldu." : $"Kayıt: {file}");
        UpdateRecordButton();
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
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

    void StopAll_Click(object sender, RoutedEventArgs e) => StopAll();

    void FullScreen_Click(object sender, RoutedEventArgs e) => FullScreenRequested?.Invoke();
}
