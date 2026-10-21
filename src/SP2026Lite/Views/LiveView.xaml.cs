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
    const int MaxTiles = 16;

    readonly List<VideoTile> _tiles = [];
    readonly DispatcherTimer _qualityTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    int _columns = 2;
    VideoTile? _selected;
    VideoTile? _maximized;

    public LiveView()
    {
        InitializeComponent();
        for (var i = 0; i < MaxTiles; i++)
        {
            var tile = new VideoTile();
            tile.Activated += Select;
            tile.DoubleClicked += ToggleMaximize;
            tile.CameraDropped += (t, source) => ShowCamera(t, source);
            tile.Changed += _ => SaveLayout();
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
            : _tiles.Take(_columns * _columns).FirstOrDefault(t => t.Camera is null) ?? _selected ?? _tiles[0];
        ShowCamera(target, source);
        Select(target);
    }

    /// <summary>Opens every camera of a device into the grid, growing the layout if needed.</summary>
    public void ShowDevice(Device device)
    {
        var cameras = device.Cameras.Where(c => c.Online).Take(MaxTiles).ToList();
        if (cameras.Count == 0)
        {
            return;
        }
        var columns = cameras.Count switch { 1 => 1, <= 4 => 2, <= 9 => 3, _ => 4 };
        SetLayout(Math.Max(columns, _columns));
        for (var i = 0; i < _tiles.Count; i++)
        {
            if (i < cameras.Count)
            {
                _tiles[i].Show(device, cameras[i]);
            }
            else if (i < _columns * _columns)
            {
                _tiles[i].Clear();
            }
        }
        SaveLayout();
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
            Grid.Children.Clear();
            Grid.Columns = Grid.Rows = 1;
            Grid.Children.Add(tile);
        }
        else
        {
            _maximized = null;
            ArrangeTiles();
        }
        _qualityTimer.Start();
    }

    void SetLayout(int columns)
    {
        _columns = columns;
        _maximized = null;
        for (var i = columns * columns; i < _tiles.Count; i++)
        {
            _tiles[i].Clear();
        }
        ArrangeTiles();
        foreach (var button in new[] { Layout1, Layout4, Layout9, Layout16 })
        {
            button.IsChecked = (string)button.Tag == columns.ToString();
        }
        if (_selected is null || _tiles.IndexOf(_selected) >= columns * columns)
        {
            Select(_tiles[0]);
        }
        _qualityTimer.Start();
        SaveLayout();
    }

    void ArrangeTiles()
    {
        Grid.Children.Clear();
        Grid.Columns = Grid.Rows = _columns;
        foreach (var tile in _tiles.Take(_columns * _columns))
        {
            Grid.Children.Add(tile);
        }
    }

    /// <summary>
    /// Small tiles use the sub stream, large ones the main stream. The old client always pulled main streams,
    /// which is what kept a CPU core busy.
    /// </summary>
    void UpdateAutoQuality()
    {
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleY;
        foreach (var tile in _tiles.Take(_columns * _columns))
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
        SetLayout(view?.Columns is >= 1 and <= 4 ? view.Columns : 2);
        if (view is null)
        {
            return;
        }
        for (var i = 0; i < Math.Min(view.Tiles.Count, _columns * _columns); i++)
        {
            if (view.Tiles[i] is { } source)
            {
                ShowCamera(_tiles[i], source);
            }
        }
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
            Columns = _columns,
            Tiles = _tiles.Take(_columns * _columns).Select(t => t.Source).ToList(),
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
        SetLayout(int.Parse((string)((ToggleButton)sender).Tag));

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
