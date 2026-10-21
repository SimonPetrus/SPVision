namespace SPVision.Core;

/// <summary>What the playback view does with its open streams when "Donanım hızlandırma" is switched.</summary>
public enum PlaybackReopen
{
    /// <summary>Nothing plays (view not shown, no shared time, stopped): the next open uses the new decoder.</summary>
    None,

    /// <summary>Every tile re-seeks to the shared time (the clock waits for the focused tile, nothing before that time).</summary>
    RestartAll,

    /// <summary>A clip is running: the clipping tiles keep their stream, the others re-seek in step (drift path).</summary>
    ReseekOthers,
}

/// <summary>What a live tile does with its stream when "Donanım hızlandırma" is switched.</summary>
public enum LiveReopen
{
    /// <summary>Nothing to do: no stream wanted, or it already decodes where it should.</summary>
    None,

    /// <summary>Reopened now ("Yeniden bağlan" path).</summary>
    Now,

    /// <summary>A recording runs (or waits) on it: the stream is kept and reopened once the recording is stopped.</summary>
    AfterRecording,
}

/// <summary>
/// "Donanım hızlandırma" (Ayarlar › Uygulama ayarları › Görüntü) switched while streams run: which ones reopen so they
/// pick the new decoder up without restarting the app. A stream that carries a recording or clip keeps running (a reopen
/// would end the recording); it switches on its next reopen (the recording stopped, a reconnect, a seek).
/// </summary>
public static class DecoderSwitch
{
    /// <summary>
    /// A live tile reopens when it shows a camera it should be playing and its decoder is not already the wanted one
    /// (<paramref name="gpu"/>: what the player reports, null = not known yet, e.g. still opening). With a recording
    /// running or waiting for the main stream it keeps the stream instead and reopens when the recording is stopped.
    /// </summary>
    public static LiveReopen Live(bool hasCamera, bool wantPlaying, bool recording, bool? gpu, bool hardware) =>
        !hasCamera || !wantPlaying || gpu == hardware ? LiveReopen.None
        : recording ? LiveReopen.AfterRecording
        : LiveReopen.Now;

    /// <summary>A live tile that kept its stream because of a recording reopens once the recording is stopped.</summary>
    public static bool ReopenAfterRecording(bool stale, bool hasCamera, bool wantPlaying) => stale && hasCamera && wantPlaying;

    /// <summary>
    /// The playback view's reaction: only while it is shown and its tiles play (or are paused) at a shared time.
    /// </summary>
    public static PlaybackReopen Playback(bool active, TransportState transport, int clipping) =>
        !active || !PlaybackTransport.TilesJoin(transport) ? PlaybackReopen.None
        : clipping > 0 ? PlaybackReopen.ReseekOthers
        : PlaybackReopen.RestartAll;

    /// <summary>Status bar notice after the switch; <paramref name="kept"/> = streams kept for a running recording or clip.</summary>
    public static string Notice(bool hardware, int reopened, int kept)
    {
        var text = hardware ? "Donanım hızlandırma açıldı" : "Donanım hızlandırma kapatıldı";
        if (reopened > 0)
        {
            text += "; görüntüler yeniden açılıyor";
        }
        text += ".";
        if (kept > 0)
        {
            text += kept == 1
                ? " Kayıt süren kamera, kayıt bitince yeni ayara geçer."
                : $" Kayıt süren {kept} kamera, kayıt bitince yeni ayara geçer.";
        }
        return text;
    }
}
