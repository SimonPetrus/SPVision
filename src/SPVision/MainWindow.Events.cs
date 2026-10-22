using System.Media;
using System.Windows;
using SPVision.Controls;
using SPVision.Core;
using SPVision.Views;

namespace SPVision;

/// <summary>
/// Event notifications ("Olay bildirimleri"): the bell of the top bar with its unread count, the OLAYLAR panel and the
/// pop-ups. Sources, all read only: the recorders' alert streams (<see cref="AlertStreamMonitor"/>), live tiles that lose a
/// playing stream (<see cref="TileDropWatch"/>) and the disk check going from healthy to a problem (<see cref="DiskEventWatch"/>).
/// Core <see cref="EventCenter"/> merges, throttles and keeps the last 200. While the app is locked no pop-up shows and no
/// sound plays (open ones close); events still reach the list and the count, and missed pop-ups are not shown afterwards.
/// </summary>
public partial class MainWindow
{
    static readonly TimeSpan PlaybackLead = TimeSpan.FromSeconds(10);

    EventCenter _events = null!;
    AlertStreamMonitor _alerts = null!;
    readonly TileDropWatch _tileDrops = new();
    readonly DiskEventWatch _diskEvents = new();
    readonly EventToasts _toasts = new();
    bool _eventsOpen;

    EventNotificationSettings Notifications => App.Settings.Notifications;

    /// <summary>Constructor: everything is wired, nothing connects yet (<see cref="StartEvents"/>).</summary>
    void InitEvents()
    {
        _events = new EventCenter(Notifications);
        _alerts = new AlertStreamMonitor(Dispatcher);
        _alerts.Alert += OnAlert;
        _disks.DisksRead += OnDisksRead;
        EventsPanel.Attach(_events);
        EventsPanel.LiveRequested += ShowEventLive;
        EventsPanel.PlaybackRequested += ShowEventPlayback;
        EventsPanel.CloseRequested += () => SetEventsPanel(false);
        EventsPanel.ReadChanged += UpdateBell;
        EventsPanel.SettingsChanged += EventSettingsChanged;
        _toasts.LiveRequested += ShowEventLive;
        _toasts.PlaybackRequested += ShowEventPlayback;
        AppLock.LockedChanged += () =>
        {
            if (AppLock.IsLocked)
            {
                _toasts.CloseAll(); // hidden while locked; not shown again after the PIN (the bell keeps the count)
            }
        };
        UpdateBell();
    }

    /// <summary>App started (after the PIN if the lock held it): the alert streams connect, if any kind is switched on.</summary>
    void StartEvents()
    {
        if (Notifications.AnyKind)
        {
            _alerts.Start(App.Settings.Devices);
        }
    }

    /// <summary>Closing: streams end, pop-ups go.</summary>
    void StopEvents()
    {
        _alerts.Stop();
        _toasts.CloseAll();
    }

    void EventsDeviceSaved(Device device)
    {
        if (Notifications.AnyKind && !_startHeld)
        {
            _alerts.Restart(device); // address or login may differ: one new stream after the old one closed
        }
    }

    void EventsDeviceRemoved(Guid deviceId)
    {
        _alerts.Remove(deviceId);
        _diskEvents.Forget(deviceId);
        _events.ForgetDevice(deviceId);
        EventsPanel.Invalidate();
        UpdateBell();
    }

    /// <summary>A switch of the panel's settings changed (saved already): no kind on = no stream to any recorder.</summary>
    void EventSettingsChanged()
    {
        if (!Notifications.AnyKind)
        {
            _alerts.Clear();
        }
        else if (!_startHeld)
        {
            _alerts.Start(App.Settings.Devices);
        }
    }

    // ---- Sources ----

    void OnAlert(Device device, AlertEvent alert)
    {
        if (!App.Settings.Devices.Contains(device) || AlertMapping.Candidate(device, alert, DateTime.Now) is not { } mapped)
        {
            return;
        }
        Record(mapped.Candidate, mapped.KnownCamera, device);
    }

    void OnDisksRead(Device device, IReadOnlyList<DiskProblem> problems)
    {
        var news = _diskEvents.Update(device.Id, problems);
        if (news.Count > 0)
        {
            Record(new EventCandidate(AlertKind.Disk, device.Id, device.Name, null, null, EventText.DiskTitle(news), DateTime.Now,
                EventSource.DiskCheck), knownCamera: false, device);
        }
    }

    /// <summary>Live wall changed (coalesced): a tile that was playing and now waits to reconnect is a lost camera.</summary>
    void CheckTileDrops()
    {
        var tiles = Live.VisibleTiles.Where(t => t.Source is not null).Select(t => (t.Source!, Condition(t))).ToList();
        foreach (var source in _tileDrops.Update(tiles))
        {
            if (App.Settings.Devices.FirstOrDefault(d => d.Id == source.DeviceId) is not { } device ||
                device.Cameras.FirstOrDefault(c => c.Channel == source.Channel) is not { } camera)
            {
                continue;
            }
            Record(new EventCandidate(AlertKind.CameraOffline, device.Id, device.Name, camera.Channel, camera.Name, EventText.Offline,
                DateTime.Now, EventSource.LiveTile), knownCamera: true, device);
        }
    }

    static TileDropWatch.TileCondition Condition(VideoTile tile) => tile.State switch
    {
        TileState.Playing => TileDropWatch.TileCondition.Playing,
        TileState.Failed when tile.IsReconnecting => TileDropWatch.TileCondition.Lost,
        TileState.Connecting => TileDropWatch.TileCondition.Connecting,
        _ => TileDropWatch.TileCondition.Other,
    };

    void Record(EventCandidate candidate, bool knownCamera, Device device)
    {
        var outcome = _events.Add(candidate, DateTime.Now, knownCamera);
        if (outcome is EventOutcome.Ignored or EventOutcome.Merged)
        {
            return;
        }
        EventsPanel.Invalidate();
        UpdateBell();
        if (outcome != EventOutcome.Notify || AppLock.IsLocked || _closeStarted || _events.Last is not { } row)
        {
            return;
        }
        if (Notifications.Popup)
        {
            _toasts.Show(row, device);
        }
        if (Notifications.Sound)
        {
            SystemSounds.Exclamation.Play();
        }
    }

    // ---- Bell and panel ----

    void EventsButton_Click(object sender, RoutedEventArgs e) => SetEventsPanel(EventsButton.IsChecked == true);

    void SetEventsPanel(bool open)
    {
        _eventsOpen = open;
        EventsButton.IsChecked = open;
        if (open)
        {
            EventsPanel.ShowList();
        }
        ApplyEventsPanel();
    }

    /// <summary>Shown when opened and not in full screen (the top bar with the bell is hidden there).</summary>
    void ApplyEventsPanel() =>
        EventsPanel.Visibility = _eventsOpen && !_fullScreen ? Visibility.Visible : Visibility.Collapsed;

    void UpdateBell()
    {
        var unread = _events.Unread;
        EventsBadgeText.Text = EventText.Badge(unread);
        EventsBadge.Visibility = unread > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationPropertiesName(EventsButton, unread > 0 ? $"Olaylar, {unread} okunmamış" : "Olaylar");
        EventsButton.ToolTip = unread > 0 ? $"Olaylar · {unread} okunmamış" : "Olaylar";
    }

    // ---- "Canlı izle" / "Kaydı oynat" (panel rows and pop-ups) ----

    /// <summary>The window comes forward (restored if minimized) and the camera plays live, alone (its tile maximized).</summary>
    void ShowEventLive(DeviceEvent e)
    {
        if (!PrepareEventAction(e, out var camera))
        {
            return;
        }
        SelectSection(MainSection.Live);
        Live.ShowCameraAlone(camera);
    }

    /// <summary>Kayıt Oynatma on the camera, from 10 s before the event (the recorder's own time for its events).</summary>
    void ShowEventPlayback(DeviceEvent e)
    {
        if (!PrepareEventAction(e, out var camera))
        {
            return;
        }
        Playback.ShowEventRecording(camera, e.Time - PlaybackLead);
        SelectSection(MainSection.Playback);
    }

    bool PrepareEventAction(DeviceEvent e, out CameraRef camera)
    {
        camera = e.Camera!;
        e.Read = true;
        EventsPanel.Invalidate();
        UpdateBell();
        if (!BringToFront() || _lock is not null || _firstRun is not null || e.Camera is null)
        {
            return false;
        }
        var source = e.Camera;
        if (App.Settings.Devices.FirstOrDefault(d => d.Id == source.DeviceId)?.Cameras.Any(c => c.Channel == source.Channel) != true)
        {
            ShowMessage("Bu kamera artık kayıtlı değil.");
            return false;
        }
        return true;
    }
}
