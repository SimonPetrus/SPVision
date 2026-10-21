using System.Globalization;

namespace SPVision.Core;

// Phase 3 (Bakım): the pure orchestration of a firmware upgrade. The SDK calls live in the WPF project
// (Hik/HikFirmware.cs) behind the two transports below; everything here is unit-tested with fakes.

/// <summary>One look at a running recorder upgrade (NET_DVR_GetUpgradeState / GetUpgradeProgress / GetUpgradeStep).</summary>
public readonly record struct SdkUpgradeSnapshot(int State, int Progress, int Step, int SubProgress);

/// <summary>The recorder upgrade of the SDK (NET_DVR_Upgrade_V50). Calls may block; the runner calls them off the UI thread.</summary>
public interface IRecorderFirmwareTransport
{
    /// <summary>Starts the upgrade; returns the upgrade handle. Throws when the SDK refuses.</summary>
    int Start(FirmwareJob job);

    SdkUpgradeSnapshot Poll(int handle);

    /// <summary>NET_DVR_CloseUpgradeHandle; called once, after a final state.</summary>
    void Close(int handle);
}

/// <summary>Camera upgrade through the recorder (NET_DVR_UPGRADE_IPC) plus the read-only check that it came back.</summary>
public interface ICameraFirmwareTransport
{
    /// <summary>Upgrades one camera and returns when the SDK reports a final status (or gives up).</summary>
    Task<CameraUpgradeResult> UpgradeAsync(FirmwareJob job, int channel, IProgress<int>? percent, CancellationToken ct);

    /// <summary>The camera's firmware text while it is online; null while offline or unreadable (GET only).</summary>
    Task<string?> ReadVersionAsync(int channel, CancellationToken ct);
}

public sealed record CameraUpgradeResult(bool Ok, string Text);

public enum FirmwareOutcome
{
    Done,
    Failed,

    /// <summary>No final answer (lost connection, no progress for too long): the device may still be working.</summary>
    Unknown,
}

public sealed record FirmwareProgress(int Percent, string Text);

public sealed record FirmwareRunResult(FirmwareOutcome Outcome, string Text);

/// <summary>Turkish texts of the SDK upgrade states and steps (SDK General Developer Guide Table 12-3, HCNetSDK.h STEP_*).</summary>
public static class FirmwareText
{
    public static string State(int state) => state switch
    {
        1 => "Yazılım cihaza yüklendi.",
        2 => "Yükseltiliyor…",
        3 => "Yükseltme başarısız oldu.",
        4 => "Ağ bağlantısı koptu; yükseltmenin durumu bilinmiyor.",
        5 => "Yazılım dosyasının dili cihazla uyuşmuyor.",
        6 => "Yazılım cihazın belleğine (flash) yazılamadı.",
        7 => "Yazılım dosyasının türü bu cihaza uymuyor (yanlış model).",
        8 => "Yazılım dosyasının sürümü bu cihaza uymuyor.",
        9 => "Dosya kilitli; Hikvision desteğine başvurun.",
        10 => "Yedekleme hatası; Hikvision desteğine başvurun.",
        11 => "Cihazın belleği dolu; Hikvision desteğine başvurun.",
        _ => string.Create(CultureInfo.InvariantCulture, $"Bilinmeyen yükseltme durumu ({state})."),
    };

    public static string Step(int step) => step switch
    {
        0 => "Hazırlanıyor",
        1 => "Dosya gönderiliyor",
        2 => "Sistem yükseltiliyor",
        3 => "Sistem yedekleniyor",
        255 => "Yükseltme dosyası aranıyor",
        _ => "Yükseltiliyor",
    };

    /// <summary>NET_SDK_CALLBACK_STATUS_* of a remote config (camera upgrade).</summary>
    public static string CameraStatus(int status) => status switch
    {
        1000 => "Kamera yazılımı yüklendi.",
        1001 => "Yükleniyor…",
        1002 => "Kamera yazılımı yüklenemedi.",
        1003 => "Bağlantı hatası; yüklemenin durumu bilinmiyor.",
        1004 => "Yazılım dosyasının dili kamerayla uyuşmuyor.",
        1005 => "Yazılım dosyası bu kamera modeline ait değil.",
        _ => string.Create(CultureInfo.InvariantCulture, $"Bilinmeyen durum ({status})."),
    };

    /// <summary>NET_SDK_UPGRADE_IPC_ERR_CODE of one channel.</summary>
    public static string CameraError(int code) => code switch
    {
        1 => "başarılı",
        2 => "kamera çevrimdışı",
        3 => "kamera bu yükseltmeyi desteklemiyor",
        4 => "dil uyuşmuyor",
        5 => "kameranın belleğine yazılamadı",
        6 => "diğer hata",
        _ => string.Create(CultureInfo.InvariantCulture, $"hata {code}"),
    };
}

/// <summary>
/// Recorder upgrade: start → poll every second until a final state → close the handle. Never cancelled by the UI
/// (stopping a running upgrade is not offered); a stall or the total limit ends the wait as <see cref="FirmwareOutcome.Unknown"/>.
/// </summary>
public static class RecorderFirmwareRun
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>No change of progress, step or sub-progress for this long → stop waiting ("durum bilinmiyor").</summary>
    public static readonly TimeSpan StallLimit = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan TotalLimit = TimeSpan.FromMinutes(60);

    /// <summary>GetUpgradeState returning -1 this many times in a row ends the wait as unknown.</summary>
    const int MaxReadFailures = 5;

    public static async Task<FirmwareRunResult> RunAsync(FirmwareJob job, IRecorderFirmwareTransport transport,
        IProgress<FirmwareProgress>? progress = null, Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTimeOffset>? clock = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Operation != RiskyOperation.RecorderFirmware)
        {
            throw new IsapiWriteDeniedException("Bu iş bir kayıt cihazı güncellemesi değil.");
        }
        delay ??= Task.Delay;
        clock ??= () => DateTimeOffset.UtcNow;

        progress?.Report(new FirmwareProgress(0, FirmwareText.Step(0)));
        var handle = await Task.Run(() => transport.Start(job), CancellationToken.None);
        try
        {
            var started = clock();
            var lastChange = started;
            SdkUpgradeSnapshot? last = null;
            var failures = 0;
            while (true)
            {
                await delay(PollInterval, ct);
                var snapshot = await Task.Run(() => transport.Poll(handle), CancellationToken.None);
                var now = clock();
                if (snapshot.State == -1)
                {
                    if (++failures >= MaxReadFailures)
                    {
                        return new FirmwareRunResult(FirmwareOutcome.Unknown,
                            "Yükseltmenin durumu cihazdan okunamıyor. Cihazın elektriğini kesmeyin; birkaç dakika sonra sürümü yeniden okuyun.");
                    }
                    continue;
                }
                failures = 0;
                if (snapshot.State is 0 or 2)
                {
                    if (last is not { } previous || previous.Progress != snapshot.Progress || previous.Step != snapshot.Step ||
                        previous.SubProgress != snapshot.SubProgress)
                    {
                        lastChange = now;
                    }
                    last = snapshot;
                    var percent = Math.Clamp(snapshot.Progress, 0, 100);
                    progress?.Report(new FirmwareProgress(percent,
                        string.Create(CultureInfo.InvariantCulture, $"{FirmwareText.Step(snapshot.Step)} · %{percent}")));
                    if (now - lastChange > StallLimit || now - started > TotalLimit)
                    {
                        return new FirmwareRunResult(FirmwareOutcome.Unknown,
                            "Cihazdan uzun süredir ilerleme bilgisi gelmiyor. Cihazın elektriğini KESMEYİN; birkaç dakika bekleyip sürümü yeniden okuyun.");
                    }
                    continue;
                }
                return snapshot.State switch
                {
                    1 => new FirmwareRunResult(FirmwareOutcome.Done, FirmwareText.State(1)),
                    4 => new FirmwareRunResult(FirmwareOutcome.Unknown, FirmwareText.State(4) + " Cihazın elektriğini kesmeyin; birkaç dakika sonra sürümü yeniden okuyun."),
                    _ => new FirmwareRunResult(FirmwareOutcome.Failed, FirmwareText.State(snapshot.State)),
                };
            }
        }
        finally
        {
            await Task.Run(() => transport.Close(handle), CancellationToken.None);
        }
    }
}

public enum ReturnOutcome
{
    /// <summary>Came back with a different firmware.</summary>
    NewVersion,

    /// <summary>Went away and came back, firmware unchanged.</summary>
    SameVersion,

    /// <summary>Never went away and kept the old firmware: it did not restart by itself.</summary>
    NoRestart,

    /// <summary>Still unreachable at the limit.</summary>
    NotBack,
}

/// <summary>Waiting (read-only) for a device or camera to restart with its new firmware.</summary>
public static class FirmwareReturn
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Polls <paramref name="readVersion"/> (null = unreachable) until the version differs from <paramref name="oldVersion"/>,
    /// or it came back unchanged after being away, or it stayed reachable and unchanged for <paramref name="noRestartAfter"/>,
    /// or <paramref name="limit"/> passed.
    /// </summary>
    public static async Task<(ReturnOutcome Outcome, string? Version)> WaitAsync(Func<CancellationToken, Task<string?>> readVersion,
        string? oldVersion, TimeSpan noRestartAfter, TimeSpan limit, IProgress<TimeSpan>? elapsed = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Func<DateTimeOffset>? clock = null, CancellationToken ct = default)
    {
        delay ??= Task.Delay;
        clock ??= () => DateTimeOffset.UtcNow;
        var started = clock();
        var wentAway = false;
        while (true)
        {
            await delay(Interval, ct);
            var now = clock();
            elapsed?.Report(now - started);
            string? version;
            try
            {
                version = await readVersion(ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                version = null;
            }
            if (version is null)
            {
                wentAway = true;
            }
            else if (!string.Equals(version, oldVersion, StringComparison.Ordinal))
            {
                return (ReturnOutcome.NewVersion, version);
            }
            else if (wentAway)
            {
                return (ReturnOutcome.SameVersion, version);
            }
            else if (now - started >= noRestartAfter)
            {
                return (ReturnOutcome.NoRestart, version);
            }
            if (now - started >= limit)
            {
                return (ReturnOutcome.NotBack, null);
            }
        }
    }
}

public enum CameraStep
{
    Waiting,
    Uploading,
    Restarting,
    Done,
    Failed,
    Skipped,
}

public sealed record CameraUpdate(int Channel, CameraStep Step, int Percent, string Text);

/// <summary>Cameras of a confirmed job, strictly one after another; optionally stops at the first failure.</summary>
public static class CameraFirmwareBatch
{
    public static readonly TimeSpan NoRestartAfter = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ReturnLimit = TimeSpan.FromMinutes(10);

    public static async Task<IReadOnlyList<CameraUpdate>> RunAsync(FirmwareJob job, IReadOnlyDictionary<int, string?> oldVersions,
        ICameraFirmwareTransport transport, bool stopOnFirstFailure, IProgress<CameraUpdate>? progress = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Func<DateTimeOffset>? clock = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Operation != RiskyOperation.CameraFirmware)
        {
            throw new IsapiWriteDeniedException("Bu iş bir kamera güncellemesi değil.");
        }
        var results = new List<CameraUpdate>();
        void Report(CameraUpdate update)
        {
            progress?.Report(update);
        }
        foreach (var channel in job.Channels)
        {
            Report(new CameraUpdate(channel, CameraStep.Waiting, 0, "Sırada"));
        }
        var stopped = false;
        foreach (var channel in job.Channels)
        {
            if (stopped || ct.IsCancellationRequested)
            {
                var skipped = new CameraUpdate(channel, CameraStep.Skipped, 0, stopped ? "Başlatılmadı (önceki hata)" : "Başlatılmadı");
                results.Add(skipped);
                Report(skipped);
                continue;
            }
            Report(new CameraUpdate(channel, CameraStep.Uploading, 0, "Yükleniyor"));
            var percent = new InlineProgress<int>(p => Report(new CameraUpdate(channel, CameraStep.Uploading, Math.Clamp(p, 0, 100),
                string.Create(CultureInfo.InvariantCulture, $"Yükleniyor · %{Math.Clamp(p, 0, 100)}"))));
            CameraUpdate final;
            CameraUpgradeResult upgrade;
            try
            {
                upgrade = await transport.UpgradeAsync(job, channel, percent, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                upgrade = new CameraUpgradeResult(false, e.Message);
            }
            if (!upgrade.Ok)
            {
                final = new CameraUpdate(channel, CameraStep.Failed, 0, upgrade.Text);
            }
            else
            {
                Report(new CameraUpdate(channel, CameraStep.Restarting, 100, "Yeniden başlıyor"));
                var old = oldVersions.GetValueOrDefault(channel);
                var (outcome, version) = await FirmwareReturn.WaitAsync(c => transport.ReadVersionAsync(channel, c), old,
                    NoRestartAfter, ReturnLimit, null, delay, clock, ct);
                final = outcome switch
                {
                    ReturnOutcome.NewVersion => new CameraUpdate(channel, CameraStep.Done, 100, $"Güncellendi · {version}"),
                    ReturnOutcome.SameVersion => new CameraUpdate(channel, CameraStep.Failed, 100, "Yeniden başladı, sürüm değişmedi"),
                    ReturnOutcome.NoRestart => new CameraUpdate(channel, CameraStep.Failed, 100, "Yeniden başlamadı; sürüm değişmedi"),
                    _ => new CameraUpdate(channel, CameraStep.Failed, 100, "Kamera geri gelmedi"),
                };
            }
            results.Add(final);
            Report(final);
            if (final.Step == CameraStep.Failed && stopOnFirstFailure)
            {
                stopped = true;
            }
        }
        return results;
    }
}

/// <summary>Reports synchronously on the reporting thread (keeps the order of updates; the caller's IProgress marshals).</summary>
sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
