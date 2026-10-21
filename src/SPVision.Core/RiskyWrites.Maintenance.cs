using System.Globalization;

namespace SPVision.Core;

// Phase 3 (Bakım): the risky maintenance operations reuse the phase 2 mechanism — a RiskyWriteRequest built and
// policy-checked here, the separate RiskyConfirmDialog with a typed word, a single-use ConfirmedRiskyWrite bound to
// that request. Factory reset is an ISAPI request (IsapiClient.SendRiskyAsync); firmware goes through the HCNetSDK,
// whose runner accepts only a FirmwareJob, and a FirmwareJob exists only by consuming a confirmed firmware token.

public sealed partial class RiskyWriteRequest
{
    /// <summary>The firmware file the user confirmed (firmware operations only).</summary>
    public FirmwareFile? Firmware { get; internal init; }

    /// <summary>ISAPI channel numbers of the cameras to upgrade, in order (camera firmware only).</summary>
    public IReadOnlyList<int> Channels { get; internal init; } = [];

    /// <summary>Shown with the red danger band instead of the amber warning (factory reset).</summary>
    public bool Severe { get; internal init; }
}

public sealed partial class ConfirmedRiskyWrite
{
    /// <summary>
    /// Takes a confirmed firmware request out exactly once (second use or expiry throws, like
    /// <see cref="IsapiClient.SendRiskyAsync"/>), re-checks the policy entry and verifies that the file is still the
    /// confirmed one (size, last write time, SHA-256). The SDK runners accept nothing else.
    /// </summary>
    public async Task<FirmwareJob> TakeFirmwareJobAsync(CancellationToken ct = default)
    {
        var request = Consume();
        if (request.Operation is not (RiskyOperation.RecorderFirmware or RiskyOperation.CameraFirmware) || request.Firmware is not { } file)
        {
            throw new IsapiWriteDeniedException("Bu onay bir yazılım güncellemesi için değil.");
        }
        IsapiWritePolicy.EnsureRiskyAllowed(request.Operation, request.Method, request.Path);
        if (request.Operation == RiskyOperation.CameraFirmware && request.Channels.Count == 0)
        {
            throw new IsapiWriteDeniedException("Güncellenecek kamera seçilmedi.");
        }
        await file.VerifyUnchangedAsync(ct);
        return new FirmwareJob(request.Operation, file, request.Channels);
    }
}

/// <summary>
/// A confirmed firmware upgrade, ready for the SDK. Only <see cref="ConfirmedRiskyWrite.TakeFirmwareJobAsync"/> creates
/// one, so holding it proves the typed confirmation happened for exactly this file and these cameras.
/// </summary>
public sealed class FirmwareJob
{
    internal FirmwareJob(RiskyOperation operation, FirmwareFile file, IReadOnlyList<int> channels)
    {
        Operation = operation;
        File = file;
        Channels = channels;
    }

    public RiskyOperation Operation { get; }
    public FirmwareFile File { get; }
    public IReadOnlyList<int> Channels { get; }
}

/// <summary>Builders of the maintenance requests; each one is policy-checked when built (RiskyWriteRequest constructor).</summary>
public static class MaintenanceWrites
{
    public static RiskyWriteRequest RecorderFirmware(string deviceName, RecorderFirmware? current, FirmwareFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return new RiskyWriteRequest(
            RiskyOperation.RecorderFirmware, MaintenancePolicy.SdkMethod, MaintenancePolicy.RecorderFirmwareCall, null,
            $"“{deviceName}” kayıt cihazının yazılımı güncellensin mi?",
            "Dosya kayıt cihazına gönderilip yazılır, sonra cihaz yeniden başlar; bu birkaç dakika sürer. Bu sürede kayıt durur, " +
            "canlı görüntü ve oynatma kesilir. Güncelleme sürerken cihazın elektriğini KESMEYİN, ağ kablosunu çıkarmayın ve " +
            "SPVision'ı kapatmayın. Bu modele ait olmayan ya da bozuk bir dosya cihazı kullanılamaz hale getirebilir.",
            [
                $"Cihaz: {current?.Model ?? "model okunamadı"}",
                $"Yüklü sürüm: {current?.Firmware?.Text ?? "okunamadı"}",
                $"Dosya: {file.Name} · {file.SizeText}",
                $"SHA-256: {file.Sha256}",
            ],
            "Güncelle", MaintenancePolicy.UpdateWord)
        {
            Firmware = file,
        };
    }

    /// <summary>
    /// Selected cameras, upgraded one after another through the recorder. Every camera must be online and reached
    /// over the Hikvision protocol; different models are allowed but called out in the confirmation.
    /// </summary>
    public static RiskyWriteRequest CameraFirmware(IReadOnlyList<CameraFirmware> cameras, FirmwareFile file, bool stopOnFirstFailure)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (cameras.Count == 0)
        {
            throw new IsapiWriteDeniedException("Güncellenecek kamera seçilmedi.");
        }
        if (cameras.Count > MaintenancePolicy.MaxCameras || cameras.Select(c => c.Channel).Distinct().Count() != cameras.Count ||
            cameras.Any(c => c.Channel is < 1 or > 999))
        {
            throw new IsapiWriteDeniedException("Kamera listesi geçersiz.");
        }
        if (cameras.FirstOrDefault(c => !c.Upgradable) is { } blocked)
        {
            throw new IsapiWriteDeniedException($"{blocked.Label} güncellenemez (çevrimdışı ya da Hikvision protokolüyle bağlı değil).");
        }
        var models = Core.CameraFirmware.Models(cameras);
        var warning = "Kameralar sırayla güncellenir: her biri birkaç dakika görüntüsüz kalır ve kaydetmez, sonra kendiliğinden " +
                      "yeniden başlar. Güncelleme sürerken kayıt cihazının ve kameraların elektriğini KESMEYİN, SPVision'ı " +
                      "kapatmayın. Bu modele ait olmayan bir dosya kamerayı kullanılamaz hale getirebilir.";
        if (models.Count > 1)
        {
            warning += $" DİKKAT: seçili kameralar farklı modellerde ({string.Join(", ", models)}); bir yazılım dosyası genellikle tek model ailesi içindir.";
        }
        var lines = new List<string>
        {
            $"Dosya: {file.Name} · {file.SizeText}",
            $"SHA-256: {file.Sha256}",
            stopOnFirstFailure ? "İlk hatada durulur; kalan kameralar güncellenmez." : "Hata olsa da sıradaki kamerayla devam edilir.",
        };
        lines.AddRange(cameras.Select(c => string.Create(CultureInfo.InvariantCulture,
            $"Kanal {c.Channel} · {c.Label} · {c.Model ?? "model bilinmiyor"} · {c.Firmware?.Text ?? "sürüm okunamadı"}")));
        return new RiskyWriteRequest(
            RiskyOperation.CameraFirmware, MaintenancePolicy.SdkMethod, MaintenancePolicy.CameraFirmwareCall, null,
            cameras.Count == 1
                ? $"“{cameras[0].Label}” kamerasının yazılımı güncellensin mi?"
                : string.Create(CultureInfo.InvariantCulture, $"{cameras.Count} kameranın yazılımı sırayla güncellensin mi?"),
            warning, lines, "Güncelle", MaintenancePolicy.UpdateWord)
        {
            Firmware = file,
            Channels = cameras.Select(c => c.Channel).ToList(),
        };
    }

    /// <summary>PUT ISAPI/System/factoryReset?mode=basic|full, no body. Typed word <see cref="MaintenancePolicy.ResetWord"/>.</summary>
    public static RiskyWriteRequest FactoryReset(string deviceName, FactoryResetMode mode) => new(
        RiskyOperation.FactoryReset, "PUT", MaintenancePaths.FactoryReset(mode), null,
        mode == FactoryResetMode.Basic
            ? $"“{deviceName}” temel ayarlara sıfırlansın mı?"
            : $"“{deviceName}” TAMAMEN fabrika ayarlarına sıfırlansın mı?",
        "Bu işlem geri alınamaz. Sıfırlama akış şifrelemeyi ve Hik-Connect'i de cihazda varsayılana döndürür; cihaz yeniden " +
        "etkinleştirme isteyebilir ve SPVision bağlantıyı kaybeder. Cihaz yeniden başlar: bu sürede kayıt durur. SPVision akış " +
        "şifrelemeyi ya da Hik-Connect'i sonradan kendisi açmaz veya değiştirmez; gerekirse bunları cihazın web arayüzünden siz " +
        "yeniden ayarlarsınız (yeni doğrulama kodu SPVision'daki akış anahtarını da değiştirir).",
        FactoryResetText.Lines(mode),
        mode == FactoryResetMode.Basic ? "Temel sıfırla" : "Tam sıfırla", MaintenancePolicy.ResetWord)
    {
        Severe = true,
    };
}
