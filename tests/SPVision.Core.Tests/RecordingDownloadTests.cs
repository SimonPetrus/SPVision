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

    /// <summary>Types <paramref name="keys"/> one by one at the caret ('\b' = Backspace, '\x7f' = Delete).</summary>
    static TimeFieldEdit Type(TimeFieldEdit field, string keys)
    {
        foreach (var key in keys)
        {
            field = key switch
            {
                '\b' => TimeFieldMask.Backspace(field.Text, field.Caret, 0),
                '\x7f' => TimeFieldMask.Delete(field.Text, field.Caret, 0),
                _ => TimeFieldMask.Insert(field.Text, field.Caret, 0, key.ToString()),
            };
        }
        return field;
    }

    static readonly TimeFieldEdit Empty = new("", 0);

    [Fact]
    public void Time_field_puts_the_colons_in_while_typing()
    {
        Assert.Equal(new TimeFieldEdit("1", 1), Type(Empty, "1"));
        Assert.Equal(new TimeFieldEdit("17:", 3), Type(Empty, "17"));        // colon at once, caret after it
        Assert.Equal(new TimeFieldEdit("17:5", 4), Type(Empty, "175"));
        Assert.Equal(new TimeFieldEdit("17:52:", 6), Type(Empty, "1752"));
        Assert.Equal(new TimeFieldEdit("17:52:00", 8), Type(Empty, "175200"));
        Assert.Equal(new TimeFieldEdit("17:52:00", 8), Type(Empty, "1752009"));  // at most 6 digits
        Assert.Equal(new TimeFieldEdit("17:52:00", 8), Type(Empty, "17:52:00")); // typed colons are skipped
        Assert.Equal(new TimeFieldEdit("09:", 3), Type(Empty, "9:"));            // one-digit hour, then a colon
        Assert.Equal(new TimeFieldEdit("09:05:", 6), Type(Empty, "9:5:"));
        Assert.Equal(new TimeFieldEdit("", 0), Type(Empty, "a:"));               // letters ignored, nothing to pad
    }

    [Fact]
    public void Time_field_backspace_and_delete_step_over_the_colons()
    {
        Assert.Equal(new TimeFieldEdit("1", 1), Type(Empty, "17\b"));           // "17:" → "1" (not stuck on the colon)
        Assert.Equal(new TimeFieldEdit("17:5", 4), Type(Empty, "1752\b"));
        Assert.Equal(new TimeFieldEdit("", 0), Type(Empty, "175200\b\b\b\b\b\b\b"));
        // Caret just after the first colon of "17:52:00": Backspace removes the 7, the rest moves up.
        Assert.Equal(new TimeFieldEdit("15:20:0", 1), TimeFieldMask.Backspace("17:52:00", 3, 0));
        // Caret just before the colon: Delete removes the 5 behind it.
        Assert.Equal(new TimeFieldEdit("17:20:0", 3), TimeFieldMask.Delete("17:52:00", 2, 0));
        Assert.Equal(new TimeFieldEdit("17:52:00", 8), TimeFieldMask.Delete("17:52:00", 8, 0));
        // A selection is removed as a whole ("52" of "17:52:00").
        Assert.Equal(new TimeFieldEdit("17:00:", 3), TimeFieldMask.Backspace("17:52:00", 3, 2));
    }

    [Fact]
    public void Time_field_overwrites_inserts_and_pastes()
    {
        // Typing in the middle of a full field overwrites what follows the caret.
        Assert.Equal(new TimeFieldEdit("18:52:00", 3), TimeFieldMask.Insert("17:52:00", 1, 0, "8"));
        // A selection is replaced.
        Assert.Equal(new TimeFieldEdit("17:30:00", 6), TimeFieldMask.Insert("17:52:00", 3, 2, "30"));
        // The whole field selected and pasted over, in any spelling.
        foreach (var pasted in new[] { "175200", "17:52:00", "17.52.00", " 17:52:00 " })
        {
            Assert.Equal(new TimeFieldEdit("17:52:00", 8), TimeFieldMask.Insert("08:30:00", 0, 8, pasted));
        }
        Assert.Equal(new TimeFieldEdit("08:30:00", 8), TimeFieldMask.Insert("", 0, 0, "8:30:00"));
        Assert.Equal(new TimeFieldEdit("12:", 3), TimeFieldMask.Insert("", 0, 0, "12abc"));
        // Anything else (cut, undo) is masked again, the caret after the same digits.
        Assert.Equal(new TimeFieldEdit("17:52:0", 4), TimeFieldMask.Normalize("1752:0", 3));
    }

    [Theory]
    [InlineData("1752", "17:52:00")]
    [InlineData("17:52:", "17:52:00")]
    [InlineData("17", "17:00:00")]
    [InlineData("17:", "17:00:00")]
    [InlineData("9", "09:00:00")]
    [InlineData("175", "17:50:00")]
    [InlineData("17:5", "17:50:00")]
    [InlineData("175200", "17:52:00")]
    [InlineData("8:30:00", "08:30:00")]
    [InlineData("17.52.00", "17:52:00")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Time_field_completes_a_short_input_on_leave(string? text, string completed) =>
        Assert.Equal(completed, TimeFieldMask.Complete(text));

    [Fact]
    public void Time_field_validation_stays()
    {
        Assert.Equal(new TimeSpan(17, 52, 0), DownloadTimeText.ParseInput("17:52:"));
        Assert.Equal(new TimeSpan(9, 0, 0), DownloadTimeText.ParseInput("9"));
        Assert.Null(DownloadTimeText.ParseInput("24"));        // hour ≤ 23
        Assert.Null(DownloadTimeText.ParseInput("17:60:"));    // minutes ≤ 59
        Assert.Null(DownloadTimeText.ParseInput("17:59:60"));  // seconds ≤ 59
        Assert.Null(DownloadTimeText.ParseInput(""));
        Assert.True(DownloadForm.Evaluate(Today, "1000", Today, "10:10:", 1, 1, null, Now).CanStart);
    }

    [Fact]
    public void Summary_explains_why_download_is_off()
    {
        Assert.Equal("En az bir kamera seçin.", DownloadForm.Evaluate(Today, "10:00:00", Today, "10:10:00", 0, 1, null, Now).Text);
        Assert.Equal("Saatleri ss:dd:ss biçiminde yazın (ör. 08:30:00).", DownloadForm.Evaluate(Today, "25:00:00", Today, "10:10:00", 1, 1, null, Now).Text);
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
    static readonly DigitalZoom Twice = new(2, 0.5, 0.5);

    [Fact]
    public void Zoom_button_only_switches_the_mode()
    {
        var on = TileZoom.Off.Toggle();
        Assert.True(on.Mode);
        Assert.False(on.IsZoomed);                       // the picture stays at 1× until the wheel turns
        Assert.Null(on.View.Region(1920, 1080));          // 1× = the whole frame (no source rectangle)
        Assert.Equal(TileZoom.Off, on.Toggle());          // pressed again: off
        Assert.Equal(TileZoom.Off, TileZoom.Off.Wheel(120, 0.3, 0.3)); // mode off: the wheel does nothing
        Assert.Equal(TileZoom.Off, TileZoom.Off.Pan(0.2, 0.2));
    }

    [Fact]
    public void Zooming_back_to_1x_keeps_the_mode_on()
    {
        var zoom = TileZoom.Off.Toggle().Wheel(120, 0.3, 0.7).Wheel(240, 0.3, 0.7);
        Assert.True(zoom.IsZoomed);
        Assert.Equal(Math.Pow(DigitalZoom.WheelStep, 3), zoom.View.Level, 9);
        for (var i = 0; i < 10; i++)
        {
            zoom = zoom.Wheel(-120, 0.3, 0.7);
        }
        Assert.True(zoom.Mode);                          // 1× reached: still in zoom mode
        Assert.False(zoom.IsZoomed);
        Assert.Equal(DigitalZoom.None, zoom.View);
        Assert.Equal(zoom, zoom.Pan(0.3, 0.3));          // nothing to drag at 1×
        Assert.True(zoom.Wheel(120, 0.5, 0.5).IsZoomed); // and the wheel zooms again
        Assert.Equal(DigitalZoom.MaxLevel, zoom.Wheel(120 * 50, 0.5, 0.5).View.Level);
    }

    [Theory]
    [InlineData(0.5, 0.5)]
    [InlineData(0.25, 0.75)]
    [InlineData(0.9, 0.1)]
    [InlineData(0.62, 0.41)]
    public void Wheel_keeps_the_picture_pixel_under_the_cursor(double vx, double vy)
    {
        // The source pixel under the cursor on the rectangle PlayCtrl really gets (whole, even pixels), step by step in and out.
        var zoom = TileZoom.Off.Toggle();
        var anchor = ZoomGeometry.SourcePixel(zoom.View.Region(1920, 1080), 1920, 1080, vx, vy);
        Assert.Equal((vx * 1920, vy * 1080), anchor);
        foreach (var delta in new[] { 120, 120, 120, 240, 120, -120, 120, -240, -120 })
        {
            zoom = zoom.Wheel(delta, vx, vy);
            var now = ZoomGeometry.SourcePixel(zoom.View.Region(1920, 1080), 1920, 1080, vx, vy);
            Assert.InRange(now.X, anchor.X - 3, anchor.X + 3);
            Assert.InRange(now.Y, anchor.Y - 3, anchor.Y + 3);
        }
    }

    [Fact]
    public void Wheel_zoom_at_the_cursor_not_at_the_centre()
    {
        var zoom = TileZoom.Off.Toggle().Wheel(120 * 3, 0.75, 0.25).View; // ≈1.95×, cursor right / top
        var region = zoom.Region(1920, 1080)!.Value;
        // The point at 3/4 width, 1/4 height (1440, 270) stays under the cursor: the region is pulled right / up.
        Assert.True(region.Left > (1920 - (region.Right - region.Left)) / 2);
        Assert.True(region.Top < (1080 - (region.Bottom - region.Top)) / 2);
        var (x, y) = zoom.PictureAt(0.75, 0.25);
        Assert.Equal(0.75, x, 9);
        Assert.Equal(0.25, y, 9);
    }

    [Fact]
    public void Zoom_at_the_edge_is_clamped_to_the_picture()
    {
        // Cursor in the bottom-right corner: the corner stays there and the region ends at the picture's edge.
        var corner = TileZoom.Off.Toggle().Wheel(120 * 4, 1, 1).View.Region(1920, 1080)!.Value;
        Assert.Equal(1920, corner.Right);
        Assert.Equal(1080, corner.Bottom);
        var topLeft = TileZoom.Off.Toggle().Wheel(120 * 4, 0, 0).View.Region(1920, 1080)!.Value;
        Assert.Equal((0, 0), (topLeft.Left, topLeft.Top));
        // A drag never shows anything outside the picture.
        var zoom = new TileZoom(true, Twice);
        Assert.Equal((0, 0, 960, 540), zoom.Pan(5, 5).View.Region(1920, 1080));
        Assert.Equal((960, 540, 1920, 1080), zoom.Pan(-3, -3).View.Region(1920, 1080));
        var odd = new DigitalZoom(3, 0.5, 0.5).Region(1921, 1081)!.Value;
        Assert.True(odd.Left % 2 == 0 && odd.Top % 2 == 0 && (odd.Right - odd.Left) % 2 == 0);
    }

    [Fact]
    public void Cursor_maps_onto_the_picture_with_the_dpi_scale()
    {
        // Video area at screen pixel (300, 200), 640 × 360 DIP on a 150 % screen = 960 × 540 pixels.
        Assert.Equal((0.5, 0.5), ZoomGeometry.ViewPoint(300 + 480, 200 + 270, 300, 200, 640, 360, 1.5));
        Assert.Equal((0.25, 0.75), ZoomGeometry.ViewPoint(300 + 240, 200 + 405, 300, 200, 640, 360, 1.5));
        // Ignoring the scale (pixels ÷ DIPs) would have put this cursor at 0.375 / 1.125 instead.
        Assert.Null(ZoomGeometry.ViewPoint(299, 300, 300, 200, 640, 360, 1.5));       // left of the area
        Assert.Null(ZoomGeometry.ViewPoint(300 + 960, 300, 300, 200, 640, 360, 1.5)); // right of it
        Assert.Equal((0.25, -0.5), ZoomGeometry.ViewDelta(240, -270, 640, 360, 1.5));
    }

    [Fact]
    public void Letterboxed_picture_offsets_the_cursor()
    {
        // 16:9 picture in a 1000 × 1000 area: 1000 × 562.5, bars of 218.75 above and below.
        Assert.Equal((0.0, 218.75, 1000.0, 562.5), ZoomGeometry.PictureRect(1000, 1000, 16.0 / 9));
        Assert.Equal((0.5, 0.5), ZoomGeometry.ViewPoint(500, 500, 0, 0, 1000, 1000, 1, 16.0 / 9));
        Assert.Equal((0.25, 0.0), ZoomGeometry.ViewPoint(250, 218.75, 0, 0, 1000, 1000, 1, 16.0 / 9));
        Assert.Equal((0.25, 0.0), ZoomGeometry.ViewPoint(250, 100, 0, 0, 1000, 1000, 1, 16.0 / 9)); // on the bar: the edge
        // Pillarbox: 4:3 picture in a 1600 × 900 area, bars of 200 left and right.
        Assert.Equal((200.0, 0.0, 1200.0, 900.0), ZoomGeometry.PictureRect(1600, 900, 4.0 / 3));
        Assert.Equal((0.5, 0.5), ZoomGeometry.ViewPoint(800, 450, 0, 0, 1600, 900, 1, 4.0 / 3));
        Assert.Equal((0.0, 0.5), ZoomGeometry.ViewDelta(0, 450, 1600, 900, 1, 4.0 / 3));
        // Stretched (the SDK path): the area is the picture.
        Assert.Equal((0.0, 0.0, 1600.0, 900.0), ZoomGeometry.PictureRect(1600, 900, 0));
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

public class DownloadPillTests
{
    [Fact]
    public void Pill_is_440_wide_centred_and_shrinks_between_tabs_and_tools()
    {
        // Wide bar: the full 440 px.
        Assert.Equal(440, DownloadPill.Width(1416, 351, 960));
        // The tools come within 12 px of a 440 px pill: as wide as fits, symmetric about the centre (2 × (780 − 700 − 12)).
        Assert.Equal(136, DownloadPill.Width(1400, 351, 780));
        // The tabs are the closer side.
        Assert.Equal(300, DownloadPill.Width(1400, 538, 1200));
        // About the minimum window width on Canlı İzleme: only the mini pill fits; a little less and it is hidden.
        Assert.Equal(122, DownloadPill.Width(1060, 351, 603));
        Assert.Equal(0, DownloadPill.Width(1060, 351, 590));
        Assert.Equal(DownloadPillLayout.Full, DownloadPill.LayoutFor(440));
        Assert.Equal(DownloadPillLayout.Compact, DownloadPill.LayoutFor(250));
        Assert.Equal(DownloadPillLayout.Mini, DownloadPill.LayoutFor(130));
        Assert.Equal(DownloadPillLayout.Hidden, DownloadPill.LayoutFor(0));
    }

    [Fact]
    public void Pill_texts_while_running()
    {
        Assert.Equal("kamera 2 / 3", DownloadPill.Step(2, 3));
        Assert.Equal("İndiriliyor", DownloadPill.Step(1, 1));
        Assert.Equal("0%", DownloadPill.Percent(null));
        Assert.Equal("42%", DownloadPill.Percent(new DownloadProgressInfo(0.429, 6, null, null)));
        Assert.Equal("kalan 1:05", DownloadPill.Remaining(TimeSpan.FromSeconds(64.2)));
        Assert.Equal("kalan 0:09", DownloadPill.Remaining(TimeSpan.FromSeconds(9)));
        Assert.Equal("kalan 1:02:09", DownloadPill.Remaining(new TimeSpan(1, 2, 9)));
        Assert.Equal("", DownloadPill.Remaining(null));
    }

    [Fact]
    public void Result_pill_is_green_only_when_every_file_is_complete()
    {
        Assert.Equal((true, "İndirme bitti · 3 dosya kaydedildi"), DownloadPill.Result(new(3, 0, 0, 0, false)));
        Assert.Equal((true, "İndirme bitti · 1 dosya kaydedildi · 1 kamerada kayıt yok"), DownloadPill.Result(new(1, 0, 0, 1, false)));
        Assert.Equal((false, "İndirme bitti · 1 dosya eksik indirildi"), DownloadPill.Result(new(0, 1, 0, 0, false)));
        Assert.Equal((false, "İndirme bitti · 2 dosya kaydedildi · 1 dosya eksik indirildi"), DownloadPill.Result(new(2, 1, 0, 0, false)));
        Assert.Equal((false, "İndirme bitti · 1 dosya kaydedildi · 1 kamera indirilemedi"), DownloadPill.Result(new(1, 0, 1, 0, false)));
        Assert.Equal((false, "1 kamera indirilemedi"), DownloadPill.Result(new(0, 0, 1, 0, false)));
        Assert.Equal((false, "Bu aralıkta kayıt yok"), DownloadPill.Result(new(0, 0, 0, 2, false)));
        Assert.Equal((false, "İndirme iptal edildi"), DownloadPill.Result(new(0, 0, 0, 0, true)));
        Assert.Equal((false, "İndirme iptal edildi · 1 dosya kaydedildi"), DownloadPill.Result(new(1, 0, 0, 0, true)));
    }

    [Fact]
    public void Open_folder_selects_one_file_or_opens_the_folder()
    {
        Assert.Equal((@"C:\Kayit\a.mp4", true), DownloadPill.OpenTarget([@"C:\Kayit\a.mp4"], @"C:\Kayit"));
        Assert.Equal((@"C:\Kayit", false), DownloadPill.OpenTarget([@"C:\Kayit\a.mp4", @"C:\Kayit\b.mp4"], @"D:\x"));
        Assert.Equal((@"D:\x", false), DownloadPill.OpenTarget([], @"D:\x"));
    }
}
