using System.Diagnostics;
using System.Windows.Threading;
using SPVision.Core;

namespace SPVision;

/// <summary>
/// Event notifications from the recorders: one <c>GET ISAPI/Event/notification/alertStream</c> per device (a read-only
/// subscription through <see cref="IsapiClient"/>: Digest, no redirects, certificate pin; nothing is configured on the
/// device). The body is read off the UI thread by Core <see cref="AlertStreamParser"/> (per-part size cap); active alerts
/// of a kind SPVision maps (<see cref="AlertMapping"/>) come to the UI thread through <see cref="Alert"/>, heartbeats and
/// other types stay here. A dropped stream (error, end, or no byte for <see cref="IdleLimit"/> — the recorder sends a
/// heartbeat every ~10 s) reconnects after <see cref="AlertBackoff"/>. Never more than one stream per device: a restart
/// waits for the old loop to end before connecting. Stopped for a removed device, when no kind is switched on, and on
/// shutdown.
/// </summary>
sealed class AlertStreamMonitor(Dispatcher dispatcher)
{
    /// <summary>No byte at all for this long (heartbeats included): the connection is dead, reconnect.</summary>
    public static readonly TimeSpan IdleLimit = TimeSpan.FromSeconds(60);

    /// <summary>The headers of the stream must come within this time; the body itself has no overall timeout.</summary>
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    sealed class Loop
    {
        public required Device Device { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public Task Task { get; set; } = Task.CompletedTask;
    }

    readonly Dictionary<Guid, Loop> _loops = [];
    bool _stopped;

    /// <summary>An active, mapped alert of a device (UI thread).</summary>
    public event Action<Device, AlertEvent>? Alert;

    /// <summary>Starts a loop for every device that has none (UI thread).</summary>
    public void Start(IEnumerable<Device> devices)
    {
        foreach (var device in devices.Where(d => !_loops.ContainsKey(d.Id)))
        {
            Run(device, Task.CompletedTask);
        }
    }

    /// <summary>The device was saved (address or login may differ): its stream reconnects with the new settings.</summary>
    public void Restart(Device device)
    {
        var previous = Cancel(device.Id);
        Run(device, previous);
    }

    public void Remove(Guid deviceId) => Cancel(deviceId);

    /// <summary>Every stream ends (no kind switched on); <see cref="Start"/> brings them back.</summary>
    public void Clear()
    {
        foreach (var id in _loops.Keys.ToList())
        {
            Cancel(id);
        }
    }

    /// <summary>App closing: every stream ends and none starts again.</summary>
    public void Stop()
    {
        _stopped = true;
        Clear();
    }

    Task Cancel(Guid deviceId)
    {
        if (!_loops.Remove(deviceId, out var loop))
        {
            return Task.CompletedTask;
        }
        loop.Cts.Cancel();
        return loop.Task;
    }

    void Run(Device device, Task previous)
    {
        if (_stopped)
        {
            return;
        }
        var loop = new Loop { Device = device, Cts = new CancellationTokenSource() };
        _loops[device.Id] = loop;
        loop.Task = Task.Run(() => RunAsync(loop, previous));
    }

    async Task RunAsync(Loop loop, Task previous)
    {
        var ct = loop.Cts.Token;
        try
        {
            await previous.ConfigureAwait(false); // the old stream of this device is closed first
        }
        catch
        {
        }
        var failures = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var watch = Stopwatch.StartNew();
                try
                {
                    await ReadAsync(loop.Device, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    // Refused (403/404: not supported), unreachable, certificate refused…: try again later.
                }
                failures = watch.Elapsed >= AlertBackoff.StableAfter ? 0 : failures + 1;
                await Task.Delay(AlertBackoff.Delay(failures - 1), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            loop.Cts.Dispose();
        }
    }

    /// <summary>One connection: read until the device ends it, it goes silent, or the loop is cancelled.</summary>
    async Task ReadAsync(Device device, CancellationToken ct)
    {
        using var isapi = new IsapiClient(device, ConnectTimeout);
        using var stream = await isapi.OpenAlertStreamAsync(ct).ConfigureAwait(false);
        var parser = new AlertStreamParser(stream.Boundary);
        var buffer = new byte[16 * 1024];
        while (true)
        {
            int read;
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                idle.CancelAfter(IdleLimit);
                try
                {
                    read = await stream.Body.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return; // silent for a minute: reconnect
                }
            }
            if (read == 0)
            {
                return; // the device closed the stream
            }
            foreach (var alert in parser.Feed(buffer.AsSpan(0, read)))
            {
                if (alert.IsActive && AlertMapping.KindOf(alert.EventType) is not null)
                {
                    _ = dispatcher.BeginInvoke(() =>
                    {
                        if (!ct.IsCancellationRequested)
                        {
                            Alert?.Invoke(device, alert);
                        }
                    });
                }
            }
        }
    }
}
