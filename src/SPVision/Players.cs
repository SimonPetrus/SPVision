using FlyleafLib;
using FlyleafLib.MediaPlayer;

namespace SPVision;

/// <summary>
/// Player presets. Decoding on the GPU (D3D11VA) unless "Donanım hızlandırma" is off; rendering is always D3D11.
/// </summary>
static class Players
{
    /// <summary>
    /// GPU decoding for Flyleaf players ("Donanım hızlandırma", <see cref="Core.AppSettings.HardwareDecoding"/>). Flyleaf reads
    /// <c>Config.Video.VideoAcceleration</c> each time it sets a decoder up (every open), so <see cref="Apply"/> before an
    /// open is enough for a player that already exists. UI thread.
    /// </summary>
    public static bool VideoAcceleration { get; set; } = true;

    /// <summary>Brings an existing player's decoder setting up to date; takes effect with its next open.</summary>
    public static void Apply(Player player) => player.Config.Video.VideoAcceleration = VideoAcceleration;

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
        config.Video.VideoAcceleration = VideoAcceleration;
        config.Decoder.VideoThreads = 2;       // normally decoding runs on the GPU; FFmpeg's default is one thread per core per player
        config.Video.ClearScreen = true;
        config.Video.BackColor = System.Windows.Media.Colors.Black;
        config.Demuxer.FormatOpt["rtsp_transport"] = "tcp";
        config.Demuxer.FormatOpt["timeout"] = "5000000";
        config.Demuxer.OpenTimeout = TimeSpan.FromSeconds(8).Ticks;
        return config;
    }
}
