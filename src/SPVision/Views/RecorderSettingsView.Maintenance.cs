using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Xml.Linq;
using SPVision.Controls;
using SPVision.Core;
using SPVision.Hik;

namespace SPVision.Views;

// Settings › Cihaz ayarları › Bakım (phase 3). Opening the tab only reads (ISAPI GET deviceInfo, InputProxy channels +
// status); the recorder's "Denetle" only reads (online upgrade capabilities / server / version); the cameras' "Denetle"
// only reads (the camera table again) and compares versions (CameraFirmwareCheck). Three risky operations, each behind
// RiskyConfirmDialog with a typed word: recorder firmware (GÜNCELLE, SDK NET_DVR_Upgrade_V50), camera firmware
// (GÜNCELLE, SDK NET_DVR_UPGRADE_IPC, one camera after another) and factory reset (SIFIRLA, ISAPI PUT
// factoryReset?mode=basic|full). Nothing is downloaded or installed without the user picking a file and confirming.
public partial class RecorderSettingsView
{
    sealed class MaintenanceData
    {
        public required Read<RecorderFirmware> Recorder { get; init; }
        public required Read<List<CameraFirmware>> Cameras { get; init; }
    }

    static readonly GridLength[] FirmwareColumns =
    [
        new(36), new(56), new(1, GridUnitType.Star), new(150), new(150), new(190), new(130),
    ];

    /// <summary>
    /// The themed CheckBox is a 16 px box plus an 8 px gap before its (empty) content: 10 px from the left centers the
    /// box in the 36 px column, in the header and in every row alike.
    /// </summary>
    static readonly Thickness CheckBoxInset = new(10, 0, 0, 0);

    static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);
    static readonly TimeSpan RecorderNoRestartAfter = TimeSpan.FromMinutes(5);
    static readonly TimeSpan RecorderReturnLimit = TimeSpan.FromMinutes(20);

    static int s_firmwareJobs;

    /// <summary>A firmware upgrade is running somewhere in the app (closing SPVision would cut the upload).</summary>
    public static bool FirmwareBusy => Volatile.Read(ref s_firmwareJobs) > 0;

    MaintenanceData? _maintenance;
    OnlineCheckResult? _onlineCheck;
    DateTimeOffset? _onlineCheckedAt;
    bool _checking;
    bool _checkingCameras;
    CameraCheck? _cameraCheck;
    bool _pickingFile;
    bool _firmwareBusy;
    FirmwareFile? _recorderFile;
    FirmwareFile? _cameraFile;
    readonly HashSet<int> _cameraSelection = [];
    bool _stopOnFirstFailure = true;
    (bool? Ok, string Text)? _recorderResult;
    (bool? Ok, string Text)? _cameraResult;
    FirmwareProgress? _recorderProgress;
    readonly Dictionary<int, CameraUpdate> _cameraUpdates = [];

    // Elements of the current render that change in place (no re-render while the user scrolls or ticks boxes).
    TextBlock? _recorderProgressText;
    Grid? _recorderProgressBar;
    readonly Dictionary<int, Border> _cameraStatusCells = [];
    readonly Dictionary<int, CheckBox> _cameraBoxes = [];
    CheckBox? _cameraAllBox;
    TextBlock? _cameraSummary;
    Button? _cameraUpdateButton;
    Border? _cameraMismatch;
    double _cameraScroll;
    bool _syncingBoxes;

    /// <summary>LoadDevice: forget what was read from the previous device (no job runs: the selector is locked meanwhile).</summary>
    void ForgetMaintenance()
    {
        _maintenance = null;
        _onlineCheck = null;
        _onlineCheckedAt = null;
        _cameraCheck = null;
        _recorderFile = null;
        _cameraFile = null;
        _cameraSelection.Clear();
        _recorderResult = null;
        _cameraResult = null;
        _recorderProgress = null;
        _cameraUpdates.Clear();
        _cameraScroll = 0;
    }

    void RenderMaintenance()
    {
        _recorderProgressText = null;
        _recorderProgressBar = null;
        _cameraStatusCells.Clear();
        _cameraBoxes.Clear();
        _cameraAllBox = null;
        _cameraSummary = null;
        _cameraUpdateButton = null;
        _cameraMismatch = null;
        if (_maintenance is not { } data)
        {
            if (ShowFailed(Tab.Maintenance, () => _maintenance = null))
            {
                return;
            }
            TabContent.Children.Add(LoadingCard());
            Load(Tab.Maintenance, async (device, isapi, ct) =>
            {
                var recorder = await RecorderReader.TryAsync(isapi, MaintenancePaths.DeviceInfo, RecorderFirmware.Parse, ct);
                var cameras = await CameraFirmwareReader.ReadAsync(isapi, ct);
                if (device == _device)
                {
                    _maintenance = new MaintenanceData { Recorder = recorder, Cameras = cameras };
                    _cameraCheck = null; // versions may have changed (e.g. after an upgrade): the YAZILIM column waits for "Denetle"
                    if (cameras.Value is { } list)
                    {
                        _cameraSelection.IntersectWith(list.Where(c => c.Upgradable).Select(c => c.Channel));
                    }
                }
            });
            return;
        }

        TabContent.Children.Add(RecorderInfoCard(data));
        TabContent.Children.Add(RecorderFirmwareCard(data));
        TabContent.Children.Add(CameraFirmwareCard(data));
        TabContent.Children.Add(FactoryResetCard());
    }

    bool MaintenanceIdle => !_firmwareBusy && !_writing && !_checking && !_checkingCameras && !_pickingFile;

    // ================================================================ cards

    Border RecorderInfoCard(MaintenanceData data)
    {
        var info = data.Recorder.Value;
        var panel = new StackPanel();
        panel.Children.Add(CardHeader("Kayıt cihazı bilgisi", "", [], (Geometry)FindResource("IconInfo")));
        panel.Children.Add(InfoRow("Model", ValueText(info?.Model ?? Reason(data.Recorder, RecorderText.Unreadable))));
        panel.Children.Add(InfoRow("Yazılım sürümü", ValueText(info?.Firmware?.Version ?? Reason(data.Recorder, RecorderText.Unreadable))));
        panel.Children.Add(InfoRow("Yazılım tarihi", ValueText(info?.Firmware?.BuildDate is { } date
            ? date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)
            : RecorderText.Unreadable)));
        var web = SettingsButton("Web arayüzünü aç", Web_Click);
        web.ToolTip = "Cihazın kendi ayar sayfasını varsayılan tarayıcıda açar";
        AutomationProperties.SetAutomationId(web, "MaintenanceWebUi");
        panel.Children.Add(InfoRow("Web arayüzü", web, last: true));
        return Card(panel);
    }

    Border RecorderFirmwareCard(MaintenanceData data)
    {
        var info = data.Recorder.Value;
        var check = IconSettingsButton("Denetle", "IconRefresh", (_, _) => CheckOnline());
        check.ToolTip = "Kayıt cihazına Hikvision sunucusunda yeni sürüm olup olmadığını sorar; hiçbir şey indirmez";
        AutomationProperties.SetAutomationId(check, "MaintenanceCheck");
        check.IsEnabled = MaintenanceIdle;
        var pick = SettingsButton("Dosyadan yükle…", (_, _) => PickFirmware(cameras: false));
        pick.ToolTip = "Bilgisayardaki yazılım dosyasını (.dav) seçer; seçmek cihaza bir şey göndermez";
        AutomationProperties.SetAutomationId(pick, "MaintenanceRecorderFile");
        pick.IsEnabled = MaintenanceIdle;
        var update = SettingsButton("Güncelle…", (_, _) => UpdateRecorder(), "SettingsAccentButton");
        AutomationProperties.SetName(update, "Kayıt cihazı yazılımını güncelle");
        AutomationProperties.SetAutomationId(update, "MaintenanceRecorderUpdate");
        update.IsEnabled = MaintenanceIdle && _recorderFile is not null && info is not null;
        update.ToolTip = _recorderFile is null ? "Önce “Dosyadan yükle” ile yazılım dosyasını seçin" : "Seçili dosyayı onayınızdan sonra kayıt cihazına yükler";

        var (badgeText, badgeKind) = _checking
            ? ("Denetleniyor…", StatusKind.Neutral)
            : _onlineCheck switch
            {
                { State: OnlineCheckState.UpToDate } r => (r.Badge, StatusKind.Ok),
                { State: OnlineCheckState.NewVersion } r => (r.Badge, StatusKind.Warning),
                { } r => (r.Badge, StatusKind.Neutral),
                null => ("Denetlenmedi", StatusKind.Neutral),
            };
        var checkedText = _onlineCheckedAt is { } at
            ? "son denetim: " + at.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)
            : "henüz denetlenmedi";
        var description = $"{info?.Model ?? RecorderText.Device} · yüklü sürüm: {info?.Firmware?.Text ?? RecorderText.Unreadable} · {checkedText}";

        var panel = new StackPanel();
        panel.Children.Add(MaintenanceHeader("Kayıt cihazı yazılımı", StatusBadge(badgeText, badgeKind), description, [check, pick, update]));
        panel.Children.Add(CardWarning("“Denetle” kayıt cihazına Hikvision sunucusunda yeni sürüm olup olmadığını sorar (cihazın internet erişimi gerekir); " +
                                       "hiçbir şey indirmez ve kurmaz. Güncelleme sırasında elektriği kesmeyin: canlı görüntü ve kayıt birkaç dakika durur, cihaz yeniden başlar."));
        if (_recorderResult is { } result)
        {
            panel.Children.Add(ResultLine(result.Ok, result.Text));
        }
        if (_onlineCheck is { } online)
        {
            var help = online.Text + (online.ChangeLog is { Length: > 0 } log ? " Değişiklikler: " + log : "");
            UIElement control = online.OffersDownloadPage
                ? HikvisionPageButton()
                : ValueText(online.Badge);
            panel.Children.Add(Row("Çevrimiçi denetim", help, control));
        }
        var file = _recorderFile;
        panel.Children.Add(Row("Yazılım dosyası",
            file is null
                ? "Hikvision sitesinden bu modele ait yazılımı (.dav) indirip “Dosyadan yükle” ile seçin. Dosya yalnızca “Güncelle” ve onayınızla gönderilir."
                : $"{file.SizeText} · SHA-256 {file.Sha256[..16]}… (indirme sayfasındaki özetle karşılaştırabilirsiniz)",
            file is null ? MutedText("Seçilmedi") : ValueText(file.Name),
            last: _recorderProgress is null));
        if (_recorderProgress is { } progress)
        {
            panel.Children.Add(Row("İlerleme", "Cihazın elektriğini kesmeyin, ağ kablosunu çıkarmayın, SPVision'ı kapatmayın.",
                ProgressControl(progress), last: true));
        }
        panel.SetValue(RoundedClip.RadiusProperty, 7.0);
        return Card(panel, new Thickness(0, 16, 0, 0));
    }

    Border CameraFirmwareCard(MaintenanceData data)
    {
        // Same 120×30 icon button, text and position as the recorder card's "Denetle" (first of three buttons).
        var check = IconSettingsButton("Denetle", "IconRefresh", (_, _) => CheckCameras());
        check.ToolTip = "Kameraların modelini, yazılım sürümünü ve durumunu yeniden okur ve daha yeni yazılım olup olmadığına bakar; hiçbir şey indirmez";
        AutomationProperties.SetName(check, "Kamera yazılımlarını denetle");
        AutomationProperties.SetAutomationId(check, "CameraFirmwareCheck");
        check.IsEnabled = MaintenanceIdle;
        var pick = SettingsButton("Dosyadan yükle…", (_, _) => PickFirmware(cameras: true));
        AutomationProperties.SetName(pick, "Kamera yazılım dosyası seç");
        AutomationProperties.SetAutomationId(pick, "MaintenanceCameraFile");
        pick.IsEnabled = MaintenanceIdle;
        _cameraUpdateButton = SettingsButton("Güncelle…", (_, _) => UpdateCameras(), "SettingsAccentButton");
        AutomationProperties.SetAutomationId(_cameraUpdateButton, "MaintenanceCameraUpdate");

        var panel = new StackPanel();
        var (badgeText, badgeKind) = _checkingCameras
            ? ("Denetleniyor…", StatusKind.Neutral)
            : CameraFirmwareCheck.Overall(_cameraCheck) switch
            {
                CameraCheckOverall.UpToDate => (CameraFirmwareCheck.OverallBadge(CameraCheckOverall.UpToDate), StatusKind.Ok),
                CameraCheckOverall.Updates => (CameraFirmwareCheck.OverallBadge(CameraCheckOverall.Updates), StatusKind.Warning),
                var overall => (CameraFirmwareCheck.OverallBadge(overall), StatusKind.Neutral),
            };
        var header = MaintenanceHeader("Kamera yazılımları", StatusBadge(badgeText, badgeKind), "", [check, pick, _cameraUpdateButton], out var summary);
        _cameraSummary = summary;
        AutomationProperties.SetAutomationId(summary, "CameraFirmwareSummary");
        panel.Children.Add(header);
        panel.Children.Add(CardWarning("“Denetle” kamera listesini kayıt cihazından yeniden okur ve sürümleri karşılaştırır; hiçbir şey indirmez ve kurmaz. " +
                                       "Bu kayıt cihazı kameralar için çevrimiçi sürüm denetimi sunmuyor: “Güncelleme var”, aynı modeldeki başka bir kamerada daha yeni sürüm yüklü demektir."));
        if (_cameraResult is { } result)
        {
            panel.Children.Add(ResultLine(result.Ok, result.Text));
        }
        if (_cameraCheck?.Results is { } checkResults && data.Cameras.Value is { } checkedCameras &&
            CameraFirmwareCheck.ModelsToLookUp(checkedCameras, checkResults) is { Count: > 0 } models)
        {
            panel.Children.Add(Row("Çevrimiçi denetim", ModelLinks(models), HikvisionPageButton("CameraFirmwareHikvisionPage")));
        }
        _cameraMismatch = CardWarning("");
        panel.Children.Add(_cameraMismatch);

        var file = _cameraFile;
        panel.Children.Add(Row("Yazılım dosyası",
            file is null
                ? "Hikvision sitesinden kamera modeline ait yazılımı (.dav) indirip “Dosyadan yükle” ile seçin; kameralar kayıt cihazı üzerinden güncellenir."
                : $"{file.SizeText} · SHA-256 {file.Sha256[..16]}…",
            file is null ? MutedText("Seçilmedi") : ValueText(file.Name)));
        var stop = Switch(_stopOnFirstFailure, "İlk hatada dur", v => _stopOnFirstFailure = v);
        AutomationProperties.SetAutomationId(stop, "MaintenanceStopOnFailure");
        stop.IsEnabled = MaintenanceIdle;
        panel.Children.Add(Row("İlk hatada dur",
            "Seçilen kameralar sırayla güncellenir, her biri birkaç dakika görüntüsüz kalır. Açıkken bir kamera güncellenemezse kalanlar başlatılmaz (önerilir).",
            stop, last: true));

        // Table: ☐ · KANAL · KAMERA · MODEL · YÜKLÜ SÜRÜM · YAZILIM · DURUM
        var headerGrid = TableGrid(FirmwareColumns);
        _cameraAllBox = new CheckBox { HorizontalAlignment = HorizontalAlignment.Left, Margin = CheckBoxInset, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(_cameraAllBox, "Tümünü seç");
        AutomationProperties.SetAutomationId(_cameraAllBox, "MaintenanceSelectAll");
        _cameraAllBox.Checked += (_, _) => SelectAllCameras(true);
        _cameraAllBox.Unchecked += (_, _) => SelectAllCameras(false);
        Put(headerGrid, _cameraAllBox, 0);
        var captions = new[] { ("KANAL", false), ("KAMERA", false), ("MODEL", false), ("YÜKLÜ SÜRÜM", false), ("YAZILIM", true), ("DURUM", true) };
        for (var i = 0; i < captions.Length; i++)
        {
            var caption = Text(captions[i].Item1, "CaptionText");
            if (captions[i].Item2)
            {
                caption.HorizontalAlignment = HorizontalAlignment.Center;
            }
            Put(headerGrid, caption, i + 1);
        }
        var tableHeader = new Border { Height = 36, Padding = new Thickness(16, 0, 16, 0), BorderThickness = new Thickness(0, 1, 0, 1), Child = headerGrid };
        tableHeader.SetResourceReference(Border.BackgroundProperty, "SubtleBg");
        tableHeader.SetResourceReference(Border.BorderBrushProperty, "Border");
        panel.Children.Add(tableHeader);

        var rows = new StackPanel();
        if (data.Cameras.Value is { Count: > 0 } cameras)
        {
            for (var i = 0; i < cameras.Count; i++)
            {
                rows.Children.Add(CameraFirmwareRow(cameras[i], i == cameras.Count - 1));
            }
        }
        else
        {
            var grid = TableGrid(FirmwareColumns);
            Put(grid, MutedText(data.Cameras.Ok ? "Kayıt cihazında IP kamera yok" : "Kamera listesi okunamadı", HorizontalAlignment.Left), 1, span: 6);
            rows.Children.Add(TableRow(grid, last: true, height: 36));
            _cameraAllBox.IsEnabled = false;
        }
        var scroller = new ScrollViewer { MaxHeight = 36 * 10, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = rows, Focusable = false };
        scroller.Loaded += (_, _) => scroller.ScrollToVerticalOffset(_cameraScroll);
        scroller.ScrollChanged += (_, e) => _cameraScroll = e.VerticalOffset;
        panel.Children.Add(scroller);
        panel.SetValue(RoundedClip.RadiusProperty, 7.0);

        RefreshCameraSelection();
        return Card(panel, new Thickness(0, 16, 0, 0));
    }

    Border CameraFirmwareRow(CameraFirmware camera, bool last)
    {
        var grid = TableGrid(FirmwareColumns);
        var box = new CheckBox
        {
            IsChecked = _cameraSelection.Contains(camera.Channel),
            IsEnabled = camera.Upgradable && MaintenanceIdle,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = CheckBoxInset,
            ToolTip = camera.Upgradable ? null : camera.Online == true ? "Hikvision protokolüyle bağlı olmayan kamera kayıt cihazı üzerinden güncellenemez" : "Çevrimdışı kamera güncellenemez",
        };
        ToolTipService.SetShowOnDisabled(box, true);
        AutomationProperties.SetName(box, $"{camera.Label} seç");
        AutomationProperties.SetAutomationId(box, string.Create(CultureInfo.InvariantCulture, $"MaintenanceCamera{camera.Channel}"));
        box.Checked += (_, _) => ToggleCamera(camera.Channel, true);
        box.Unchecked += (_, _) => ToggleCamera(camera.Channel, false);
        _cameraBoxes[camera.Channel] = box;
        Put(grid, box, 0);
        Put(grid, ChannelText(camera.Channel), 1);
        Put(grid, NameText(camera.Label), 2);
        var model = Text(camera.Model ?? "—", "CellText");
        model.ToolTip = camera.Model;
        Put(grid, model, 3);
        var version = Text(camera.Firmware?.Text ?? RecorderText.Unreadable, "CellText");
        version.ToolTip = camera.Firmware?.Text;
        Put(grid, version, 4);
        Put(grid, CameraCheckBadge(camera.Channel), 5);
        var status = new Border { HorizontalAlignment = HorizontalAlignment.Center };
        _cameraStatusCells[camera.Channel] = status;
        status.Child = CameraStatusBadge(camera);
        Put(grid, status, 6);
        return TableRow(grid, last, height: 36);
    }

    Border FactoryResetCard()
    {
        var panel = new StackPanel();
        panel.Children.Add(MaintenanceHeader("Fabrika ayarları", null,
            "Kayıt cihazının ayarlarını varsayılana döndürür. İkisi de geri alınamaz; onay için SIFIRLA yazmanız istenir.", [], danger: true));
        var band = DialogChrome.DangerBand(
            "Sıfırlama akış şifrelemeyi ve Hik-Connect'i de cihazda varsayılana döndürür; cihaz yeniden etkinleştirme isteyebilir ve " +
            "SPVision bağlantıyı kaybeder. SPVision bu ayarları sonradan kendisi açmaz veya değiştirmez: gerekirse cihazın web arayüzünden siz ayarlarsınız.",
            new Thickness(0), radius: 0);
        band.Padding = new Thickness(16, 10, 16, 10);
        band.BorderThickness = new Thickness(0, 0, 0, 1);
        band.SetResourceReference(Border.BorderBrushProperty, "Border");
        panel.Children.Add(band);
        if (ResultBanner(Tab.Maintenance) is { } banner)
        {
            panel.Children.Add(banner);
        }
        var basic = SettingsButton("Temel sıfırla…", (_, _) => FactoryReset(FactoryResetMode.Basic), "SettingsDangerButton");
        AutomationProperties.SetAutomationId(basic, "MaintenanceResetBasic");
        basic.IsEnabled = MaintenanceIdle;
        panel.Children.Add(Row(FactoryResetText.Name(FactoryResetMode.Basic), FactoryResetText.Help(FactoryResetMode.Basic), basic));
        var full = SettingsButton("Tam sıfırla…", (_, _) => FactoryReset(FactoryResetMode.Full), "SettingsDangerFillButton");
        AutomationProperties.SetAutomationId(full, "MaintenanceResetFull");
        full.IsEnabled = MaintenanceIdle;
        panel.Children.Add(Row(FactoryResetText.Name(FactoryResetMode.Full), FactoryResetText.Help(FactoryResetMode.Full), full, last: true));
        panel.SetValue(RoundedClip.RadiusProperty, 7.0);
        var card = Card(panel, new Thickness(0, 16, 0, 0));
        card.SetResourceReference(Border.BorderBrushProperty, "Danger");
        return card;
    }

    // ================================================================ small builders

    enum StatusKind
    {
        Ok,
        Warning,
        Danger,
        Neutral,
        Active,
    }

    static Border StatusBadge(string text, StatusKind kind)
    {
        var label = new TextBlock { Text = text };
        label.SetResourceReference(StyleProperty, "BadgeText");
        label.SetResourceReference(TextBlock.ForegroundProperty, kind switch
        {
            StatusKind.Ok => "Online",
            StatusKind.Warning => "WarningText",
            StatusKind.Danger => "DangerText",
            StatusKind.Active => "SelectedFg",
            _ => "TextSecondary",
        });
        var badge = new Border { Child = label, HorizontalAlignment = HorizontalAlignment.Center };
        badge.SetResourceReference(StyleProperty, "Badge");
        badge.SetResourceReference(Border.BackgroundProperty, kind switch
        {
            StatusKind.Ok => "OnlineSoftBg",
            StatusKind.Warning => "WarningSoftBg",
            StatusKind.Danger => "DangerSoftBg",
            StatusKind.Active => "SelectedBg",
            _ => "HoverBg",
        });
        badge.HorizontalAlignment = HorizontalAlignment.Center;
        AutomationProperties.SetName(badge, text);
        return badge;
    }

    Border CameraStatusBadge(CameraFirmware camera)
    {
        if (_cameraUpdates.TryGetValue(camera.Channel, out var update))
        {
            var kind = update.Step switch
            {
                CameraStep.Done => StatusKind.Ok,
                CameraStep.Failed => StatusKind.Danger,
                CameraStep.Uploading or CameraStep.Restarting => StatusKind.Active,
                _ => StatusKind.Neutral,
            };
            var text = update.Step switch
            {
                CameraStep.Waiting => "Sırada",
                CameraStep.Uploading => update.Percent > 0 ? string.Create(CultureInfo.InvariantCulture, $"Yükleniyor %{update.Percent}") : "Yükleniyor",
                CameraStep.Restarting => "Yeniden başlıyor",
                CameraStep.Done => "Güncellendi",
                CameraStep.Failed => "Hata",
                _ => "Başlatılmadı",
            };
            var badge = StatusBadge(text, kind);
            badge.ToolTip = update.Text;
            return badge;
        }
        return camera.Online switch
        {
            true => StatusBadge("Çevrimiçi", StatusKind.Ok),
            false => StatusBadge("Çevrimdışı", StatusKind.Neutral),
            _ => StatusBadge("—", StatusKind.Neutral),
        };
    }

    /// <summary>YAZILIM cell: the last "Denetle" result of this camera ("—" before the first check).</summary>
    Border CameraCheckBadge(int channel)
    {
        var result = _cameraCheck?.For(channel) ?? new CameraCheckResult(channel, CameraCheckState.NotChecked);
        var kind = result.State switch
        {
            CameraCheckState.UpToDate => StatusKind.Ok,
            CameraCheckState.NewVersion => StatusKind.Warning,
            _ => StatusKind.Neutral,
        };
        var badge = StatusBadge(result.Badge, kind);
        badge.ToolTip = result.Detail;
        badge.MaxWidth = FirmwareColumns[5].Value;
        if (badge.Child is TextBlock label)
        {
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            AutomationProperties.SetAutomationId(label, string.Create(CultureInfo.InvariantCulture, $"CameraFirmwareStatus{channel}"));
            AutomationProperties.SetHelpText(label, result.Detail);
        }
        return badge;
    }

    /// <summary>
    /// Help text of the "Çevrimiçi denetim" row with one link per model: copies the model name to the clipboard and opens
    /// Hikvision's firmware page (it has its own search box; SPVision never downloads anything).
    /// </summary>
    TextBlock ModelLinks(IReadOnlyList<string> models)
    {
        var text = Text("Kameralar için çevrimiçi denetim yok; daha yeni yazılımı Hikvision sitesinden kontrol edin. Modele tıklayınca adı panoya kopyalanır ve indirme sayfası açılır: ", "RowHelp");
        for (var i = 0; i < models.Count; i++)
        {
            if (i > 0)
            {
                text.Inlines.Add(new Run(" · "));
            }
            var model = models[i];
            var link = new Hyperlink(new Run(model)) { ToolTip = $"{model}: adı panoya kopyalar ve Hikvision yazılım sayfasını açar" };
            link.SetResourceReference(TextElement.ForegroundProperty, "Accent");
            AutomationProperties.SetName(link, $"{model} yazılımını Hikvision sitesinde ara");
            AutomationProperties.SetAutomationId(link, string.Create(CultureInfo.InvariantCulture, $"CameraFirmwareModelLink{i + 1}"));
            link.Click += (_, _) => LookUpModel(model);
            text.Inlines.Add(link);
        }
        return text;
    }

    void LookUpModel(string model)
    {
        try
        {
            Clipboard.SetText(model);
            Message?.Invoke($"“{model}” panoya kopyalandı; Hikvision sayfasındaki arama kutusuna yapıştırın.");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Clipboard busy: the page still opens.
        }
        OpenBrowser(MaintenancePaths.HikvisionFirmwarePage);
    }

    /// <summary>Header like <see cref="CardHeader"/> with an optional badge after the title and a red title for the danger card.</summary>
    static Border MaintenanceHeader(string title, Border? badge, string description, IReadOnlyList<Button> buttons, bool danger = false) =>
        MaintenanceHeader(title, badge, description, buttons, out _, danger);

    static Border MaintenanceHeader(string title, Border? badge, string description, IReadOnlyList<Button> buttons, out TextBlock help,
        bool danger = false)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        var titleText = Text(title, "CardTitle");
        titleText.VerticalAlignment = VerticalAlignment.Center;
        if (danger)
        {
            titleText.SetResourceReference(TextBlock.ForegroundProperty, "DangerText");
        }
        titleRow.Children.Add(titleText);
        if (badge is not null)
        {
            badge.Margin = new Thickness(8, 0, 0, 0);
            titleRow.Children.Add(badge);
        }
        left.Children.Add(titleRow);
        help = Text(description, "RowHelp");
        help.Visibility = description.Length > 0 || buttons.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        left.Children.Add(help);
        grid.Children.Add(left);
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < buttons.Count; i++)
        {
            buttons[i].Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0);
            right.Children.Add(buttons[i]);
        }
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        var header = new Border { Padding = new Thickness(16, 12, 16, 12), BorderThickness = new Thickness(0, 0, 0, 1), Child = grid };
        header.SetResourceReference(Border.BorderBrushProperty, "Border");
        return header;
    }

    /// <summary>A 120×30 settings button with a 13 px icon before the text (Denetle, Yenile).</summary>
    Button IconSettingsButton(string text, string icon, RoutedEventHandler click)
    {
        var button = SettingsButton(text, click);
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new GlyphIcon { Data = (Geometry)FindResource(icon), Size = 13, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        button.Content = content;
        return button;
    }

    Button HikvisionPageButton(string automationId = "MaintenanceHikvisionPage")
    {
        var button = SettingsButton("Hikvision sitesi", (_, _) => OpenBrowser(MaintenancePaths.HikvisionFirmwarePage));
        button.ToolTip = "Hikvision'ın yazılım indirme sayfasını tarayıcıda açar";
        AutomationProperties.SetName(button, "Hikvision yazılım indirme sayfasını aç");
        AutomationProperties.SetAutomationId(button, automationId);
        return button;
    }

    static Border ResultLine(bool? ok, string text)
    {
        var block = Text(text, "CellText");
        block.TextWrapping = TextWrapping.Wrap;
        block.TextTrimming = TextTrimming.None;
        block.SetResourceReference(TextBlock.ForegroundProperty, ok switch { true => "Online", false => "WarningText", _ => "TextSecondary" });
        var banner = new Border { Padding = new Thickness(16, 10, 16, 10), BorderThickness = new Thickness(0, 0, 0, 1), Child = block };
        banner.SetResourceReference(Border.BorderBrushProperty, "Border");
        banner.SetResourceReference(Border.BackgroundProperty, ok switch { true => "OnlineSoftBg", false => "WarningSoftBg", _ => "SubtleBg" });
        AutomationProperties.SetName(banner, text);
        return banner;
    }

    /// <summary>240 px: 6 px bar + "Dosya gönderiliyor · %45" under it; updated in place by <see cref="UpdateRecorderProgress"/>.</summary>
    FrameworkElement ProgressControl(FirmwareProgress progress)
    {
        var stack = new StackPanel { Width = ControlWidth };
        var bar = new Grid { Height = 6 };
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        var track = new Border { CornerRadius = new CornerRadius(3) };
        track.SetResourceReference(Border.BackgroundProperty, "Divider");
        Grid.SetColumnSpan(track, 2);
        bar.Children.Add(track);
        var fill = new Border { CornerRadius = new CornerRadius(3) };
        fill.SetResourceReference(Border.BackgroundProperty, "Accent");
        bar.Children.Add(fill);
        stack.Children.Add(bar);
        var text = Text("", "CellText");
        text.HorizontalAlignment = HorizontalAlignment.Right;
        text.Margin = new Thickness(0, 6, 0, 0);
        stack.Children.Add(text);
        _recorderProgressBar = bar;
        _recorderProgressText = text;
        UpdateRecorderProgress();
        return stack;
    }

    void UpdateRecorderProgress()
    {
        if (_recorderProgressBar is not { } bar || _recorderProgressText is not { } text || _recorderProgress is not { } progress)
        {
            return;
        }
        var percent = Math.Clamp(progress.Percent, 0, 100);
        bar.ColumnDefinitions[0].Width = new GridLength(percent, GridUnitType.Star);
        bar.ColumnDefinitions[1].Width = new GridLength(100 - percent, GridUnitType.Star);
        text.Text = progress.Text;
        AutomationProperties.SetName(text, progress.Text);
    }

    void OpenBrowser(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Message?.Invoke($"Tarayıcı açılamadı: {ex.Message}");
        }
    }

    // ================================================================ camera selection (in place)

    void ToggleCamera(int channel, bool on)
    {
        if (_syncingBoxes)
        {
            return;
        }
        if (on)
        {
            _cameraSelection.Add(channel);
        }
        else
        {
            _cameraSelection.Remove(channel);
        }
        RefreshCameraSelection();
    }

    void SelectAllCameras(bool on)
    {
        if (_syncingBoxes || _maintenance?.Cameras.Value is not { } cameras)
        {
            return;
        }
        _cameraSelection.Clear();
        if (on)
        {
            _cameraSelection.UnionWith(cameras.Where(c => c.Upgradable).Select(c => c.Channel));
        }
        RefreshCameraSelection();
    }

    /// <summary>Boxes, the "Tümünü seç" box, the summary line, "Güncelle (n)…" and the model warning follow the selection.</summary>
    void RefreshCameraSelection()
    {
        var cameras = _maintenance?.Cameras.Value ?? [];
        var selectable = cameras.Where(c => c.Upgradable).ToList();
        var selected = cameras.Where(c => _cameraSelection.Contains(c.Channel)).ToList();
        _syncingBoxes = true;
        try
        {
            foreach (var (channel, box) in _cameraBoxes)
            {
                box.IsChecked = _cameraSelection.Contains(channel);
            }
            if (_cameraAllBox is { } all)
            {
                all.IsChecked = selectable.Count > 0 && selected.Count == selectable.Count;
                all.IsEnabled = selectable.Count > 0 && MaintenanceIdle;
            }
        }
        finally
        {
            _syncingBoxes = false;
        }
        if (_cameraSummary is { } summary)
        {
            summary.Text = (_checkingCameras ? "Denetleniyor…" : CameraFirmwareCheck.Summary(_cameraCheck, DateTimeOffset.Now)) +
                           (selected.Count == 0 ? "" : string.Create(CultureInfo.InvariantCulture, $" · {selected.Count} kamera seçili"));
            summary.ToolTip = summary.Text;
            AutomationProperties.SetName(summary, summary.Text);
        }
        if (_cameraUpdateButton is { } update)
        {
            var text = selected.Count == 0 ? "Güncelle…" : string.Create(CultureInfo.InvariantCulture, $"Güncelle ({selected.Count})…");
            update.Content = text;
            AutomationProperties.SetName(update, selected.Count == 0 ? "Seçili kameraları güncelle" : $"{selected.Count} kamerayı güncelle");
            update.IsEnabled = MaintenanceIdle && selected.Count > 0 && _cameraFile is not null;
            update.ToolTip = _cameraFile is null ? "Önce “Dosyadan yükle” ile kamera yazılım dosyasını seçin"
                : selected.Count == 0 ? "Önce tablodan kamera seçin" : "Seçili kameraları onayınızdan sonra sırayla günceller";
        }
        if (_cameraMismatch is { } mismatch)
        {
            var models = CameraFirmware.Models(selected);
            mismatch.Visibility = models.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            if (models.Count > 1 && mismatch.Child is Grid { Children: [_, TextBlock message] })
            {
                message.Text = $"Seçili kameralar farklı modellerde ({string.Join(", ", models)}). Bir yazılım dosyası genellikle tek model ailesi içindir; dosyanın seçili kameraların hepsine uyduğundan emin olun.";
                AutomationProperties.SetName(mismatch, message.Text);
            }
        }
    }

    void UpdateCameraStatus(int channel)
    {
        if (_cameraStatusCells.TryGetValue(channel, out var cell) &&
            _maintenance?.Cameras.Value?.FirstOrDefault(c => c.Channel == channel) is { } camera)
        {
            cell.Child = CameraStatusBadge(camera);
        }
    }

    // ================================================================ actions

    void BeginFirmware()
    {
        _firmwareBusy = true;
        Interlocked.Increment(ref s_firmwareJobs);
        UpdateEnabled();
        Render();
    }

    void EndFirmware()
    {
        _firmwareBusy = false;
        Interlocked.Decrement(ref s_firmwareJobs);
        UpdateEnabled();
    }

    async void CheckOnline()
    {
        if (_device is not { } device || !MaintenanceIdle)
        {
            return;
        }
        _checking = true;
        Render();
        OnlineCheckResult result;
        try
        {
            using var isapi = new IsapiClient(device, CheckTimeout);
            result = await OnlineUpgrade.CheckAsync(isapi);
        }
        catch (Exception ex) when (ex is IsapiException or System.Net.Http.HttpRequestException or System.Xml.XmlException)
        {
            result = new OnlineCheckResult(OnlineCheckState.Failed, Error: ex.Message);
        }
        finally
        {
            _checking = false;
        }
        if (device != _device)
        {
            return;
        }
        _onlineCheck = result;
        _onlineCheckedAt = DateTimeOffset.Now;
        Message?.Invoke($"Yazılım denetimi: {result.Badge}");
        if (_tab == Tab.Maintenance)
        {
            Render();
        }
    }

    /// <summary>
    /// Cameras' "Denetle": reads the camera table again (GET InputProxy/channels + /status) and compares versions
    /// (<see cref="CameraFirmwareCheck"/>). Reads only; nothing is downloaded, installed or written.
    /// </summary>
    async void CheckCameras()
    {
        if (_device is not { } device || !MaintenanceIdle)
        {
            return;
        }
        _checkingCameras = true;
        Render();
        Read<List<CameraFirmware>> cameras;
        try
        {
            using var isapi = new IsapiClient(device, CheckTimeout);
            cameras = await CameraFirmwareReader.ReadAsync(isapi);
        }
        catch (Exception ex) when (ex is IsapiException or System.Net.Http.HttpRequestException or System.Xml.XmlException)
        {
            cameras = new Read<List<CameraFirmware>>(ReadState.Failed, null, ex.Message);
        }
        finally
        {
            _checkingCameras = false;
        }
        if (device != _device)
        {
            return;
        }
        var at = DateTimeOffset.Now;
        if (_maintenance is { } data)
        {
            _maintenance = new MaintenanceData { Recorder = data.Recorder, Cameras = cameras };
        }
        if (cameras.Value is { } list)
        {
            _cameraSelection.IntersectWith(list.Where(c => c.Upgradable).Select(c => c.Channel));
            _cameraCheck = new CameraCheck(at, CameraFirmwareCheck.Evaluate(list));
        }
        else
        {
            _cameraSelection.Clear();
            _cameraCheck = new CameraCheck(at, null, cameras.Error);
        }
        Message?.Invoke($"Kamera yazılımı denetimi: {CameraFirmwareCheck.Summary(_cameraCheck, at)}");
        if (_tab == Tab.Maintenance)
        {
            Render();
        }
    }

    async void PickFirmware(bool cameras)
    {
        if (_device is not { } device || !MaintenanceIdle)
        {
            return;
        }
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = cameras ? "Kamera yazılım dosyasını seçin" : "Kayıt cihazı yazılım dosyasını seçin",
            Filter = "Hikvision yazılım dosyası (*.dav;*.digicap)|*.dav;*.digicap|Tüm dosyalar (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }
        var path = dialog.FileName;
        _pickingFile = true;
        SetFileResult(cameras, (null, "Dosya denetleniyor (boyut ve SHA-256)…"));
        Render();
        try
        {
            var file = await Task.Run(() => FirmwareFile.OpenAsync(path));
            if (cameras)
            {
                _cameraFile = file;
            }
            else
            {
                _recorderFile = file;
            }
            SetFileResult(cameras, null);
        }
        catch (FirmwareFileException ex)
        {
            if (cameras)
            {
                _cameraFile = null;
            }
            else
            {
                _recorderFile = null;
            }
            SetFileResult(cameras, (false, ex.Message));
        }
        finally
        {
            _pickingFile = false;
        }
        if (device == _device && _tab == Tab.Maintenance)
        {
            Render();
        }
    }

    void SetFileResult(bool cameras, (bool? Ok, string Text)? result)
    {
        if (cameras)
        {
            _cameraResult = result;
        }
        else
        {
            _recorderResult = result;
        }
    }

    async void UpdateRecorder()
    {
        if (_device is not { } device || !MaintenanceIdle || _recorderFile is not { } file || _maintenance?.Recorder.Value is not { } current)
        {
            return;
        }
        RiskyWriteRequest request;
        try
        {
            request = MaintenanceWrites.RecorderFirmware(device.Name, current, file);
        }
        catch (IsapiWriteDeniedException ex)
        {
            _recorderResult = (false, ex.Message);
            Render();
            return;
        }
        var token = ConfirmedRiskyWrite.Confirm(request, new RiskyConfirmDialog(Window.GetWindow(this)));
        if (token is null)
        {
            return;
        }

        _recorderResult = (null, "Dosya yeniden denetleniyor…");
        _recorderProgress = new FirmwareProgress(0, "Başlatılıyor");
        BeginFirmware();
        try
        {
            var job = await Task.Run(() => token.TakeFirmwareJobAsync());
            _recorderResult = (null, "Yazılım kayıt cihazına gönderiliyor. Elektriği kesmeyin, SPVision'ı kapatmayın.");
            Render();
            var progress = new Progress<FirmwareProgress>(p =>
            {
                _recorderProgress = p;
                UpdateRecorderProgress();
            });
            var run = await RecorderFirmwareRun.RunAsync(job, new HikRecorderFirmware(device), progress);
            if (run.Outcome != FirmwareOutcome.Done)
            {
                _recorderResult = (run.Outcome == FirmwareOutcome.Failed ? false : null, run.Text);
            }
            else
            {
                _recorderResult = (null, "Yazılım yüklendi; cihazın yeniden başlaması bekleniyor (birkaç dakika). Elektriği kesmeyin.");
                _recorderProgress = new FirmwareProgress(100, "Cihaz yeniden başlıyor…");
                Render();
                var elapsed = new Progress<TimeSpan>(t =>
                {
                    _recorderProgress = new FirmwareProgress(100, string.Create(CultureInfo.InvariantCulture,
                        $"Cihaz yeniden başlıyor… {(int)t.TotalMinutes} dk {t.Seconds:00} sn"));
                    UpdateRecorderProgress();
                });
                var (outcome, version) = await FirmwareReturn.WaitAsync(ct => ReadRecorderVersionAsync(device, ct), current.Firmware?.Key,
                    RecorderNoRestartAfter, RecorderReturnLimit, elapsed);
                _recorderResult = outcome switch
                {
                    ReturnOutcome.NewVersion => (true, $"Güncellendi. Kayıt cihazının yeni yazılımı: {version}. Kameralar kendiliğinden yeniden bağlanır."),
                    ReturnOutcome.SameVersion => (false, "Cihaz yeniden başladı ama yazılım sürümü değişmedi. Dosyanın bu model için olduğunu kontrol edin."),
                    ReturnOutcome.NoRestart => (null, "Yazılım yüklendi ama cihaz kendiliğinden yeniden başlamadı; yeni yazılım cihaz yeniden başlatılınca etkinleşir (Sistem › Yeniden başlat)."),
                    _ => (null, "Cihaza 20 dakikadır ulaşılamıyor. Elektriği KESMEYİN; birkaç dakika daha bekleyip bu sayfayı yeniden açın."),
                };
            }
        }
        catch (FirmwareFileException ex)
        {
            _recorderResult = (false, ex.Message);
        }
        catch (IsapiWriteDeniedException ex)
        {
            _recorderResult = (false, ex.Message);
        }
        catch (HikException ex)
        {
            _recorderResult = (false, $"Güncelleme başlatılamadı: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _recorderResult = (null, $"Beklenmeyen hata: {ex.Message} Cihazın elektriğini kesmeyin; birkaç dakika sonra sürümü yeniden okuyun.");
        }
        finally
        {
            _recorderProgress = null;
            EndFirmware();
        }
        Message?.Invoke($"Kayıt cihazı yazılımı: {_recorderResult?.Text}");
        if (device == _device)
        {
            _maintenance = null; // read the version again
            _overview = null;
            _system = null;
            Render();
        }
    }

    static async Task<string?> ReadRecorderVersionAsync(Device device, CancellationToken ct)
    {
        try
        {
            using var isapi = new IsapiClient(device, TimeSpan.FromSeconds(8));
            return RecorderFirmware.Parse(await isapi.GetConfigAsync(MaintenancePaths.DeviceInfo, ct)).Firmware?.Key;
        }
        catch (Exception e) when (e is IsapiException or System.Xml.XmlException)
        {
            return null;
        }
    }

    async void UpdateCameras()
    {
        if (_device is not { } device || !MaintenanceIdle || _cameraFile is not { } file || _maintenance?.Cameras.Value is not { } cameras)
        {
            return;
        }
        var selected = cameras.Where(c => _cameraSelection.Contains(c.Channel)).ToList();
        if (selected.Count == 0)
        {
            return;
        }
        var stopOnFirstFailure = _stopOnFirstFailure;
        RiskyWriteRequest request;
        try
        {
            request = MaintenanceWrites.CameraFirmware(selected, file, stopOnFirstFailure);
        }
        catch (IsapiWriteDeniedException ex)
        {
            _cameraResult = (false, ex.Message);
            Render();
            return;
        }
        var token = ConfirmedRiskyWrite.Confirm(request, new RiskyConfirmDialog(Window.GetWindow(this)));
        if (token is null)
        {
            return;
        }

        _cameraUpdates.Clear();
        _cameraResult = (null, "Dosya yeniden denetleniyor…");
        BeginFirmware();
        try
        {
            var job = await Task.Run(() => token.TakeFirmwareJobAsync());
            _cameraResult = (null, "Kameralar sırayla güncelleniyor. Elektriği kesmeyin, SPVision'ı kapatmayın.");
            Render();
            var progress = new Progress<CameraUpdate>(u =>
            {
                _cameraUpdates[u.Channel] = u;
                UpdateCameraStatus(u.Channel);
            });
            var old = selected.ToDictionary(c => c.Channel, c => c.Firmware?.Key);
            var results = await CameraFirmwareBatch.RunAsync(job, old, new HikCameraFirmware(device), stopOnFirstFailure, progress);
            foreach (var r in results)
            {
                _cameraUpdates[r.Channel] = r;
            }
            var done = results.Count(r => r.Step == CameraStep.Done);
            var failed = results.Where(r => r.Step == CameraStep.Failed).ToList();
            var skipped = results.Count(r => r.Step == CameraStep.Skipped);
            _cameraResult = failed.Count == 0 && skipped == 0
                ? (true, done == 1 ? "Kamera güncellendi." : string.Create(CultureInfo.InvariantCulture, $"{done} kamera güncellendi."))
                : (false, string.Create(CultureInfo.InvariantCulture, $"{done} kamera güncellendi · {failed.Count} başarısız · {skipped} başlatılmadı. ") +
                          string.Join(" ", failed.Select(f => string.Create(CultureInfo.InvariantCulture, $"Kanal {f.Channel}: {f.Text}."))));
        }
        catch (FirmwareFileException ex)
        {
            _cameraResult = (false, ex.Message);
        }
        catch (IsapiWriteDeniedException ex)
        {
            _cameraResult = (false, ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _cameraResult = (null, $"Beklenmeyen hata: {ex.Message} Kameraların durumunu “Denetle” ile yeniden okuyun.");
        }
        finally
        {
            EndFirmware();
        }
        Message?.Invoke($"Kamera yazılımı: {_cameraResult?.Text}");
        if (device == _device)
        {
            _maintenance = null; // read versions and online states again; the per-camera results stay in the table
            Render();
        }
    }

    async void FactoryReset(FactoryResetMode mode)
    {
        if (_device is not { } device || !MaintenanceIdle)
        {
            return;
        }
        var (outcome, text) = await RunRiskyAsync(Tab.Maintenance, device, MaintenanceWrites.FactoryReset(device.Name, mode));
        if (outcome == RiskyOutcome.Cancelled)
        {
            return;
        }
        _results[Tab.Maintenance] = outcome switch
        {
            RiskyOutcome.Done => (true, mode == FactoryResetMode.Basic
                ? "Temel sıfırlama komutu cihaza ulaştı; cihaz yeniden başlıyor. Akış şifreleme ve Hik-Connect varsayılana döndü: gerekiyorsa cihazın web arayüzünden yeniden açın. Cihaz yeniden etkinleştirme isterse bunu web arayüzünden ya da Hikvision SADP aracıyla yapın."
                : "Tam sıfırlama komutu cihaza ulaştı; cihaz yeniden başlıyor ve etkin olmayan duruma geçecek, IP adresi fabrika değerine döner. Cihazı Hikvision SADP aracıyla ya da web arayüzünden yeniden etkinleştirin, sonra Ayarlar › Cihaz yönetimi'nden adresi ve şifreyi güncelleyin."),
            RiskyOutcome.NoAnswer => (null, $"Cihaz yanıt vermedi; sıfırlama başlamış olabilir. Birkaç dakika sonra cihaza erişimi kontrol edin. ({text})"),
            _ => (false, $"Sıfırlanamadı: {text}"),
        };
        Message?.Invoke(outcome == RiskyOutcome.Done ? $"Fabrika ayarlarına sıfırlanıyor: {device.Name}" : $"Fabrika ayarları: {text}");
        if (device == _device)
        {
            Render();
        }
    }
}
