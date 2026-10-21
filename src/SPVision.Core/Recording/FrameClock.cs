namespace SPVision.Core;

/// <summary>
/// Turns the decoder's per-frame timestamps (PlayCtrl <c>FRAME_INFO.nStamp</c>, milliseconds of the stream) into the
/// presentation times of a recording: the first frame is 0 and every later one strictly increases. The stamps
/// follow the recorder's stream, so a clip recorded at 4× playback still plays at normal speed. A stamp that goes
/// backwards, does not move, or jumps by more than <see cref="MaxGap"/> (stream restart, 32-bit wrap, a gap in the
/// recording) advances by one nominal frame instead, so the file never gets a frozen stretch or a time jump.
/// </summary>
public sealed class FrameClock
{
    /// <summary>Largest believable distance between two frames.</summary>
    public static TimeSpan MaxGap { get; } = TimeSpan.FromSeconds(3);

    const double DefaultFps = 25;

    long? _lastStamp;
    long _pts;

    /// <summary>Presentation time (ms) of the frame with the given stream stamp; frameRate 0 = unknown.</summary>
    public long Next(long stampMs, int frameRate = 0)
    {
        if (_lastStamp is not { } last)
        {
            _lastStamp = stampMs;
            _pts = 0;
            return 0;
        }
        var delta = stampMs - last;
        if (delta <= 0 || delta > (long)MaxGap.TotalMilliseconds)
        {
            delta = NominalInterval(frameRate);
        }
        _lastStamp = stampMs;
        _pts += delta;
        return _pts;
    }

    /// <summary>One frame at the stream's rate (25 fps when unknown or implausible), at least 1 ms.</summary>
    public static long NominalInterval(int frameRate) =>
        Math.Max(1, (long)Math.Round(1000 / (frameRate is >= 1 and <= 120 ? frameRate : DefaultFps)));
}
