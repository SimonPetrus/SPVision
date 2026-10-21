using SPVision.Core;

namespace SPVision.Core.Tests;

/// <summary>"Donanım hızlandırma" switched while streams run: which streams reopen, which wait for their recording.</summary>
public class DecoderSwitchTests
{
    [Theory]
    [InlineData(true, null)]   // still opening: decoder unknown
    [InlineData(true, false)]  // turned on, tile on the CPU
    [InlineData(false, true)]  // turned off, tile on the GPU
    [InlineData(false, null)]
    public void PlayingLiveTileOnTheOtherDecoderReopens(bool hardware, bool? gpu) =>
        Assert.Equal(LiveReopen.Now, DecoderSwitch.Live(hasCamera: true, wantPlaying: true, recording: false, gpu, hardware));

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)] // turned off, the GPU had refused anyway: no needless reconnect
    public void LiveTileAlreadyOnTheWantedDecoderStays(bool hardware, bool gpu) =>
        Assert.Equal(LiveReopen.None, DecoderSwitch.Live(true, true, false, gpu, hardware));

    [Fact]
    public void EmptyOrStoppedLiveTileStays()
    {
        Assert.Equal(LiveReopen.None, DecoderSwitch.Live(hasCamera: false, wantPlaying: false, recording: false, gpu: null, hardware: false));
        // Stopped ("Durduruldu"): its next open uses the new setting anyway.
        Assert.Equal(LiveReopen.None, DecoderSwitch.Live(hasCamera: true, wantPlaying: false, recording: false, gpu: true, hardware: false));
    }

    [Fact]
    public void RecordingLiveTileKeepsItsStreamUntilTheRecordingStops()
    {
        Assert.Equal(LiveReopen.AfterRecording, DecoderSwitch.Live(true, true, recording: true, gpu: true, hardware: false));
        Assert.Equal(LiveReopen.AfterRecording, DecoderSwitch.Live(true, true, recording: true, gpu: null, hardware: true));
        // Switched back before the recording ended: nothing left to do.
        Assert.Equal(LiveReopen.None, DecoderSwitch.Live(true, true, recording: true, gpu: true, hardware: true));
    }

    [Fact]
    public void KeptTileReopensAfterTheRecordingOnlyWhileItStillPlays()
    {
        Assert.True(DecoderSwitch.ReopenAfterRecording(stale: true, hasCamera: true, wantPlaying: true));
        Assert.False(DecoderSwitch.ReopenAfterRecording(stale: false, hasCamera: true, wantPlaying: true));
        Assert.False(DecoderSwitch.ReopenAfterRecording(stale: true, hasCamera: true, wantPlaying: false));
        Assert.False(DecoderSwitch.ReopenAfterRecording(stale: true, hasCamera: false, wantPlaying: false));
    }

    [Theory]
    [InlineData(TransportState.Playing)]
    [InlineData(TransportState.Paused)]
    public void ActivePlaybackRestartsAtTheSharedTime(TransportState transport)
    {
        Assert.Equal(PlaybackReopen.RestartAll, DecoderSwitch.Playback(active: true, transport, clipping: 0));
        Assert.Equal(PlaybackReopen.ReseekOthers, DecoderSwitch.Playback(active: true, transport, clipping: 1));
    }

    [Theory]
    [InlineData(TransportState.NoTime)]
    [InlineData(TransportState.Stopped)]
    public void PlaybackWithoutRunningStreamsDoesNothing(TransportState transport) =>
        Assert.Equal(PlaybackReopen.None, DecoderSwitch.Playback(active: true, transport, clipping: 0));

    [Fact]
    public void HiddenPlaybackDoesNothing() =>
        // Leaving Kayıt Oynatma (e.g. for Ayarlar) stops the tiles; showing it again re-seeks them with the new decoder.
        Assert.Equal(PlaybackReopen.None, DecoderSwitch.Playback(active: false, TransportState.Playing, clipping: 0));

    [Fact]
    public void Notices()
    {
        Assert.Equal("Donanım hızlandırma kapatıldı; görüntüler yeniden açılıyor.", DecoderSwitch.Notice(false, 4, 0));
        Assert.Equal("Donanım hızlandırma açıldı; görüntüler yeniden açılıyor.", DecoderSwitch.Notice(true, 1, 0));
        Assert.Equal("Donanım hızlandırma açıldı.", DecoderSwitch.Notice(true, 0, 0));
        Assert.Equal("Donanım hızlandırma kapatıldı; görüntüler yeniden açılıyor. Kayıt süren kamera, kayıt bitince yeni ayara geçer.",
            DecoderSwitch.Notice(false, 3, 1));
        Assert.Equal("Donanım hızlandırma kapatıldı. Kayıt süren 2 kamera, kayıt bitince yeni ayara geçer.",
            DecoderSwitch.Notice(false, 0, 2));
    }
}
