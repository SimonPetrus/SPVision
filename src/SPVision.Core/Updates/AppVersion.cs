using System.Globalization;

namespace SPVision.Core.Updates;

/// <summary>
/// "major.minor.patch" of SPVision, compared numerically (1.2.10 &gt; 1.2.9). A pre-release / build suffix
/// ("1.2.3-beta.1", "1.2.3+abc123") is accepted by the lenient parse and ignored: only the three numbers count, so
/// "1.2.3-beta" equals "1.2.3". The signed update manifest uses the strict form (three plain numbers, nothing else).
/// </summary>
public readonly record struct AppVersion(int Major, int Minor, int Patch) : IComparable<AppVersion>
{
    /// <summary>Longest number part accepted (keeps every part inside <see cref="int"/>).</summary>
    const int MaxPartDigits = 9;

    public static readonly AppVersion Zero = new(0, 0, 0);

    public int CompareTo(AppVersion other) =>
        Major != other.Major ? Major.CompareTo(other.Major) :
        Minor != other.Minor ? Minor.CompareTo(other.Minor) :
        Patch.CompareTo(other.Patch);

    public static bool operator <(AppVersion left, AppVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(AppVersion left, AppVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(AppVersion left, AppVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(AppVersion left, AppVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    /// <summary>Exactly "X.Y.Z": digits only, no sign, no leading zeros (except "0"), no prefix or suffix.</summary>
    public static bool TryParseStrict(string? text, out AppVersion version)
    {
        version = default;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        var parts = text.Split('.');
        if (parts.Length != 3 ||
            !TryPart(parts[0], out var major) || !TryPart(parts[1], out var minor) || !TryPart(parts[2], out var patch))
        {
            return false;
        }
        version = new AppVersion(major, minor, patch);
        return true;
    }

    /// <summary>
    /// Strict form with an optional leading "v" and an optional "-pre-release" and/or "+build" suffix (ignored), e.g. the
    /// assembly informational version "1.0.0+32c81ab" or a tag "v1.2.0".
    /// </summary>
    public static bool TryParseLenient(string? text, out AppVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        var core = text.Trim();
        if (core.StartsWith('v') || core.StartsWith('V'))
        {
            core = core[1..];
        }
        var plus = core.IndexOf('+');
        if (plus >= 0)
        {
            core = core[..plus];
        }
        var dash = core.IndexOf('-');
        if (dash >= 0)
        {
            if (dash == core.Length - 1)
            {
                return false; // "1.2.3-" has an empty pre-release part
            }
            core = core[..dash];
        }
        return TryParseStrict(core, out version);
    }

    public static AppVersion ParseStrict(string text) =>
        TryParseStrict(text, out var version) ? version : throw new FormatException($"Geçersiz sürüm: “{text}” (beklenen: 1.2.3).");

    static bool TryPart(string part, out int value)
    {
        value = 0;
        if (part.Length is 0 or > MaxPartDigits || (part.Length > 1 && part[0] == '0'))
        {
            return false;
        }
        foreach (var c in part)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }
        value = int.Parse(part, NumberStyles.None, CultureInfo.InvariantCulture);
        return true;
    }
}
