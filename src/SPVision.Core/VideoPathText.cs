namespace SPVision.Core;

/// <summary>
/// Status bar "video path" text: which path the tiles on screen use (SDK for encrypted streams, FFmpeg otherwise) and
/// where they actually decode. "GPU çözme" is only written for tiles whose player confirmed the hardware decoder
/// (PlayCtrl <c>PlayM4_GetDecodeEngine</c>, Flyleaf <c>VideoDecoder.VideoAccelerated</c>); tiles that are still opening
/// (decoder not known yet) are left out of the count.
/// </summary>
public static class VideoPathText
{
    /// <summary>One tile on screen: SDK or FFmpeg path, and its decoder (true GPU, false CPU, null not known yet).</summary>
    public readonly record struct Tile(bool UsesSdk, bool? Gpu);

    public static string Describe(IEnumerable<Tile> tiles)
    {
        var list = tiles.ToList();
        var sdk = list.Count(t => t.UsesSdk);
        var ffmpeg = list.Count - sdk;
        var path =
            sdk > 0 && ffmpeg > 0 ? $"Şifreli akış · SDK ({sdk}) + Şifresiz · FFmpeg ({ffmpeg})" :
            sdk > 0 ? "Şifreli akış · SDK" :
            ffmpeg > 0 ? "Şifresiz akış · FFmpeg" :
            "";
        if (path.Length == 0)
        {
            return "";
        }
        var gpu = list.Count(t => t.Gpu == true);
        var cpu = list.Count(t => t.Gpu == false);
        var decode =
            gpu > 0 && cpu == 0 ? "GPU çözme" :
            gpu == 0 && cpu > 0 ? "CPU çözme" :
            gpu > 0 ? $"GPU çözme ({gpu}/{gpu + cpu})" :
            null;
        return decode is null ? path : $"{path} · {decode}";
    }
}
