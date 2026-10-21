using SPVision.Core;

namespace SPVision.Core.Tests;

public class AutoStreamQualityTests
{
    const int Threshold = 540;

    static StreamQuality Decide(GridLayout layout, int tile, double pixels, StreamQuality current = StreamQuality.Sub, bool maximized = false) =>
        AutoStreamQuality.Decide(layout, tile, maximized, pixels, Threshold, current);

    [Fact]
    public void BigTileOfTwentyTwoIsHdEvenWhenSmall()
    {
        // DIS KAPI on a 1440×900 window: about 312 DIP high, far below the threshold.
        Assert.Equal(StreamQuality.Main, Decide(GridLayout.TwentyTwo, 0, 280));
        Assert.Equal(StreamQuality.Sub, Decide(GridLayout.TwentyTwo, 1, 130));
        Assert.Equal(StreamQuality.Sub, Decide(GridLayout.TwentyTwo, 21, 130));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(13)]
    [InlineData(22)]
    public void EverySpanningTileIsHdAndTheSmallOnesFollowTheirSize(int tileCount)
    {
        var layout = GridLayout.WithTileCount(tileCount)!;
        for (var i = 0; i < layout.TileCount; i++)
        {
            var big = layout.Cells[i].RowSpan > 1;
            Assert.Equal(big ? StreamQuality.Main : StreamQuality.Sub, Decide(layout, i, 200));
            Assert.Equal(StreamQuality.Main, Decide(layout, i, 600)); // a small cell on a huge screen may still be HD
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void OneAndFourGridsAreHd(int tileCount)
    {
        var layout = GridLayout.WithTileCount(tileCount)!;
        for (var i = 0; i < layout.TileCount; i++)
        {
            Assert.Equal(StreamQuality.Main, Decide(layout, i, 300));
        }
    }

    [Theory]
    [InlineData(9)]
    [InlineData(16)]
    [InlineData(25)]
    [InlineData(36)]
    public void LargerEqualGridsUseTheSizeThreshold(int tileCount)
    {
        var layout = GridLayout.WithTileCount(tileCount)!;
        Assert.Equal(StreamQuality.Sub, Decide(layout, 0, 539));
        Assert.Equal(StreamQuality.Main, Decide(layout, 0, 540));
    }

    [Fact]
    public void MaximizedTileIsHd()
    {
        Assert.Equal(StreamQuality.Main, Decide(GridLayout.ThirtySix, 20, 100, maximized: true));
    }

    [Fact]
    public void HysteresisKeepsTheCurrentStreamNearTheThreshold()
    {
        var layout = GridLayout.Nine;
        // SD → HD only at the threshold.
        Assert.Equal(StreamQuality.Sub, Decide(layout, 0, 520, StreamQuality.Sub));
        // HD stays HD down to 10 % below it, then drops.
        Assert.Equal(StreamQuality.Main, Decide(layout, 0, 520, StreamQuality.Main));
        Assert.Equal(StreamQuality.Main, Decide(layout, 0, 490, StreamQuality.Main));
        Assert.Equal(StreamQuality.Sub, Decide(layout, 0, 480, StreamQuality.Main));
    }

    [Fact]
    public void IndexOutsideTheLayoutIsNotABigTile()
    {
        Assert.False(AutoStreamQuality.IsLargeByLayout(GridLayout.Six, -1));
        Assert.False(AutoStreamQuality.IsLargeByLayout(GridLayout.Six, 6));
        Assert.True(AutoStreamQuality.IsLargeByLayout(GridLayout.Six, 0));
    }
}
