using System.Windows.Threading;
using SPVision.Core;
using SPVision.Core.Updates;

namespace SPVision.Updates;

enum UpdatePhase
{
    /// <summary>Not checked yet in this run.</summary>
    Idle,

    /// <summary>Placeholder key or proxy address: the updater never runs (<see cref="UpdateService.DisabledReason"/>).</summary>
    Disabled,

    Checking,

    UpToDate,

    /// <summary><see cref="UpdateController.Kind"/> is Optional or Mandatory, <see cref="UpdateController.Offer"/> is set.</summary>
    Available,

    Downloading,

    /// <summary>The installer started; the app is closing.</summary>
    Installing,

    /// <summary>The last check failed (offline…); <see cref="UpdateController.Status"/> says why. Never blocks the app.</summary>
    Failed,
}

/// <summary>
/// App-wide state of the in-app updater (UI thread only). The main window shows the optional banner and the mandatory
/// dialog, Settings › Uygulama ayarları › Güncellemeler shows the same state. Nothing is installed without the user
/// pressing Güncelle — also for a mandatory update, which only blocks using the app.
/// Rules (Core <see cref="UpdatePolicy"/>): automatic check 15 s after startup and then hourly, but at most every 6 h;
/// a failed check never blocks (fail-open), except that a mandatory manifest verified earlier stays enforced.
/// </summary>
static class UpdateController
{
    static readonly DispatcherTimer StartupTimer = new() { Interval = UpdatePolicy.StartupDelay };
    static readonly DispatcherTimer HourlyTimer = new() { Interval = TimeSpan.FromHours(1) };
    static CancellationTokenSource? _download;
    static VerifiedInstaller? _ready;
    static AppVersion? _dismissed;
    static bool _started;

    /// <summary>Anything below changed.</summary>
    public static event Action? Changed;

    /// <summary>A mandatory update must be shown (the main window opens the blocking dialog).</summary>
    public static event Action? MandatoryRequired;

    /// <summary>Short feedback for the status bar.</summary>
    public static event Action<string>? Message;

    public static UpdatePhase Phase { get; private set; } = UpdatePhase.Idle;

    public static UpdateKind Kind { get; private set; }

    /// <summary>The release to install when <see cref="Phase"/> is Available / Downloading.</summary>
    public static UpdateManifest? Offer { get; private set; }

    /// <summary>One Turkish line for the settings card and the dialog.</summary>
    public static string Status { get; private set; } = "Henüz denetlenmedi";

    /// <summary>
    /// What Güncelle is doing or how it ended (download progress, "Güncelleme iptal edildi", a download error, "Kurulum
    /// başlatılıyor…"); null when nothing was pressed since the last check result.
    /// </summary>
    public static string? Activity { get; private set; }

    /// <summary><see cref="Activity"/> is an error (shown in the danger colour).</summary>
    public static bool ActivityFailed { get; private set; }

    public static (long Received, long Total) Progress { get; private set; }

    public static AppVersion CurrentVersion => UpdateService.CurrentVersion;

    public static DateTimeOffset? LastCheckUtc => App.Settings.Update?.LastCheckUtc;

    public static bool IsBusy => Phase is UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.Installing;

    /// <summary>The optional banner: an optional offer that was not put off with "Sonra" in this run.</summary>
    public static bool ShowBanner =>
        Kind == UpdateKind.Optional && Offer is { } offer && offer.Version != _dismissed &&
        Phase is UpdatePhase.Available or UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.Installing or UpdatePhase.Failed;

    /// <summary>Main window loaded: remembered mandatory update at once, the automatic check after 15 s, then hourly.</summary>
    public static void Start()
    {
        if (_started)
        {
            return;
        }
        _started = true;
        UpdateService.CleanupOldDownloads();
        if (UpdateService.DisabledReason is { } reason)
        {
            Phase = UpdatePhase.Disabled;
            Status = reason;
            Changed?.Invoke();
            return;
        }
        // No network yet: the last verified result (and a remembered mandatory update) apply right away.
        var latest = RememberedLatest();
        Apply(UpdatePolicy.Resolve(CurrentVersion, latest?.Manifest, RememberedMandatory()?.Manifest), latest, null, silent: true);
        StartupTimer.Tick += (_, _) =>
        {
            StartupTimer.Stop();
            AutoCheck();
        };
        HourlyTimer.Tick += (_, _) => AutoCheck();
        StartupTimer.Start();
        HourlyTimer.Start();
    }

    static void AutoCheck()
    {
        if (!IsBusy && Phase != UpdatePhase.Disabled && UpdatePolicy.IsCheckDue(LastCheckUtc, DateTimeOffset.UtcNow))
        {
            _ = CheckAsync(manual: false);
        }
    }

    /// <summary>"Denetle" in Settings: checks now, whatever the 6 h rule says.</summary>
    public static Task CheckNowAsync() => CheckAsync(manual: true);

    static async Task CheckAsync(bool manual)
    {
        if (IsBusy)
        {
            return;
        }
        if (UpdateService.DisabledReason is { } reason)
        {
            Phase = UpdatePhase.Disabled;
            Status = reason;
            Changed?.Invoke();
            if (manual)
            {
                Message?.Invoke(reason);
            }
            return;
        }
        Phase = UpdatePhase.Checking;
        Status = "Denetleniyor…";
        Changed?.Invoke();

        VerifiedManifest? source;
        string? error = null;
        var state = App.Settings.Update ??= new UpdateState();
        try
        {
            source = await UpdateService.FetchManifestAsync(CancellationToken.None); // null: no release published yet
            state.LastCheckUtc = DateTimeOffset.UtcNow;
            state.LatestManifest = source is null ? null : Convert.ToBase64String(source.Bytes);
            state.LatestSignature = source is null ? null : Convert.ToBase64String(source.Signature);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Never leaves the updater stuck in "Denetleniyor…"; fail-open: what the last successful check found.
            error = ex is UpdateException ? ex.Message : "beklenmeyen hata (" + ex.GetType().Name + ")";
            source = RememberedLatest();
        }
        Apply(UpdatePolicy.Resolve(CurrentVersion, source?.Manifest, RememberedMandatory()?.Manifest), source, error, silent: !manual);
    }

    /// <summary>
    /// Shows a resolution. <paramref name="source"/> is the verified manifest it was computed from (to remember it when it
    /// made the update mandatory); <paramref name="error"/> is set when the check failed.
    /// </summary>
    static void Apply(UpdateResolution resolution, VerifiedManifest? source, string? error, bool silent)
    {
        var checkFailed = error is not null;
        // Remember (or forget) the mandatory manifest with its signature; it is verified again whenever it is used.
        var state = App.Settings.Update ??= new UpdateState();
        if (resolution.KeepMandatory is null)
        {
            state.MandatoryManifest = state.MandatorySignature = null;
        }
        else if (source is not null && ReferenceEquals(resolution.KeepMandatory, source.Manifest))
        {
            state.MandatoryManifest = Convert.ToBase64String(source.Bytes);
            state.MandatorySignature = Convert.ToBase64String(source.Signature);
        }
        TrySave();

        if (_ready is not null && (resolution.Target is null || _ready.Manifest.Version != resolution.Target.Version))
        {
            _ready.Dispose(); // a newer release replaced the downloaded one
            _ready = null;
        }
        Kind = resolution.Kind;
        Offer = resolution.Target;
        Activity = null;
        ActivityFailed = false;
        if (Kind != UpdateKind.None)
        {
            Phase = UpdatePhase.Available;
            Status = Kind == UpdateKind.Mandatory
                ? $"Zorunlu güncelleme: SPVision {Offer!.Version}"
                : $"Yeni sürüm var: {Offer!.Version}";
        }
        else if (checkFailed)
        {
            Phase = UpdatePhase.Failed;
            Status = "Denetlenemedi: " + error;
        }
        else
        {
            Phase = LastCheckUtc is null ? UpdatePhase.Idle : UpdatePhase.UpToDate;
            Status = Phase == UpdatePhase.UpToDate ? "SPVision güncel" : "Henüz denetlenmedi";
        }
        Changed?.Invoke();
        if (!silent)
        {
            Message?.Invoke(Status);
        }
        if (Kind == UpdateKind.Mandatory)
        {
            MandatoryRequired?.Invoke();
        }
    }

    /// <summary>"Sonra": hides the banner for this version until the next start.</summary>
    public static void Dismiss()
    {
        if (Offer is { } offer && Kind == UpdateKind.Optional)
        {
            _dismissed = offer.Version;
            Changed?.Invoke();
        }
    }

    public static void CancelDownload() => _download?.Cancel();

    /// <summary>
    /// Güncelle: downloads and verifies the offered installer (unless already done), then starts it elevated. True when
    /// the installer started; the caller then closes the app through the normal shutdown path (MainWindow.OnClosing).
    /// UAC declined → "Güncelleme iptal edildi", the app goes on.
    /// </summary>
    public static async Task<bool> InstallAsync()
    {
        if (Offer is not { } offer || IsBusy)
        {
            return false;
        }
        if (Views.RecorderSettingsView.FirmwareBusy)
        {
            Message?.Invoke("Kayıt cihazı yazılım güncellemesi sürüyor; SPVision o bittikten sonra güncellenebilir.");
            return false;
        }
        if (_ready is null || _ready.Manifest.Version != offer.Version)
        {
            _ready?.Dispose();
            _ready = null;
            _download = new CancellationTokenSource();
            Phase = UpdatePhase.Downloading;
            Progress = (0, offer.Size);
            Activity = UpdateText.Progress(0, offer.Size);
            ActivityFailed = false;
            Changed?.Invoke();
            try
            {
                _ready = await UpdateService.DownloadAsync(offer, new Progress<(long Received, long Total)>(p =>
                {
                    if (Phase != UpdatePhase.Downloading)
                    {
                        return;
                    }
                    Progress = p;
                    Activity = UpdateText.Progress(p.Received, p.Total);
                    Changed?.Invoke();
                }), _download.Token);
            }
            catch (OperationCanceledException)
            {
                BackToOffer("İndirme iptal edildi", failed: false);
                return false;
            }
            catch (UpdateException ex)
            {
                BackToOffer(ex.Message, failed: true);
                return false;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                BackToOffer("Kurulum dosyası indirilemedi: beklenmeyen hata (" + ex.GetType().Name + ").", failed: true);
                return false;
            }
            finally
            {
                _download.Dispose();
                _download = null;
            }
        }

        Phase = UpdatePhase.Installing; // busy: no second Güncelle / Denetle while the UAC prompt is up
        Activity = "Kurulum başlatılıyor… (yönetici izni istenecek)";
        ActivityFailed = false;
        Changed?.Invoke();
        var (result, error) = UpdateService.Launch(_ready);
        switch (result)
        {
            case LaunchResult.Started:
                _ready = null;
                Activity = "Kurulum başladı; SPVision kapanıyor ve güncellendikten sonra yeniden açılacak.";
                Changed?.Invoke();
                App.LogShutdown($"Güncelleme kurulumu başlatıldı: {CurrentVersion} → {offer.Version}");
                return true;
            case LaunchResult.Declined:
                BackToOffer(UpdateText.Cancelled, failed: false);
                return false;
            default:
                _ready.Dispose();
                _ready = null;
                BackToOffer(error ?? "Kurulum başlatılamadı.", failed: true);
                return false;
        }
    }

    /// <summary>
    /// After a cancel or a failed download/start: the offer stays (Güncelle can be pressed again; a verified download is
    /// kept for it), <paramref name="message"/> shows as <see cref="Activity"/> and in the status bar.
    /// </summary>
    static void BackToOffer(string message, bool failed)
    {
        Phase = UpdatePhase.Available;
        Activity = message;
        ActivityFailed = failed;
        Changed?.Invoke();
        Message?.Invoke(message);
    }

    static VerifiedManifest? RememberedLatest() =>
        App.Settings.Update is { } state ? UpdateService.Restore(state.LatestManifest, state.LatestSignature) : null;

    static VerifiedManifest? RememberedMandatory() =>
        App.Settings.Update is { } state ? UpdateService.Restore(state.MandatoryManifest, state.MandatorySignature) : null;

    static void TrySave()
    {
        try
        {
            App.SaveSettings();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            // Not fatal: the next check runs again.
        }
    }
}
