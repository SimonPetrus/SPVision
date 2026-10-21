using FlyleafLib;
using FlyleafLib.MediaPlayer;

namespace SPVision;

/// <summary>Player presets. Everything decodes on the GPU (D3D11VA) and renders through D3D11.</summary>
static class Players
{
    public static Player CreateLive()
    {
        var config = Base();
        config.Audio.Enabled = false;          // live wall is silent; saves a decoder per tile
        config.Player.ZeroLatency = true;      // always show the newest frame
        config.Player.MinBufferDuration = 0;
        config.Demuxer.BufferDuration = TimeSpan.FromSeconds(2).Ticks;
        config.Decoder.LowDelay = true;
        config.Decoder.MaxVideoFrames = 4;
        return new Player(config);
    }

    public static Player CreatePlayback()
    {
        var config = Base();
        config.Audio.Enabled = true;
        config.Demuxer.BufferDuration = TimeSpan.FromSeconds(10).Ticks;
        return new Player(config);
    }

    static Config Base()
    {
        var config = new Config();
        config.Player.AutoPlay = true;
        config.Player.Stats = false;
        config.Video.VideoAcceleration = true;
        config.Decoder.VideoThreads = 2;       // decoding runs on the GPU; FFmpeg's default is one thread per core per player
        config.Video.ClearScreen = true;
        config.Video.BackColor = System.Windows.Media.Colors.Black;
        config.Demuxer.FormatOpt["rtsp_transport"] = "tcp";
        config.Demuxer.FormatOpt["timeout"] = "5000000";
        config.Demuxer.OpenTimeout = TimeSpan.FromSeconds(8).Ticks;
        return config;
    }
}
