using System.Globalization;
using System.Xml.Linq;

namespace SPVision.Core;

// Settings › Cihaz ayarları › Olaylar (phase 2): video loss / tampering per camera and the recorder's reaction
// ("linkage": buzzer, notify surveillance center, e-mail) to them and to system exceptions. Discovered on a
// DS-8632NI-I8 with GETs: ISAPI/Event/triggers (+ /{id}), ISAPI/Event/triggersCap,
// ContentMgmt/InputProxy/channels/{n}/video/videoLoss and /tamperDetection. Unit tested.

/// <summary>How the recorder reacts to an event (EventTriggerNotification/notificationMethod).</summary>
public enum NotifyMethod
{
    Beep,
    Center,
    Email,
}

public static class EventNotify
{
    public static readonly IReadOnlyList<NotifyMethod> All = [NotifyMethod.Beep, NotifyMethod.Center, NotifyMethod.Email];

    /// <summary>ISAPI notificationMethod (also used as the notification's id, as the device itself does: "beep").</summary>
    public static string Id(NotifyMethod method) => method switch
    {
        NotifyMethod.Beep => "beep",
        NotifyMethod.Center => "center",
        _ => "email",
    };

    public static string Text(NotifyMethod method) => method switch
    {
        NotifyMethod.Beep => "Sesli uyarı",
        NotifyMethod.Center => "Merkeze bildir",
        _ => "E-posta",
    };

    /// <summary>Element of a *TriggerCap block of ISAPI/Event/triggersCap.</summary>
    public static string CapElement(NotifyMethod method) => method switch
    {
        NotifyMethod.Beep => "isSupportBeep",
        NotifyMethod.Center => "isSupportCenter",
        _ => "isSupportEmail",
    };
}

/// <summary>One row of the reaction table: an event trigger kind (per camera: "{Key}-{channel}", else "{Key}").</summary>
public sealed record EventKind(string Key, string Text, string Help, string CapName, bool PerCamera)
{
    public string TriggerId(int channel) => PerCamera ? string.Create(CultureInfo.InvariantCulture, $"{Key}-{channel}") : Key;
}

public static class EventKinds
{
    public static readonly EventKind VideoLoss = new("videoloss", "Görüntü kaybı", "Açık olan kameralarda görüntü kesilince", "VideoLossTriggerCap", true);
    public static readonly EventKind Tamper = new("tamper", "Kurcalama", "Açık olan kameralarda önü kapatılınca", "TamperDetectionTriggerCap", true);

    /// <summary>System exceptions of the recorder (trigger id = event type).</summary>
    public static readonly IReadOnlyList<EventKind> Exceptions =
    [
        new("diskfull", "Disk dolu", "Kayıt diski dolduğunda", "DiskfullTriggerCap", false),
        new("diskerror", "Disk hatası", "Disk okunamadığında veya arızalandığında", "DiskerrorTriggerCap", false),
        new("nicbroken", "Ağ bağlantısı koptu", "Ağ kablosu çıktığında", "NicbrokenTriggerCap", false),
        new("ipconflict", "IP çakışması", "Ağda aynı adresli başka bir cihaz olduğunda", "IpconflictTriggerCap", false),
        new("illaccess", "Yetkisiz giriş denemesi", "Yanlış kullanıcı adı veya şifreyle girilmeye çalışıldığında", "IllaccesTriggerCap", false),
        new("recordingfailure", "Kayıt hatası", "Kayıt yazılamadığında", "RecordExceptionTriggerCap", false),
    ];

    public static IReadOnlyList<EventKind> All { get; } = [VideoLoss, Tamper, .. Exceptions];
}

/// <summary>Which reactions each trigger kind supports (ISAPI/Event/triggersCap; "*TriggerCap" blocks, isSupportX).</summary>
public sealed record EventTriggerCaps(IReadOnlyDictionary<string, IReadOnlySet<NotifyMethod>> Supported)
{
    public static readonly EventTriggerCaps None = new(new Dictionary<string, IReadOnlySet<NotifyMethod>>());

    public static EventTriggerCaps Parse(XDocument doc)
    {
        var map = new Dictionary<string, IReadOnlySet<NotifyMethod>>(StringComparer.Ordinal);
        foreach (var block in doc.Root!.Elements())
        {
            var methods = EventNotify.All.Where(m => IsapiXml.GetBool(block, EventNotify.CapElement(m)) == true).ToHashSet();
            if (methods.Count > 0)
            {
                map[block.Name.LocalName] = methods;
            }
        }
        return new EventTriggerCaps(map);
    }

    public bool Supports(EventKind kind, NotifyMethod method) =>
        Supported.TryGetValue(kind.CapName, out var methods) && methods.Contains(method);
}

/// <summary>Reaction state of a row over its triggers (one, or one per camera).</summary>
public enum LinkState
{
    Off,
    On,

    /// <summary>On for some cameras only.</summary>
    Mixed,

    /// <summary>The device has no such trigger.</summary>
    Missing,
}

/// <summary>Notification methods of every trigger (ISAPI/Event/triggers): trigger id → methods ("beep", "record"…).</summary>
public sealed record EventTriggers(IReadOnlyDictionary<string, IReadOnlySet<string>> Methods)
{
    public static EventTriggers Parse(XDocument doc)
    {
        var map = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (var trigger in doc.Root!.Elements().Where(e => e.Name.LocalName == "EventTrigger"))
        {
            if (IsapiXml.Get(trigger, "id") is not { Length: > 0 } id || map.ContainsKey(id))
            {
                continue; // a few smart-event triggers repeat one id per channel; none of them is used here
            }
            map[id] = Notifications(trigger).Select(n => IsapiXml.Get(n, "notificationMethod") ?? "")
                .Where(m => m.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        return new EventTriggers(map);
    }

    internal static IEnumerable<XElement> Notifications(XElement trigger) =>
        IsapiXml.Child(trigger, "EventTriggerNotificationList")?.Elements().Where(e => e.Name.LocalName == "EventTriggerNotification") ?? [];

    /// <summary>Null when the device has no such trigger.</summary>
    public bool? Has(string triggerId, NotifyMethod method) =>
        Methods.TryGetValue(triggerId, out var methods) ? methods.Contains(EventNotify.Id(method)) : null;

    public LinkState State(EventKind kind, IEnumerable<int> channels, NotifyMethod method)
    {
        var ids = kind.PerCamera ? channels.Select(kind.TriggerId).ToList() : [kind.Key];
        var states = ids.Select(id => Has(id, method)).Where(s => s is not null).Select(s => s!.Value).ToList();
        return states.Count == 0 ? LinkState.Missing
            : states.All(s => s) ? LinkState.On
            : states.Any(s => s) ? LinkState.Mixed
            : LinkState.Off;
    }
}

/// <summary>On/off of a camera event (videoLoss, tamperDetection): the document's <c>enabled</c>.</summary>
public sealed record EventEnable(bool Enabled)
{
    public const string EnabledPath = "enabled";

    public static EventEnable? Parse(XDocument doc) =>
        IsapiXml.GetBool(doc.Root!, EnabledPath) is { } on ? new EventEnable(on) : null;
}

/// <summary>Adds or removes one notification method of an EventTrigger document; the others stay untouched.</summary>
public sealed class NotificationEdit(string eventText, NotifyMethod method, bool on)
    : ConfigEdit($"{eventText} · {EventNotify.Text(method)}", RecorderText.OnOff(!on), RecorderText.OnOff(on))
{
    public NotifyMethod Method { get; } = method;
    public bool On { get; } = on;

    public override void Apply(XElement root)
    {
        var list = IsapiXml.Child(root, "EventTriggerNotificationList")
                   ?? throw new ConfigEditException("Cihaz belgesinde bildirim listesi yok.");
        var id = EventNotify.Id(Method);
        var existing = Matching(root).ToList();
        if (On && existing.Count == 0)
        {
            var ns = list.Name.Namespace;
            list.Add(new XElement(ns + "EventTriggerNotification",
                new XElement(ns + "id", id),
                new XElement(ns + "notificationMethod", id)));
        }
        else if (!On)
        {
            existing.ForEach(e => e.Remove());
        }
    }

    public override string? Verify(XElement root) => Matching(root).Any() == On ? null : RecorderText.OnOff(!On);

    IEnumerable<XElement> Matching(XElement root) =>
        EventTriggers.Notifications(root).Where(n =>
            string.Equals(IsapiXml.Get(n, "notificationMethod"), EventNotify.Id(Method), StringComparison.OrdinalIgnoreCase));
}

/// <summary>Olaylar tab: form values → writes (camera on/off and reactions).</summary>
public static class EventPlan
{
    /// <summary>One camera's on/off switches; null "after" values mean the camera does not offer the event.</summary>
    public static List<ConfigWrite> Camera(int channel, string target, EventEnable? videoLossBefore, bool? videoLoss,
        EventEnable? tamperBefore, bool? tamper)
    {
        var writes = new List<ConfigWrite>();
        if (videoLossBefore is { } vl && videoLoss is { } v && v != vl.Enabled)
        {
            writes.Add(new ConfigWrite(IsapiPaths.VideoLoss(channel), target,
                [new SetValueEdit(EventKinds.VideoLoss.Text, EventEnable.EnabledPath, IsapiXml.Bool(v), RecorderText.OnOff(vl.Enabled), RecorderText.OnOff(v))]));
        }
        if (tamperBefore is { } tb && tamper is { } t && t != tb.Enabled)
        {
            writes.Add(new ConfigWrite(IsapiPaths.Tamper(channel), target,
                [new SetValueEdit(EventKinds.Tamper.Text, EventEnable.EnabledPath, IsapiXml.Bool(t), RecorderText.OnOff(tb.Enabled), RecorderText.OnOff(t))]));
        }
        return writes;
    }

    /// <summary>
    /// Reactions the user changed (<paramref name="wanted"/>: only touched rows/methods). Per-camera rows set every
    /// camera's trigger; only triggers whose state differs are written, one document per trigger.
    /// </summary>
    public static List<ConfigWrite> Linkage(EventTriggers before, IReadOnlyDictionary<(string Kind, NotifyMethod Method), bool> wanted,
        IReadOnlyList<(int Channel, string Target)> cameras)
    {
        var writes = new List<ConfigWrite>();
        foreach (var kind in EventKinds.All)
        {
            var targets = kind.PerCamera
                ? cameras.Select(c => (Id: kind.TriggerId(c.Channel), c.Target)).ToList()
                : [(Id: kind.Key, Target: RecorderText.Device)];
            foreach (var (id, target) in targets)
            {
                var edits = new List<ConfigEdit>();
                foreach (var method in EventNotify.All)
                {
                    if (wanted.TryGetValue((kind.Key, method), out var on) && before.Has(id, method) is { } has && has != on)
                    {
                        edits.Add(new NotificationEdit(kind.Text, method, on));
                    }
                }
                if (edits.Count > 0)
                {
                    writes.Add(new ConfigWrite(IsapiPaths.EventTrigger(id), target, edits));
                }
            }
        }
        return writes;
    }
}
