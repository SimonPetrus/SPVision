using System.Diagnostics;
using System.Runtime.InteropServices;
using SPVision.Core;

namespace SPVision.Hik;

/// <summary>What one camera's download should do.</summary>
/// <param name="Segments">Recordings inside the range (read-only search before the start): one playback session per
/// recording file and the progress; null = unknown (one session for the whole range).</param>
public sealed record DownloadJob(Device Device, Camera Camera, DateTime Start, DateTime End, string Path,
    IReadOnlyList<RecordingSegment>? Segments);

public enum DownloadOutcomeKind
{
    /// <summary>The whole range is in the file (verified).</summary>
    Done,

    /// <summary>The recorder stopped sending in the middle of the range: the file holds what came (verified).</summary>
    Incomplete,

    /// <summary>No file (deleted); <see cref="DownloadOutcome.Message"/> says why.</summary>
    Failed,

    /// <summary>Cancelled by the user or the app's exit: the partial file was deleted.</summary>
    Cancelled,

    /// <summary>The range holds no recording (nothing was opened).</summary>
    NoRecordings,
}

/// <summary>Result of one camera's download.</summary>
/// <param name="Speed">Recorded time per wall second (× real time) over the whole download.</param>
public sealed record DownloadOutcome(DownloadOutcomeKind Kind, string Path, string Message, long Frames, TimeSpan Duration,
    double? Speed, long Bytes, string? Encoder, int Gaps, TimeSpan Elapsed)
{
    public bool HasFile => Kind is DownloadOutcomeKind.Done or DownloadOutcomeKind.Incomplete;
}

/// <summary>
/// Downloads a time range of one camera to an MP4 with the decrypted picture ("İndir"). Playback sessions of its own —
/// <c>NET_DVR_PlayBackByTime_V40</c> with no window, so the SDK neither draws nor paces them; one per recording file
/// (<see cref="DownloadParts"/>: across a file boundary the recorder skips ~7 s) — feed a dedicated PlayCtrl port (stream
/// key, software decode, decode callback = YV12 pictures, no window) per session. PlayCtrl paces a playing port like a
/// display (1×; PlayM4_Fast decodes every frame only up to 4×, from 8× on only key frames), so the port is stepped
/// frame by frame instead (<c>PlayM4_OneByOne</c>, the next step as soon as a picture arrives): it decodes as fast as the
/// recorder sends and the CPU decodes (measured 7–30× on 1080p, depending on the recorder's load). The pictures go to the recording writer
/// (<see cref="VideoRecorder"/>, H.264: NVENC → QSV → AMF → OpenH264 → MF → MPEG-4) with presentation times from the
/// stream (<see cref="DownloadTimeline"/>), so the file plays at real speed. Nothing is ever dropped: the encoder's queue
/// waits when full (holding the decode thread), and the data callback waits while the port has <see cref="QueueAhead"/>
/// undecoded (holding the SDK's data thread, which stops the network reads). Those waits only ever block this download's
/// own callback threads — never the UI or a playback tile. The recorder sends no end signal: a session ends at the first
/// frame at or after its end (the recorder is asked for 10 s more), or when its stream has gone quiet
/// (<see cref="DownloadEnd"/>). Read-only on the recorder (a playback); the file is written on this PC.
/// <para>
/// Lock rule as in the players: the SDK and PlayCtrl callbacks take no lock; the port is published with
/// <see cref="Volatile"/>; <see cref="Close"/> first cancels the session's stop flag (every wait in a callback and the
/// stepping thread end), then stops the SDK playback, then the port (PlayM4_Stop joins the decode thread, which by then
/// cannot be waiting).
/// </para>
/// </summary>
public sealed class HikRecordingDownload
{
    /// <summary>Undecoded data the port may hold before the data callback waits (bytes).</summary>
    const uint QueueAhead = 6 * 1024 * 1024;

    /// <summary>PlayCtrl source buffer of the download port.</summary>
    const uint PortBuffer = 16 * 1024 * 1024;

    /// <summary>Pictures between decoder and encoder (YV12, ~3 MB each at 1080p).</summary>
    const int EncoderQueue = 12;

    /// <summary>The next frame step is also issued after this long without a picture (a step that produced none).</summary>
    const int StepTimeoutMs = 50;

    /// <summary>The recorder started this much before the part (start of the recording file): reopened.</summary>
    const int MaxWrongStartRetries = 2;

    /// <summary>Asked from the recorder past each part's end, so a frame after it arrives (it stops sending at the end).</summary>
    static readonly TimeSpan EndMargin = TimeSpan.FromSeconds(10);

    /// <summary>Probe diagnostics (frame times / decisions); null in the app.</summary>
    internal static Action<string>? Trace { get; set; }

    readonly DownloadJob _job;
    readonly DownloadTimeline _timeline;
    readonly DownloadProgressTracker _tracker;
    readonly HikNative.DataCallback _data;
    readonly HikNative.DecodeCallback _decode;
    readonly Stopwatch _watch = Stopwatch.StartNew();
    readonly AutoResetEvent _signal = new(false);
    readonly AutoResetEvent _frameEvent = new(false);
    CancellationTokenSource _stop = new(); // per session: Close cancels it, so every wait of its threads ends
    VideoRecorder? _recorder;
    string? _key;
    volatile int _playHandle = -1;
    int _port = -1;
    long _lastActivityMs;
    long _firstWrittenMs = -1;
    long _firstWrittenTicks;  // DateTime ticks of the first written frame (0 = none)
    long _lastWrittenTicks;   // of the last written frame; read by the loop
    long _partWrittenTicks;   // of the last frame written by the current session (0 = none yet)
    long _firstSeenTicks;     // of the first decoded frame of the current session (wrong-start check)
    volatile bool _partEnded;
    volatile bool _ended;
    volatile string? _callbackError;
    bool _opened;

    HikRecordingDownload(DownloadJob job)
    {
        _job = job;
        _timeline = new DownloadTimeline(job.Start, job.End);
        _tracker = new DownloadProgressTracker(job.Start, job.End, job.Segments);
        _data = OnData;
        _decode = OnDecoded;
    }

    /// <summary>
    /// Runs the download (blocking: login, playback sessions, the whole transfer, the MP4 check). Call it on a thread of
    /// its own, never the UI thread. <paramref name="progress"/> is called on that thread about four times a second.
    /// </summary>
    public static DownloadOutcome Run(DownloadJob job, Action<DownloadProgressInfo>? progress, CancellationToken ct) =>
        new HikRecordingDownload(job).RunCore(progress, ct);

    /// <summary>The end asked from the recorder: <see cref="EndMargin"/> past the part, but not past now.</summary>
    internal static DateTime RequestEnd(DateTime end, DateTime now) =>
        end + EndMargin <= now ? end + EndMargin : end > now ? end : now;

    DownloadOutcome RunCore(Action<DownloadProgressInfo>? progress, CancellationToken ct)
    {
        var parts = DownloadParts.Split(_job.Start, _job.End, _job.Segments);
        if (_tracker.IsEmpty || parts.Count == 0)
        {
            return Outcome(DownloadOutcomeKind.NoRecordings, DownloadText.NoRecordings);
        }
        var path = _job.Path;
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            return Outcome(DownloadOutcomeKind.Failed, DownloadText.Failed($"klasör oluşturulamadı ({e.Message})"));
        }
        HikSession session;
        try
        {
            session = HikSession.GetOrCreate(_job.Device);
        }
        catch (HikException e)
        {
            return Outcome(DownloadOutcomeKind.Failed, DownloadText.Failed(e.Message));
        }
        if (ct.IsCancellationRequested)
        {
            return Outcome(DownloadOutcomeKind.Cancelled, DownloadText.Cancelled);
        }
        _key = _job.Device.StreamKeyFor(_job.Camera);
        _recorder = new VideoRecorder(path, queueCapacity: EncoderQueue);
        var channel = session.SdkChannel(_job.Camera.Channel);
        var cancelled = false;
        var stalled = false;
        var lastReport = TimeSpan.Zero;
        using var registration = ct.Register(() => _signal.Set());
        try
        {
            foreach (var (partStart, partEnd) in parts)
            {
                if (_ended)
                {
                    break;
                }
                _timeline.BeginPart(partStart, partEnd);
                var state = PlayPart(session.UserId, channel, partStart, partEnd, progress, ref lastReport, ct);
                if (ct.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }
                // A file the recorder would not send (NoData) is left out; one that stopped in the middle is kept as far
                // as it came. Either way the next file follows; the outcome says the range is incomplete.
                stalled |= state is DownloadState.Stalled or DownloadState.NoData;
            }
        }
        catch (Exception e)
        {
            // Whatever failed: the session is closed and no partial file is left (the encoder is finalized, so the window's
            // close does not wait for it).
            Close();
            Discard();
            var refused = e is HikException && !_opened; // NET_DVR_PlayBackByTime_V40 itself failed (e.g. the playback limit)
            return Outcome(DownloadOutcomeKind.Failed, refused ? DownloadText.Refused(e.Message) : DownloadText.Failed(e.Message));
        }
        Close();
        progress?.Invoke(Measure());
        if (cancelled)
        {
            Discard();
            return Outcome(DownloadOutcomeKind.Cancelled, DownloadText.Cancelled);
        }
        if (_timeline.FramesWritten == 0)
        {
            Discard();
            return Outcome(DownloadOutcomeKind.Failed, DownloadText.Failed(_timeline.FramesSkipped > 0
                ? "kayıt cihazı aralığın başına kadar görüntü gönderdi, sonrası gelmedi"
                : "kayıt cihazı bu aralık için görüntü göndermedi"));
        }
        var result = _recorder.Complete().GetAwaiter().GetResult(); // finalized (moov) and checked by Mp4Inspector
        if (!result.Success || _recorder.WriteError is not null)
        {
            DeleteFile(path);
            return Outcome(DownloadOutcomeKind.Failed, DownloadText.Failed(result.Error ?? _recorder.WriteError ?? "dosya yazılamadı"), result);
        }
        if (stalled && LastWritten is { } last)
        {
            return Outcome(DownloadOutcomeKind.Incomplete, DownloadText.Incomplete(last), result);
        }
        return Outcome(DownloadOutcomeKind.Done, DownloadText.Done(path), result);
    }

    /// <summary>One session: [start, end) of one recording file, until it is written, quiet, or cancelled.</summary>
    DownloadState PlayPart(int userId, int channel, DateTime start, DateTime end, Action<DownloadProgressInfo>? progress,
        ref TimeSpan lastReport, CancellationToken ct)
    {
        var retries = 0;
        Open(userId, channel, start, end);
        var openWatch = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                _signal.WaitOne(200);
                if (ct.IsCancellationRequested)
                {
                    return DownloadState.Running;
                }
                if (_callbackError is { } error)
                {
                    throw new HikException(0, error);
                }
                // The recorder sometimes starts at the beginning of the recording file (minutes early): reopen before
                // decoding all that (nothing of this session was written yet: frames before its start are skipped).
                var seen = Interlocked.Read(ref _firstSeenTicks);
                if (seen != 0 && Interlocked.Read(ref _partWrittenTicks) == 0 && retries < MaxWrongStartRetries &&
                    PlaybackStart.IsWrong(start, new DateTime(seen)))
                {
                    retries++;
                    Close();
                    Open(userId, channel, start, end);
                    openWatch.Restart();
                    continue;
                }
                var quiet = TimeSpan.FromMilliseconds(_watch.ElapsedMilliseconds - Interlocked.Read(ref _lastActivityMs));
                var partLast = Interlocked.Read(ref _partWrittenTicks) is var ticks and > 0 ? new DateTime(ticks) : (DateTime?)null;
                var state = DownloadEnd.Decide(_partEnded || _ended, partLast, quiet, openWatch.Elapsed, end, null);
                if (progress is not null && _watch.Elapsed - lastReport >= TimeSpan.FromMilliseconds(250))
                {
                    lastReport = _watch.Elapsed;
                    progress(Measure());
                }
                if (state != DownloadState.Running)
                {
                    Trace?.Invoke($"PART {start:HH:mm:ss}-{end:HH:mm:ss} {state} quiet {quiet.TotalSeconds:0.0}");
                    return state;
                }
            }
        }
        finally
        {
            Close();
        }
    }

    DateTime? LastWritten => Interlocked.Read(ref _lastWrittenTicks) is var ticks and > 0 ? new DateTime(ticks) : null;

    DownloadProgressInfo Measure()
    {
        var first = Interlocked.Read(ref _firstWrittenTicks);
        var firstMs = Interlocked.Read(ref _firstWrittenMs);
        return _tracker.Measure(LastWritten, first > 0 ? new DateTime(first) : null,
            firstMs < 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(_watch.ElapsedMilliseconds - firstMs));
    }

    DownloadOutcome Outcome(DownloadOutcomeKind kind, string message, RecordingResult? result = null)
    {
        long bytes = 0;
        if (kind is DownloadOutcomeKind.Done or DownloadOutcomeKind.Incomplete)
        {
            try
            {
                bytes = new System.IO.FileInfo(_job.Path).Length;
            }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
            {
            }
        }
        var info = Measure();
        return new DownloadOutcome(kind, _job.Path, message, result?.Frames ?? 0, result?.Duration ?? TimeSpan.Zero,
            info.Speed, bytes, _recorder?.EncoderName, _timeline.Gaps, _watch.Elapsed);
    }

    /// <summary>Cancel / failure: no partial file is left behind.</summary>
    void Discard()
    {
        if (_recorder is not { } recorder)
        {
            return;
        }
        try
        {
            recorder.Complete().Wait(TimeSpan.FromSeconds(10));
        }
        catch (AggregateException)
        {
        }
        DeleteFile(_job.Path);
    }

    static void DeleteFile(string path)
    {
        try
        {
            System.IO.File.Delete(path);
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
        }
    }

    // ---- SDK session ----

    void Open(int userId, int channel, DateTime start, DateTime end)
    {
        Volatile.Write(ref _stop, new CancellationTokenSource());
        Interlocked.Exchange(ref _firstSeenTicks, 0);
        Interlocked.Exchange(ref _partWrittenTicks, 0);
        _partEnded = false;
        Touch();
        var vod = new HikNative.VodPara
        {
            Size = (uint)Marshal.SizeOf<HikNative.VodPara>(),
            IdInfo = new HikNative.StreamInfo
            {
                Size = (uint)Marshal.SizeOf<HikNative.StreamInfo>(),
                Id = new byte[HikNative.StreamIdLen],
                Channel = (uint)channel,
                Res = new byte[32],
            },
            BeginTime = HikNative.DvrTime.From(start),
            // Past the end: the recorder stops sending at the requested end and PlayCtrl keeps the last second undecoded
            // (it waits for the next frame); a frame after the end completes the part. The timeline cuts exactly.
            EndTime = HikNative.DvrTime.From(RequestEnd(end, DateTime.Now)),
            Wnd = IntPtr.Zero, // no SDK renderer, no SDK pacing
            DrawFrame = 0,
            StreamType = 0,    // main stream
            UseAsync = 0,
            Res2 = new byte[19],
        };
        int handle = HikNative.NET_DVR_PlayBackByTime_V40(userId, ref vod);
        if (handle < 0)
        {
            throw HikException.LastError($"İndirme oynatması açılamadı (kanal {channel})");
        }
        _playHandle = handle;
        _opened = true;
        if (!HikNative.NET_DVR_SetPlayDataCallBack_V40(handle, _data, IntPtr.Zero))
        {
            _playHandle = -1;
            HikNative.NET_DVR_StopPlayBack(handle);
            throw HikException.LastError("İndirme veri geri çağrısı kurulamadı");
        }
        uint length = 0;
        HikNative.NET_DVR_PlayBackControl_V40(handle, HikNative.PlayStart, IntPtr.Zero, 0, IntPtr.Zero, ref length);
    }

    /// <summary>Ends the current session (SDK playback and port). Every callback wait ends first (stop flag). Idempotent.</summary>
    void Close()
    {
        Volatile.Read(ref _stop).Cancel();
        int handle = _playHandle;
        _playHandle = -1;
        if (handle >= 0)
        {
            HikNative.NET_DVR_StopPlayBack(handle); // the SDK stops calling back (waits for a callback in progress)
        }
        int port = Interlocked.Exchange(ref _port, -1);
        // The stepping thread first (its stop flag is set): PlayM4_Stop while a PlayM4_OneByOne is in progress hangs.
        Interlocked.Exchange(ref _stepper, null)?.Join(TimeSpan.FromSeconds(2));
        // A decode callback that started before the stop flag may still be in a PlayCtrl call (system time): PlayM4_Stop
        // holds the port while it joins the decode thread, so such a call would never return. Let it leave first; later
        // callbacks see the flag and call nothing.
        var wait = Stopwatch.StartNew();
        while (Volatile.Read(ref _decoding) > 0 && wait.ElapsedMilliseconds < 2000)
        {
            Thread.Sleep(1);
        }
        if (port >= 0)
        {
            HikNative.PlayM4_Stop(port); // joins the decode thread (not waiting: the stop flag ended its wait)
            HikNative.PlayM4_CloseStream(port);
            HikNative.PlayM4_FreePort(port);
        }
    }

    CancellationToken StopToken => Volatile.Read(ref _stop).Token;

    void Touch() => Interlocked.Exchange(ref _lastActivityMs, _watch.ElapsedMilliseconds);

    void OnData(int handle, uint dataType, IntPtr buffer, uint size, IntPtr user)
    {
        try
        {
            if (handle != _playHandle)
            {
                return;
            }
            Touch();
            int port = Volatile.Read(ref _port);
            if (dataType == HikNative.SysHead)
            {
                if (port < 0)
                {
                    OpenPort(buffer, size);
                }
                return;
            }
            if (dataType != HikNative.StreamData || port < 0)
            {
                return;
            }
            var stop = StopToken;
            // Backpressure, never a drop: wait while the port has enough to decode, then until it takes the packet.
            while (!stop.IsCancellationRequested && HikNative.PlayM4_GetSourceBufferRemain(port) > QueueAhead)
            {
                Thread.Sleep(3);
            }
            while (!HikNative.PlayM4_InputData(port, buffer, size))
            {
                if (stop.IsCancellationRequested)
                {
                    return;
                }
                Thread.Sleep(2);
            }
            Touch();
        }
        catch (Exception e)
        {
            _callbackError = e.Message;
            _signal.Set();
        }
    }

    void OpenPort(IntPtr header, uint size)
    {
        int port = -1;
        if (!HikNative.PlayM4_GetPort(ref port))
        {
            throw HikException.LastError("İndirme için PlayCtrl portu alınamadı");
        }
        try
        {
            if (!string.IsNullOrEmpty(_key))
            {
                HikNative.PlayM4_SetSecretKey(port, HikNative.AesKeyType, _key, HikNative.AesKeyBits);
            }
            // Software decoding: the decode callback needs pictures in system memory. (Measured 21.10.2026: with the hardware
            // engine the callback still fires but with size 0 — no picture — so downloads / recordings cannot use the GPU.)
            HikNative.PlayM4_SetDecodeEngineEx(port, HikNative.SoftDecodeEngine);
            HikNative.PlayM4_SetStreamOpenMode(port, HikNative.StreamFile);
            if (!HikNative.PlayM4_OpenStream(port, header, size, PortBuffer))
            {
                throw new HikException(HikNative.PlayM4_GetLastError(port), "İndirme akışı açılamadı (PlayCtrl)");
            }
            HikNative.PlayM4_SetDecCBStream(port, HikNative.DecodeVideoOnly);
            if (!HikNative.PlayM4_SetDecCallBackMend(port, _decode, 0) || !HikNative.PlayM4_Play(port, IntPtr.Zero))
            {
                throw new HikException(HikNative.PlayM4_GetLastError(port), "İndirme kod çözücüsü başlatılamadı (PlayCtrl)");
            }
        }
        catch
        {
            HikNative.PlayM4_Stop(port);
            HikNative.PlayM4_CloseStream(port);
            HikNative.PlayM4_FreePort(port);
            throw;
        }
        Volatile.Write(ref _port, port);
        StartStepping(port, StopToken);
    }

    /// <summary>
    /// Decodes the port as fast as it can: one PlayM4_OneByOne step, then the next as soon as the picture arrived (or after
    /// <see cref="StepTimeoutMs"/>). Calling it in a tight loop starved the decoder (0.7×); one step per picture measured
    /// 7–30×. The thread ends with the session's stop flag, before Close stops the port.
    /// </summary>
    void StartStepping(int port, CancellationToken stop)
    {
        var thread = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                HikNative.PlayM4_OneByOne(port);
                WaitHandle.WaitAny([_frameEvent, stop.WaitHandle], StepTimeoutMs);
            }
        })
        {
            IsBackground = true,
            Name = "SPVision indirme adımı",
        };
        Volatile.Write(ref _stepper, thread);
        thread.Start();
    }

    Thread? _stepper;

    int _decoding; // decode callbacks in progress (Close waits for them before PlayM4_Stop)

    void OnDecoded(int port, IntPtr buffer, int size, ref HikNative.FrameInfo info, int user, int reserved)
    {
        Interlocked.Increment(ref _decoding);
        try
        {
            if (StopToken.IsCancellationRequested)
            {
                return; // closing: no PlayCtrl call from here on (PlayM4_Stop is joining this thread)
            }
            Touch();
            if (info.Type != HikNative.FrameTypeYv12 || _ended || _partEnded)
            {
                _frameEvent.Set();
                return;
            }
            var time = new HikNative.Pm4SystemTime();
            if (!HikNative.PlayM4_GetSystemTime(port, ref time) || time.ToDateTime() is not { } frameTime)
            {
                _frameEvent.Set();
                return; // no absolute time yet (the session's first picture): cannot place it in the range
            }
            Interlocked.CompareExchange(ref _firstSeenTicks, frameTime.Ticks, 0);
            var (decision, pts) = _timeline.Next(frameTime, (uint)info.Stamp, info.FrameRate);
            Trace?.Invoke($"F {frameTime:HH:mm:ss.fff} st {(uint)info.Stamp} n {info.FrameNum} {decision} {pts} q {_recorder!.Queued} src {HikNative.PlayM4_GetSourceBufferRemain(port)}");
            switch (decision)
            {
                case FrameDecision.Write:
                    if (Interlocked.Read(ref _firstWrittenTicks) == 0)
                    {
                        Interlocked.Exchange(ref _firstWrittenMs, _watch.ElapsedMilliseconds);
                        Interlocked.Exchange(ref _firstWrittenTicks, frameTime.Ticks);
                    }
                    // Waits while the encoder's queue is full (this decode thread only); the stop flag ends the wait.
                    if (_recorder!.AddYv12Waiting(buffer, size, info.Width, info.Height, pts, info.FrameRate, StopToken))
                    {
                        var written = (_timeline.LastWritten ?? frameTime).Ticks;
                        Interlocked.Exchange(ref _lastWrittenTicks, written);
                        Interlocked.Exchange(ref _partWrittenTicks, written);
                    }
                    break;
                case FrameDecision.PartEnd:
                    _partEnded = true;
                    _signal.Set();
                    break;
                case FrameDecision.End:
                    _ended = true;
                    _signal.Set();
                    break;
            }
            _frameEvent.Set(); // the next step
        }
        catch (Exception e)
        {
            // Never let an exception reach PlayCtrl's decode thread.
            _callbackError = e.Message;
            _signal.Set();
        }
        finally
        {
            Interlocked.Decrement(ref _decoding);
        }
    }
}
