using System.Text.Json;
using SPVision.Core;

namespace SPVision.Core.Tests;

/// <summary>Status bar video path / decoder text, SDK log level rule and the new settings' defaults.</summary>
public class PerformanceTests
{
    static VideoPathText.Tile Sdk(bool? gpu) => new(true, gpu);

    static VideoPathText.Tile Ffmpeg(bool? gpu) => new(false, gpu);

    [Fact]
    public void NoTilesNoText() => Assert.Equal("", VideoPathText.Describe([]));

    [Fact]
    public void AllSdkTilesOnGpu() =>
        Assert.Equal("Şifreli akış · SDK · GPU çözme", VideoPathText.Describe([Sdk(true), Sdk(true)]));

    [Fact]
    public void AllSdkTilesOnCpuSayCpu() =>
        Assert.Equal("Şifreli akış · SDK · CPU çözme", VideoPathText.Describe([Sdk(false), Sdk(false)]));

    [Fact]
    public void MixedDecodersCountGpuTiles() =>
        Assert.Equal("Şifreli akış · SDK · GPU çözme (2/3)", VideoPathText.Describe([Sdk(true), Sdk(false), Sdk(true)]));

    [Fact]
    public void UnknownDecoderIsNotClaimed()
    {
        Assert.Equal("Şifreli akış · SDK", VideoPathText.Describe([Sdk(null), Sdk(null)]));
        // Tiles still opening are left out of the count.
        Assert.Equal("Şifreli akış · SDK · GPU çözme", VideoPathText.Describe([Sdk(true), Sdk(null)]));
        Assert.Equal("Şifreli akış · SDK · CPU çözme", VideoPathText.Describe([Sdk(null), Sdk(false)]));
    }

    [Fact]
    public void FfmpegAndMixedPaths()
    {
        Assert.Equal("Şifresiz akış · FFmpeg · GPU çözme", VideoPathText.Describe([Ffmpeg(true)]));
        Assert.Equal("Şifreli akış · SDK (2) + Şifresiz · FFmpeg (1) · GPU çözme (2/3)",
            VideoPathText.Describe([Sdk(true), Sdk(true), Ffmpeg(false)]));
    }

    [Theory]
    [InlineData(-5, 0u)]
    [InlineData(0, 0u)]
    [InlineData(1, 1u)]
    [InlineData(2, 2u)]
    [InlineData(3, 3u)]
    [InlineData(9, 3u)]
    public void SdkLogLevelIsClamped(int setting, uint level) => Assert.Equal(level, SdkLog.Level(setting));

    [Fact]
    public void DefaultsAreErrorsOnlyLogAndHardwareDecoding()
    {
        var settings = new AppSettings();
        Assert.Equal(1, settings.SdkLogLevel);
        Assert.Equal(1u, SdkLog.Level(settings.SdkLogLevel));
        Assert.True(settings.HardwareDecoding);
    }

    [Fact]
    public void OldSettingsFilesGetTheDefaults()
    {
        // settings.json written before these settings existed: the fields are missing.
        var settings = JsonSerializer.Deserialize<AppSettings>("""{"Devices":[],"ShowMotionBoxes":false}""")!;
        Assert.Equal(SdkLog.DefaultLevel, settings.SdkLogLevel);
        Assert.True(settings.HardwareDecoding);
    }

    [Fact]
    public void RaisedLogLevelRoundTrips()
    {
        var json = JsonSerializer.Serialize(new AppSettings { SdkLogLevel = 3, HardwareDecoding = false });
        var back = JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.Equal(3, back.SdkLogLevel);
        Assert.False(back.HardwareDecoding);
    }
}
