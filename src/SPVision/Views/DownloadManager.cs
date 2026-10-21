using System.Windows.Threading;
using SPVision.Core;
using SPVision.Hik;

namespace SPVision.Views;

/// <summary>
/// The app's "Video indir" downloads, owned by the main window (not by Kayıt Oynatma): a batch started from the panel keeps
/// running across page switches (Canlı İzleme / Kayıt Oynatma / Ayarlar), playback changes and the panel being closed;
/// only İptal or closing the app stops it (closing deletes the partial files). One batch at a time. Its progress and
/// result are shown by the top bar's <see cref="Controls.DownloadStatusPill"/>; every event comes on the UI thread.
/// </summary>
sealed class DownloadManager(Dispatcher dispatcher)
{
    /// <summary>The running batch; null when idle.</summary>
    public DownloadBatch? Current { get; private set; }

    public bool IsRunning => Current is not null;

    /// <summary>A batch started.</summary>
    public event Action<DownloadBatch>? Started;

    /// <summary>Progress or status of a camera of the running batch changed.</summary>
    public event Action<DownloadBatch, DownloadItem>? Changed;

    /// <summary>The batch ended (done, partly done or cancelled); a new one can start.</summary>
    public event Action<DownloadBatch>? Finished;

    /// <summary>Outcome messages for the status bar ("İndirildi: …").</summary>
    public event Action<string>? Message;

    /// <summary>Starts a batch; false when one is already running (the panel does not offer İndir then).</summary>
    public bool Start(IEnumerable<(Device Device, Camera Camera)> cameras, DateTime start, DateTime end, string folder)
    {
        if (Current is not null)
        {
            return false;
        }
        var batch = new DownloadBatch(cameras, start, end, folder, dispatcher);
        batch.ItemChanged += item => OnItemChanged(batch, item);
        Current = batch;
        Started?.Invoke(batch);
        _ = RunAsync(batch);
        return true;
    }

    public void Cancel() => Current?.Cancel();

    /// <summary>App closing: running downloads stop, their partial files are deleted.</summary>
    public static Task ShutdownAsync() => DownloadBatch.CancelAllAsync(TimeSpan.FromSeconds(4));

    async Task RunAsync(DownloadBatch batch)
    {
        try
        {
            await batch.RunAsync();
        }
        finally
        {
            // The batch's last item events were posted before this continuation: the pill has its final state.
            await dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(Current, batch))
                {
                    Current = null;
                }
                Finished?.Invoke(batch);
            });
        }
    }

    void OnItemChanged(DownloadBatch batch, DownloadItem item)
    {
        if (item.Outcome is { HasFile: true } outcome && !item.Reported)
        {
            item.Reported = true;
            Message?.Invoke(outcome.Message);
        }
        else if (!item.Reported && !item.Running &&
            (item.Outcome is { Kind: DownloadOutcomeKind.Failed } || item.Status.StartsWith("İndirilemedi", StringComparison.Ordinal)))
        {
            item.Reported = true;
            Message?.Invoke($"{item.Camera.Name}: {item.Status}");
        }
        Changed?.Invoke(batch, item);
    }

    /// <summary>The result pill's counts (only items that ended; cancelled ones count as the batch being cancelled).</summary>
    public static DownloadResultCounts Counts(DownloadBatch batch) => new(
        batch.Items.Count(i => i.Outcome?.Kind == DownloadOutcomeKind.Done),
        batch.Items.Count(i => i.Outcome?.Kind == DownloadOutcomeKind.Incomplete),
        batch.Items.Count(i => i.Outcome?.Kind == DownloadOutcomeKind.Failed || (i.Outcome is null && i.Status.StartsWith("İndirilemedi", StringComparison.Ordinal))),
        batch.Items.Count(i => i.Outcome?.Kind == DownloadOutcomeKind.NoRecordings || (i.Outcome is null && i.Status == DownloadText.NoRecordings)),
        batch.IsCancelled);
}
