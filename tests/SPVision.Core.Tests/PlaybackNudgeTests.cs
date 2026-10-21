using SPVision.Core;

namespace SPVision.Core.Tests;

/// <summary>Drift correction on the real frame time (SDK path) and the wrong-start check (midnight bug).</summary>
public class PlaybackNudgeTests
{
    static readonly DateTime Day = new(2026, 10, 21);

    static DateTime At(int hours, int minutes, double seconds = 0) => Day + new TimeSpan(hours, minutes, 0) + TimeSpan.FromSeconds(seconds);

    // The recording containing midnight began the evening before (as on DIS KAPI: 23:10:49 → 00:30:02).
    static readonly IReadOnlyList<RecordingSegment> Recordings =
    [
        new(Day.AddDays(-1) + new TimeSpan(23, 10, 49), Day + new TimeSpan(0, 30, 2), ""),
        new(Day + new TimeSpan(0, 30, 2), Day + new TimeSpan(23, 0, 0), ""),
    ];

    static readonly TimeSpan Tolerance = PlaybackSync.Tolerance(PlaybackSpeed.Normal);

    static SyncAction Decide(DateTime master, DateTime? tile, SyncNudge nudge = SyncNudge.None, double rate = 1, TimeSpan nudgeAge = default, bool canNudge = true) =>
        PlaybackSync.Decide(master, tile, false, Recordings, PlaybackSync.Tolerance(PlaybackSpeed.Normal) * Math.Max(1, rate), rate, canNudge, nudge, nudgeAge);

    [Fact]
    public void Key_frame_offsets_are_nudged_not_reseeked()
    {
        var master = At(0, 0, 10);
        Assert.Equal(SyncAction.None, Decide(master, At(0, 0, 8.5)));
        Assert.Equal(SyncAction.CatchUp, Decide(master, At(0, 0, 6.8)));   // started on the key frame 3.2 s early
        Assert.Equal(SyncAction.Hold, Decide(master, At(0, 0, 13)));
        Assert.Equal(SyncAction.None, Decide(master, null));
    }

    [Fact]
    public void Nudge_ends_when_in_step()
    {
        var master = At(0, 1, 0);
        Assert.Equal(SyncAction.None, Decide(master, At(0, 0, 58), SyncNudge.CatchUp));
        Assert.Equal(SyncAction.Resume, Decide(master, At(0, 0, 59.6), SyncNudge.CatchUp));
        Assert.Equal(SyncAction.Resume, Decide(master, At(0, 1, 1), SyncNudge.CatchUp)); // overshot: done too
        Assert.Equal(SyncAction.None, Decide(master, At(0, 1, 1.5), SyncNudge.Hold));
        Assert.Equal(SyncAction.Resume, Decide(master, At(0, 1, 0.4), SyncNudge.Hold));
    }

    [Fact]
    public void Wrong_start_far_off_is_reseeked()
    {
        // The DIS KAPI case: shared time 00:00:47, the tile shows 23:11:36 of the day before.
        Assert.Equal(SyncAction.Seek, Decide(At(0, 0, 47), Day.AddDays(-1) + new TimeSpan(23, 11, 36)));
        Assert.Equal(SyncAction.Seek, Decide(At(0, 0, 47), At(0, 0, 20)));
        Assert.Equal(SyncAction.Seek, Decide(At(0, 0, 47), At(0, 0, 20), SyncNudge.CatchUp));
    }

    [Fact]
    public void Stuck_nudge_becomes_a_seek()
    {
        Assert.Equal(SyncAction.Seek, Decide(At(0, 5), At(0, 4, 55), SyncNudge.CatchUp, nudgeAge: TimeSpan.FromSeconds(30)));
        Assert.Equal(SyncAction.None, Decide(At(0, 5), At(0, 4, 55), SyncNudge.CatchUp, nudgeAge: TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Limits_scale_with_fast_speeds()
    {
        // 8×: 60 s of recording behind is still a catch-up (limit 160 s), 3 s is within the 16 s tolerance.
        Assert.Equal(SyncAction.CatchUp, Decide(At(1, 0), At(0, 59), rate: 8));
        Assert.Equal(SyncAction.None, Decide(At(1, 0), At(0, 59, 57), rate: 8));
    }

    [Fact]
    public void Without_nudging_the_plain_rule_applies()
    {
        Assert.Equal(SyncAction.Seek, Decide(At(0, 0, 10), At(0, 0, 6.8), canNudge: false));
        Assert.Equal(SyncAction.None, Decide(At(0, 0, 10), At(0, 0, 9), canNudge: false));
    }

    [Fact]
    public void Gaps_still_park_and_resume()
    {
        var gap = Day + new TimeSpan(23, 30, 0);
        Assert.Equal(SyncAction.Park, Decide(gap, gap));
        Assert.Equal(SyncAction.Seek, PlaybackSync.Decide(At(1, 0), null, true, Recordings, Tolerance, 1, true, SyncNudge.None, default));
    }

    [Fact]
    public void Start_on_the_key_frame_before_is_right_file_start_is_wrong()
    {
        var requested = At(0, 0, 0);
        Assert.False(PlaybackStart.IsWrong(requested, Day.AddSeconds(-3.2)));             // probe: 23:59:56.8
        Assert.False(PlaybackStart.IsWrong(requested, requested.AddSeconds(40)));          // gap: next recording
        Assert.True(PlaybackStart.IsWrong(requested, Day.AddDays(-1) + new TimeSpan(23, 11, 4))); // DIS KAPI
        Assert.True(PlaybackStart.IsWrong(requested, Day.AddDays(-1) + new TimeSpan(23, 21, 0))); // KARŞILAMA
    }

    [Fact]
    public void Retry_target_follows_the_shared_clock()
    {
        var requested = At(0, 0, 0);
        Assert.Equal(At(0, 0, 3), PlaybackStart.RetryTarget(requested, TimeSpan.FromSeconds(3), 1, paused: false));
        Assert.Equal(At(0, 0, 12), PlaybackStart.RetryTarget(requested, TimeSpan.FromSeconds(3), 4, paused: false));
        Assert.Equal(requested, PlaybackStart.RetryTarget(requested, TimeSpan.FromSeconds(3), 1, paused: true));
    }
}
