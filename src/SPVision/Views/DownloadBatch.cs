using System.Windows.Threading;
using SPVision.Core;
using SPVision.Hik;

namespace SPVision.Views;

/// <summary>One camera of a download batch, as the panel shows it (changed on the UI thread only).</summary>
sealed class DownloadItem(Device device, Camera camera)
{
    public Device Device { get; } = device;
    public Camera Camera { get; } = camera;

    /// <summary>The file (known once its download starts).</summary>
    public string? Path { get; set; }

    public DownloadProgressInfo? Progress { get; set; }

    /// <summary>"Sırada", "Kayıtlar aranıyor…", "İndiriliyor", then the outcome's message.</summary>
    public string Status { get; set; } = "Sırada";

    public DownloadOutcome? Outcome { get; set; }

    public bool Running { get; set; }
}

/// <summary>
/// Downloads the checked cameras one after another (one playback session at a time, so a download takes one of the
/// recorder's simultaneous playbacks, not n). Per camera: the read-only recording search of the range (none → "Bu
/// aralıkta kayıt yok", nothing is opened), then <see cref="HikRecordingDownload"/> on a thread of its own. Progress and
/// outcomes reach the panel on the UI thread. <see cref="CancelAll"/> (window closing) cancels every running batch; the
/// partial files are deleted.
/// </summary>
sealed class DownloadBatch
{
    static readonly object Gate = new();
    static readonly HashSet<DownloadBatch> Active = [];

    readonly Dispatcher _dispatcher;
    readonly CancellationTokenSource _cts = new();

    public DownloadBatch(IEnumerable<(Device Device, Camera Camera)> cameras, DateTime start, DateTime end, string folder, Dispatcher dispatcher)
    {
        Items = cameras.Select(c => new DownloadItem(c.Device, c.Camera)).ToList();
        Start = start;
        End = end;
        Folder = folder;
        _dispatcher = dispatcher;
    }

    public IReadOnlyList<DownloadItem> Items { get; }

    public DateTime Start { get; }

    public DateTime End { get; }

    public string Folder { get; }

    public bool IsCancelled => _cts.IsCancellationRequested;

    /// <summary>An item changed (UI thread).</summary>
    public event Action<DownloadItem>? ItemChanged;

    public Task? Completion { get; private set; }

    public Task RunAsync()
    {
        lock (Gate)
        {
            Active.Add(this);
        }
        Completion = RunCoreAsync();
        return Completion;
    }

    public void Cancel() => _cts.Cancel();

    /// <summary>Window closing: every running download stops and deletes its partial file; ends when they did (or after <paramref name="wait"/>).</summary>
    public static async Task CancelAllAsync(TimeSpan wait)
    {
        List<DownloadBatch> batches;
        lock (Gate)
        {
            batches = [.. Active];
        }
        foreach (var batch in batches)
        {
            batch.Cancel();
        }
        var all = Task.WhenAll(batches.Select(b => b.Completion ?? Task.CompletedTask));
        await Task.WhenAny(all, Task.Delay(wait)).ConfigureAwait(false);
    }

    async Task RunCoreAsync()
    {
        try
        {
            foreach (var item in Items)
            {
                if (_cts.IsCancellationRequested)
                {
                    item.Status = DownloadText.Cancelled;
                    Changed(item);
                    continue;
                }
                await RunItemAsync(item).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (Gate)
            {
                Active.Remove(this);
            }
        }
    }

    async Task RunItemAsync(DownloadItem item)
    {
        var ct = _cts.Token;
        item.Running = true;
        item.Status = "Kayıtlar aranıyor…";
        Changed(item);
        IReadOnlyList<RecordingSegment>? segments = null;
        try
        {
            using var isapi = new IsapiClient(item.Device, TimeSpan.FromSeconds(20));
            segments = await isapi.SearchRecordingsAsync(item.Camera.Channel, Start, End, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is IsapiException or UriFormatException or System.Net.Http.HttpRequestException or System.IO.IOException)
        {
            segments = null; // unknown: downloaded as one session, the progress follows the range
        }
        if (ct.IsCancellationRequested)
        {
            Finish(item, null, DownloadText.Cancelled);
            return;
        }
        if (segments is { Count: 0 })
        {
            Finish(item, null, DownloadText.NoRecordings);
            return;
        }
        var path = DownloadNaming.UniquePath(Folder, DownloadNaming.FileName(item.Camera.Name, Start, End), System.IO.File.Exists);
        item.Path = path;
        item.Status = "İndiriliyor";
        Changed(item);
        var job = new DownloadJob(item.Device, item.Camera, Start, End, path, segments);
        DownloadOutcome outcome;
        try
        {
            outcome = await Task.Factory.StartNew(() => HikRecordingDownload.Run(job, progress =>
            {
                item.Progress = progress; // a record: replaced whole, read on the UI thread
                Changed(item);
            }, ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Finish(item, null, DownloadText.Cancelled);
            return;
        }
        catch (Exception e)
        {
            Finish(item, null, DownloadText.Failed(e.Message));
            return;
        }
        Finish(item, outcome, outcome.Message);
    }

    void Finish(DownloadItem item, DownloadOutcome? outcome, string status)
    {
        item.Outcome = outcome;
        item.Running = false;
        item.Status = status;
        Changed(item);
    }

    void Changed(DownloadItem item) => _dispatcher.BeginInvoke(() => ItemChanged?.Invoke(item));
}
