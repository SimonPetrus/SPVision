using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SPVision.Core;

namespace SPVision.Views;

/// <summary>
/// The event pop-up (onaylı tasarım "Olay bildirimleri"): a borderless, topmost window at the bottom right of the screen's
/// work area that never takes the focus (<c>ShowActivated</c> false, <c>WS_EX_NOACTIVATE</c> + <c>WS_EX_TOOLWINDOW</c>: no
/// taskbar button, no Alt+Tab entry), so it also shows while SPVision is minimized or another program is in front. App icon ·
/// "SPVision" · close; the camera's picture (96×54, if it comes quickly) · title · "camera · time"; "Canlı izle" (accent) and
/// "Kaydı oynat" for a known camera. Placed and stacked by <see cref="EventToasts"/>; closes itself after
/// <see cref="EventToasts.ShowFor"/> unless the mouse is over it.
/// </summary>
public partial class EventToast : Window
{
    const int GwlExStyle = -20;
    const long WsExToolWindow = 0x00000080;
    const long WsExNoActivate = 0x08000000;

    readonly DispatcherTimer _hide = new() { Interval = EventToasts.ShowFor };

    internal EventToast(DeviceEvent e)
    {
        InitializeComponent();
        Event = e;
        var (_, _, icon) = EventPanel.Tone(e.Kind);
        KindIcon.Data = (Geometry)FindResource(icon);
        TitleText.Text = e.Title;
        PlaceText.Text = EventText.PlaceAndTime(e);
        Buttons.Visibility = e.Camera is null ? Visibility.Collapsed : Visibility.Visible;
        System.Windows.Automation.AutomationProperties.SetName(this, $"{e.Title}, {EventText.PlaceAndTime(e)}");
        System.Windows.Automation.AutomationProperties.SetName(LiveButton, $"{e.Place}: canlı izle");
        System.Windows.Automation.AutomationProperties.SetName(PlaybackButton, $"{e.Place}: kaydı oynat");
        _hide.Tick += (_, _) => Close();
        MouseEnter += (_, _) => _hide.Stop();
        MouseLeave += (_, _) => _hide.Start();
        SourceInitialized += (_, _) => NoActivate();
        Closed += (_, _) => _hide.Stop();
    }

    public DeviceEvent Event { get; }

    /// <summary>Height of the card alone (the window adds room for the shadow).</summary>
    internal double CardHeight => ActualHeight - EventToasts.ShadowTop - EventToasts.ShadowBottom;

    internal event Action<DeviceEvent>? LiveRequested;
    internal event Action<DeviceEvent>? PlaybackRequested;

    internal void StartTimer() => _hide.Start();

    /// <summary>
    /// The camera's current picture (read only: <c>GET ISAPI/Streaming/channels/{n}01/picture</c>, 3 s at most), decoded small
    /// off the UI thread. Optional: on any failure the kind's icon stays.
    /// </summary>
    internal async void LoadPicture(Device device, int channel)
    {
        try
        {
            var picture = await Task.Run(async () =>
            {
                using var isapi = new IsapiClient(device, TimeSpan.FromSeconds(3));
                var bytes = await isapi.GetSnapshotAsync(channel);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 192; // 96 px at 200 %
                image.StreamSource = new MemoryStream(bytes);
                image.EndInit();
                image.Freeze();
                return image;
            });
            if (IsLoaded)
            {
                Picture.ImageSource = picture;
                PictureFrame.Visibility = Visibility.Visible;
            }
        }
        catch (Exception)
        {
            // No picture (camera offline, slow, encrypted snapshot refused…): the icon stays.
        }
    }

    void NoActivate()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style | WsExNoActivate | WsExToolWindow));
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void Live_Click(object sender, RoutedEventArgs e)
    {
        Close();
        LiveRequested?.Invoke(Event);
    }

    void Playback_Click(object sender, RoutedEventArgs e)
    {
        Close();
        PlaybackRequested?.Invoke(Event);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}

/// <summary>
/// The open event pop-ups: newest at the bottom right of the primary screen's work area (card 16 px from its right and
/// bottom edge), older ones above it 8 px apart, at most <see cref="MaxOpen"/> — a fourth closes the oldest. Each closes
/// after <see cref="ShowFor"/> unless the mouse is over it. UI thread only.
/// </summary>
sealed class EventToasts
{
    public const int MaxOpen = 3;
    public static readonly TimeSpan ShowFor = TimeSpan.FromSeconds(8);

    // Room around the 364 px card for its shadow (EventToast.xaml: Margin 24,16,24,32).
    internal const double ShadowSide = 24;
    internal const double ShadowTop = 16;
    internal const double ShadowBottom = 32;
    const double CardWidth = 364;
    const double Edge = 16;
    const double Gap = 8;

    readonly List<EventToast> _open = []; // oldest first

    public event Action<DeviceEvent>? LiveRequested;
    public event Action<DeviceEvent>? PlaybackRequested;

    public void Show(DeviceEvent e, Device? device)
    {
        while (_open.Count >= MaxOpen)
        {
            var oldest = _open[0];
            _open.RemoveAt(0);
            oldest.Close();
        }
        var toast = new EventToast(e);
        toast.LiveRequested += ev => LiveRequested?.Invoke(ev);
        toast.PlaybackRequested += ev => PlaybackRequested?.Invoke(ev);
        toast.Closed += (_, _) =>
        {
            _open.Remove(toast);
            Arrange();
        };
        toast.SizeChanged += (_, _) => Arrange();
        _open.Add(toast);
        var work = SystemParameters.WorkArea;
        toast.Left = work.Right - Edge - CardWidth - ShadowSide; // card's right edge 16 px in
        toast.Top = work.Bottom; // placed by Arrange once its height is known
        toast.Show();
        toast.StartTimer();
        Arrange();
        if (device is not null && e.Channel is { } channel && e.Camera is not null &&
            e.Kind is AlertKind.Motion or AlertKind.Smart or AlertKind.Tamper)
        {
            toast.LoadPicture(device, channel); // a lost camera has no picture to give
        }
    }

    /// <summary>App locked or closing: every pop-up goes (missed ones are not shown again).</summary>
    public void CloseAll()
    {
        foreach (var toast in _open.ToList())
        {
            toast.Close();
        }
    }

    void Arrange()
    {
        var work = SystemParameters.WorkArea;
        var bottom = work.Bottom - Edge; // bottom edge of the next card
        for (var i = _open.Count - 1; i >= 0; i--)
        {
            var toast = _open[i];
            if (toast.ActualHeight <= 0)
            {
                continue;
            }
            toast.Left = work.Right - Edge - CardWidth - ShadowSide;
            toast.Top = bottom + ShadowBottom - toast.ActualHeight;
            bottom -= toast.CardHeight + Gap;
        }
    }
}
