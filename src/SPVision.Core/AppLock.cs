using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SPVision.Core;

/// <summary>
/// Ayarlar › Uygulama ayarları › Güvenlik ("Uygulama kilidi"). A UI barrier for other people using the same Windows session,
/// not encryption: the PIN itself is never stored, only <see cref="PinHash"/> (PBKDF2-SHA256, protected with DPAPI on disk
/// like the device passwords). The failure count and the lockout end survive a restart (<see cref="PinLockout"/>).
/// </summary>
public sealed class AppLockSettings
{
    /// <summary>The PIN is asked on startup (before any device connection) and after <see cref="IdleMinutes"/>.</summary>
    public bool Enabled { get; set; }

    /// <summary><see cref="PinHasher"/> format "pbkdf2-sha256$iterations$salt$hash"; null = no PIN set.</summary>
    public string? PinHash { get; set; }

    /// <summary>"İşlem yapılmazsa kilitle": 0 (Kapalı), 5, 10 (default) or 30 minutes of system-wide idle time.</summary>
    public int IdleMinutes { get; set; } = IdleLock.DefaultMinutes;

    /// <summary>Wrong PINs in a row (lock screen and the settings dialogs alike); a correct PIN resets it.</summary>
    public int FailedAttempts { get; set; }

    /// <summary>No PIN is checked before this moment (UTC); null = no lockout.</summary>
    public DateTimeOffset? LockedOutUntilUtc { get; set; }
}

/// <summary>What a typed PIN must look like: exactly six ASCII digits, not a trivial one.</summary>
public static class PinRules
{
    public const int Length = 6;

    public static bool IsComplete(string? pin) => pin is { Length: Length } && pin.All(char.IsAsciiDigit);

    /// <summary>The digits of <paramref name="text"/> (pasted text, IME input) in order, at most <paramref name="room"/> of them.</summary>
    public static string Digits(string? text, int room = Length)
    {
        var digits = new StringBuilder();
        foreach (var c in text ?? "")
        {
            if (digits.Length >= room)
            {
                break;
            }
            if (char.IsAsciiDigit(c))
            {
                digits.Append(c);
            }
            else if (c is >= '０' and <= '９')
            {
                digits.Append((char)('0' + (c - '０'))); // full-width digits of an East Asian IME
            }
        }
        return digits.ToString();
    }

    /// <summary>
    /// An easy-to-guess PIN: one digit repeated (000000), a run up or down, also wrapping past 9/0 (123456, 890123, 654321,
    /// 210987), a 2- or 3-digit pattern repeated (121212, 147147), doubled pairs (112233, 998877) or mirrored halves
    /// (123321, 100001). Only a complete PIN is judged.
    /// </summary>
    public static bool IsTrivial(string pin)
    {
        if (!IsComplete(pin))
        {
            return false;
        }
        var d = pin.Select(c => c - '0').ToArray();
        bool All(Func<int, bool> rule) => Enumerable.Range(0, Length).All(rule);
        return All(i => i == 0 || (d[i] - d[i - 1] + 10) % 10 == 1) ||   // run up, 890123 (9 → 0)
               All(i => i == 0 || (d[i - 1] - d[i] + 10) % 10 == 1) ||   // run down, 210987 (0 → 9)
               All(i => d[i] == d[i % 2]) ||                             // 121212; also one digit repeated, 000000
               All(i => d[i] == d[i % 3]) ||                             // 123123
               All(i => i % 2 == 0 || d[i] == d[i - 1]) ||               // 112233
               All(i => d[i] == d[Length - 1 - i]);                      // 123321
    }

    /// <summary>Error of a new PIN ("PIN belirle"), null when it can be used.</summary>
    public static string? CheckNew(string pin) =>
        !IsComplete(pin) ? AppLockText.TooShort : IsTrivial(pin) ? AppLockText.TooEasy : null;
}

/// <summary>
/// PBKDF2-SHA256 of the PIN: 600 000 iterations, 16-byte random salt, 32-byte hash, stored as
/// "pbkdf2-sha256$600000$&lt;salt base64&gt;$&lt;hash base64&gt;". Compared in constant time. One check takes a few hundred
/// milliseconds: call it off the UI thread.
/// </summary>
public static class PinHasher
{
    public const string Scheme = "pbkdf2-sha256";
    public const int Iterations = 600_000;
    public const int SaltBytes = 16;
    public const int HashBytes = 32;

    /// <summary>A stored hash with fewer iterations is refused (a settings.json edit cannot make guessing cheap).</summary>
    const int MinIterations = Iterations;

    /// <summary>Upper bound so an edited value cannot hang the app.</summary>
    const int MaxIterations = 10_000_000;

    public static string Hash(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(pin, salt, Iterations);
        return string.Join('$', Scheme, Iterations.ToString(CultureInfo.InvariantCulture),
            Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    /// <summary>True only for a well-formed <paramref name="stored"/> hash of exactly <paramref name="pin"/>.</summary>
    public static bool Verify(string pin, string? stored)
    {
        if (!PinRules.IsComplete(pin) || !TryParse(stored, out var iterations, out var salt, out var expected))
        {
            return false;
        }
        var actual = Derive(pin, salt, iterations);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    internal static bool TryParse(string? stored, out int iterations, out byte[] salt, out byte[] hash)
    {
        iterations = 0;
        salt = hash = [];
        var parts = stored?.Split('$');
        if (parts is not { Length: 4 } || parts[0] != Scheme ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out iterations) ||
            iterations is < MinIterations or > MaxIterations)
        {
            return false;
        }
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            hash = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }
        return salt.Length == SaltBytes && hash.Length == HashBytes;
    }

    static byte[] Derive(string pin, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, HashBytes);
}

/// <summary>
/// Wrong-PIN schedule: the first two wrong PINs only clear the boxes; the 3rd waits 30 s, the 4th 1 min, every further one
/// 5 min. Count and end time live in <see cref="AppLockSettings"/>, so a restart does not reset them.
/// </summary>
public static class PinLockout
{
    public static TimeSpan Delay(int failures) => failures switch
    {
        < 3 => TimeSpan.Zero,
        3 => TimeSpan.FromSeconds(30),
        4 => TimeSpan.FromMinutes(1),
        _ => TimeSpan.FromMinutes(5),
    };

    /// <summary>
    /// Wait left at <paramref name="now"/>; never longer than the schedule allows for the count (a clock set back cannot
    /// stretch it), zero when over.
    /// </summary>
    public static TimeSpan Remaining(AppLockSettings settings, DateTimeOffset now)
    {
        if (settings.LockedOutUntilUtc is not { } until || until <= now)
        {
            return TimeSpan.Zero;
        }
        var left = until - now;
        var max = Delay(settings.FailedAttempts);
        return left > max ? max : left;
    }

    /// <summary>A wrong PIN: counts it and starts the wait it earns. Returns that wait (zero for the first two).</summary>
    public static TimeSpan RecordFailure(AppLockSettings settings, DateTimeOffset now)
    {
        settings.FailedAttempts = settings.FailedAttempts < int.MaxValue ? settings.FailedAttempts + 1 : int.MaxValue;
        var delay = Delay(settings.FailedAttempts);
        settings.LockedOutUntilUtc = delay > TimeSpan.Zero ? now.ToUniversalTime() + delay : null;
        return delay;
    }

    public static void RecordSuccess(AppLockSettings settings)
    {
        settings.FailedAttempts = 0;
        settings.LockedOutUntilUtc = null;
    }

    /// <summary>Whole seconds shown in the countdown (rounded up: never "0 saniye" while still waiting).</summary>
    public static int Seconds(TimeSpan remaining) => remaining <= TimeSpan.Zero ? 0 : (int)Math.Ceiling(remaining.TotalSeconds);
}

/// <summary>"İşlem yapılmazsa kilitle": system-wide idle time (GetLastInputInfo) against the chosen minutes.</summary>
public static class IdleLock
{
    public const int DefaultMinutes = 10;

    /// <summary>The segmented control: Kapalı / 5 dk / 10 dk / 30 dk.</summary>
    public static readonly IReadOnlyList<int> Options = [0, 5, 10, 30];

    /// <summary>A value not offered (edited settings.json) reads as the default.</summary>
    public static int Normalize(int minutes) => Options.Contains(minutes) ? minutes : DefaultMinutes;

    /// <summary>
    /// Idle time from two 32-bit tick counts (GetTickCount and LASTINPUTINFO.dwTime, both wrap every 49.7 days); the
    /// unsigned difference stays right across the wrap.
    /// </summary>
    public static TimeSpan IdleTime(uint nowTicks, uint lastInputTicks) => TimeSpan.FromMilliseconds(unchecked(nowTicks - lastInputTicks));

    public static bool IsDue(TimeSpan idle, int minutes) => minutes > 0 && idle >= TimeSpan.FromMinutes(minutes);
}

/// <summary>Texts of the lock screen and the PIN dialogs.</summary>
public static class AppLockText
{
    public const string Wrong = "Yanlış PIN, tekrar deneyin.";
    public const string TooEasy = "Çok kolay bir PIN; başka bir PIN seçin.";
    public const string Mismatch = "PIN'ler aynı değil.";
    public const string TooShort = "PIN 6 haneli olmalı.";

    public static string Wait(TimeSpan remaining) =>
        string.Create(CultureInfo.InvariantCulture, $"Yanlış PIN. {PinLockout.Seconds(remaining)} saniye bekleyin.");

    /// <summary>After a wrong PIN: the plain message, or the countdown when it earned a wait.</summary>
    public static string AfterFailure(TimeSpan wait) => wait > TimeSpan.Zero ? Wait(wait) : Wrong;
}

/// <summary>
/// Kilit ekranı › "PIN'i unuttum" › Sıfırla: forgets everything about the devices on this computer — saved devices with
/// their passwords, stream keys and certificate pins, saved views (they name device cameras), camera groups — and the PIN
/// and lock settings. App preferences (theme, quality, hardware decoding, folders, start options, updater state) stay.
/// Nothing is sent to a recorder.
/// </summary>
public static class AppReset
{
    public static void ForgetDevices(AppSettings settings)
    {
        settings.Devices = [];
        settings.Views = [];
        settings.LastView = null;
        settings.LastPlaybackView = null;
        settings.Groups = []; // camera groups name device cameras too
        settings.ActiveGroup = null;
        settings.AppLock = new AppLockSettings();
    }
}
