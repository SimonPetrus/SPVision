using SPVision.Core;

namespace SPVision.Core.Tests;

public class RecordingDownloadTests
{
    static readonly DateTime Now = new(2026, 10, 21, 22, 0, 0);

    static DateTime T(int hour, int minute, int second = 0, int ms = 0) => new(2026, 10, 21, hour, minute, second, ms);

    static RecordingSegment Seg(DateTime start, DateTime end) => new(start, end, "");

    // ---- Range ----

    [Fact]
    public void Range_validation_needs_end_after_start_and_nothing_in_the_future()
    {
        Assert.Equal("Bitiş, başlangıçtan sonra olmalı.", DownloadRange.Validate(T(10, 0), T(10, 0), Now));
        Assert.Equal("Bitiş, başlangıçtan sonra olmalı.", DownloadRange.Validate(T(10, 5), T(10, 0), Now));
        Assert.Equal("Başlangıç gelecekte olamaz.", DownloadRange.Validate(T(22, 0, 1), T(22, 5), Now));
        Assert.StartsWith("Bitiş gelecekte olamaz", DownloadRange.Validate(T(21, 55), T(22, 5), Now));
        Assert.Null(DownloadRange.Validate(T(21, 55), T(22, 0), Now));
    }

    [Fact]
    public void Range_has_no_maximum_length()
    {
        Assert.Null(DownloadRange.Validate(T(10, 0).AddDays(-6), T(10, 0), Now)); // six days: one file per camera
    }

    [Fact]
    public void Default_range_is_the_playback_time_plus_minus_five_minutes()
    {
        var (start, end) = DownloadRange.Default(T(12, 30, 15, 640), T(0, 0), Now);
        Assert.Equal(T(12, 25, 15), start);
        Assert.Equal(T(12, 35, 15), end);
    }

    [Fact]
    public void Default_range_never_reaches_into_the_future()
    {
        var (start, end) = DownloadRange.Default(T(21, 58), T(0, 0), Now);
        Assert.Equal(Now, end);
        Assert.Equal(Now.AddMinutes(-10), start);
    }

    [Fact]
    public void Default_range_without_a_playback_time_is_the_selected_day()
    {
        Assert.Equal((new DateTime(2026, 10, 20), new DateTime(2026, 10, 20, 23, 59, 59)), DownloadRange.Default(null, new DateTime(2026, 10, 20), Now));
        Assert.Equal((new DateTime(2026, 10, 21), Now), DownloadRange.Default(null, new DateTime(2026, 10, 21), Now));
        var justAfterMidnight = new DateTime(2026, 10, 21, 0, 0, 0);
        Assert.Equal((justAfterMidnight.AddMinutes(-10), justAfterMidnight), DownloadRange.Default(null, justAfterMidnight, justAfterMidnight));
    }

    // ---- File names ----

    [Fact]
    public void File_name_is_camera_start_end()
    {
        Assert.Equal("DIS KAPI_2026-10-21_12-25-15_2026-10-21_12-35-15.mp4", DownloadNaming.FileName("DIS KAPI", T(12, 25, 15), T(12, 35, 15)));
        Assert.Equal("Ön_Kapı_2026-10-21_00-00-00_2026-10-22_00-00-00.mp4",
            DownloadNaming.FileName("Ön/Kapı", T(0, 0), T(0, 0).AddDays(1)));
    }

    [Fact]
    public void Camera_name_is_made_safe()
    {
        Assert.Equal("a_b_c_d", DownloadNaming.SafeName("a:b*c?d"));
        Assert.Equal("Kamera", DownloadNaming.SafeName("  "));
        Assert.Equal("Kamera", DownloadNaming.SafeName(null));
        Assert.Equal("KASA", DownloadNaming.SafeName(" KASA. "));
        Assert.Equal("ATÖLYE İÇİ", DownloadNaming.SafeName("ATÖLYE İÇİ")); // Turkish letters stay
    }

    [Fact]
    public void Existing_file_is_never_overwritten()
    {
        var taken = new HashSet<string> { Path.Combine("C:\\v", "x.mp4"), Path.Combine("C:\\v", "x (2).mp4") };
        Assert.Equal(Path.Combine("C:\\v", "x (3).mp4"), DownloadNaming.UniquePath("C:\\v", "x.mp4", taken.Contains));
        Assert.Equal(Path.Combine("C:\\v", "y.mp4"), DownloadNaming.UniquePath("C:\\v", "y.mp4", taken.Contains));
    }

    // ---- Sessions per recording file ----

    [Fact]
    public void Parts_are_the_recording_files_clipped_to_the_range()
    {
        var parts = DownloadParts.Split(T(10, 0), T(12, 0),
            [Seg(T(11, 0), T(13, 0)), Seg(T(9, 0), T(10, 30)), Seg(T(10, 30), T(11, 0)), Seg(T(13, 0), T(14, 0))]);
        Assert.Equal([(T(10, 0), T(10, 30)), (T(10, 30), T(11, 0)), (T(11, 0), T(12, 0))], parts);
    }

    [Fact]
    public void Overlapping_files_do_not_repeat_time_and_unknown_files_give_one_part()
    {
        var parts = DownloadParts.Split(T(10, 0), T(12, 0), [Seg(T(10, 0), T(11, 0)), Seg(T(10, 30), T(11, 30)), Seg(T(10, 40), T(11, 10))]);
        Assert.Equal([(T(10, 0), T(11, 0)), (T(11, 0), T(11, 30))], parts);
        Assert.Equal([(T(10, 0), T(12, 0))], DownloadParts.Split(T(10, 0), T(12, 0), null));
        Assert.Empty(DownloadParts.Split(T(10, 0), T(12, 0), [Seg(T(13, 0), T(14, 0))]));
    }

    // ---- Timeline ----

    [Fact]
    public void Frames_before_the_start_are_skipped_and_the_end_completes()
    {
        var timeline = new DownloadTimeline(T(12, 0), T(12, 0, 1));
        Assert.Equal(FrameDecision.Skip, timeline.Next(T(11, 59, 56, 190), 0, 25).Decision); // the key frame before the start
        Assert.Equal((FrameDecision.Write, 0L), timeline.Next(T(12, 0, 0, 30), 3840, 25));
        Assert.Equal((FrameDecision.Write, 40L), timeline.Next(T(12, 0, 0, 70), 3880, 25));
        Assert.Equal(FrameDecision.End, timeline.Next(T(12, 0, 1, 10), 4820, 25).Decision);
        Assert.True(timeline.Ended);
        Assert.Equal(FrameDecision.End, timeline.Next(T(12, 0, 0, 500), 4900, 25).Decision); // nothing after the end
        Assert.Equal(2, timeline.FramesWritten);
        Assert.Equal(1, timeline.FramesSkipped);
        Assert.Equal(T(12, 0, 0, 70), timeline.LastWritten);
    }

    [Fact]
    public void Steps_follow_the_stream_stamps_so_the_file_plays_at_real_speed()
    {
        var timeline = new DownloadTimeline(T(12, 0), T(13, 0));
        timeline.Next(T(12, 0, 0, 0), 1000, 15);
        Assert.Equal(67, timeline.Next(T(12, 0, 0, 0), 1067, 15).Pts);   // same OSD millisecond: the stamp decides
        Assert.Equal(133, timeline.Next(T(12, 0, 0, 133), 1133, 15).Pts);
        Assert.Equal(200, timeline.Next(T(12, 0, 0, 200), 0, 15).Pts);   // stamp restarted: the OSD difference
        Assert.Equal(267, timeline.Next(T(12, 0, 0, 200), 0, 15).Pts);   // neither moves: one nominal frame
    }

    [Fact]
    public void A_gap_without_recording_is_joined_without_a_frozen_picture()
    {
        var timeline = new DownloadTimeline(T(12, 0), T(13, 0));
        timeline.Next(T(12, 0), 0, 25);
        timeline.Next(T(12, 0, 0, 40), 40, 25);
        var (decision, pts) = timeline.Next(T(12, 10), 600_040, 25); // ten minutes without recording
        Assert.Equal(FrameDecision.Write, decision);
        Assert.Equal(80, pts);
        Assert.Equal(1, timeline.Gaps);
        Assert.Equal(TimeSpan.FromMinutes(10) - TimeSpan.FromMilliseconds(40), timeline.Skipped);
    }

    [Fact]
    public void Each_part_ends_at_its_file_end_and_the_next_one_continues_the_times()
    {
        var timeline = new DownloadTimeline(T(12, 0), T(13, 0));
        timeline.BeginPart(T(12, 0), T(12, 30));
        timeline.Next(T(12, 29, 59, 900), 50_000, 15);
        Assert.Equal(FrameDecision.PartEnd, timeline.Next(T(12, 30, 6, 790), 50_067, 15).Decision); // the recorder skipped ahead
        Assert.False(timeline.Ended);
        timeline.BeginPart(T(12, 30), T(13, 0));
        Assert.Equal(FrameDecision.Skip, timeline.Next(T(12, 29, 57), 100, 15).Decision); // the new session's key frame before its start
        var (decision, pts) = timeline.Next(T(12, 30, 0, 600), 3700, 15);
        Assert.Equal(FrameDecision.Write, decision);
        Assert.Equal(700, pts); // the OSD difference (a new session restarts the stamps)
        Assert.Equal(0, timeline.Gaps);
    }

    // ---- Progress ----

    [Fact]
    public void Progress_counts_recorded_time_only()
    {
        var tracker = new DownloadProgressTracker(T(10, 0), T(12, 0), [Seg(T(9, 0), T(10, 30)), Seg(T(11, 30), T(13, 0))]);
        Assert.Equal(TimeSpan.FromHours(1), tracker.Total);
        Assert.Equal(0.5, tracker.Fraction(T(10, 30)));
        Assert.Equal(0.5, tracker.Fraction(T(11, 15))); // inside the gap: no progress, no jump later
        Assert.Equal(0.75, tracker.Fraction(T(11, 45)));
        Assert.Equal(T(12, 0), tracker.LastRecordedEnd);
        Assert.False(tracker.IsEmpty);
        Assert.True(new DownloadProgressTracker(T(10, 0), T(12, 0), []).IsEmpty);
    }

    [Fact]
    public void Unknown_recordings_measure_the_whole_range()
    {
        var tracker = new DownloadProgressTracker(T(10, 0), T(11, 0), null);
        Assert.Equal(0.25, tracker.Fraction(T(10, 15)));
        Assert.Null(tracker.LastRecordedEnd is { } end && end < T(11, 0) ? end : null);
    }

    [Fact]
    public void Speed_and_estimate_come_from_the_recorded_time_per_wall_second()
    {
        var tracker = new DownloadProgressTracker(T(10, 0), T(10, 10), null);
        var info = tracker.Measure(T(10, 2), T(10, 0), TimeSpan.FromSeconds(10)); // 2 min in 10 s
        Assert.Equal(20, info.Percent);
        Assert.Equal(12, info.Speed!.Value, 3);
        Assert.Equal(40, info.Remaining!.Value.TotalSeconds, 3);
        var early = tracker.Measure(T(10, 0, 5), T(10, 0), TimeSpan.FromMilliseconds(500));
        Assert.Null(early.Speed);
        Assert.Null(early.Remaining);
        Assert.Equal(0, tracker.Measure(null, null, TimeSpan.Zero).Percent);
    }

    [Fact]
    public void Progress_text_is_percent_speed_and_estimate()
    {
        Assert.Equal("%42 · 6,3× · kalan ~1 dk 20 sn",
            DownloadText.Progress(new DownloadProgressInfo(0.425, 6.25, TimeSpan.FromSeconds(80), null)));
        Assert.Equal("%7", DownloadText.Progress(new DownloadProgressInfo(0.07, null, null, null)));
        Assert.Equal("%100 · 28×", DownloadText.Progress(new DownloadProgressInfo(1, 28.4, null, null)));
        Assert.Equal("45 sn", DownloadText.Duration(TimeSpan.FromSeconds(44.2)));
        Assert.Equal("1 sa 02 dk", DownloadText.Duration(TimeSpan.FromMinutes(62)));
    }

    // ---- End of stream ----

    [Fact]
    public void Download_ends_at_the_end_frame_or_when_quiet_near_the_last_recording()
    {
        Assert.Equal(DownloadState.Finished, DownloadEnd.Decide(true, null, TimeSpan.Zero, TimeSpan.Zero, T(13, 0), null));
        Assert.Equal(DownloadState.Running, DownloadEnd.Decide(false, T(12, 59, 58), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1), T(13, 0), null));
        Assert.Equal(DownloadState.Finished, DownloadEnd.Decide(false, T(12, 59, 58), TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1), T(13, 0), null));
        // The recording ends before the range: quiet at its end is the end.
        Assert.Equal(DownloadState.Finished, DownloadEnd.Decide(false, T(12, 29, 59), TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1), T(13, 0), T(12, 30)));
    }

    [Fact]
    public void Quiet_in_the_middle_is_a_stall_and_nothing_at_all_is_no_data()
    {
        Assert.Equal(DownloadState.Running, DownloadEnd.Decide(false, T(12, 10), TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1), T(13, 0), null));
        Assert.Equal(DownloadState.Stalled, DownloadEnd.Decide(false, T(12, 10), TimeSpan.FromSeconds(31), TimeSpan.FromMinutes(1), T(13, 0), null));
        Assert.Equal(DownloadState.Running, DownloadEnd.Decide(false, null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20), T(13, 0), null));
        Assert.Equal(DownloadState.NoData, DownloadEnd.Decide(false, null, TimeSpan.FromSeconds(26), TimeSpan.FromSeconds(26), T(13, 0), null));
        // Frames before the start are still being decoded (not quiet): wait.
        Assert.Equal(DownloadState.Running, DownloadEnd.Decide(false, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), T(13, 0), null));
    }

    // ---- Size estimate ----

    [Fact]
    public void Estimate_uses_the_writer_rate_and_keeps_a_reserve()
    {
        Assert.Equal(1920L * 1080 * 15 / 12, DownloadEstimate.BitsPerSecond(1920, 1080, 15));
        Assert.Equal(1_000_000, DownloadEstimate.BitsPerSecond(640, 360, 0)); // floor 1 Mb/s
        Assert.Equal((long)(300 * 2_592_000 / 8 * 1.02), DownloadEstimate.Bytes(TimeSpan.FromSeconds(300), 2_592_000));
        Assert.Equal(0, DownloadEstimate.Bytes(TimeSpan.Zero, 2_592_000));
        Assert.True(DownloadEstimate.Fits(1_000_000_000, 2_000_000_000));
        Assert.False(DownloadEstimate.Fits(1_000_000_000, 1_400_000_000)); // 512 MB stays free
        Assert.Equal("1,5 GB", DownloadEstimate.Text(1_610_612_736));
        Assert.Equal("95 MB", DownloadEstimate.Text(99_300_000));
    }
}

public class DownloadFormTests
{
    static readonly DateTime Today = new(2026, 10, 21);
    static readonly DateTime Now = Today.AddHours(22);

    [Fact]
    public void Range_picker_takes_start_then_end_and_swaps_an_earlier_end()
    {
        var pick = new DateRangePick(Today, Today);
        Assert.False(pick.Picking);
        Assert.Equal("Başlangıç gününe, sonra bitiş gününe tıklayın", pick.Hint);
        Assert.True(pick.Click(Today.AddDays(-3), Today));
        Assert.True(pick.Picking);
        Assert.Equal("Şimdi bitiş gününü seçin", pick.Hint);
        Assert.Equal((Today.AddDays(-3), Today.AddDays(-3)), (pick.Start, pick.End));
        Assert.True(pick.Click(Today.AddDays(-5), Today)); // earlier: swapped
        Assert.Equal((Today.AddDays(-5), Today.AddDays(-3)), (pick.Start, pick.End));
        Assert.False(pick.Picking);
        Assert.True(pick.Click(Today, Today)); // a third click starts again
        Assert.Equal((Today, Today), (pick.Start, pick.End));
        Assert.True(pick.Picking);
        Assert.False(pick.Click(Today.AddDays(1), Today)); // the future: nothing
        Assert.True(pick.Picking);
    }

    [Fact]
    public void Band_is_rounded_at_the_range_ends_and_at_week_edges()
    {
        // 14 Oct 2026 is a Wednesday (column 2) … 20 Oct a Tuesday (column 1).
        var pick = new DateRangePick(new DateTime(2026, 10, 14), new DateTime(2026, 10, 20));
        Assert.Equal(RangeBand.Start, pick.Band(new DateTime(2026, 10, 14), 2));
        Assert.Equal(RangeBand.Middle, pick.Band(new DateTime(2026, 10, 15), 3));
        Assert.Equal(RangeBand.End, pick.Band(new DateTime(2026, 10, 18), 6));      // Sunday: the row ends
        Assert.Equal(RangeBand.Start, pick.Band(new DateTime(2026, 10, 19), 0));    // Monday: the next row starts
        Assert.Equal(RangeBand.End, pick.Band(new DateTime(2026, 10, 20), 1));
        Assert.Equal(RangeBand.None, pick.Band(new DateTime(2026, 10, 21), 2));
        Assert.Equal(RangeBand.StartEnd, new DateRangePick(new DateTime(2026, 10, 18), new DateTime(2026, 10, 19)).Band(new DateTime(2026, 10, 18), 6));
        Assert.Equal(RangeBand.Single, new DateRangePick(Today, Today).Band(Today, 2));
        Assert.True(pick.IsEndpoint(new DateTime(2026, 10, 20)));
        Assert.False(pick.IsEndpoint(new DateTime(2026, 10, 16)));
    }

    [Theory]
    [InlineData("08:30:00", 8, 30, 0)]
    [InlineData("8:30:00", 8, 30, 0)]
    [InlineData(" 23:59:59 ", 23, 59, 59)]
    public void Time_field_reads_hours_minutes_seconds(string text, int h, int m, int s) =>
        Assert.Equal(new TimeSpan(h, m, s), DownloadTimeText.Parse(text));

    [Theory]
    [InlineData("24:00:00")]
    [InlineData("12:60:00")]
    [InlineData("12:00")]
    [InlineData("12:0:00")]
    [InlineData("ab:cd:ef")]
    [InlineData("")]
    [InlineData(null)]
    public void Invalid_time_field_is_refused(string? text) => Assert.Null(DownloadTimeText.Parse(text));

    [Fact]
    public void Summary_explains_why_download_is_off()
    {
        Assert.Equal("En az bir kamera seçin.", DownloadForm.Evaluate(Today, "10:00:00", Today, "10:10:00", 0, 1, null, Now).Text);
        Assert.Equal("Saatleri ss:dd:ss biçiminde yazın (ör. 08:30:00).", DownloadForm.Evaluate(Today, "10:00", Today, "10:10:00", 1, 1, null, Now).Text);
        var reversed = DownloadForm.Evaluate(Today, "10:10:00", Today, "10:00:00", 1, 1, null, Now);
        Assert.False(reversed.CanStart);
        Assert.Equal("Bitiş, başlangıçtan sonra olmalı.", reversed.Text);
        Assert.False(DownloadForm.Evaluate(Today, "21:55:00", Today, "22:05:00", 1, 1, null, Now).CanStart);
    }

    [Fact]
    public void Summary_shows_duration_cameras_size_and_free_space()
    {
        var summary = DownloadForm.Evaluate(Today.AddDays(-2), "10:00:00", Today, "13:10:00", 2, 2 * 324_000, 335L * 1024 * 1024 * 1024, Now);
        Assert.True(summary.CanStart);
        Assert.Equal(Today.AddDays(-2).AddHours(10), summary.Start);
        Assert.Equal(Today.AddHours(13).AddMinutes(10), summary.End);
        Assert.Equal("Süre: 2 gün 3 sa 10 dk  ·  2 kamera", summary.Text);
        Assert.StartsWith("Tahmini boyut: ", summary.SizeLine);
        Assert.EndsWith("Boş alan: 335,0 GB", summary.SizeLine);
    }

    [Fact]
    public void Download_is_off_when_the_files_would_not_fit()
    {
        var summary = DownloadForm.Evaluate(Today.AddDays(-6), "00:00:00", Today, "00:00:00", 4, 4 * 500_000, 10L * 1024 * 1024 * 1024, Now);
        Assert.False(summary.CanStart);
        Assert.EndsWith("Diskte yeterli boş alan yok.", summary.Text);
        Assert.NotNull(summary.SizeLine);
    }

    [Fact]
    public void Duration_text()
    {
        Assert.Equal("10 dk", DownloadForm.Duration(TimeSpan.FromMinutes(10)));
        Assert.Equal("1 sa", DownloadForm.Duration(TimeSpan.FromHours(1)));
        Assert.Equal("3 gün", DownloadForm.Duration(TimeSpan.FromDays(3)));
        Assert.Equal("30 sn", DownloadForm.Duration(TimeSpan.FromSeconds(30)));
        Assert.Equal("1 gün 5 dk", DownloadForm.Duration(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Rate_per_camera_comes_from_its_main_stream_picture()
    {
        Assert.Equal(1920.0 * 1080 * 15 / 12 / 8, DownloadForm.BytesPerSecond(1920, 1080, 1500, 4096));
        Assert.Equal(8_000_000 / 8.0, DownloadForm.BytesPerSecond(2688, 1520, 0, 6144)); // full frame rate (25), capped at 8 Mb/s
        Assert.Equal(4096 * 1000 / 8.0, DownloadForm.BytesPerSecond(0, 0, 0, 4096));       // size unknown: the camera's rate
        Assert.Equal(500_000, DownloadForm.BytesPerSecond(0, 0, 0, 0));
        Assert.Equal("2 / 5", DownloadForm.Count(2, 5));
    }
}

public class PlaybackTileToolsTests
{
    [Fact]
    public void Zoom_toggle_shows_the_centre_twice_as_large()
    {
        var zoom = DigitalZoom.None.Toggle();
        Assert.True(zoom.IsZoomed);
        Assert.Equal(2, zoom.Level);
        Assert.Equal((480, 270, 1440, 810), zoom.Region(1920, 1080));
        Assert.Equal(DigitalZoom.None, zoom.Toggle());
        Assert.Null(DigitalZoom.None.Region(1920, 1080));
    }

    [Fact]
    public void Wheel_zoom_keeps_the_point_under_the_cursor()
    {
        var zoom = DigitalZoom.None.Toggle().ZoomAt(2, 0.75, 0.5); // 4×, at three quarters of the width
        Assert.Equal(4, zoom.Level);
        // The picture point under the cursor was 0.25 + 0.75 × 0.5 = 0.625; it is still under it.
        var half = 0.5 / zoom.Level;
        Assert.Equal(0.625, zoom.CenterX - half + 0.75 * 2 * half, 6);
        Assert.Equal(DigitalZoom.None, zoom.ZoomAt(0.1, 0.5, 0.5)); // back to the whole picture
        Assert.Equal(DigitalZoom.MaxLevel, zoom.ZoomAt(100, 0.5, 0.5).Level);
    }

    [Fact]
    public void Zoomed_region_never_leaves_the_picture()
    {
        var zoom = DigitalZoom.None.Toggle().PanBy(5, 5); // dragged far right / down: the picture's top-left comes into view
        Assert.Equal((0, 0, 960, 540), zoom.Region(1920, 1080));
        var corner = DigitalZoom.None.Toggle().ZoomAt(1, 1, 1).PanBy(-3, -3); // dragged left / up: the bottom-right
        var region = corner.Region(1920, 1080)!.Value;
        Assert.Equal((960, 540, 1920, 1080), region);
        Assert.Equal(DigitalZoom.None, DigitalZoom.None.PanBy(0.3, 0.3)); // not zoomed: nothing to move
        var odd = new DigitalZoom(3, 0.5, 0.5).Region(1921, 1081)!.Value;
        Assert.True(odd.Left % 2 == 0 && odd.Top % 2 == 0 && (odd.Right - odd.Left) % 2 == 0);
    }

    [Fact]
    public void Tool_bar_sits_on_the_bottom_edge_of_the_video_full_width()
    {
        Assert.Equal(140, TileToolBar.MinWidth(4));
        Assert.Equal((100.0, 532.0, 640.0, 48.0), TileToolBar.Place(100, 20, 640, 560, 1.5, 4));
        Assert.Null(TileToolBar.Place(0, 0, 139, 400, 1, 4));   // too narrow for the buttons
        Assert.Null(TileToolBar.Place(0, 0, 400, 63, 1, 4));    // too low
        Assert.True(TileToolBar.Contains((10, 10, 100, 50), 10, 59.9));
        Assert.False(TileToolBar.Contains((10, 10, 100, 50), 110, 20));
    }

    [Fact]
    public void Tool_bar_lingers_briefly_after_the_mouse_left()
    {
        var t0 = new DateTime(2026, 10, 21, 12, 0, 0);
        Assert.Equal(3, TileToolBar.Owner(3, null, t0, t0));
        Assert.Equal(5, TileToolBar.Owner(5, 3, t0, t0));                         // another tile: at once
        Assert.Equal(3, TileToolBar.Owner(null, 3, t0, t0.AddMilliseconds(300))); // just left (onto the bar's edge…)
        Assert.Null(TileToolBar.Owner(null, 3, t0, t0.AddMilliseconds(400)));
        Assert.Null(TileToolBar.Owner(null, null, t0, t0));
    }
}
