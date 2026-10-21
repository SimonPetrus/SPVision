using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SP2026Lite.Core;

/// <summary>
/// Persists <see cref="AppSettings"/> as JSON under %APPDATA%\SP-2026 Lite. Device passwords and stream keys
/// are encrypted with Windows DPAPI (current user), so they are remembered but unreadable for other accounts.
/// </summary>
public sealed class SettingsStore(string? path = null)
{
    const string SecretPrefix = "dpapi:";
    static readonly byte[] Entropy = "SP-2026 Lite"u8.ToArray();

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Path { get; } = path ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SP-2026 Lite", "settings.json");

    public AppSettings Load()
    {
        if (!File.Exists(Path))
        {
            return new AppSettings();
        }
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), JsonOptions) ?? new AppSettings();
        foreach (var device in settings.Devices)
        {
            device.Password = Unprotect(device.Password) ?? "";
            device.StreamKey = Unprotect(device.StreamKey);
            foreach (var camera in device.Cameras)
            {
                camera.StreamKey = Unprotect(camera.StreamKey);
            }
        }
        return settings;
    }

    public void Save(AppSettings settings)
    {
        // Serialize a protected copy so the in-memory settings keep plain values.
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var copy = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)!;
        foreach (var device in copy.Devices)
        {
            device.Password = Protect(device.Password) ?? "";
            device.StreamKey = Protect(device.StreamKey);
            foreach (var camera in device.Cameras)
            {
                camera.StreamKey = Protect(camera.StreamKey);
            }
        }
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(copy, JsonOptions));
        File.Move(temp, Path, overwrite: true);
    }

    internal static string? Protect(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
        return SecretPrefix + Convert.ToBase64String(bytes);
    }

    internal static string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith(SecretPrefix, StringComparison.Ordinal))
        {
            return string.IsNullOrEmpty(value) ? null : value;
        }
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value[SecretPrefix.Length..]), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (CryptographicException)
        {
            // Settings copied from another Windows account: the secret must be entered again.
            return null;
        }
    }
}
