using System.Globalization;
using System.Net;
using System.Xml.Linq;

namespace SPVision.Core;

/// <summary>ISAPI paths of the recorder settings page (IP channels of an NVR, discovered on a DS-8632NI-I8).</summary>
public static class IsapiPaths
{
    public const string DeviceInfo = "ISAPI/System/deviceInfo";
    public const string Time = "ISAPI/System/time";
    public const string NtpServers = "ISAPI/System/time/ntpServers";
    public const string Hdd = "ISAPI/ContentMgmt/Storage/hdd";
    public const string InputProxyChannels = "ISAPI/ContentMgmt/InputProxy/channels";
    public const string InputProxyCapabilities = "ISAPI/ContentMgmt/InputProxy/channels/capabilities";

    /// <summary>Read only: Hik-Connect and stream encryption status. Never written (see <see cref="IsapiWritePolicy"/>).</summary>
    public const string Ezviz = "ISAPI/System/Network/EZVIZ";

    public static string InputProxyChannel(int channel) => Invariant($"ISAPI/ContentMgmt/InputProxy/channels/{channel}");
    public static string Overlays(int channel) => Invariant($"ISAPI/ContentMgmt/InputProxy/channels/{channel}/video/overlays");
    public static string OverlaysCapabilities(int channel) => Overlays(channel) + "/capabilities";
    public static string Motion(int channel) => Invariant($"ISAPI/ContentMgmt/InputProxy/channels/{channel}/video/motionDetection");
    public static string MotionCapabilities(int channel) => Motion(channel) + "/capabilities";
    public static string Image(int channel) => Invariant($"ISAPI/Image/channels/{channel}");
    public static string ImageCapabilities(int channel) => Image(channel) + "/capabilities";
    public static string Stream(int channel, StreamQuality quality) =>
        Invariant($"ISAPI/ContentMgmt/StreamingProxy/channels/{StreamUrls.TrackId(channel, quality)}");
    public static string StreamCapabilities(int channel, StreamQuality quality) => Stream(channel, quality) + "/capabilities";
    public static string RecordTrack(int channel) => Invariant($"ISAPI/ContentMgmt/record/tracks/{StreamUrls.TrackId(channel, StreamQuality.Main)}");
    public static string RecordTrackCapabilities(int channel) => RecordTrack(channel) + "/capabilities";

    // ---- phase 2: Olaylar ----
    public const string EventTriggers = "ISAPI/Event/triggers";
    public const string EventTriggersCap = "ISAPI/Event/triggersCap";
    public static string EventTrigger(string id) => "ISAPI/Event/triggers/" + id;
    public static string VideoLoss(int channel) => Invariant($"ISAPI/ContentMgmt/InputProxy/channels/{channel}/video/videoLoss");
    public static string Tamper(int channel) => Invariant($"ISAPI/ContentMgmt/InputProxy/channels/{channel}/video/tamperDetection");

    // ---- phase 2: Ağ (ports: risky write of the port numbers only, see IsapiWritePolicy.CheckAdminPortsBody) ----
    public const string Interfaces = "ISAPI/System/Network/interfaces";
    public const string Bond = "ISAPI/System/Network/Bond";
    public const string AdminAccesses = "ISAPI/Security/adminAccesses";
    public const string AdminAccessesCapabilities = "ISAPI/Security/adminAccesses/capabilities";
    public static string InterfaceAddress(int id) => Invariant($"ISAPI/System/Network/interfaces/{id}/ipAddress");

    // ---- phase 2: Kullanıcılar (no password is ever read: the list has none) ----
    public const string Users = "ISAPI/Security/users";
    public const string SecurityCapabilities = "ISAPI/Security/capabilities";
    public static string User(int id) => Invariant($"ISAPI/Security/users/{id}");

    // ---- phase 2: Sistem (risky; sent only by IsapiClient.SendRiskyAsync) ----
    public const string Reboot = "ISAPI/System/reboot";
    public static string HddFormat(int id) => Invariant($"ISAPI/ContentMgmt/Storage/hdd/{id}/format");

    static string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}

/// <summary>ISAPI &lt;ResponseStatus&gt; of a PUT: statusCode 1 = OK; anything else is a failure.</summary>
public sealed record IsapiResponseStatus(int StatusCode, string? StatusString, string? SubStatusCode)
{
    public bool IsOk => StatusCode == 1 && (string.IsNullOrEmpty(SubStatusCode) || SubStatusCode.Equals("ok", StringComparison.OrdinalIgnoreCase));

    /// <summary>Null when the document is not a ResponseStatus.</summary>
    public static IsapiResponseStatus? Parse(XDocument? doc)
    {
        var root = doc?.Root;
        if (root is null || root.Name.LocalName != "ResponseStatus")
        {
            return null;
        }
        var code = IsapiXml.Child(root, "statusCode")?.Value.Trim();
        return new IsapiResponseStatus(
            int.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? c : 0,
            IsapiXml.Child(root, "statusString")?.Value.Trim(),
            IsapiXml.Child(root, "subStatusCode")?.Value.Trim());
    }

    /// <summary>A failed HTTP request without a ResponseStatus body.</summary>
    public static IsapiResponseStatus FromHttp(HttpStatusCode status) =>
        new(-(int)status, $"HTTP {(int)status} {status}", null);

    /// <summary>Readable Turkish text, e.g. "Geçersiz işlem (notSupport: cihaz bu ayarı desteklemiyor)".</summary>
    public string Describe()
    {
        if (IsOk)
        {
            return "Tamam";
        }
        var main = StatusCode switch
        {
            2 => "Cihaz meşgul",
            3 => "Cihaz hatası",
            4 => "Geçersiz işlem",
            5 => "Geçersiz XML biçimi",
            6 => "Geçersiz XML içeriği",
            7 => "Cihazın yeniden başlatılması gerekiyor",
            1 => "Cihaz isteği tam kabul etmedi",
            _ when StatusCode < 0 => StatusString ?? "HTTP hatası",
            _ => $"Bilinmeyen yanıt ({StatusCode})",
        };
        if (string.IsNullOrEmpty(SubStatusCode))
        {
            return main;
        }
        var detail = SubStatusCode switch
        {
            "notSupport" => "cihaz bu ayarı desteklemiyor",
            "lowPrivilege" => "kullanıcının yetkisi yetmiyor",
            "badParameters" or "invalidContent" or "badXmlContent" => "değer geçersiz",
            "deviceBusy" => "cihaz meşgul",
            "invalidOperation" => "işleme izin verilmiyor",
            _ => null,
        };
        return detail is null ? $"{main} ({SubStatusCode})" : $"{main} ({SubStatusCode}: {detail})";
    }
}

/// <summary>Namespace-agnostic element access. Paths are "/"-separated local names of direct children.</summary>
public static class IsapiXml
{
    /// <summary>Largest XML document accepted from a device, in characters (ISAPI documents are a few KB).</summary>
    public const int MaxLength = 4 * 1024 * 1024;

    /// <summary>
    /// The one parser for device-supplied XML: like <see cref="XDocument.Parse(string)"/> (insignificant whitespace
    /// dropped), but a DTD is refused (no entity expansion, nothing resolved from outside) and so is a document over
    /// <see cref="MaxLength"/> characters. Throws <see cref="System.Xml.XmlException"/>.
    /// </summary>
    public static XDocument Parse(string text)
    {
        var settings = new System.Xml.XmlReaderSettings
        {
            DtdProcessing = System.Xml.DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxLength,
            IgnoreWhitespace = true, // what XDocument.Parse does without LoadOptions.PreserveWhitespace
        };
        using var reader = System.Xml.XmlReader.Create(new StringReader(text), settings);
        return XDocument.Load(reader);
    }

    public static XElement? Child(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    public static XElement? Find(XElement root, string path)
    {
        var current = root;
        foreach (var part in path.Split('/'))
        {
            current = Child(current, part);
            if (current is null)
            {
                return null;
            }
        }
        return current;
    }

    public static string? Get(XElement root, string path) => Find(root, path)?.Value.Trim();

    public static int? GetInt(XElement root, string path) =>
        int.TryParse(Get(root, path), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    public static bool? GetBool(XElement root, string path) => Get(root, path) switch
    {
        { } s when s.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
        { } s when s.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
        _ => null,
    };

    /// <summary>
    /// Changes one existing element's text and nothing else (namespace, attributes and siblings stay).
    /// Missing elements are not created: the device did not offer the setting.
    /// </summary>
    public static void Set(XElement root, string path, string value)
    {
        var element = Find(root, path) ?? throw new ConfigEditException($"Cihaz belgesinde “{path}” alanı yok.");
        element.Value = value;
    }

    /// <summary>Capability attribute of an element (e.g. opt, min, max) of a "/capabilities" document.</summary>
    public static string? Attribute(XElement root, string path, string attribute) =>
        Find(root, path)?.Attributes().FirstOrDefault(a => a.Name.LocalName == attribute)?.Value;

    public static int? IntAttribute(XElement root, string path, string attribute) =>
        int.TryParse(Attribute(root, path, attribute), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>The comma separated "opt" list, trimmed, in device order (duplicates kept for paired lists).</summary>
    public static List<string> Options(XElement root, string path) =>
        Attribute(root, path, "opt") is { Length: > 0 } opt
            ? opt.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList()
            : [];

    public static string Bool(bool value) => value ? "true" : "false";
}

/// <summary>An edit could not be applied to the device's document.</summary>
public sealed class ConfigEditException(string message) : Exception(message);

/// <summary>One user-visible change inside a document: applied to the freshly read XML, verified after the PUT.</summary>
public abstract class ConfigEdit(string label, string oldText, string newText)
{
    public string Label { get; } = label;
    public string OldText { get; } = oldText;
    public string NewText { get; } = newText;

    public abstract void Apply(XElement root);

    /// <summary>Null when the re-read document holds the new value; otherwise the device's value as text.</summary>
    public abstract string? Verify(XElement root);
}

/// <summary>Sets one or more elements together (e.g. resolution = width + height).</summary>
public sealed class SetValueEdit(string label, IReadOnlyList<(string Path, string Value)> values, string oldText, string newText)
    : ConfigEdit(label, oldText, newText)
{
    public SetValueEdit(string label, string path, string value, string oldText, string newText)
        : this(label, [(path, value)], oldText, newText)
    {
    }

    public IReadOnlyList<(string Path, string Value)> Values { get; } = values;

    public override void Apply(XElement root)
    {
        foreach (var (path, value) in Values)
        {
            IsapiXml.Set(root, path, value);
        }
    }

    public override string? Verify(XElement root)
    {
        var actual = Values.Select(v => IsapiXml.Get(root, v.Path)).ToList();
        var same = Values.Select((v, i) => string.Equals(actual[i], v.Value, StringComparison.OrdinalIgnoreCase)).All(x => x);
        return same ? null : string.Join(" × ", actual.Select(a => a ?? "yok"));
    }
}

/// <summary>A document to read, edit and write back, with the camera (or "Kayıt cihazı") it belongs to.</summary>
public sealed record ConfigWrite(string Path, string Target, IReadOnlyList<ConfigEdit> Edits)
{
    public IEnumerable<string> Descriptions => Edits.Select(e => ConfigChangeText.Format(Target, e.Label, e.OldText, e.NewText));
}

public static class ConfigChangeText
{
    /// <summary>"&lt;kamera&gt; · &lt;alan&gt;: &lt;eski&gt; → &lt;yeni&gt;" — one line of the confirmation.</summary>
    public static string Format(string target, string label, string oldText, string newText) =>
        $"{target} · {label}: {Show(oldText)} → {Show(newText)}";

    static string Show(string text) => string.IsNullOrEmpty(text) ? "(boş)" : text;
}

/// <summary>GET and PUT of whole ISAPI documents. <see cref="IsapiClient"/> is the real one.</summary>
public interface IConfigTransport
{
    Task<XDocument> GetConfigAsync(string path, CancellationToken ct = default);

    /// <summary>Must check <see cref="IsapiWritePolicy"/> before any I/O.</summary>
    Task<IsapiResponseStatus> PutConfigAsync(string path, XDocument body, CancellationToken ct = default);
}

/// <summary>Outcome of a batch: what reached the device, the first failure (if any) and what was not sent.</summary>
public sealed record ConfigWriteReport(IReadOnlyList<string> Applied, string? FailedChange, string? FailureMessage, IReadOnlyList<string> NotApplied)
{
    public bool Success => FailureMessage is null;
}

/// <summary>
/// Read-modify-write, one request at a time: GET the current document, change only the edited elements, PUT the
/// whole document back, check the ResponseStatus, GET again and verify. Stops at the first failure.
/// </summary>
public static class ConfigWriter
{
    sealed record Outcome(string? Failure, ConfigEdit? RejectedEdit);

    public static async Task<ConfigWriteReport> ApplyAsync(IConfigTransport transport, IReadOnlyList<ConfigWrite> writes,
        CancellationToken ct = default)
    {
        // Every path is checked before the first request, so a batch with a bad path sends nothing at all.
        foreach (var write in writes)
        {
            IsapiWritePolicy.EnsureAllowed(write.Path);
        }
        var applied = new List<string>();
        for (var i = 0; i < writes.Count; i++)
        {
            var write = writes[i];
            Outcome outcome;
            try
            {
                outcome = await ApplyOneAsync(transport, write, ct);
            }
            catch (Exception e) when (e is IsapiException or ConfigEditException or IsapiWriteDeniedException)
            {
                outcome = new Outcome(e.Message, null);
            }
            if (outcome.Failure is null)
            {
                applied.AddRange(write.Descriptions);
                continue;
            }
            var later = writes.Skip(i + 1).SelectMany(w => w.Descriptions);
            if (outcome.RejectedEdit is { } rejected)
            {
                // The PUT was accepted, so the document's other edits did reach the device; only this one did not stick.
                var text = Describe(write, rejected);
                applied.AddRange(write.Edits.Where(e => e != rejected).Select(e => Describe(write, e)));
                return new ConfigWriteReport(applied, text, outcome.Failure, [text, .. later]);
            }
            return new ConfigWriteReport(applied, string.Join("; ", write.Descriptions), outcome.Failure, [.. write.Descriptions, .. later]);
        }
        return new ConfigWriteReport(applied, null, null, []);
    }

    static string Describe(ConfigWrite write, ConfigEdit edit) => ConfigChangeText.Format(write.Target, edit.Label, edit.OldText, edit.NewText);

    static async Task<Outcome> ApplyOneAsync(IConfigTransport transport, ConfigWrite write, CancellationToken ct)
    {
        var doc = await transport.GetConfigAsync(write.Path, ct);
        var root = doc.Root ?? throw new ConfigEditException("Cihazdan boş belge geldi.");
        foreach (var edit in write.Edits)
        {
            edit.Apply(root);
        }
        var status = await transport.PutConfigAsync(write.Path, doc, ct);
        if (!status.IsOk)
        {
            return new Outcome($"Cihaz değeri kabul etmedi: {status.Describe()}", null);
        }
        var after = (await transport.GetConfigAsync(write.Path, ct)).Root ?? throw new ConfigEditException("Cihazdan boş belge geldi.");
        foreach (var edit in write.Edits)
        {
            if (edit.Verify(after) is { } actual)
            {
                return new Outcome($"Cihaz değeri kabul etmedi: {edit.Label} cihazda “{actual}” olarak kaldı.", edit);
            }
        }
        return new Outcome(null, null);
    }
}
