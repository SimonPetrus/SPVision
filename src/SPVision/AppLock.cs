using System.Runtime.InteropServices;
using SPVision.Core;

namespace SPVision;

/// <summary>Result of a PIN check (<see cref="AppLock.CheckAsync"/>).</summary>
public enum PinCheck
{
    Correct,
    Wrong,

    /// <summary>Not checked: a wrong-PIN wait is running (<see cref="AppLock.LockoutRemaining"/>).</summary>
    LockedOut,
}

/// <summary>
/// App-wide state of "Uygulama kilidi" (Core <see cref="AppLockSettings"/>): whether the lock screen is up, PIN checks
/// with the persistent wrong-PIN lockout, and the system-wide idle time. A UI barrier for other people using the same
/// Windows session; it encrypts nothing. Features that show something on their own (event pop-ups…) check
/// <see cref="IsLocked"/> and follow <see cref="LockedChanged"/>.
/// </summary>
public static class AppLock
{
    static AppLockSettings Settings => App.Settings.AppLock;

    /// <summary>The lock screen covers the main window; nothing may show camera images or events now.</summary>
    public static bool IsLocked { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="IsLocked"/> changes.</summary>
    public static event Action? LockedChanged;

    /// <summary>The lock is switched on (Ayarlar › Güvenlik).</summary>
    public static bool Enabled => Settings.Enabled;

    internal static void SetLocked(bool locked)
    {
        if (IsLocked == locked)
        {
            return;
        }
        IsLocked = locked;
        LockedChanged?.Invoke();
    }

    /// <summary>Wait left after wrong PINs (persisted: a restart does not shorten it).</summary>
    public static TimeSpan LockoutRemaining => PinLockout.Remaining(Settings, DateTimeOffset.UtcNow);

    /// <summary>
    /// Checks <paramref name="pin"/> against the stored hash (off the UI thread: 600 000 PBKDF2 iterations) unless a wait
    /// is running. A wrong PIN counts toward the lockout, a correct one resets it; both are saved at once. Call on the UI
    /// thread.
    /// </summary>
    public static async Task<PinCheck> CheckAsync(string pin)
    {
        if (LockoutRemaining > TimeSpan.Zero)
        {
            return PinCheck.LockedOut;
        }
        var stored = Settings.PinHash;
        var correct = await Task.Run(() => PinHasher.Verify(pin, stored));
        if (correct)
        {
            PinLockout.RecordSuccess(Settings);
        }
        else
        {
            PinLockout.RecordFailure(Settings, DateTimeOffset.UtcNow);
        }
        App.SaveSettings();
        return correct ? PinCheck.Correct : PinCheck.Wrong;
    }

    /// <summary>"PIN belirle" finished: the lock is on with this PIN (hashed off the UI thread; the PIN is not kept).</summary>
    public static async Task EnableAsync(string pin)
    {
        var hash = await Task.Run(() => PinHasher.Hash(pin));
        Settings.PinHash = hash;
        Settings.Enabled = true;
        PinLockout.RecordSuccess(Settings);
        App.SaveSettings();
    }

    /// <summary>"PIN'i değiştir": new hash, same switch and idle setting.</summary>
    public static async Task ChangePinAsync(string pin)
    {
        var hash = await Task.Run(() => PinHasher.Hash(pin));
        Settings.PinHash = hash;
        App.SaveSettings();
    }

    /// <summary>Switch off after the current PIN was confirmed: the hash is dropped, the idle choice kept.</summary>
    public static void Disable()
    {
        Settings.Enabled = false;
        Settings.PinHash = null;
        PinLockout.RecordSuccess(Settings);
        App.SaveSettings();
    }

    // ---- Idle ("İşlem yapılmazsa kilitle") ----

    /// <summary>Minutes of system-wide inactivity before the lock screen comes up; 0 = never (or the lock is off).</summary>
    public static int IdleMinutes => Settings.Enabled ? IdleLock.Normalize(Settings.IdleMinutes) : 0;

    /// <summary>Time since the last mouse or keyboard input anywhere in this Windows session (GetLastInputInfo).</summary>
    public static TimeSpan SystemIdleTime()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? IdleLock.IdleTime(unchecked((uint)Environment.TickCount), info.Time) : TimeSpan.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetLastInputInfo(ref LastInputInfo info);
}
