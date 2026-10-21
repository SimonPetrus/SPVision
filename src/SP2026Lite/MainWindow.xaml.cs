using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SP2026Lite.Controls;
using SP2026Lite.Core;
using SP2026Lite.Views;

namespace SP2026Lite;

public partial class MainWindow : Window
{
    Point? _dragOrigin;
    bool _fullScreen;
    WindowState _stateBeforeFullScreen;

    public MainWindow()
    {
        InitializeComponent();
        Live.FullScreenRequested += ToggleFullScreen;
        BuildTree();
        Playback.RefreshCameras();
        Closing += (_, _) =>
        {
            Live.StopAll();
            Playback.Stop();
        };
        Loaded += (_, _) =>
        {
            if (App.Settings.Devices.Count == 0)
            {
                AddDevice_Click(this, new RoutedEventArgs());
            }
        };
    }

    // ---- Device tree ----

    void BuildTree()
    {
        Tree.Items.Clear();
        foreach (var device in App.Settings.Devices)
        {
            var deviceItem = new TreeViewItem { Header = DeviceHeader(device), Tag = device, ContextMenu = DeviceMenu(device) };
            foreach (var camera in device.Cameras.OrderBy(c => c.Channel))
            {
                deviceItem.Items.Add(new TreeViewItem
                {
                    Header = CameraHeader(camera),
                    Tag = new CameraRef(device.Id, camera.Channel),
                    ToolTip = camera.Online ? $"Kanal {camera.Channel}" : $"Kanal {camera.Channel} — çevrimdışı",
                });
            }
            Tree.Items.Add(deviceItem);
        }
    }

    static object DeviceHeader(Device device) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Children =
        {
            new TextBlock { Text = "🖥", Margin = new Thickness(0, 0, 6, 0) },
            new TextBlock { Text = device.Name, FontWeight = FontWeights.SemiBold },
            new TextBlock { Text = $"  {device.Host}", Foreground = (Brush)Application.Current.Resources["MutedFg"], FontSize = 11, VerticalAlignment = VerticalAlignment.Center },
        },
    };

    static object CameraHeader(Camera camera) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Children =
        {
            new Ellipse
            {
                Width = 7, Height = 7, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center,
                Fill = camera.Online ? (Brush)Application.Current.Resources["Online"] : Brushes.Gray,
            },
            new TextBlock { Text = camera.Name, Opacity = camera.Online ? 1 : 0.5 },
        },
    };

    ContextMenu DeviceMenu(Device device)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Tüm kameraları canlı izle", () => ShowLive(device)));
        menu.Items.Add(MenuItem("Kamera listesini yenile", () => _ = RefreshCamerasAsync(device)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Düzenle…", () => EditDevice(device)));
        menu.Items.Add(MenuItem("Sil", () => RemoveDevice(device)));
        return menu;
    }

    static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    void Tree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        switch ((Tree.SelectedItem as TreeViewItem)?.Tag)
        {
            case CameraRef source when PlaybackTab.IsChecked == true:
                Playback.SelectCamera(source);
                break;
            case CameraRef source:
                Live.ShowCamera(source);
                break;
            case Device device:
                ShowLive(device);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    void Tree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _dragOrigin = e.GetPosition(Tree);

    void Tree_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragOrigin is not { } origin)
        {
            return;
        }
        var delta = e.GetPosition(Tree) - origin;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }
        var item = FindParent<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item?.Tag is CameraRef source)
        {
            _dragOrigin = null;
            DragDrop.DoDragDrop(item, new DataObject(VideoTile.DragFormat, source), DragDropEffects.Copy);
        }
    }

    static T? FindParent<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null and not T)
        {
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return node as T;
    }

    // ---- Device management ----

    void AddDevice_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new DeviceDialog(null) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is { } device)
        {
            App.Settings.Devices.Add(device);
            App.SaveSettings();
            DevicesChanged();
            ShowLive(device);
        }
    }

    void EditDevice(Device device)
    {
        var dialog = new DeviceDialog(device) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is { } updated)
        {
            var index = App.Settings.Devices.IndexOf(device);
            App.Settings.Devices[index] = updated;
            App.SaveSettings();
            DevicesChanged();
            Live.ReloadDevice(updated);
        }
    }

    void RemoveDevice(Device device)
    {
        if (MessageBox.Show(this, $"\"{device.Name}\" silinsin mi?", "Cihazı sil", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        Live.RemoveDevice(device.Id);
        App.Settings.Devices.Remove(device);
        App.SaveSettings();
        DevicesChanged();
    }

    async Task RefreshCamerasAsync(Device device)
    {
        try
        {
            using var isapi = new IsapiClient(device);
            var cameras = await isapi.GetCamerasAsync();
            foreach (var camera in cameras)
            {
                camera.StreamKey = device.Cameras.FirstOrDefault(c => c.Channel == camera.Channel)?.StreamKey;
            }
            device.Cameras = cameras;
            App.SaveSettings();
            DevicesChanged();
        }
        catch (IsapiException ex)
        {
            MessageBox.Show(this, ex.Message, device.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void DevicesChanged()
    {
        BuildTree();
        Playback.RefreshCameras();
    }

    void ShowLive(Device device)
    {
        SelectTab(live: true);
        Live.ShowDevice(device);
    }

    // ---- Tabs / full screen ----

    void LiveTab_Click(object sender, RoutedEventArgs e) => SelectTab(live: true);

    void PlaybackTab_Click(object sender, RoutedEventArgs e) => SelectTab(live: false);

    void SelectTab(bool live)
    {
        LiveTab.IsChecked = live;
        PlaybackTab.IsChecked = !live;
        Live.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        Playback.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
        if (live)
        {
            Playback.Stop();
        }
    }

    void ToggleFullScreen()
    {
        _fullScreen = !_fullScreen;
        Header.Visibility = SidePanel.Visibility = _fullScreen ? Visibility.Collapsed : Visibility.Visible;
        if (_fullScreen)
        {
            _stateBeforeFullScreen = WindowState;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Normal; // re-maximize so the taskbar is covered
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = _stateBeforeFullScreen;
        }
    }

    void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11 || (e.Key == Key.Escape && _fullScreen))
        {
            ToggleFullScreen();
            e.Handled = true;
        }
    }
}
