using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using SPVision.Core.Updates;

namespace SPVision.Updates;

/// <summary>A manifest whose signature verified, with the exact bytes (kept to re-verify a remembered mandatory update).</summary>
sealed record VerifiedManifest(UpdateManifest Manifest, byte[] Bytes, byte[] Signature);

/// <summary>A check or download failed (Turkish message, safe to show).</summary>
sealed class UpdateException(string message) : Exception(message);

enum LaunchResult
{
    Started,

    /// <summary>The user answered "Hayır" to the UAC prompt.</summary>
    Declined,

    Failed,
}

/// <summary>
/// The downloaded installer after its size and SHA-256 matched the signed manifest. The file stays open read-only with
/// <see cref="FileShare.Read"/> (nobody can write, rename or delete it) from the verification until the installer process
/// has started, so it cannot be swapped between the check and the elevated start.
/// </summary>
sealed class VerifiedInstaller(UpdateManifest manifest, string path, FileStream handle) : IDisposable
{
    public UpdateManifest Manifest { get; } = manifest;

    public string Path { get; } = path;

    public string LogPath { get; } = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, "kurulum.log");

    public void Dispose() => handle.Dispose();
}

/// <summary>
/// Network and file side of the in-app updater: talks only to the update proxy (tools/update-proxy, a Cloudflare Worker
/// that reads the private GitHub releases with its own token), over https with normal certificate validation, no
/// credentials, no cookies, bounded sizes and timeouts. Nothing from the network is used before the manifest signature
/// (<see cref="UpdateSignature"/>) verified with the key compiled into the app (<see cref="UpdateTrust"/>).
/// </summary>
static class UpdateService
{
    /// <summary>
    /// TODO (sahibi): the Worker address printed by <c>npx wrangler deploy</c> (tools/update-proxy/README.md), e.g.
    /// "https://spvision-update.ornek.workers.dev". While this placeholder is here the updater is switched off
    /// ("Güncelleme sunucusu ayarlanmamış").
    /// </summary>
    public const string UpdateProxyBaseUrl = "https://spvision-update.<hesap>.workers.dev";

    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    static readonly TimeSpan SmallRequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The download is cancelled when no byte arrives for this long.</summary>
    static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    const int MaxRedirects = 3;

    static readonly Lazy<HttpClient> Client = new(CreateClient);

    public static AppVersion CurrentVersion { get; } = ReadCurrentVersion();

    /// <summary>Why the updater is off (placeholder key or address), or null when it can run.</summary>
    public static string? DisabledReason =>
        !UpdateTrust.IsConfigured ? UpdateText.KeyNotConfigured :
        !UpdateEndpoint.IsConfigured(UpdateProxyBaseUrl) ? UpdateText.ProxyNotConfigured :
        null;

    /// <summary>Folder of the downloads; every download gets its own random subfolder.</summary>
    static string DownloadRoot => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SPVision-Update");

    /// <summary>
    /// The latest release's manifest, signature-verified and parsed; null when the proxy says there is no release (404).
    /// Throws <see cref="UpdateException"/> for every other failure (offline, bad signature, bad manifest…).
    /// </summary>
    public static async Task<VerifiedManifest?> FetchManifestAsync(CancellationToken cancellationToken)
    {
        if (DisabledReason is { } reason)
        {
            throw new UpdateException(reason);
        }
        var manifest = await GetSmallAsync(UpdateEndpoint.Manifest(UpdateProxyBaseUrl), UpdateManifest.MaxBytes, cancellationToken).ConfigureAwait(false);
        if (manifest is null)
        {
            return null;
        }
        var signature = await GetSmallAsync(UpdateEndpoint.Signature(UpdateProxyBaseUrl), UpdateSignature.MaxFileBytes, cancellationToken).ConfigureAwait(false)
            ?? throw new UpdateException("Güncelleme imzası bulunamadı; güncelleme yapılmadı.");
        return Verify(manifest, signature) ?? throw new UpdateException("Güncelleme bilgisinin imzası geçersiz; güncelleme yapılmadı.");
    }

    /// <summary>Re-verifies remembered bytes (settings.json); null when missing, unreadable or no longer valid.</summary>
    public static VerifiedManifest? Restore(string? manifestBase64, string? signatureBase64)
    {
        if (string.IsNullOrEmpty(manifestBase64) || string.IsNullOrEmpty(signatureBase64))
        {
            return null;
        }
        try
        {
            return Verify(Convert.FromBase64String(manifestBase64), Convert.FromBase64String(signatureBase64));
        }
        catch (FormatException)
        {
            return null;
        }
        catch (UpdateException)
        {
            return null;
        }
    }

    static VerifiedManifest? Verify(byte[] manifest, byte[] signature)
    {
        if (UpdateSignature.Verify(manifest, signature, UpdateTrust.PublicKeyPem) != SignatureCheck.Valid)
        {
            return null;
        }
        try
        {
            return new VerifiedManifest(UpdateManifest.Parse(manifest), manifest, signature);
        }
        catch (UpdateManifestException ex)
        {
            throw new UpdateException(ex.Message);
        }
    }

    /// <summary>
    /// Downloads the installer of <paramref name="manifest"/> through the proxy's /download (a redirect to GitHub's CDN)
    /// into %TEMP%\SPVision-Update\&lt;random&gt;\, checks size and SHA-256 against the signed manifest and returns it held
    /// open. <paramref name="progress"/> gets (received, total) bytes.
    /// </summary>
    public static async Task<VerifiedInstaller> DownloadAsync(UpdateManifest manifest, IProgress<(long Received, long Total)> progress,
        CancellationToken cancellationToken)
    {
        if (DisabledReason is { } reason)
        {
            throw new UpdateException(reason);
        }
        if (!UpdateManifest.IsValidAssetName(manifest.Asset))
        {
            throw new UpdateException("Kurulum dosyasının adı geçersiz.");
        }
        var directory = System.IO.Path.Combine(DownloadRoot, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12)));
        if (Directory.Exists(directory))
        {
            throw new UpdateException("Geçici klasör zaten var; yeniden deneyin.");
        }
        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, manifest.Asset);
        try
        {
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stall.CancelAfter(StallTimeout);
            using (var response = await SendAsync(UpdateEndpoint.Download(UpdateProxyBaseUrl), stall.Token).ConfigureAwait(false))
            {
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new UpdateException($"Kurulum dosyası indirilemedi (HTTP {(int)response.StatusCode}).");
                }
                if (response.Content.Headers.ContentLength is { } length && length != manifest.Size)
                {
                    throw new UpdateException("Kurulum dosyasının boyutu güncelleme bilgisindekiyle uyuşmuyor.");
                }
                var source = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
                await using var sourceScope = source.ConfigureAwait(false);
                var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                await using var targetScope = target.ConfigureAwait(false);
                var buffer = new byte[81920];
                long received = 0;
                var lastReport = -1;
                progress.Report((0, manifest.Size));
                while (true)
                {
                    stall.CancelAfter(StallTimeout);
                    var read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }
                    received += read;
                    if (received > manifest.Size)
                    {
                        throw new UpdateException("Kurulum dosyası beklenenden büyük; indirme durduruldu.");
                    }
                    await target.WriteAsync(buffer.AsMemory(0, read), stall.Token).ConfigureAwait(false);
                    var percent = UpdateText.Percent(received, manifest.Size);
                    if (percent != lastReport)
                    {
                        lastReport = percent;
                        progress.Report((received, manifest.Size));
                    }
                }
                await target.FlushAsync(stall.Token).ConfigureAwait(false);
            }

            // Verify through a read-only handle that denies writing and deleting; it stays open until the installer starts.
            var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
            try
            {
                if (!await UpdateFile.MatchesAsync(handle, manifest.Sha256, manifest.Size, cancellationToken).ConfigureAwait(false))
                {
                    throw new UpdateException("İndirilen dosya doğrulanamadı (boyut ya da SHA-256 tutmuyor); kurulmadı.");
                }
                return new VerifiedInstaller(manifest, path, handle);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryDelete(directory);
            throw new UpdateException("İndirme yanıt vermedi; bağlantıyı denetleyip yeniden deneyin.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            TryDelete(directory);
            throw new UpdateException("Kurulum dosyası indirilemedi: " + ex.Message);
        }
        catch
        {
            TryDelete(directory);
            throw;
        }
    }

    /// <summary>
    /// Starts the installer elevated ("runas": UAC prompt), silently; Inno Setup closes SPVision if needed, installs and
    /// starts it again un-elevated (/RELAUNCH=1). The verified handle is released only after the process exists.
    /// </summary>
    public static (LaunchResult Result, string? Error) Launch(VerifiedInstaller installer)
    {
        var start = new ProcessStartInfo(installer.Path)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = System.IO.Path.GetDirectoryName(installer.Path)!,
            Arguments = $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH=1 /LOG=\"{installer.LogPath}\"",
        };
        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return (LaunchResult.Failed, "Kurulum başlatılamadı.");
            }
            installer.Dispose();
            return (LaunchResult.Started, null);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: UAC declined
        {
            return (LaunchResult.Declined, null);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return (LaunchResult.Failed, "Kurulum başlatılamadı: " + ex.Message);
        }
    }

    /// <summary>Removes download folders older than a day (their install logs are kept that long). Best effort.</summary>
    public static void CleanupOldDownloads()
    {
        try
        {
            if (!Directory.Exists(DownloadRoot))
            {
                return;
            }
            foreach (var directory in Directory.GetDirectories(DownloadRoot))
            {
                if (DateTime.UtcNow - Directory.GetCreationTimeUtc(directory) > TimeSpan.FromDays(1))
                {
                    TryDelete(directory);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>GET of a small file (manifest, signature) with a size cap; null on 404.</summary>
    static async Task<byte[]?> GetSmallAsync(Uri uri, int maxBytes, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SmallRequestTimeout);
        try
        {
            using var response = await SendAsync(uri, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new UpdateException($"Güncelleme sunucusu yanıt vermedi (HTTP {(int)response.StatusCode}).");
            }
            if (response.Content.Headers.ContentLength > maxBytes)
            {
                throw new UpdateException("Güncelleme sunucusunun yanıtı beklenenden büyük.");
            }
            var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using var streamScope = stream.ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    throw new UpdateException("Güncelleme sunucusunun yanıtı beklenenden büyük.");
                }
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException("Güncelleme sunucusu zamanında yanıt vermedi.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new UpdateException("Güncelleme sunucusuna ulaşılamadı: " + ex.Message);
        }
    }

    /// <summary>GET following at most <see cref="MaxRedirects"/> redirects, each to an https address only.</summary>
    static async Task<HttpResponseMessage> SendAsync(Uri uri, CancellationToken cancellationToken)
    {
        for (var hop = 0; ; hop++)
        {
            if (!UpdateEndpoint.IsHttps(uri))
            {
                throw new UpdateException("Güncelleme yalnız https üzerinden indirilir.");
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            var response = await Client.Value.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is not (301 or 302 or 303 or 307 or 308))
            {
                return response;
            }
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null || hop >= MaxRedirects)
            {
                throw new UpdateException("Güncelleme sunucusu geçersiz bir yönlendirme döndürdü.");
            }
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
        }
    }

    static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,      // followed by hand: https only, at most 3 hops
            UseCookies = false,
            Credentials = null,
            PreAuthenticate = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = ConnectTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxResponseHeadersLength = 64,  // KiB
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }; // per request: timeouts above
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"SPVision/{CurrentVersion}");
        return client;
    }

    static AppVersion ReadCurrentVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(UpdateService).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (AppVersion.TryParseLenient(informational, out var version))
        {
            return version;
        }
        return assembly.GetName().Version is { } v ? new AppVersion(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0)) : AppVersion.Zero;
    }
}
