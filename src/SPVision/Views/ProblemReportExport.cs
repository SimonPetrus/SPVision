using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;
using SPVision.Core;

namespace SPVision.Views;

/// <summary>
/// Ayarlar › Uygulama ayarları › Destek › "Sorunu bildir…": asks where to save (Desktop, <c>SPVision-sorun-yyyyMMdd-HHmm.zip</c>),
/// gathers sistem.txt (app version, OS, .NET, GPU names, hardware decoding, per recorder model / firmware / camera count — the
/// firmware from a read-only deviceInfo GET) and writes the zip with Core <see cref="ProblemReport"/>: crash.log*, shutdown.log
/// and player.log*, scrubbed. settings.json, the SDK logs, certificate pins and credentials never go in. Nothing is sent anywhere.
/// </summary>
static class ProblemReportExport
{
    static readonly TimeSpan DeviceInfoTimeout = TimeSpan.FromSeconds(5);

    public static async Task RunAsync(Window? owner, Action<string> message)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Sorun raporunu kaydet",
            FileName = ProblemReport.DefaultFileName(DateTime.Now),
            DefaultExt = ".zip",
            Filter = "Zip arşivi (*.zip)|*.zip",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(owner) != true)
        {
            return;
        }
        var path = dialog.FileName;
        message("Sorun raporu hazırlanıyor…");

        var system = new ReportSystem(
            Updates.UpdateService.CurrentVersion.ToString(),
            $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})",
            RuntimeInformation.FrameworkDescription,
            GpuNames(),
            App.Settings.HardwareDecoding,
            await DevicesAsync());
        var text = ProblemReport.SystemText(system, DateTimeOffset.Now);
        try
        {
            await Task.Run(() => ProblemReport.Write(path, App.LogDirectory, text));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            message($"Sorun raporu kaydedilemedi: {ex.Message}");
            return;
        }
        message("Sorun raporu kaydedildi: " + Path.GetFileName(path));

        if (ConfirmDialog.Show(owner,
                $"Sorun raporu kaydedildi:\n{path}\n\nBu dosyayı bize iletebilirsiniz. Şifre, adres, kullanıcı adı ve ayar dosyası içermez.",
                "Klasörü aç", "Kapat"))
        {
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                message($"Klasör açılamadı: {ex.Message}");
            }
        }
    }

    /// <summary>Model, firmware and camera count of every recorder; firmware from a read-only GET (null when it does not answer).</summary>
    static async Task<IReadOnlyList<ReportDevice>> DevicesAsync()
    {
        var devices = App.Settings.Devices.ToList();
        var details = await Task.WhenAll(devices.Select(async Task<DeviceDetails?> (device) =>
        {
            try
            {
                using var isapi = new IsapiClient(device, DeviceInfoTimeout);
                return await isapi.GetDeviceInfoAsync();
            }
            catch (Exception)
            {
                return null; // offline, refused, certificate changed…: "okunamadı" in the report
            }
        }));
        return devices.Select((device, i) => ReportDevice.From(device, details[i])).ToList();
    }

    /// <summary>Display adapter names from the registry (DriverDesc of the display class); empty when unreadable.</summary>
    static List<string> GpuNames()
    {
        var names = new List<string>();
        try
        {
            using var display = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            foreach (var sub in display?.GetSubKeyNames() ?? [])
            {
                try
                {
                    using var adapter = display!.OpenSubKey(sub);
                    if (adapter?.GetValue("DriverDesc") is string { Length: > 0 } name && !names.Contains(name))
                    {
                        names.Add(name);
                    }
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                    // e.g. the "Properties" key: not readable by users
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        return names;
    }
}
