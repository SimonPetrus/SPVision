using SPVision.Core;

namespace SPVision.Core.Tests;

/// <summary>
/// "Nothing before the requested time": hidden 4× catch-up from the recorder's key frame (SDK path), the shared-clock
/// floor, and the play / pause / stop transport rules.
/// </summary>
public class PlaybackCatchUpTests
{
    static readonly DateTime Midnight = new(2026, 10, 21);

    static DateTime At(double secondsFromMidnight) => Midnight + TimeSpan.FromSeconds(secondsFromMidnight);

    static CatchUpAction Decide(DateTime? frame, double elapsed = 0.5, bool canReopen = true) =>
        PlaybackCatchUp.Decide(Midnight, frame, TimeSpan.FromSeconds(elapsed), canReopen);

    [Fact]
    public void Key_frame_of_the_previous_day_is_fast_forwarded_hidden()
    {
        // Measured: 00:00:00 requested, first frame 23:59:56.193 (the OSD shows the previous day).
        Assert.Equal(CatchUpAction.FastForward, Decide(At(-3.807)));
        Assert.Equal(CatchUpAction.FastForward, Decide(At(-1.1)));
        Assert.Equal(CatchUpAction.Reveal, Decide(At(0.12)));
        Assert.Equal(CatchUpAction.Reveal, Decide(At(0.5)));
    }

    [Fact]
    public void The_last_second_is_played_hidden_at_normal_speed()
    {
        // A slow-down takes effect ~250 ms late (1.1 s of recording at 4×): normal speed from a second before.
        Assert.Equal(CatchUpAction.Settle, Decide(At(-1)));
        Assert.Equal(CatchUpAction.Settle, Decide(At(-0.007)));   // 23:59:59.993: the OSD still says 23:59:59
        Assert.Equal(CatchUpAction.Settle, Decide(Midnight));     // exactly at it: a few frames more
        Assert.Equal(CatchUpAction.Settle, Decide(At(0.1)));
    }

    [Fact]
    public void The_window_is_shown_once_its_only_renderer_is_a_few_frames_past_the_requested_time()
    {
        // Our port is the window's only renderer (the SDK gets no window): its frame time alone decides.
        Assert.Equal(CatchUpAction.Settle, Decide(At(0.119)));
        Assert.Equal(CatchUpAction.Reveal, Decide(At(0.12)));
        Assert.Equal(CatchUpAction.Reveal, Decide(At(0.2)));
    }

    [Fact]
    public void Waits_for_the_first_frame_time_then_gives_up_waiting()
    {
        Assert.Equal(CatchUpAction.WaitForTime, Decide(null, elapsed: 0.1));
        Assert.Equal(CatchUpAction.WaitForTime, Decide(null, elapsed: 3.9));
        Assert.Equal(CatchUpAction.Reveal, Decide(null, elapsed: 4));
    }

    [Fact]
    public void A_start_at_the_beginning_of_the_recording_file_is_reopened()
    {
        // DIS KAPI: 23:11:04 for a request of 00:00:00.
        var fileStart = Midnight - new TimeSpan(0, 48, 56);
        Assert.Equal(CatchUpAction.Reopen, Decide(fileStart));
        Assert.Equal(CatchUpAction.Reveal, Decide(fileStart, canReopen: false)); // retries used up: cannot catch up minutes
        Assert.Equal(CatchUpAction.FastForward, Decide(At(-29)));               // within a GOP-ish range: caught up
    }

    [Fact]
    public void A_later_start_shows_at_once_and_a_long_catch_up_is_cut_off()
    {
        Assert.Equal(CatchUpAction.Reveal, Decide(At(95))); // request inside a gap: the next recording
        Assert.Equal(CatchUpAction.FastForward, Decide(At(-20), elapsed: 9.9));
        Assert.Equal(CatchUpAction.Reveal, Decide(At(-20), elapsed: 10));
    }

    [Fact]
    public void Shared_clock_never_follows_a_tile_before_the_requested_time()
    {
        Assert.False(PlaybackCatchUp.MayFollow(At(-3.8), Midnight));
        Assert.True(PlaybackCatchUp.MayFollow(Midnight, Midnight));
        Assert.True(PlaybackCatchUp.MayFollow(At(42), Midnight));
    }

    [Fact]
    public void Transport_state_follows_time_pause_and_stop()
    {
        Assert.Equal(TransportState.NoTime, PlaybackTransport.State(hasTime: false, paused: false, stopped: false));
        Assert.Equal(TransportState.NoTime, PlaybackTransport.State(hasTime: false, paused: true, stopped: true));
        Assert.Equal(TransportState.Playing, PlaybackTransport.State(true, false, false));
        Assert.Equal(TransportState.Paused, PlaybackTransport.State(true, true, false));
        Assert.Equal(TransportState.Stopped, PlaybackTransport.State(true, false, true));
        Assert.Equal(TransportState.Stopped, PlaybackTransport.State(true, true, true));
    }

    [Fact]
    public void Stop_is_enabled_only_while_streams_are_open()
    {
        Assert.True(PlaybackTransport.CanStop(TransportState.Playing, anyCamera: true));
        Assert.True(PlaybackTransport.CanStop(TransportState.Paused, anyCamera: true));
        Assert.False(PlaybackTransport.CanStop(TransportState.Stopped, anyCamera: true));
        Assert.False(PlaybackTransport.CanStop(TransportState.NoTime, anyCamera: true));
        Assert.False(PlaybackTransport.CanStop(TransportState.Playing, anyCamera: false));
    }

    [Fact]
    public void Play_button_after_stop_restarts_at_the_kept_time()
    {
        Assert.Equal(PlayPauseAction.Restart, PlaybackTransport.PlayPause(TransportState.Stopped));
        Assert.Equal(PlayPauseAction.Start, PlaybackTransport.PlayPause(TransportState.NoTime));
        Assert.Equal(PlayPauseAction.Pause, PlaybackTransport.PlayPause(TransportState.Playing));
        Assert.Equal(PlayPauseAction.Resume, PlaybackTransport.PlayPause(TransportState.Paused));
        Assert.False(PlaybackTransport.ShowsPause(TransportState.Stopped)); // shows "play" after stop
        Assert.False(PlaybackTransport.ShowsPause(TransportState.Paused));
        Assert.True(PlaybackTransport.ShowsPause(TransportState.Playing));
    }

    [Fact]
    public void Tiles_do_not_open_streams_on_their_own_while_stopped()
    {
        Assert.False(PlaybackTransport.TilesJoin(TransportState.Stopped));
        Assert.False(PlaybackTransport.TilesJoin(TransportState.NoTime));
        Assert.True(PlaybackTransport.TilesJoin(TransportState.Playing));
        Assert.True(PlaybackTransport.TilesJoin(TransportState.Paused));
    }
}
