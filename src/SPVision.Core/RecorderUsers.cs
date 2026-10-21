using System.Globalization;
using System.Xml.Linq;

namespace SPVision.Core;

// Settings › Cihaz ayarları › Kullanıcılar (phase 2). Read: ISAPI/Security/users (id, userName, userLevel only — the
// list holds no password and none is ever read), ISAPI/Security/capabilities (supportUserNums). Writes are risky
// operations (RiskyWrites.AddUser / ChangePassword / DeleteUser). Unit tested.

public enum UserLevel
{
    Administrator,
    Operator,
    Viewer,
    Other,
}

public sealed record DeviceUser(int Id, string Name, string LevelRaw)
{
    public UserLevel Level => LevelRaw switch
    {
        "Administrator" => UserLevel.Administrator,
        "Operator" => UserLevel.Operator,
        "Viewer" => UserLevel.Viewer,
        _ => UserLevel.Other,
    };

    /// <summary>The built-in admin (id 1) or any administrator: never deleted from SPVision.</summary>
    public bool IsAdmin => Id == 1 || Level == UserLevel.Administrator;

    public static string LevelValue(UserLevel level) => level switch
    {
        UserLevel.Administrator => "Administrator",
        UserLevel.Operator => "Operator",
        UserLevel.Viewer => "Viewer",
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    public static string LevelText(UserLevel level) => level switch
    {
        UserLevel.Administrator => "Yönetici",
        UserLevel.Operator => "Operatör",
        UserLevel.Viewer => "İzleyici",
        _ => "Diğer",
    };

    /// <summary>Only id, userName and userLevel are taken; any other element (there is no password) is ignored.</summary>
    public static List<DeviceUser> ParseList(XDocument doc) =>
        doc.Root!.Elements().Where(e => e.Name.LocalName == "User")
            .Select(e => new DeviceUser(IsapiXml.GetInt(e, "id") ?? 0, IsapiXml.Get(e, "userName") ?? "", IsapiXml.Get(e, "userLevel") ?? ""))
            .Where(u => u.Id > 0 && u.Name.Length > 0)
            .OrderBy(u => u.Id)
            .ToList();
}

/// <summary>ISAPI/Security/capabilities: how many accounts the recorder holds.</summary>
public sealed record UserLimits(int? MaxUsers)
{
    public static UserLimits Parse(XDocument doc) => new(IsapiXml.GetInt(doc.Root!, "supportUserNums"));
}

/// <summary>Hikvision account rules, checked before anything is sent.</summary>
public static class UserRules
{
    public const int MinPassword = 8;
    public const int MaxPassword = 16;
    public const int MaxName = 32;

    public const string PasswordHelp = "8–16 karakter; rakam, küçük harf, büyük harf ve işaretten en az ikisi; Türkçe harf ve boşluk olmadan.";

    public static string? CheckName(string? name, IEnumerable<DeviceUser> existing)
    {
        var n = name?.Trim() ?? "";
        if (n.Length == 0)
        {
            return "Kullanıcı adı boş olamaz.";
        }
        if (n.Length > MaxName)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Kullanıcı adı en çok {MaxName} karakter olabilir.");
        }
        if (!n.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
        {
            return "Kullanıcı adında yalnızca İngilizce harf, rakam, nokta, alt çizgi ve tire kullanılabilir.";
        }
        if (existing.Any(u => string.Equals(u.Name, n, StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(n, "admin", StringComparison.OrdinalIgnoreCase))
        {
            return "Bu adla bir kullanıcı zaten var.";
        }
        return null;
    }

    public static string? CheckPassword(string userName, string? password, string? repeat)
    {
        var p = password ?? "";
        if (p.Length is < MinPassword or > MaxPassword)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Şifre {MinPassword}–{MaxPassword} karakter olmalı.");
        }
        if (!p.All(c => c is > ' ' and <= '~'))
        {
            return "Şifrede Türkçe harf, boşluk veya özel olmayan karakter kullanılamaz.";
        }
        var kinds = (p.Any(char.IsAsciiDigit) ? 1 : 0) + (p.Any(char.IsAsciiLetterLower) ? 1 : 0) +
                    (p.Any(char.IsAsciiLetterUpper) ? 1 : 0) + (p.Any(c => !char.IsAsciiLetterOrDigit(c)) ? 1 : 0);
        if (kinds < 2)
        {
            return "Şifrede rakam, küçük harf, büyük harf ve işaretten en az ikisi bulunmalı.";
        }
        if (userName.Length > 0 && p.Contains(userName, StringComparison.OrdinalIgnoreCase))
        {
            return "Şifre kullanıcı adını içeremez.";
        }
        if (!string.Equals(p, repeat, StringComparison.Ordinal))
        {
            return "Şifreler aynı değil.";
        }
        return null;
    }

    /// <summary>The first free account slot from 2 (1 is the built-in admin); null when the recorder is full.</summary>
    public static int? NextFreeId(IEnumerable<DeviceUser> users, int? max)
    {
        var used = users.Select(u => u.Id).ToHashSet();
        var limit = max is > 1 ? max.Value : 32;
        for (var id = 2; id <= Math.Min(limit, 99); id++)
        {
            if (!used.Contains(id))
            {
                return id;
            }
        }
        return null;
    }
}
