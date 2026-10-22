using System.Globalization;

namespace SPVision.Core;

/// <summary>One line of the status bar's disk warning: which recorder, which disk, what is wrong (Turkish).</summary>
public sealed record DiskProblem(Guid DeviceId, string DeviceName, string Disk, string Status)
{
    public string Text => Disk.Length > 0 ? $"{DeviceName} · {Disk}: {Status}" : $"{DeviceName}: {Status}";
}

/// <summary>
/// Status bar disk warning (MainWindow, DiskMonitor): every recorder's <c>ContentMgmt/Storage/hdd</c> list is read with a GET
/// after start and every 5 minutes. Only a successful read changes what is known about a recorder: a network error, a
/// timeout or an unsupported answer keeps the last state (no warning from a flaky connection). A read with no disks, or
/// with any disk not healthy (<see cref="HddInfo.IsHealthy"/>: ok / normal / idle / sleeping / standby), is a problem.
/// Unit tested (DiskHealthTests).
/// </summary>
public sealed class DiskWatch
{
    public const string WarningText = "Disk sorunu — kayıt yapılmıyor olabilir";
    public const string NoDisk = "Disk yok";

    readonly Dictionary<Guid, IReadOnlyList<DiskProblem>> _known = [];
    readonly List<Guid> _order = [];

    /// <summary>Problems of one successful read (empty = all healthy).</summary>
    public static IReadOnlyList<DiskProblem> ProblemsOf(Guid deviceId, string deviceName, IReadOnlyList<HddInfo> disks)
    {
        if (disks.Count == 0)
        {
            return [new DiskProblem(deviceId, deviceName, "", NoDisk)];
        }
        return disks.Where(d => !d.Healthy)
            .Select(d => new DiskProblem(deviceId, deviceName,
                d.Name.Length > 0 ? d.Name : string.Create(CultureInfo.InvariantCulture, $"Disk {d.Id}"), d.StatusText))
            .ToList();
    }

    /// <summary>A read finished. Returns true when the warning (or its text) changes.</summary>
    public bool Update(Guid deviceId, string deviceName, Read<List<HddInfo>> read)
    {
        if (!read.Ok)
        {
            return false; // network error / unsupported: keep what we knew
        }
        var problems = ProblemsOf(deviceId, deviceName, read.Value!);
        var before = _known.TryGetValue(deviceId, out var old) ? old : null;
        _known[deviceId] = problems;
        if (!_order.Contains(deviceId))
        {
            _order.Add(deviceId);
        }
        return before is null ? problems.Count > 0 : !before.SequenceEqual(problems);
    }

    /// <summary>The recorder was removed or its connection changed: what was known about it no longer applies.</summary>
    public bool Forget(Guid deviceId)
    {
        _order.Remove(deviceId);
        return _known.Remove(deviceId, out var old) && old.Count > 0;
    }

    /// <summary>Whether a successful read of this recorder has been seen (the monitor reads sooner until then).</summary>
    public bool Knows(Guid deviceId) => _known.ContainsKey(deviceId);

    /// <summary>All problems, recorders in the order they were first read.</summary>
    public IReadOnlyList<DiskProblem> Problems => _order.SelectMany(id => _known[id]).ToList();

    public bool HasProblem => _known.Values.Any(p => p.Count > 0);

    /// <summary>Recorder to open in Cihaz ayarları when the warning is clicked (the first one with a problem).</summary>
    public Guid? FirstProblemDevice => Problems.Select(p => (Guid?)p.DeviceId).FirstOrDefault();

    public string ToolTip
    {
        get
        {
            var problems = Problems;
            if (problems.Count == 0)
            {
                return "";
            }
            var lines = new List<string> { "Kayıt cihazı diskleri:" };
            lines.AddRange(problems.Select(p => p.Text));
            lines.Add("Tıklayın: Ayarlar › Cihaz ayarları › Sistem (Diskler)");
            return string.Join(Environment.NewLine, lines);
        }
    }
}
