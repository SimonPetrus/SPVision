using SPVision.Core;

namespace SPVision;

/// <summary>
/// Status bar disk warning: reads each recorder's disk list (<c>GET ISAPI/ContentMgmt/Storage/hdd</c>, read only — nothing
/// is ever written) shortly after start and then every <see cref="Interval"/>; a recorder that has not answered yet is
/// read again when its video connects. IP cameras are skipped (no recorder disks). Results go to a Core
/// <see cref="DiskWatch"/>, which keeps the last known state on network errors. One loop per device, on the UI thread
/// (the request itself is async); cancelled when the device is saved again, removed, or the app closes.
/// </summary>
sealed class DiskMonitor
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(5);   // let the streams connect first
    static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);
    static readonly TimeSpan RetryGap = TimeSpan.FromSeconds(30);    // "connected" nudges at most this often

    sealed class Loop
    {
        public required Device Device { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public TaskCompletionSource Nudge { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTime LastAttempt { get; set; } = DateTime.MinValue;
    }

    readonly Dictionary<Guid, Loop> _loops = [];
    bool _stopped;

    public DiskWatch Watch { get; } = new();

    /// <summary>The warning or its text changed.</summary>
    public event Action? Changed;

    /// <summary>Starts a loop for every device that has none.</summary>
    public void Start(IEnumerable<Device> devices)
    {
        foreach (var device in devices.Where(d => !_loops.ContainsKey(d.Id)))
        {
            Run(device, FirstDelay);
        }
    }

    /// <summary>The device was saved (address or login may differ): forget what was known and read it again now.</summary>
    public void Restart(Device device)
    {
        Remove(device.Id);
        Run(device, TimeSpan.Zero);
    }

    public void Remove(Guid deviceId)
    {
        if (_loops.Remove(deviceId, out var loop))
        {
            loop.Cts.Cancel();
        }
        if (Watch.Forget(deviceId))
        {
            Changed?.Invoke();
        }
    }

    /// <summary>A video of this device is playing: if its disks were never read successfully, read them now.</summary>
    public void Connected(Guid deviceId)
    {
        if (_loops.TryGetValue(deviceId, out var loop) && !Watch.Knows(deviceId) &&
            DateTime.UtcNow - loop.LastAttempt >= RetryGap)
        {
            loop.Nudge.TrySetResult();
        }
    }

    /// <summary>App closing: every loop ends, a running request is cancelled.</summary>
    public void Stop()
    {
        _stopped = true;
        foreach (var loop in _loops.Values)
        {
            loop.Cts.Cancel();
        }
        _loops.Clear();
    }

    void Run(Device device, TimeSpan firstDelay)
    {
        if (_stopped)
        {
            return;
        }
        var loop = new Loop { Device = device, Cts = new CancellationTokenSource() };
        _loops[device.Id] = loop;
        _ = RunAsync(loop, firstDelay); // UI thread; awaits come back to it
    }

    async Task RunAsync(Loop loop, TimeSpan firstDelay)
    {
        var ct = loop.Cts.Token;
        try
        {
            await WaitAsync(loop, firstDelay, ct);
            while (!ct.IsCancellationRequested)
            {
                loop.LastAttempt = DateTime.UtcNow;
                var read = await ReadAsync(loop.Device, ct);
                if (ct.IsCancellationRequested)
                {
                    return;
                }
                if (read is not null && Watch.Update(loop.Device.Id, loop.Device.Name, read))
                {
                    Changed?.Invoke();
                }
                await WaitAsync(loop, Interval, ct);
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

    /// <summary>Waits <paramref name="delay"/>, or less when <see cref="Connected"/> nudges the loop.</summary>
    static async Task WaitAsync(Loop loop, TimeSpan delay, CancellationToken ct)
    {
        loop.Nudge = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Task.WhenAny(Task.Delay(delay, ct), loop.Nudge.Task);
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>The disk list (GET only), or a failed/unsupported read; null for an IP camera (not checked).</summary>
    static async Task<Read<List<HddInfo>>?> ReadAsync(Device device, CancellationToken ct)
    {
        try
        {
            using var isapi = new IsapiClient(device, ReadTimeout);
            var type = device.DeviceType;
            if (string.IsNullOrWhiteSpace(type))
            {
                type = (await isapi.GetDeviceInfoAsync(ct)).DeviceType; // not saved: Settings' probe does that
            }
            if (IsCamera(type))
            {
                return null;
            }
            return await RecorderReader.TryAsync(isapi, IsapiPaths.Hdd, HddInfo.ParseList, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new Read<List<HddInfo>>(ReadState.Failed, null, ex.Message); // keeps the last known state
        }
    }

    static bool IsCamera(string? deviceType) =>
        DeviceKind.Describe(new Device { DeviceType = deviceType }) == "IP kamera";
}
