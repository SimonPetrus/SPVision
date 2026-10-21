using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SPVision.Core;
using SPVision.Views;
using Pill = SPVision.Core.DownloadPill;

namespace SPVision.Controls;

/// <summary>
/// The download pill in the middle of the main window's top bar (approved design): while a "Video indir" batch runs, the
/// camera, "kamera i / n", NN%, "kalan m:ss", a 4 px bar and İptal; when it ends, a green (or amber) result with "Klasörü
/// aç" that disappears after 15 s — the time stands still while the mouse is over it and starts again (15 s) when it
/// leaves; "Klasörü aç" opens Explorer and hides it at once. The main window sets its width (<see cref="Core.DownloadPill.Width"/>)
/// and shows it on every page.
/// </summary>
public partial class DownloadStatusPill : UserControl
{
    enum PillState
    {
        Hidden,
        Running,
        Result,
    }

    readonly DispatcherTimer _hideTimer = new() { Interval = Pill.ResultTimeout };
    PillState _state;
    double _width;
    IReadOnlyList<string> _files = [];
    string _folder = "";

    public DownloadStatusPill()
    {
        InitializeComponent();
        _hideTimer.Tick += (_, _) => Hide();
        MouseEnter += (_, _) => _hideTimer.Stop(); // read at leisure
        MouseLeave += (_, _) =>
        {
            if (_state == PillState.Result)
            {
                _hideTimer.Stop();
                _hideTimer.Start(); // 15 s again from now
            }
        };
    }

    /// <summary>İptal was pressed.</summary>
    public event Action? CancelRequested;

    /// <summary>A short message for the status bar (Explorer could not be opened).</summary>
    public event Action<string>? Message;

    /// <summary>
    /// Room in the top bar (0 = none: hidden); set by the main window whenever the bar's layout changes. Narrower than
    /// 440 px the pill drops "kamera i / n" and the time left, then the icon and the camera (<see cref="Pill.LayoutFor"/>).
    /// </summary>
    public double PillWidth
    {
        get => _width;
        set
        {
            _width = value;
            if (value > 0)
            {
                Width = value;
            }
            ApplyLayout(Pill.LayoutFor(value));
            UpdateVisibility();
        }
    }

    void ApplyLayout(DownloadPillLayout layout)
    {
        var full = layout == DownloadPillLayout.Full;
        var mini = layout <= DownloadPillLayout.Mini;
        StepText.Visibility = RemainingText.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
        CameraText.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
        PercentText.Margin = new Thickness(mini ? 0 : 8, 0, 0, 0);
        ProgressIcon.Visibility = ResultIcon.Visibility = ResultText.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
        var icon = new GridLength(mini ? 0 : 16);
        var gap = new GridLength(mini ? 0 : 10);
        ProgressIconColumn.Width = ResultIconColumn.Width = icon;
        ProgressIconGap.Width = ResultIconGap.Width = ResultButtonGap.Width = gap;
        FitCameraName();
    }

    /// <summary>A batch started or one of its cameras changed: the running camera's progress.</summary>
    internal void ShowProgress(DownloadBatch batch)
    {
        _hideTimer.Stop();
        _state = PillState.Running;
        ProgressView.Visibility = Visibility.Visible;
        ResultView.Visibility = Visibility.Collapsed;
        PillBorder.SetResourceReference(Border.BackgroundProperty, "CardBg");
        PillBorder.SetResourceReference(Border.BorderBrushProperty, "Border");
        Shadow.Visibility = Visibility.Visible;

        var items = batch.Items;
        var index = Math.Max(0, items.ToList().FindIndex(i => i.Running));
        if (!items.Any(i => i.Running))
        {
            // Between two cameras (or before the first): the next one to run, else the last one.
            var next = items.ToList().FindIndex(i => i.Outcome is null && i.Status == "Sırada");
            index = next >= 0 ? next : items.Count - 1;
        }
        var item = items[index];
        CameraText.Text = item.Camera.Name;
        var cancelling = batch.IsCancelled;
        StepText.Text = cancelling ? "İptal ediliyor…" : Pill.Step(index + 1, items.Count);
        var progress = item.Running ? item.Progress : null;
        PercentText.Text = Pill.Percent(progress);
        RemainingText.Text = cancelling ? "" : Pill.Remaining(progress?.Remaining);
        var percent = progress?.Percent ?? 0;
        BarDone.Width = new GridLength(percent, GridUnitType.Star);
        BarLeft.Width = new GridLength(100 - percent, GridUnitType.Star);
        CancelButton.IsEnabled = !cancelling;
        ToolTip = Details(batch);
        AutomationProperties.SetName(this, $"İndirme: {item.Camera.Name} · {StepText.Text} · {PercentText.Text}");
        FitCameraName();
        UpdateVisibility();
    }

    /// <summary>The batch ended: the result pill, hidden again after 15 s.</summary>
    internal void ShowResult(DownloadBatch batch)
    {
        var (success, text) = Pill.Result(DownloadManager.Counts(batch));
        _files = batch.Items.Select(i => i.Outcome).Where(o => o?.HasFile == true).Select(o => o!.Path).ToList();
        _folder = batch.Folder;
        _state = PillState.Result;
        ProgressView.Visibility = Visibility.Collapsed;
        ResultView.Visibility = Visibility.Visible;
        Shadow.Visibility = Visibility.Collapsed;
        PillBorder.SetResourceReference(Border.BackgroundProperty, success ? "OkSoftBg" : "WarningSoftBg");
        PillBorder.SetResourceReference(Border.BorderBrushProperty, success ? "OkBorder" : "WarningSoftBorder");
        ResultIcon.Data = (System.Windows.Media.Geometry)FindResource(success ? "IconCheck" : "IconInfo");
        ResultIcon.StrokeThickness = success ? 2.6 : 2;
        ResultIcon.SetResourceReference(GlyphIcon.ForegroundProperty, success ? "Online" : "WarningText");
        ResultText.Text = text;
        ResultText.SetResourceReference(TextBlock.ForegroundProperty, success ? "OkText" : "WarningText");
        OpenFolderButton.SetResourceReference(BorderBrushProperty, success ? "OkBorder" : "WarningSoftBorder");
        OpenFolderButton.SetResourceReference(ForegroundProperty, success ? "OkText" : "WarningText");
        OpenFolderButton.IsEnabled = _files.Count > 0;
        ToolTip = Details(batch);
        AutomationProperties.SetName(this, text);
        _hideTimer.Stop();
        if (!IsMouseOver)
        {
            _hideTimer.Start();
        }
        UpdateVisibility();
    }

    void Hide()
    {
        _hideTimer.Stop();
        _state = PillState.Hidden;
        UpdateVisibility();
    }

    void UpdateVisibility() =>
        Visibility = _state != PillState.Hidden && _width > 0 ? Visibility.Visible : Visibility.Collapsed;

    static string Details(DownloadBatch batch) =>
        $"{batch.Start:dd.MM.yyyy HH:mm:ss} → {batch.End:dd.MM.yyyy HH:mm:ss}\n" +
        string.Join("\n", batch.Items.Select(i => Pill.Detail(i.Camera.Name, i.Outcome?.Message ?? i.Status)));

    /// <summary>The camera name trims so "kamera i / n", NN% and the time left always show.</summary>
    void FitCameraName()
    {
        if (ProgressLine.ActualWidth <= 0)
        {
            return;
        }
        var others = 0.0;
        foreach (var text in new[] { StepText, PercentText, RemainingText }.Where(t => t.Visibility == Visibility.Visible))
        {
            text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            others += text.DesiredSize.Width + text.Margin.Left;
        }
        CameraText.MaxWidth = Math.Max(0, ProgressLine.ActualWidth - others);
    }

    void ProgressLine_SizeChanged(object sender, SizeChangedEventArgs e) => FitCameraName();

    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        StepText.Text = "İptal ediliyor…";
        CancelRequested?.Invoke();
    }

    /// <summary>One file: Explorer with it selected; several: their folder. The pill goes away at once.</summary>
    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var (path, select) = Pill.OpenTarget(_files, _folder);
        try
        {
            if (select && System.IO.File.Exists(path))
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
            else if (System.IO.Directory.Exists(path))
            {
                System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"");
            }
            else
            {
                Message?.Invoke($"Klasör bulunamadı: {path}");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Message?.Invoke($"Klasör açılamadı: {ex.Message}");
        }
        Hide();
    }
}
