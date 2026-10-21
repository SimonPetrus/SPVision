using System.Globalization;
using System.Xml.Linq;

namespace SPVision.Core;

/// <summary>
/// One risky operation, built and checked by <see cref="RiskyWrites"/>: exact method, path and body (serialized once,
/// so what was confirmed is what is sent). <see cref="Changes"/> are the lines the confirmation shows; they never contain
/// a password. Turn it into a <see cref="ConfirmedRiskyWrite"/> with <see cref="ConfirmedRiskyWrite.Confirm"/>.
/// </summary>
public sealed partial class RiskyWriteRequest
{
    internal RiskyWriteRequest(RiskyOperation operation, string method, string path, XDocument? body, string title, string warning,
        IReadOnlyList<string> changes, string accept, string? requiredWord = null)
    {
        IsapiWritePolicy.EnsureRiskyAllowed(operation, method, path);
        IsapiWritePolicy.EnsureRiskyBodyAllowed(operation, body);
        Operation = operation;
        Method = method;
        Path = path;
        Body = body is null ? null : "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + body.ToString(SaveOptions.DisableFormatting);
        Title = title;
        Warning = warning;
        Changes = changes;
        Accept = accept;
        RequiredWord = requiredWord;
    }

    public RiskyOperation Operation { get; }
    public string Method { get; }
    public string Path { get; }

    /// <summary>The exact XML sent (may hold a password: never shown, never logged).</summary>
    internal string? Body { get; }

    public bool HasBody => Body is not null;

    /// <summary>Dialog title line, e.g. "Kayıt cihazı yeniden başlatılsın mı?".</summary>
    public string Title { get; }

    /// <summary>The consequences, shown in the warning band.</summary>
    public string Warning { get; }

    /// <summary>"&lt;alan&gt;: &lt;eski&gt; → &lt;yeni&gt;" lines (no secrets).</summary>
    public IReadOnlyList<string> Changes { get; }

    /// <summary>Text of the accept button.</summary>
    public string Accept { get; }

    /// <summary>A word the user must type to confirm (disk format: <see cref="RiskyWrites.FormatWord"/>), or null.</summary>
    public string? RequiredWord { get; }
}

/// <summary>Shows a <see cref="RiskyWriteRequest"/> and asks the user (the WPF RiskyConfirmDialog).</summary>
public interface IRiskyConfirmation
{
    /// <summary>Null = cancelled. Otherwise the text the user typed in the word box ("" when no word is required).</summary>
    string? Ask(RiskyWriteRequest request);
}

/// <summary>
/// Proof that a <see cref="RiskyWriteRequest"/> went through the confirmation: the only thing
/// <see cref="IsapiClient.SendRiskyAsync"/> accepts. Created only by <see cref="Confirm"/> (which checks the typed word),
/// bound to that one request, usable once, and only for <see cref="Lifetime"/>.
/// </summary>
public sealed partial class ConfirmedRiskyWrite
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    readonly DateTimeOffset _issuedAt;
    readonly Func<DateTimeOffset> _clock;
    int _used;

    ConfirmedRiskyWrite(RiskyWriteRequest request, Func<DateTimeOffset> clock)
    {
        Request = request;
        _clock = clock;
        _issuedAt = clock();
    }

    public RiskyWriteRequest Request { get; }

    /// <summary>
    /// Asks <paramref name="confirmation"/>; null when the user cancelled or typed the wrong word. The word is checked
    /// here as well as in the dialog: Turkish upper-casing, so "biçimlendir" counts, "BICIMLENDIR" does not.
    /// </summary>
    public static ConfirmedRiskyWrite? Confirm(RiskyWriteRequest request, IRiskyConfirmation confirmation, Func<DateTimeOffset>? clock = null)
    {
        var typed = confirmation.Ask(request);
        if (typed is null || (request.RequiredWord is { } word && !RiskyWrites.WordMatches(word, typed)))
        {
            return null;
        }
        return new ConfirmedRiskyWrite(request, clock ?? (() => DateTimeOffset.UtcNow));
    }

    /// <summary>Takes the request out exactly once; a second use or an expired confirmation is refused.</summary>
    internal RiskyWriteRequest Consume()
    {
        if (Interlocked.Exchange(ref _used, 1) != 0)
        {
            throw new IsapiWriteDeniedException("Bu onay zaten kullanıldı; işlem için yeniden onay gerekir.");
        }
        if (_clock() - _issuedAt > Lifetime)
        {
            throw new IsapiWriteDeniedException("Onayın süresi doldu; işlem için yeniden onay gerekir.");
        }
        return Request;
    }
}

/// <summary>Sends a confirmed risky write (<see cref="IsapiClient"/> is the real one).</summary>
public interface IRiskyTransport
{
    Task<IsapiResponseStatus> SendRiskyAsync(ConfirmedRiskyWrite confirmed, CancellationToken ct = default);
}

/// <summary>Builders of the risky requests. Every one re-checks the policy (path, method, body) when it is built.</summary>
public static class RiskyWrites
{
    public const string FormatWord = "BİÇİMLENDİR";

    const string Ns = "http://www.isapi.org/ver20/XMLSchema";
    static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    public static bool WordMatches(string required, string? typed) =>
        typed is not null && string.Equals(typed.Trim().ToUpper(Turkish), required, StringComparison.Ordinal);

    public static RiskyWriteRequest Reboot(string deviceName) => new(
        RiskyOperation.Reboot, "PUT", IsapiPaths.Reboot, null,
        $"“{deviceName}” yeniden başlatılsın mı?",
        "Kayıt cihazı birkaç dakika kapalı kalır: bu sürede hiçbir kamera kaydedilmez, canlı görüntü ve oynatma kesilir. SPVision cihaz açılınca kendiliğinden yeniden bağlanır.",
        [], "Yeniden başlat");

    public static RiskyWriteRequest FormatDisk(HddInfo disk)
    {
        var name = disk.Name.Length > 0 ? disk.Name : string.Create(CultureInfo.InvariantCulture, $"Disk {disk.Id}");
        return new RiskyWriteRequest(
            RiskyOperation.FormatDisk, "PUT", IsapiPaths.HddFormat(disk.Id), null,
            $"“{name}” biçimlendirilsin mi?",
            "Bu diskteki TÜM kayıtlar kalıcı olarak silinir ve geri getirilemez. Biçimlendirme sürerken disk kayıt almaz.",
            [
                $"Disk: {name}",
                $"Kapasite: {RecorderText.Size(disk.CapacityMb)} · boş {RecorderText.Size(disk.FreeMb)}",
                $"Durum: {disk.StatusText}",
            ],
            "Biçimlendir", FormatWord);
    }

    /// <summary>
    /// The interface's IPAddress document as just read, with <paramref name="edits"/> applied: the confirmed body is
    /// exactly this document.
    /// </summary>
    public static RiskyWriteRequest NetworkAddress(int interfaceId, XDocument current, IReadOnlyList<ConfigEdit> edits)
    {
        if (edits.Count == 0)
        {
            throw new ConfigEditException("Değişiklik yok.");
        }
        var body = new XDocument(current);
        var root = body.Root ?? throw new ConfigEditException("Cihazdan boş belge geldi.");
        foreach (var edit in edits)
        {
            edit.Apply(root);
        }
        return new RiskyWriteRequest(
            RiskyOperation.NetworkAddress, "PUT", IsapiPaths.InterfaceAddress(interfaceId), body,
            "Kayıt cihazının ağ ayarı değişsin mi?",
            "Yanlış ayar cihazla bağlantıyı koparabilir. Adres değişince cihaza yalnızca yeni adresten ulaşılır; ağ geçidi veya maske yanlışsa cihaza bu bilgisayardan hiç ulaşılamayabilir ve ayarı düzeltmek için cihazın başına gitmek gerekir.",
            edits.Select(e => ConfigChangeText.Format("Ağ", e.Label, e.OldText, e.NewText)).ToList(),
            "Ağ ayarını değiştir");
    }

    /// <summary>A new Operator / Viewer account (never an administrator). <paramref name="id"/> is the first free slot.</summary>
    public static RiskyWriteRequest AddUser(int id, string userName, UserLevel level, string password, string? loginPassword)
    {
        if (level is not (UserLevel.Operator or UserLevel.Viewer))
        {
            throw new IsapiWriteDeniedException("Yeni kullanıcı yalnızca Operatör veya İzleyici olabilir.");
        }
        var body = UserBody(id, userName, password, DeviceUser.LevelValue(level), loginPassword);
        return new RiskyWriteRequest(
            RiskyOperation.AddUser, "POST", IsapiPaths.Users, body,
            $"“{userName}” kullanıcısı eklensin mi?",
            "Bu hesap kayıt cihazına bağlanabilir; yetkisi seçtiğiniz düzeydedir. Şifreyi güvenli bir yerde saklayın.",
            [$"Kullanıcı adı: {userName}", $"Yetki: {DeviceUser.LevelText(level)}"],
            "Kullanıcı ekle");
    }

    /// <summary>New password for an existing account; its level is sent unchanged.</summary>
    public static RiskyWriteRequest ChangePassword(DeviceUser user, string newPassword, string? loginPassword, bool usedBySpvision)
    {
        var body = UserBody(user.Id, user.Name, newPassword, user.LevelRaw, loginPassword);
        var warning = user.IsAdmin
            ? "Yönetici şifresi değişir. Bu hesapla bağlanan her program (iVMS, telefon uygulaması, web arayüzü) yeni şifreyi ister."
            : "Bu hesapla bağlanan programlar yeni şifreyi ister.";
        if (usedBySpvision)
        {
            warning += " SPVision bu hesapla bağlanıyor: başarılı olursa SPVision'daki kayıtlı şifre de güncellenir.";
        }
        return new RiskyWriteRequest(
            RiskyOperation.ChangePassword, "PUT", IsapiPaths.User(user.Id), body,
            $"“{user.Name}” hesabının şifresi değişsin mi?",
            warning,
            [$"Kullanıcı: {user.Name} · {DeviceUser.LevelText(user.Level)}", "Şifre: yeni şifre"],
            "Şifreyi değiştir");
    }

    public static RiskyWriteRequest DeleteUser(DeviceUser user)
    {
        if (user.IsAdmin)
        {
            throw new IsapiWriteDeniedException("Yönetici hesabı silinemez.");
        }
        return new RiskyWriteRequest(
            RiskyOperation.DeleteUser, "DELETE", IsapiPaths.User(user.Id), null,
            $"“{user.Name}” kullanıcısı silinsin mi?",
            "Bu hesapla bağlanan programların bağlantısı kesilir ve hesap geri getirilemez.",
            [$"Kullanıcı: {user.Name} · {DeviceUser.LevelText(user.Level)}"],
            "Kullanıcıyı sil");
    }

    static XDocument UserBody(int id, string userName, string password, string level, string? loginPassword)
    {
        XNamespace ns = Ns;
        var user = new XElement(ns + "User", new XAttribute("version", "2.0"),
            new XElement(ns + "id", id.ToString(CultureInfo.InvariantCulture)),
            new XElement(ns + "userName", userName),
            new XElement(ns + "password", password),
            new XElement(ns + "userLevel", level));
        if (!string.IsNullOrEmpty(loginPassword))
        {
            user.Add(new XElement(ns + "loginPassword", loginPassword));
        }
        return new XDocument(user);
    }
}
