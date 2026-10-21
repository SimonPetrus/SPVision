using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FlyleafLib.MediaPlayer;
using SP2026Lite.Core;
using SP2026Lite.Hik;

namespace SP2026Lite.Views;

/// <summary>
/// NVR playback: ISAPI search for the day's recordings, then playback from any clicked time.
/// Two rendering paths share the view: RTSP via Flyleaf (unencrypted) and the Hikvision SDK
/// (PlayCtrl) — the only way to play stream-encrypted recordings.
/// </summary>
public partial class PlaybackView : UserControl
{
    public sealed record CameraItem(Device Device, Camera Camera)
    {
        public string Title => $"{Camera.Name}  ({Device.Name})";
    }

    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(500) };
    Player? _player;                    // FFmpeg path
    HikPlaybackPlayer? _hikPlayer;      // SDK path (stream encryption)
    bool _preferHik;                    // sticky: SDK proved necessary for this device
    bool _triedHikFallback;             // one FFmpeg → SDK fallback per PlayAt
    CameraItem? _current;
    List<RecordingSegment> _segments = [];
    DateTime _openedAt;
    CancellationTokenSource? _searchCts;

    public PlaybackView()
    {
        InitializeComponent();
        DayPicker.SelectedDate = DateTime.Today;
        Timeline.TimeSelected += PlayAt;
        _clock.Tick += (_, _) => UpdateClock();
        Unloaded += (_, _) => { _clock.Stop(); StopHik(); };
        Loaded += (_, _) => _clock.Start();
    }

    public void RefreshCameras()
    {
        var selected = CameraBox.SelectedItem as CameraItem;
        var items = App.Settings.Devices
            .SelectMany(d => d.Cameras.Select(c => new CameraItem(d, c)))
            .ToList();
        CameraBox.ItemsSource = items;
        CameraBox.SelectedItem = items.FirstOrDefault(i => i.Device.Id == selected?.Device.Id && i.Camera.Channel == selected.Camera.Channel)
                                 ?? items.FirstOrDefault();
    }

    public void SelectCamera(CameraRef source)
    {
        RefreshCameras();
        var item = ((IEnumerable<CameraItem>)CameraBox.ItemsSource)
            .FirstOrDefault(i => i.Device.Id == source.DeviceId && i.Camera.Channel == source.Channel);
        if (item is not null)
        {
            CameraBox.SelectedItem = item;
            _ = SearchAsync();
        }
    }

    public void Stop()
    {
        if (_player?.IsRecording == true)
        {
            _player.StopRecording();
        }
        _player?.Stop();
        StopHik();
        UpdateClipButton();
    }

    void StopHik()
    {
        if (_hikPlayer is null)
        {
            return;
        }
        _hikPlayer.Error -= OnHikError;
        _hikPlayer.Started -= OnHikStarted;
        _hikPlayer.Ended -= OnHikEnded;
        _hikPlayer.Dispose();
        _hikPlayer = null;
    }

    async Task SearchAsync()
    {
        if (CameraBox.SelectedItem is not CameraItem item || DayPicker.SelectedDate is not { } day)
        {
            return;
        }
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;
        Stop();
        _current = item;
        _preferHik = !string.IsNullOrEmpty(item.Device.StreamKeyFor(item.Camera));
        StatusText.Text = "Kayıtlar aranıyor…";
        OverlayText.Text = "";
        try
        {
            using var isapi = new IsapiClient(item.Device, TimeSpan.FromSeconds(20));
            _segments = await isapi.SearchRecordingsAsync(item.Camera.Channel, day.Date, day.Date.AddDays(1).AddSeconds(-1), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (IsapiException ex)
        {
            _segments = [];
            StatusText.Text = $"Arama başarısız: {ex.Message}";
            Timeline.SetDay(day, _segments);
            return;
        }

        Timeline.SetDay(day, _segments);
        if (_segments.Count == 0)
        {
            StatusText.Text = "Bu gün için kayıt bulunamadı.";
            OverlayText.Text = "Kayıt yok";
            return;
        }
        var total = TimeSpan.FromTicks(_segments.Sum(s => (s.End - s.Start).Ticks));
        StatusText.Text = $"{_segments.Count} kayıt parçası, toplam {total:hh\\:mm} saat. Oynatmak için zaman çizelgesine tıklayın.";
        PlayAt(_segments[0].Start);
    }

    void PlayAt(DateTime time)
    {
        if (_current is null)
        {
            return;
        }
        // Jump into the next recording when the click lands in a gap.
        var segment = _segments.FirstOrDefault(s => s.End > time);
        if (segment is not null && segment.Start > time)
        {
            time = segment.Start;
        }
        _triedHikFallback = false;

        bool useHik = (_preferHik || !string.IsNullOrEmpty(_current.Device.StreamKeyFor(_current.Camera))) && HikSdk.Available;
        if (useHik)
        {
            PlayHikAt(time);
        }
        else
        {
            PlayFlyleafAt(time);
        }
    }

    void PlayFlyleafAt(DateTime time)
    {
        StopHik();
        HikHost.Visibility = Visibility.Collapsed;
        Host.Visibility = Visibility.Visible;
        if (_player is null)
        {
            _player = Players.CreatePlayback();
            _player.PropertyChanged += Player_PropertyChanged;
            Host.Player = _player;
        }
        if (_player.IsRecording)
        {
            _player.StopRecording();
        }
        var end = time.Date.AddDays(1).AddSeconds(-1);
        _openedAt = time;
        Timeline.Position = time;
        OverlayText.Text = "Bağlanıyor…";
        _player.OpenAsync(StreamUrls.Playback(_current!.Device, _current.Camera.Channel, time, end));
        UpdateClipButton();
    }

    /// <summary>Login + PlayBackByTime are blocking network calls, so they run off the UI thread.</summary>
    void PlayHikAt(DateTime time)
    {
        _player?.Stop();
        if (_player is not null)
        {
            _player.PropertyChanged -= Player_PropertyChanged;
            Host.Player = null;
            _player.Dispose();
            _player = null;
        }
        Host.Visibility = Visibility.Collapsed;
        HikHost.Visibility = Visibility.Visible;
        HikHost.UpdateLayout(); // build the child HWND now

        var item = _current!;
        var key = item.Device.StreamKeyFor(item.Camera);
        var end = time.Date.AddDays(1).AddSeconds(-1);
        _openedAt = time;
        Timeline.Position = time;
        OverlayText.Text = ""; // Flyleaf overlay is hidden; status goes to StatusText
        StatusText.Text = "Bağlanıyor…";
        PlayPauseButton.Content = "⏸";
        UpdateClipButton();

        var player = _hikPlayer;
        if (player is null)
        {
            player = new HikPlaybackPlayer();
            player.Error += OnHikError;
            player.Started += OnHikStarted;
            player.Ended += OnHikEnded;
            _hikPlayer = player;
        }

        Task.Run(() =>
        {
            try
            {
                var hwnd = IntPtr.Zero;
                for (var i = 0; i < 150 && hwnd == IntPtr.Zero && !player.IsDisposed; i++)
                {
                    hwnd = HikHost.VideoHandle;
                    if (hwnd == IntPtr.Zero)
                    {
                        Thread.Sleep(20);
                    }
                }
                if (hwnd == IntPtr.Zero)
                {
                    throw new HikException(0, "Video penceresi hazırlanamadı");
                }
                var session = HikSession.GetOrCreate(item.Device);
                var userId = session.UserId;
                if (player.IsDisposed)
                {
                    return;
                }
                // Recorders number their digital channels from StartDChan (e.g. 33); ISAPI is 1-based.
                player.Start(userId, session.SdkChannel(item.Camera.Channel), time, end, hwnd, key);
            }
            catch (HikException ex)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (player.IsDisposed || _hikPlayer != player)
                    {
                        return;
                    }
                    StatusText.Text = $"Oynatılamadı: {ex.Message}";
                });
            }
        });
    }

    void OnHikStarted()
    {
        Dispatcher.BeginInvoke(() =>
        {
            StatusText.Text = "";
            PlayPauseButton.Content = "⏸";
        });
    }

    void OnHikError(string message)
    {
        Dispatcher.BeginInvoke(() => StatusText.Text = $"Oynatma hatası: {message}");
    }

    void OnHikEnded()
    {
        Dispatcher.BeginInvoke(() =>
        {
            StatusText.Text = "Kayıt sonu";
            PlayPauseButton.Content = "▶";
        });
    }

    /// <summary>Recorder wall-clock time of the frame on screen.</summary>
    DateTime? CurrentTime =>
        _player is { Status: Status.Playing or Status.Paused } p ? _openedAt + TimeSpan.FromTicks(Math.Max(0, p.CurTime))
        : _hikPlayer?.IsStarted == true ? _hikPlayer.GetTime()
        : null;

    void UpdateClock()
    {
        if (CurrentTime is { } now)
        {
            ClockText.Text = now.ToString("dd.MM.yyyy HH:mm:ss");
            Timeline.Position = now;
        }
    }

    void Player_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Player.Status))
        {
            return;
        }
        Dispatcher.BeginInvoke(() =>
        {
            if (_player is null)
            {
                return;
            }
            switch (_player.Status)
            {
                case Status.Opening:
                    OverlayText.Text = "Bağlanıyor…";
                    break;
                case Status.Failed when HikSdk.Available && !_triedHikFallback:
                    // FFmpeg could not open/decode — typical for encrypted recordings. Try the SDK once.
                    _triedHikFallback = true;
                    _preferHik = true;
                    StatusText.Text = "Şifreli kayıt için SDK deneniyor…";
                    if (Timeline.Position is { } position)
                    {
                        PlayHikAt(position);
                    }
                    else if (_segments.Count > 0)
                    {
                        PlayHikAt(_segments[0].Start);
                    }
                    return;
                case Status.Failed:
                    OverlayText.Text = $"Oynatılamadı\n{_player.LastError}";
                    break;
                case Status.Ended:
                    OverlayText.Text = "Kayıt sonu";
                    break;
                default:
                    OverlayText.Text = "";
                    break;
            }
            PlayPauseButton.Content = _player.Status == Status.Playing ? "⏸" : "▶";
            UpdateClipButton();
        });
    }

    void UpdateClipButton() =>
        ClipButton.Content = _player?.IsRecording == true || _hikPlayer?.IsSaving == true ? "⏹ Klibi bitir" : "⏺ Klip kaydet";

    async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync();

    void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_hikPlayer?.IsRunning == true)
        {
            if (_hikPlayer.IsPaused)
            {
                _hikPlayer.Resume();
                PlayPauseButton.Content = "⏸";
            }
            else
            {
                _hikPlayer.Pause();
                PlayPauseButton.Content = "▶";
            }
            return;
        }
        if (_player is null)
        {
            return;
        }
        if (_player.Status == Status.Playing)
        {
            _player.Pause();
        }
        else if (_player.Status == Status.Paused)
        {
            _player.Play();
        }
        else if (Timeline.Position is { } position)
        {
            PlayAt(position);
        }
    }

    void Stop_Click(object sender, RoutedEventArgs e) => Stop();

    void Back_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentTime is { } now)
        {
            PlayAt(now.AddSeconds(-30));
        }
    }

    void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentTime is { } now)
        {
            PlayAt(now.AddSeconds(30));
        }
    }

    void Snapshot_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            return;
        }
        Directory.CreateDirectory(App.Settings.SnapshotFolder);
        var path = Path.Combine(App.Settings.SnapshotFolder, $"{_current.Camera.Name}_{CurrentTime:yyyyMMdd_HHmmss}_kayit.jpg");
        if (_hikPlayer?.IsStarted == true && HikHost.Visibility == Visibility.Visible)
        {
            var saved = _hikPlayer.Snapshot(path);
            StatusText.Text = saved is null ? "Anlık görüntü alınamadı" : $"Kaydedildi: {path}";
            return;
        }
        if (_player?.Status is not (Status.Playing or Status.Paused))
        {
            return;
        }
        _player.TakeSnapshotToFile(path, 0, 0);
        StatusText.Text = $"Kaydedildi: {path}";
    }

    void Clip_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            return;
        }
        if (_hikPlayer?.IsSaving == true)
        {
            _hikPlayer.StopSave();
            StatusText.Text = "Klip kaydedildi.";
            UpdateClipButton();
            return;
        }
        if (_player?.IsRecording == true)
        {
            _player.StopRecording();
            StatusText.Text = "Klip kaydedildi.";
            UpdateClipButton();
            return;
        }
        Directory.CreateDirectory(App.Settings.RecordingFolder);
        var file = Path.Combine(App.Settings.RecordingFolder, $"{_current.Camera.Name}_{CurrentTime:yyyyMMdd_HHmmss}_klip.mp4");
        if (_hikPlayer?.IsRunning == true && _hikPlayer.IsStarted)
        {
            try
            {
                _hikPlayer.StartSave(file);
                StatusText.Text = $"Klip kaydediliyor: {file}";
            }
            catch (HikException ex)
            {
                StatusText.Text = ex.Message;
            }
        }
        else if (_player?.Status == Status.Playing)
        {
            _player.StartRecording(ref file, false);
            StatusText.Text = $"Klip kaydediliyor: {file}";
        }
        UpdateClipButton();
    }
}
