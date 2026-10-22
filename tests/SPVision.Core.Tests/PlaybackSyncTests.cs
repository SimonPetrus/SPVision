using SPVision.Core;

namespace SPVision.Core.Tests;

public class PlaybackSyncTests
{
    static readonly Guid Device = Guid.NewGuid();
    static readonly DateTime Day = new(2026, 10, 21);

    static CameraRef Cam(int channel) => new(Device, channel);

    static DateTime At(int hours, int minutes, int seconds = 0) => Day + new TimeSpan(hours, minutes, seconds);

    static RecordingSegment Segment(DateTime start, DateTime end) => new(start, end, "");

    // ---- Camera picks ----

    [Fact]
    public void PickFillsTheFirstEmptyTileAndFocusesIt()
    {
        var slots = new PlaybackSlots(4);

        Assert.Equal(0, slots.Pick(Cam(1)));
        Assert.Equal(1, slots.Pick(Cam(2)));

        Assert.Equal(new CameraRef?[] { Cam(1), Cam(2), null, null }, slots.Slots);
        Assert.Equal(1, slots.Focus);
        Assert.Equal(2, slots.Count);
    }

    [Fact]
    public void PickingACameraInTheGridRemovesItAndLeavesTheTileEmpty()
    {
        var slots = new PlaybackSlots(4);
        slots.Pick(Cam(1));
        slots.Pick(Cam(2));
        slots.Pick(Cam(3));
        slots.SetFocus(0);

        Assert.Equal(1, slots.Pick(Cam(2)));

        Assert.Equal(new CameraRef?[] { Cam(1), null, Cam(3), null }, slots.Slots);
        Assert.Equal(0, slots.Focus); // focus untouched

        // The next camera goes into the first empty tile: the hole.
        Assert.Equal(1, slots.Pick(Cam(4)));
        Assert.Equal(new CameraRef?[] { Cam(1), Cam(4), Cam(3), null }, slots.Slots);
    }

    [Fact]
    public void RemovingTheFocusedCameraMovesFocusToTheNextCamera()
    {
        var slots = new PlaybackSlots(4);
        slots.Pick(Cam(1));
        slots.Pick(Cam(2));
        slots.Pick(Cam(3));
        slots.SetFocus(1);

        slots.Pick(Cam(2));
        Assert.Equal(2, slots.Focus);

        slots.Pick(Cam(3));
        Assert.Equal(0, slots.Focus); // nothing after it: the one before

        slots.Pick(Cam(1));
        Assert.Equal(0, slots.Count);
        Assert.Null(slots.Focused);
    }

    [Fact]
    public void PickWhenFullReplacesTheFocusedTile()
    {
        var slots = new PlaybackSlots(4);
        for (var i = 1; i <= 4; i++)
        {
            slots.Pick(Cam(i));
        }
        slots.SetFocus(2);

        Assert.Equal(2, slots.Pick(Cam(9)));

        Assert.Equal(new CameraRef?[] { Cam(1), Cam(2), Cam(9), Cam(4) }, slots.Slots);
        Assert.Equal(2, slots.Focus);
    }

    [Fact]
    public void EmptyTilesCannotTakeTheFocus()
    {
        var slots = new PlaybackSlots(4);
        slots.Pick(Cam(1));

        Assert.False(slots.SetFocus(3));
        Assert.False(slots.SetFocus(9));
        Assert.Equal(0, slots.Focus);
    }

    [Fact]
    public void ShrinkingMovesCamerasIntoFreeTilesAndKeepsFocus()
    {
        var slots = new PlaybackSlots(9);
        slots.Pick(Cam(1));
        for (var i = 2; i <= 9; i++)
        {
            slots.Pick(Cam(i));
        }
        foreach (var channel in new[] { 2, 3, 4, 5 })
        {
            slots.Pick(Cam(channel)); // tiles 1-4 empty
        }
        slots.SetFocus(8);

        slots.Resize(4);

        // The focused camera (9) moves first, then the others in order; 8 no longer fits.
        Assert.Equal(new CameraRef?[] { Cam(1), Cam(9), Cam(6), Cam(7) }, slots.Slots);
        Assert.Equal(Cam(9), slots.Focused);
    }

    [Fact]
    public void ShrinkingWithoutRoomDropsTheOverflowAndRefocuses()
    {
        var slots = new PlaybackSlots(4);
        for (var i = 1; i <= 4; i++)
        {
            slots.Pick(Cam(i));
        }
        slots.SetFocus(3);

        slots.Resize(1);

        Assert.Equal(new CameraRef?[] { Cam(1) }, slots.Slots);
        Assert.Equal(0, slots.Focus);
    }

    [Fact]
    public void ResizeKeepsTheFocusedCameraWhereverItMoved()
    {
        var slots = new PlaybackSlots(6);
        slots.Pick(Cam(1));
        slots.Pick(Cam(2));
        slots.Pick(Cam(3));
        slots.Pick(Cam(2)); // tile 1 empty
        slots.Pick(Cam(4)); // goes to tile 1
        slots.Pick(Cam(5));
        slots.Pick(Cam(6));
        slots.Pick(Cam(7)); // tile 5
        slots.Pick(Cam(3)); // tile 2 empty again
        slots.SetFocus(5);

        slots.Resize(4);

        Assert.Equal(new CameraRef?[] { Cam(1), Cam(4), Cam(7), Cam(5) }, slots.Slots);
        Assert.Equal(Cam(7), slots.Focused);
    }

    [Fact]
    public void GrowingAddsEmptyTiles()
    {
        var slots = new PlaybackSlots(1);
        slots.Pick(Cam(1));

        slots.Resize(4);

        Assert.Equal(new CameraRef?[] { Cam(1), null, null, null }, slots.Slots);
        Assert.Equal(0, slots.Focus);
    }

    [Fact]
    public void RemoveDropsEveryCameraOfADevice()
    {
        var other = Guid.NewGuid();
        var slots = new PlaybackSlots(4);
        slots.Pick(Cam(1));
        slots.Pick(new CameraRef(other, 1));
        slots.Pick(Cam(2));

        slots.Remove(c => c.DeviceId == Device);

        Assert.Equal(new CameraRef?[] { null, new CameraRef(other, 1), null, null }, slots.Slots);
        Assert.Equal(1, slots.Focus);
    }

    // ---- Drag and drop ----

    /// <summary>The first tiles hold these channels (picked in order), the rest are empty.</summary>
    static PlaybackSlots Filled(int tileCount, params int[] channels)
    {
        var slots = new PlaybackSlots(tileCount);
        foreach (var channel in channels)
        {
            slots.Pick(Cam(channel));
        }
        return slots;
    }

    [Fact]
    public void DroppingANewCameraOnAnEmptyTileFillsItAndFocusesIt()
    {
        var slots = Filled(4, 1, 2);

        var drop = slots.Place(3, Cam(7));

        Assert.Equal(new SlotDrop(SlotDropKind.Placed, 3), drop);
        Assert.Equal(new CameraRef?[] { Cam(1), Cam(2), null, Cam(7) }, slots.Slots);
        Assert.Equal(3, slots.Focus);
    }

    [Fact]
    public void DroppingANewCameraOnAFilledTileReplacesItsCamera()
    {
        var slots = Filled(4, 1, 2, 3);
        slots.SetFocus(0);

        var drop = slots.Place(1, Cam(7));

        Assert.Equal(SlotDropKind.Placed, drop.Kind);
        Assert.Equal(Cam(2), drop.Removed); // left the grid (its row is unchecked)
        Assert.Equal(new CameraRef?[] { Cam(1), Cam(7), Cam(3), null }, slots.Slots);
        Assert.Equal(1, slots.Focus);
        Assert.Equal(-1, slots.IndexOf(Cam(2)));
    }

    [Fact]
    public void DroppingACameraThatIsInAnotherTileSwapsTheTiles()
    {
        var slots = Filled(4, 1, 2, 3);

        var drop = slots.Place(0, Cam(3));

        Assert.Equal(new SlotDrop(SlotDropKind.Swapped, 0, 2), drop);
        Assert.Equal(new CameraRef?[] { Cam(3), Cam(2), Cam(1), null }, slots.Slots);
        Assert.Equal(0, slots.Focus);
    }

    [Fact]
    public void DroppingACameraOfTheGridOnAnEmptyTileMovesIt()
    {
        var slots = Filled(4, 1, 2);

        var drop = slots.Place(3, Cam(1));

        Assert.Equal(new SlotDrop(SlotDropKind.Swapped, 3, 0), drop);
        Assert.Equal(new CameraRef?[] { null, Cam(2), null, Cam(1) }, slots.Slots);
        Assert.Equal(3, slots.Focus);
    }

    [Fact]
    public void ACameraNeverShowsTwiceAfterAnyDrop()
    {
        var slots = Filled(9, 1, 2, 3, 4);
        var random = new Random(7);
        for (var i = 0; i < 500; i++)
        {
            if (random.Next(2) == 0)
            {
                slots.Place(random.Next(9), Cam(random.Next(1, 8)));
            }
            else
            {
                slots.Swap(random.Next(9), random.Next(9));
            }
            var cameras = slots.Slots.OfType<CameraRef>().ToList();
            Assert.Equal(cameras.Count, cameras.Distinct().Count());
            Assert.True(slots.Focused is not null || slots.Count == 0);
        }
    }

    [Fact]
    public void DroppingACameraOnItsOwnTileChangesNothingButTheFocus()
    {
        var slots = Filled(4, 1, 2);
        slots.SetFocus(0);

        Assert.Equal(SlotDropKind.None, slots.Place(1, Cam(2)).Kind);

        Assert.Equal(new CameraRef?[] { Cam(1), Cam(2), null, null }, slots.Slots);
        Assert.Equal(1, slots.Focus);
    }

    [Fact]
    public void DropsOutsideTheGridAreIgnored()
    {
        var slots = Filled(4, 1);

        Assert.Equal(SlotDrop.None, slots.Place(4, Cam(2)));
        Assert.Equal(SlotDrop.None, slots.Place(-1, Cam(2)));
        Assert.False(slots.Swap(0, 4));
        Assert.False(slots.Swap(-1, 0));

        Assert.Equal(new CameraRef?[] { Cam(1), null, null, null }, slots.Slots);
        Assert.Equal(0, slots.Focus);
    }

    [Fact]
    public void DraggingATileOntoAnotherSwapsThemAndTheFocusFollowsTheDraggedCamera()
    {
        var slots = Filled(4, 1, 2, 3);
        slots.SetFocus(0);

        Assert.True(slots.Swap(0, 2));

        Assert.Equal(new CameraRef?[] { Cam(3), Cam(2), Cam(1), null }, slots.Slots);
        Assert.Equal(2, slots.Focus);
        Assert.Equal(Cam(1), slots.Focused);
    }

    [Fact]
    public void DraggingATileOntoAnEmptyTileMovesIt()
    {
        var slots = Filled(4, 1, 2);

        Assert.True(slots.Swap(1, 3));

        Assert.Equal(new CameraRef?[] { Cam(1), null, null, Cam(2) }, slots.Slots);
        Assert.Equal(3, slots.Focus);
    }

    [Fact]
    public void SwappingAnEmptyTileWithAFilledOneKeepsTheFocusOnACamera()
    {
        var slots = Filled(4, 1, 2);
        slots.SetFocus(1);

        Assert.True(slots.Swap(3, 1)); // the empty tile 3 "dragged" onto tile 1: camera 2 moves to 3

        Assert.Equal(new CameraRef?[] { Cam(1), null, null, Cam(2) }, slots.Slots);
        Assert.Equal(3, slots.Focus);
        Assert.Equal(Cam(2), slots.Focused);
    }

    [Fact]
    public void SwapOfTheSameTileOrTwoEmptyTilesDoesNothing()
    {
        var slots = Filled(4, 1);

        Assert.False(slots.Swap(0, 0));
        Assert.False(slots.Swap(2, 3));

        Assert.Equal(new CameraRef?[] { Cam(1), null, null, null }, slots.Slots);
    }

    // ---- Jump target resolution ----

    static readonly RecordingSegment[] Recordings =
    [
        Segment(At(0, 0), At(3, 42)),
        Segment(At(3, 46, 48), At(10, 52, 12)),
    ];

    [Fact]
    public void RecordedTimeIsPlayedAsIs()
    {
        var result = RecordingJump.Resolve(Recordings, At(9, 41));

        Assert.Equal(JumpKind.Exact, result.Kind);
        Assert.Equal(At(9, 41), result.Target);
        Assert.Null(result.Message);
    }

    [Fact]
    public void GapJumpsToTheNextRecordingWithAmberMessage()
    {
        var result = RecordingJump.Resolve(Recordings, At(3, 44));

        Assert.Equal(JumpKind.NextRecording, result.Kind);
        Assert.Equal(At(3, 46, 48), result.Target);
        Assert.Equal("Bu saatte kayıt yok, en yakın kayda atlandı (03:46:48)", result.Message);
    }

    [Fact]
    public void AfterTheLastRecordingNothingIsPlayed()
    {
        var result = RecordingJump.Resolve(Recordings, At(11, 0));

        Assert.Equal(JumpKind.NoneAfter, result.Kind);
        Assert.Equal(At(10, 52, 12), result.LastEnd);
        Assert.Equal("Bu saatten sonra kayıt yok (son kayıt 10:52)", result.Message);
    }

    [Fact]
    public void SegmentEndIsNotRecordedAndUnsortedSegmentsWork()
    {
        var reversed = Recordings.Reverse().ToArray();

        Assert.Equal(JumpKind.NextRecording, RecordingJump.Resolve(reversed, At(3, 42)).Kind);
        Assert.Equal(JumpKind.Exact, RecordingJump.Resolve(reversed, At(0, 0)).Kind);
        Assert.Equal(At(3, 46, 48), RecordingJump.NextStart(reversed, At(1, 0)));
        Assert.Null(RecordingJump.NextStart(reversed, At(4, 0)));
    }

    [Fact]
    public void NoRecordingsAtAll()
    {
        var result = RecordingJump.Resolve([], At(9, 0));

        Assert.Equal(JumpKind.NoRecordings, result.Kind);
        Assert.Equal(PlaybackText.NoRecordingsToday, result.Message);
    }

    [Theory]
    [InlineData("09", "41", 9, 41)]
    [InlineData("0", "0", 0, 0)]
    [InlineData("23", "59", 23, 59)]
    public void ValidTimeOfDayParses(string hours, string minutes, int h, int m)
    {
        Assert.Equal(new TimeSpan(h, m, 0), PlaybackText.ParseTimeOfDay(hours, minutes));
    }

    [Theory]
    [InlineData("24", "00")]
    [InlineData("12", "60")]
    [InlineData("", "10")]
    [InlineData("1a", "10")]
    [InlineData("-1", "10")]
    [InlineData(null, "10")]
    public void InvalidTimeOfDayIsRejected(string? hours, string minutes)
    {
        Assert.Null(PlaybackText.ParseTimeOfDay(hours, minutes));
    }

    // ---- Speed steps ----

    [Fact]
    public void SpeedStepsRunFromOneEighthToEightTimes()
    {
        Assert.Equal(
            new[] { "1/8×", "1/4×", "1/2×", "1×", "2×", "4×", "8×" },
            Enumerable.Range(0, 7).Select(PlaybackSpeed.Label));
        Assert.Equal(new[] { 0.125, 0.25, 0.5, 1, 2, 4, 8 }, Enumerable.Range(0, 7).Select(PlaybackSpeed.Rate));
        Assert.Equal("1×", PlaybackSpeed.Label(PlaybackSpeed.Normal));
    }

    [Fact]
    public void SpeedButtonsStopAtTheEndsOfTheRange()
    {
        Assert.False(PlaybackSpeed.CanSlowDown(0));
        Assert.True(PlaybackSpeed.CanSpeedUp(0));
        Assert.True(PlaybackSpeed.CanSlowDown(6));
        Assert.False(PlaybackSpeed.CanSpeedUp(6));
        Assert.Equal(0, PlaybackSpeed.Slower(0));
        Assert.Equal(6, PlaybackSpeed.Faster(6));
        Assert.Equal(4, PlaybackSpeed.Faster(PlaybackSpeed.Normal));
    }

    [Theory]
    [InlineData(0, -3)]
    [InlineData(2, -1)]
    [InlineData(3, 0)]
    [InlineData(4, 1)]
    [InlineData(6, 3)]
    public void SdkStepsAreRelativeToNormalSpeed(int index, int steps)
    {
        Assert.Equal(steps, PlaybackSpeed.Steps(index));
    }

    // ---- Drift correction ----

    [Fact]
    public void ToleranceIsTwoSecondsUpToNormalSpeed()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), PlaybackSync.Tolerance(0));
        Assert.Equal(TimeSpan.FromSeconds(2), PlaybackSync.Tolerance(PlaybackSpeed.Normal));
        Assert.Equal(TimeSpan.FromSeconds(16), PlaybackSync.Tolerance(6));
    }

    [Fact]
    public void TileMoreThanTwoSecondsOffIsReseeked()
    {
        var master = At(9, 0, 10);
        var tolerance = PlaybackSync.Tolerance(PlaybackSpeed.Normal);

        Assert.Equal(SyncAction.None, PlaybackSync.Decide(master, At(9, 0, 8), false, Recordings, tolerance));
        Assert.Equal(SyncAction.None, PlaybackSync.Decide(master, At(9, 0, 12), false, Recordings, tolerance));
        Assert.Equal(SyncAction.Seek, PlaybackSync.Decide(master, At(9, 0, 7), false, Recordings, tolerance));
        Assert.Equal(SyncAction.Seek, PlaybackSync.Decide(master, At(9, 0, 13), false, Recordings, tolerance));
        Assert.Equal(SyncAction.None, PlaybackSync.Decide(master, null, false, Recordings, tolerance));
    }

    [Fact]
    public void TileWithoutRecordingAtTheSharedTimeIsParkedAndResumedLater()
    {
        var tolerance = PlaybackSync.Tolerance(PlaybackSpeed.Normal);

        Assert.Equal(SyncAction.Park, PlaybackSync.Decide(At(3, 44), At(3, 46, 50), false, Recordings, tolerance));
        Assert.Equal(SyncAction.None, PlaybackSync.Decide(At(3, 45), null, true, Recordings, tolerance));
        Assert.Equal(SyncAction.Seek, PlaybackSync.Decide(At(3, 47), null, true, Recordings, tolerance));
    }

    [Fact]
    public void UnknownRecordingsNeverPark()
    {
        Assert.Equal(SyncAction.None, PlaybackSync.Decide(At(3, 44), At(3, 44), false, null, TimeSpan.FromSeconds(2)));
        Assert.Equal(SyncAction.Seek, PlaybackSync.Decide(At(3, 44), null, true, null, TimeSpan.FromSeconds(2)));
    }

    // ---- Calendar ----

    [Theory]
    [InlineData(2026, 10, 3)] // 1 Ekim 2026 Perşembe
    [InlineData(2026, 6, 0)]  // 1 Haziran 2026 Pazartesi
    [InlineData(2026, 11, 6)] // 1 Kasım 2026 Pazar
    public void CalendarWeeksStartOnMonday(int year, int month, int blanks)
    {
        Assert.Equal(blanks, CalendarMonth.LeadingBlanks(year, month));
    }

    [Fact]
    public void CalendarTitleIsTurkish()
    {
        Assert.Equal("Ekim 2026", CalendarMonth.Title(new DateTime(2026, 10, 1)));
        Assert.Equal("Şubat 2027", CalendarMonth.Title(new DateTime(2027, 2, 1)));
        Assert.Equal(new[] { "Pt", "Sa", "Ça", "Pe", "Cu", "Ct", "Pz" }, CalendarMonth.WeekdayNames);
    }

    // ---- Timeline segments ----

    [Fact]
    public void TimelineSegmentsAreClippedToTheDay()
    {
        var segments = new[]
        {
            Segment(Day.AddHours(-2), At(1, 0)),          // started the evening before
            Segment(At(23, 0), Day.AddDays(1).AddHours(2)), // runs past midnight
            Segment(Day.AddDays(-1), Day.AddDays(-1).AddHours(5)), // other day: dropped
        };

        var clipped = RecordingJump.ClipToDay(segments, Day.AddHours(13)); // any time of the day selects the day

        Assert.Equal(2, clipped.Count);
        Assert.Equal((Day, At(1, 0)), (clipped[0].Start, clipped[0].End));
        Assert.Equal((At(23, 0), Day.AddDays(1)), (clipped[1].Start, clipped[1].End));
    }

    [Fact]
    public void TimelineSegmentsAreSortedAndOverlapsMerged()
    {
        var segments = new[]
        {
            Segment(At(10, 0), At(11, 0)),
            Segment(At(8, 0), At(9, 0)),
            Segment(At(8, 30), At(9, 30)), // overlaps the previous one
            Segment(At(9, 30), At(9, 45)), // touches it
            Segment(At(12, 0), At(12, 0)), // empty
        };

        var clipped = RecordingJump.ClipToDay(segments, Day);

        Assert.Equal(new[] { (At(8, 0), At(9, 45)), (At(10, 0), At(11, 0)) }, clipped.Select(s => (s.Start, s.End)));
    }

    [Fact]
    public void TimelineWithoutSegmentsIsEmpty() => Assert.Empty(RecordingJump.ClipToDay([], Day));

    // ---- Calendar dots ----

    static readonly DateTime Month = new(2026, 10, 1);

    [Fact]
    public void CalendarDotWhenAnyCheckedCameraRecorded()
    {
        var days = new RecordedDays();
        days.Set(Cam(1), new DateTime(2026, 10, 3), true);
        days.Set(Cam(1), new DateTime(2026, 10, 4), false);
        days.Set(Cam(2), new DateTime(2026, 10, 4), true);
        days.Set(Cam(2), new DateTime(2026, 10, 5), false);

        Assert.Equal(new[] { 3, 4 }, days.Marked([Cam(1), Cam(2)], Month, Day).Order());
        Assert.Equal(new[] { 3 }, days.Marked([Cam(1)], Month, Day).Order());   // camera 2 unchecked: its dot goes
        Assert.Empty(days.Marked([Cam(3)], Month, Day));
        Assert.Empty(days.Marked([], Month, Day));
    }

    [Fact]
    public void CalendarNeverDotsFutureDaysOrOtherMonths()
    {
        var days = new RecordedDays();
        days.Set(Cam(1), new DateTime(2026, 10, 25), true); // after "today" (21 Oct)
        days.Set(Cam(1), new DateTime(2026, 9, 30), true);  // previous month
        days.Set(Cam(1), Day, true);

        Assert.Equal(new[] { 21 }, days.Marked([Cam(1)], Month, Day));
        Assert.Equal(new[] { 30 }, days.Marked([Cam(1)], new DateTime(2026, 9, 1), Day));
    }

    [Fact]
    public void CalendarSearchGoesNewestFirstAndSkipsDaysAlreadyDotted()
    {
        var days = new RecordedDays();

        Assert.Equal(Day, days.NextToSearch(Cam(1), [Cam(1), Cam(2)], Month, Day)); // today first, never the future

        days.Set(Cam(1), Day, false);
        days.Set(Cam(2), new DateTime(2026, 10, 20), true);

        // 20 Oct is dotted by camera 2 already: camera 1 is not asked about it.
        Assert.Equal(new DateTime(2026, 10, 19), days.NextToSearch(Cam(1), [Cam(1), Cam(2)], Month, Day));
        // Camera 2 unchecked: camera 1 has to answer 20 Oct itself.
        Assert.Equal(new DateTime(2026, 10, 20), days.NextToSearch(Cam(1), [Cam(1)], Month, Day));
        Assert.True(days.Get(Cam(1), Day.AddHours(12)) == false); // any time of the day is that day
        Assert.Null(days.Get(Cam(1), new DateTime(2026, 10, 19)));
    }

    [Fact]
    public void CalendarSearchEndsWhenEveryDayIsKnown()
    {
        var days = new RecordedDays();
        var september = new DateTime(2026, 9, 1);
        for (var day = september; day.Month == 9; day = day.AddDays(1))
        {
            days.Set(Cam(1), day, day.Day % 2 == 0);
        }

        Assert.Null(days.NextToSearch(Cam(1), [Cam(1)], september, Day));
        Assert.Equal(15, days.Marked([Cam(1)], september, Day).Count);
    }

    [Fact]
    public void MonthAnswersMergeAcrossCameras()
    {
        var days = new RecordedDays();
        days.SetMonth(Cam(1), Month, new HashSet<int> { 1, 2, 3 }, Day);
        days.SetMonth(Cam(2), Month, new HashSet<int> { 3, 10, 21 }, Day);

        Assert.Equal(new[] { 1, 2, 3, 10, 21 }, days.Marked([Cam(1), Cam(2)], Month, Day).Order());
        Assert.False(days.Get(Cam(1), new DateTime(2026, 10, 10))); // answered "no", not unknown
        Assert.Null(days.NextToSearch(Cam(1), [Cam(1), Cam(2)], Month, Day)); // nothing left to ask
    }

    [Fact]
    public void MonthAnswerNeverDotsFutureDays()
    {
        var days = new RecordedDays();
        days.SetMonth(Cam(1), Month, new HashSet<int> { 20, 21, 22, 31 }, Day); // "today" is 21 Oct

        Assert.Equal(new[] { 20, 21 }, days.Marked([Cam(1)], Month, Day).Order());
        Assert.Null(days.Get(Cam(1), new DateTime(2026, 10, 22)));
        // The next day: 22 Oct was not stored from the earlier answer, so it is asked again (no stale "yes").
        Assert.Equal(new[] { 20, 21 }, days.Marked([Cam(1)], Month, new DateTime(2026, 10, 22)).Order());
        Assert.Equal(new DateTime(2026, 10, 22), days.NextToSearch(Cam(1), [Cam(1)], Month, new DateTime(2026, 10, 22)));
    }

    [Fact]
    public void UncheckingACameraRemovesOnlyItsMonthDots()
    {
        var days = new RecordedDays();
        days.SetMonth(Cam(1), Month, new HashSet<int> { 1, 5 }, Day);
        days.SetMonth(Cam(2), Month, new HashSet<int> { 5, 9 }, Day);

        Assert.Equal(new[] { 1, 5 }, days.Marked([Cam(1)], Month, Day).Order());   // camera 2 unchecked
        Assert.Equal(new[] { 5, 9 }, days.Marked([Cam(2)], Month, Day).Order());   // camera 1 unchecked
        Assert.Equal(new[] { 1, 5, 9 }, days.Marked([Cam(1), Cam(2)], Month, Day).Order()); // checked again: no new query
    }

    [Fact]
    public void SearchFailureText()
    {
        Assert.Equal("Kayıt listesi alınamadı: Cihaz zaman aşımına uğradı.", PlaybackText.SearchFailed("Cihaz zaman aşımına uğradı."));
        Assert.Equal("Kayıt listesi alınamadı", PlaybackText.SearchFailed(""));
    }

    // ---- Calendar day searches: retry instead of giving up ----

    [Fact]
    public void DaySearchIsRetriedThreeTimesThenStops()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), RecordedDays.RetryDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(15), RecordedDays.RetryDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(30), RecordedDays.RetryDelay(3));
        Assert.Null(RecordedDays.RetryDelay(4));
    }

    [Fact]
    public void CalendarSearchFailureText() =>
        Assert.Equal("Takvim için kayıt günleri alınamadı (Kapı): Cihaz zaman aşımına uğradı.",
            PlaybackText.CalendarSearchFailed("Kapı", "Cihaz zaman aşımına uğradı."));

    // ---- Double-clicked day: start at 00:00 ----

    [Fact]
    public void DayRecordedAtMidnightStartsAtMidnight()
    {
        var result = DayStart.Resolve(Day, [Segment(At(0, 0), At(6, 0))], null);

        Assert.Equal(new DayStartResult(DayStartKind.Midnight, Day), result);
        Assert.Null(result!.Value.Message);
    }

    [Fact]
    public void RecordingRunningOverMidnightCountsAsRecordedAtMidnight()
    {
        var result = DayStart.Resolve(Day, [Segment(Day.AddHours(-2), At(1, 0)), Segment(At(8, 0), At(9, 0))], null);

        Assert.Equal(DayStartKind.Midnight, result!.Value.Kind);
        Assert.Equal(Day, result.Value.Target);
    }

    [Fact]
    public void NoRecordingAtMidnightStartsAtTheFocusedCamerasFirstRecording()
    {
        // The other cameras are still searching: the focused camera's own recordings decide.
        var result = DayStart.Resolve(Day, [Segment(At(9, 0), At(10, 0)), Segment(At(7, 12, 5), At(8, 0))], null);

        Assert.Equal(new DayStartResult(DayStartKind.FirstRecording, At(7, 12, 5)), result);
        Assert.Equal("00:00'da kayıt yok · ilk kayıt 07:12:05'den başlatıldı", result!.Value.Message);
    }

    [Fact]
    public void RecordingsOfOtherDaysAreIgnored()
    {
        var result = DayStart.Resolve(Day, [Segment(Day.AddHours(-5), Day.AddHours(-1)), Segment(At(3, 0), At(4, 0))], null);

        Assert.Equal(new DayStartResult(DayStartKind.FirstRecording, At(3, 0)), result);
    }

    [Fact]
    public void FocusedCameraWithoutRecordingsWaitsForTheOthers()
    {
        Assert.Null(DayStart.Resolve(Day, [], null));
    }

    [Fact]
    public void FocusedCameraWithoutRecordingsStartsAtTheEarliestOfTheOthers()
    {
        IReadOnlyList<RecordingSegment> none = [];
        var result = DayStart.Resolve(Day, none, [none, [Segment(At(11, 0), At(12, 0))], [Segment(At(5, 30), At(6, 0))]]);

        Assert.Equal(new DayStartResult(DayStartKind.FirstRecording, At(5, 30)), result);

        var midnight = DayStart.Resolve(Day, none, [none, [Segment(At(0, 0), At(2, 0))]]);
        Assert.Equal(new DayStartResult(DayStartKind.Midnight, Day), midnight);
    }

    [Fact]
    public void NoCameraRecordedOnTheDay()
    {
        IReadOnlyList<RecordingSegment> none = [];
        var result = DayStart.Resolve(Day, none, [none, none]);

        Assert.Equal(new DayStartResult(DayStartKind.NoRecordings, Day), result);
        Assert.Equal("Bu günde kayıt yok", result!.Value.Message);
    }

    [Fact]
    public void UnknownRecordingsStartAtMidnight()
    {
        // A failed search: the recorder itself starts at its first recording after 00:00.
        Assert.Equal(new DayStartResult(DayStartKind.Midnight, Day), DayStart.Resolve(Day, null, null));
        IReadOnlyList<RecordingSegment> none = [];
        Assert.Equal(new DayStartResult(DayStartKind.Midnight, Day), DayStart.Resolve(Day, none, [none, null]));
    }

    [Fact]
    public void DayTexts()
    {
        Assert.Equal("Önce kamera seçin", PlaybackText.SelectCameraForDay);
        Assert.Equal("Çift tıklayın: günün kaydını 00:00'dan oynat", PlaybackText.DayToolTip);
        Assert.Equal("00:00'da kayıt yok · ilk kayıt 13:05:09'den başlatıldı", PlaybackText.StartedAtFirstRecording(At(13, 5, 9)));
    }
}
