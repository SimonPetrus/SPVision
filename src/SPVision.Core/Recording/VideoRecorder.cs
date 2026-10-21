using System.Buffers;
using System.Collections.Concurrent;

namespace SPVision.Core;

/// <summary>Outcome of a recording or clip, known once the file is finalized.</summary>
/// <param name="Success">The file is a complete MP4 with at least one frame (checked with <see cref="Mp4Inspector"/>).</param>
/// <param name="Error">Why there is no file (Turkish); null on success. A failed recording leaves no file behind.</param>
public sealed record RecordingResult(string Path, bool Success, long Frames, TimeSpan Duration, long DroppedFrames, string? Encoder, string? Error);

/// <summary>
/// Records decoded pictures to an MP4 in the background. <see cref="TryAddYv12(ReadOnlySpan{byte}, int, int, long, int)"/>
/// is meant for a decoder callback: it copies the picture into a pooled buffer and queues it, never waits (a full
/// queue drops the picture and counts it) and never throws. One encoder thread opens the <see cref="Mp4VideoWriter"/>
/// on the first picture (its size and rate) — trying the next encoder of the list if one cannot start — and writes
/// the rest. <see cref="Complete"/> stops accepting pictures; the returned task ends when the file is finalized and
/// verified (or deleted, with the reason, when nothing usable was recorded).
/// </summary>
public sealed class VideoRecorder
{
    sealed record Frame(byte[] Buffer, int Width, int Height, long Pts, int FrameRate);

    readonly BlockingCollection<Frame> _queue;
    readonly IReadOnlyList<string> _encoders;
    readonly FrameClock _clock = new();
    readonly TaskCompletionSource<RecordingResult> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly object _addLock = new();
    volatile bool _completing;
    long _dropped;

    public VideoRecorder(string path, IReadOnlyList<string>? encoders = null, int queueCapacity = 8)
    {
        Path = path;
        _encoders = encoders ?? Mp4VideoWriter.DefaultEncoders;
        _queue = new BlockingCollection<Frame>(new ConcurrentQueue<Frame>(), Math.Max(1, queueCapacity));
        Recordings.Track(_done.Task);
        var thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "SPVision kayıt kodlayıcı",
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
    }

    public string Path { get; }

    /// <summary>Ends when the file is finalized (see <see cref="Complete"/>).</summary>
    public Task<RecordingResult> Completion => _done.Task;

    public long DroppedFrames => Interlocked.Read(ref _dropped);

    /// <summary>
    /// Queues one YV12 picture (Y plane, then V, then U, no padding: PlayCtrl's decode callback format).
    /// False when it was dropped (queue full, size invalid, or the recording is completing).
    /// </summary>
    public bool TryAddYv12(ReadOnlySpan<byte> data, int width, int height, long stampMs, int frameRate)
    {
        if (_completing || width < 16 || height < 16)
        {
            return false;
        }
        var size = width * height * 3 / 2;
        if (data.Length < size)
        {
            return false;
        }
        // One producer at a time (decoder callbacks of one port are sequential); the lock only orders the clock.
        lock (_addLock)
        {
            var pts = _clock.Next(stampMs, frameRate);
            if (_completing || _queue.Count >= _queue.BoundedCapacity)
            {
                Interlocked.Increment(ref _dropped);
                return false;
            }
            var buffer = ArrayPool<byte>.Shared.Rent(size);
            data[..size].CopyTo(buffer);
            try
            {
                if (_queue.TryAdd(new Frame(buffer, width, height, pts, frameRate)))
                {
                    return true;
                }
            }
            catch (InvalidOperationException)
            {
                // Completed meanwhile.
            }
            ArrayPool<byte>.Shared.Return(buffer);
            Interlocked.Increment(ref _dropped);
            return false;
        }
    }

    /// <inheritdoc cref="TryAddYv12(ReadOnlySpan{byte}, int, int, long, int)"/>
    public unsafe bool TryAddYv12(IntPtr data, int length, int width, int height, long stampMs, int frameRate) =>
        data != IntPtr.Zero && length > 0 && TryAddYv12(new ReadOnlySpan<byte>((void*)data, length), width, height, stampMs, frameRate);

    /// <summary>No more pictures: the queued ones are written, the file finalized and checked. Idempotent.</summary>
    public Task<RecordingResult> Complete()
    {
        lock (_addLock)
        {
            if (!_completing)
            {
                _completing = true;
                _queue.CompleteAdding();
            }
        }
        return _done.Task;
    }

    unsafe void Run()
    {
        Mp4VideoWriter? writer = null;
        string? error = null;
        int encoderIndex = 0;
        try
        {
            foreach (var frame in _queue.GetConsumingEnumerable())
            {
                try
                {
                    if (error is not null)
                    {
                        continue; // failed: only give the buffers back
                    }
                    fixed (byte* y = frame.Buffer)
                    {
                        var v = y + frame.Width * frame.Height;
                        var u = v + frame.Width / 2 * (frame.Height / 2);
                        while (true)
                        {
                            try
                            {
                                writer ??= Mp4VideoWriter.Create(Path, frame.Width, frame.Height, frame.FrameRate, _encoders[encoderIndex]);
                                writer.WriteI420(y, frame.Width, u, frame.Width / 2, v, frame.Width / 2, frame.Width, frame.Height, frame.Pts);
                                break;
                            }
                            catch (InvalidOperationException e)
                            {
                                if (writer is { FramesWritten: > 0 })
                                {
                                    error = e.Message; // the file has frames: keep them, stop here
                                    break;
                                }
                                // This encoder cannot start here (no such GPU…): next one, same picture.
                                writer?.Dispose();
                                writer = null;
                                Mp4VideoWriter.TryDelete(Path);
                                if (++encoderIndex >= _encoders.Count)
                                {
                                    error = $"görüntü kodlanamadı ({e.Message})";
                                    break;
                                }
                            }
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(frame.Buffer);
                }
            }
            writer?.Finish();
        }
        catch (Exception e)
        {
            error ??= e.Message; // e.g. FFmpeg not loaded: reported, never an unobserved crash of this thread
        }
        try
        {
            writer?.Dispose();
            _done.TrySetResult(Verify(writer, error));
        }
        catch (Exception e)
        {
            _done.TrySetResult(new RecordingResult(Path, false, 0, TimeSpan.Zero, DroppedFrames, writer?.EncoderName, e.Message));
        }
    }

    RecordingResult Verify(Mp4VideoWriter? writer, string? error)
    {
        var dropped = DroppedFrames;
        if (writer is null || writer.FramesWritten == 0)
        {
            Mp4VideoWriter.TryDelete(Path);
            return new RecordingResult(Path, false, 0, TimeSpan.Zero, dropped, writer?.EncoderName, error ?? "görüntü gelmedi, hiç kare kaydedilmedi");
        }
        var info = Mp4Inspector.Inspect(Path);
        if (!info.IsValid)
        {
            Mp4VideoWriter.TryDelete(Path);
            return new RecordingResult(Path, false, writer.FramesWritten, TimeSpan.Zero, dropped, writer.EncoderName,
                $"dosya geçersiz: {info.Problem}" + (error is null ? "" : $" ({error})"));
        }
        return new RecordingResult(Path, true, info.VideoSamples, info.Duration, dropped, writer.EncoderName, null);
    }
}

/// <summary>User messages about recordings and clips (status bar / toast).</summary>
public static class RecordingText
{
    public static string Started(bool clip, string path) => clip ? $"Klip kaydediliyor: {path}" : $"Kayıt başladı: {path}";

    public static string Finishing(bool clip) => clip ? "Klip tamamlanıyor…" : "Kayıt tamamlanıyor…";

    public static string Finished(RecordingResult result, bool clip) =>
        result.Success
            ? $"{(clip ? "Klip" : "Kayıt")} kaydedildi: {result.Path}"
            : $"{(clip ? "Klip" : "Kayıt")} kaydedilemedi: {result.Error}";

    public static string StartFailed(bool clip, string reason) => $"{(clip ? "Klip" : "Kayıt")} başlatılamadı: {reason}";
}

/// <summary>
/// Files being finalized, app-wide: the window close waits for them (<see cref="WhenAllFinished"/>) so no recording is
/// cut off by the process exit. Also checks files written by someone else (Flyleaf's remuxer).
/// </summary>
public static class Recordings
{
    static readonly ConcurrentDictionary<Task, byte> Pending = new();

    /// <summary>Registers a finalization; it leaves the list when it ends.</summary>
    public static Task<RecordingResult> Track(Task<RecordingResult> finishing)
    {
        Pending.TryAdd(finishing, 0);
        finishing.ContinueWith(t => Pending.TryRemove(t, out _), TaskScheduler.Default);
        return finishing;
    }

    /// <summary>Ends when every tracked file is finalized, or after <paramref name="timeout"/>; true when all finished.</summary>
    public static async Task<bool> WhenAllFinished(TimeSpan timeout)
    {
        var all = Task.WhenAll(Pending.Keys.ToArray());
        return await Task.WhenAny(all, Task.Delay(timeout)).ConfigureAwait(false) == all;
    }

    /// <summary>
    /// Checks a file another writer finished (Flyleaf's MP4 remuxer): valid → success; empty or broken → deleted,
    /// with the reason. Runs off the calling thread, after a short pause for the writer's last flush.
    /// </summary>
    public static Task<RecordingResult> VerifyAsync(string path) => Track(Task.Run(async () =>
    {
        await Task.Delay(300).ConfigureAwait(false);
        var info = Mp4Inspector.Inspect(path);
        if (info.IsValid)
        {
            return new RecordingResult(path, true, info.VideoSamples, info.Duration, 0, null, null);
        }
        Mp4VideoWriter.TryDelete(path);
        return new RecordingResult(path, false, 0, TimeSpan.Zero, 0, null, info.Problem);
    }));

    /// <summary>Removes a file a failed start left empty (after a short pause, in the background).</summary>
    public static Task DeleteIfEmptyAsync(string path) => Task.Run(async () =>
    {
        await Task.Delay(500).ConfigureAwait(false);
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length == 0)
            {
                File.Delete(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    });
}
