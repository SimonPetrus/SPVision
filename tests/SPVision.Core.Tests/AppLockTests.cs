using System.Text.Json;
using SPVision.Core;

namespace SPVision.Core.Tests;

/// <summary>Uygulama kilidi: PIN rules, PBKDF2 hash, wrong-PIN lockout, idle time, storage and the "PIN'i unuttum" reset.</summary>
public class AppLockTests
{
    // 600 000 PBKDF2 iterations take a few hundred ms: hash once for all tests.
    static readonly Lazy<string> Stored = new(() => PinHasher.Hash("258147"));

    static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    // ---- PIN rules ----

    [Theory]
    [InlineData("258147", true)]
    [InlineData("000001", true)]
    [InlineData("25814", false)]
    [InlineData("2581470", false)]
    [InlineData("25814a", false)]
    [InlineData("２５８１４７", false)] // full-width digits are converted on input, never accepted as such
    [InlineData("", false)]
    [InlineData(null, false)]
    public void CompletePinIsSixAsciiDigits(string? pin, bool complete) => Assert.Equal(complete, PinRules.IsComplete(pin));

    [Theory]
    [InlineData("12-34 56", 6, "123456")]
    [InlineData("1a2b3c4d5e6f7", 6, "123456")]
    [InlineData("98765", 2, "98")]
    [InlineData("１２３", 6, "123")]
    [InlineData("٣٤٥", 6, "")] // other scripts' digits are not PIN digits
    [InlineData(null, 6, "")]
    public void DigitsKeepsOnlyDigitsUpToRoom(string? text, int room, string expected) =>
        Assert.Equal(expected, PinRules.Digits(text, room));

    static void AssertTooEasy(string pin)
    {
        Assert.True(PinRules.IsTrivial(pin), pin);
        Assert.Equal("Çok kolay bir PIN; başka bir PIN seçin.", PinRules.CheckNew(pin));
    }

    [Fact]
    public void OneDigitRepeatedIsTooEasy()
    {
        for (var digit = '0'; digit <= '9'; digit++)
        {
            AssertTooEasy(new string(digit, 6));
        }
    }

    [Theory]
    [InlineData("123456")]
    [InlineData("234567")]
    [InlineData("456789")]
    [InlineData("012345")]
    [InlineData("567890")] // wraps 9 → 0
    [InlineData("890123")]
    [InlineData("654321")]
    [InlineData("987654")]
    [InlineData("543210")]
    [InlineData("098765")] // wraps 0 → 9
    [InlineData("210987")]
    public void RunsUpOrDownAreTooEasy(string pin) => AssertTooEasy(pin);

    [Theory]
    [InlineData("121212")]
    [InlineData("101010")]
    [InlineData("909090")]
    public void TwoDigitPatternRepeatedIsTooEasy(string pin) => AssertTooEasy(pin);

    [Theory]
    [InlineData("123123")]
    [InlineData("147147")]
    [InlineData("159159")]
    public void ThreeDigitPatternRepeatedIsTooEasy(string pin) => AssertTooEasy(pin);

    [Theory]
    [InlineData("112233")]
    [InlineData("001122")]
    [InlineData("998877")]
    [InlineData("445566")]
    public void DoubledPairsAreTooEasy(string pin) => AssertTooEasy(pin);

    [Theory]
    [InlineData("123321")]
    [InlineData("456654")]
    [InlineData("100001")]
    public void MirroredHalvesAreTooEasy(string pin) => AssertTooEasy(pin);

    [Theory]
    [InlineData("482917")]
    [InlineData("305861")]
    [InlineData("258147")]
    [InlineData("123457")] // a run broken by one digit
    [InlineData("135790")]
    [InlineData("121213")]
    [InlineData("112234")]
    [InlineData("123312")]
    public void OrdinaryPinsAreAccepted(string pin)
    {
        Assert.False(PinRules.IsTrivial(pin));
        Assert.Null(PinRules.CheckNew(pin));
    }

    [Fact]
    public void ShortNewPinIsRefused() => Assert.Equal("PIN 6 haneli olmalı.", PinRules.CheckNew("2581"));

    // ---- Hash ----

    [Fact]
    public void HashHasSchemeIterationsSaltAndNoPin()
    {
        var stored = Stored.Value;
        var parts = stored.Split('$');

        Assert.Equal(4, parts.Length);
        Assert.Equal("pbkdf2-sha256", parts[0]);
        Assert.True(int.Parse(parts[1]) >= 600_000);
        Assert.Equal(16, Convert.FromBase64String(parts[2]).Length);
        Assert.Equal(32, Convert.FromBase64String(parts[3]).Length);
        Assert.DoesNotContain("258147", stored);
    }

    [Fact]
    public void VerifyAcceptsOnlyTheSamePin()
    {
        Assert.True(PinHasher.Verify("258147", Stored.Value));
        Assert.False(PinHasher.Verify("258148", Stored.Value));
        Assert.False(PinHasher.Verify("25814", Stored.Value));
        Assert.False(PinHasher.Verify("258147", null));
        Assert.False(PinHasher.Verify("258147", ""));
    }

    [Fact]
    public void SaltIsRandomPerHash()
    {
        Assert.True(PinHasher.TryParse(Stored.Value, out _, out var salt, out var hash));
        Assert.True(PinHasher.TryParse(PinHasher.Hash("258147"), out _, out var salt2, out var hash2));

        Assert.NotEqual(salt, salt2);
        Assert.NotEqual(hash, hash2);
    }

    [Fact]
    public void MalformedOrWeakenedHashIsRefused()
    {
        var parts = Stored.Value.Split('$');
        string With(int index, string value)
        {
            var copy = (string[])parts.Clone();
            copy[index] = value;
            return string.Join('$', copy);
        }

        Assert.False(PinHasher.TryParse(With(0, "pbkdf2-sha1"), out _, out _, out _));
        Assert.False(PinHasher.TryParse(With(1, "1000"), out _, out _, out _)); // fewer iterations: cheap to guess
        Assert.False(PinHasher.TryParse(With(1, "-600000"), out _, out _, out _));
        Assert.False(PinHasher.TryParse(With(1, "999999999"), out _, out _, out _));
        Assert.False(PinHasher.TryParse(With(2, "not base64!"), out _, out _, out _));
        Assert.False(PinHasher.TryParse(With(2, Convert.ToBase64String(new byte[8])), out _, out _, out _));
        Assert.False(PinHasher.TryParse(With(3, Convert.ToBase64String(new byte[16])), out _, out _, out _));
        Assert.False(PinHasher.TryParse(Stored.Value + "$x", out _, out _, out _));
        Assert.False(PinHasher.Verify("258147", With(1, "1000")));
    }

    // ---- Lockout ----

    [Fact]
    public void LockoutScheduleIsThirtySecondsThenOneThenFiveMinutes()
    {
        Assert.Equal(TimeSpan.Zero, PinLockout.Delay(1));
        Assert.Equal(TimeSpan.Zero, PinLockout.Delay(2));
        Assert.Equal(TimeSpan.FromSeconds(30), PinLockout.Delay(3));
        Assert.Equal(TimeSpan.FromMinutes(1), PinLockout.Delay(4));
        Assert.Equal(TimeSpan.FromMinutes(5), PinLockout.Delay(5));
        Assert.Equal(TimeSpan.FromMinutes(5), PinLockout.Delay(40));
    }

    [Fact]
    public void FailuresCountAndStartTheWait()
    {
        var settings = new AppLockSettings();

        Assert.Equal(TimeSpan.Zero, PinLockout.RecordFailure(settings, Now));
        Assert.Equal(TimeSpan.Zero, PinLockout.RecordFailure(settings, Now));
        Assert.Null(settings.LockedOutUntilUtc);
        Assert.Equal(TimeSpan.FromSeconds(30), PinLockout.RecordFailure(settings, Now));

        Assert.Equal(3, settings.FailedAttempts);
        Assert.Equal(Now.AddSeconds(30), settings.LockedOutUntilUtc);
        Assert.Equal(TimeSpan.FromSeconds(30), PinLockout.Remaining(settings, Now));
        Assert.Equal(TimeSpan.FromSeconds(10), PinLockout.Remaining(settings, Now.AddSeconds(20)));
        Assert.Equal(TimeSpan.Zero, PinLockout.Remaining(settings, Now.AddSeconds(30)));

        Assert.Equal(TimeSpan.FromMinutes(1), PinLockout.RecordFailure(settings, Now.AddSeconds(31)));
        Assert.Equal(TimeSpan.FromMinutes(5), PinLockout.RecordFailure(settings, Now.AddMinutes(2)));
    }

    [Fact]
    public void CorrectPinResetsCountAndWait()
    {
        var settings = new AppLockSettings { FailedAttempts = 4, LockedOutUntilUtc = Now.AddMinutes(1) };

        PinLockout.RecordSuccess(settings);

        Assert.Equal(0, settings.FailedAttempts);
        Assert.Null(settings.LockedOutUntilUtc);
        Assert.Equal(TimeSpan.Zero, PinLockout.Remaining(settings, Now));
    }

    [Fact]
    public void ClockSetBackCannotStretchTheWait()
    {
        // Saved at 12:00 with a 30 s wait; the clock now says 11:00.
        var settings = new AppLockSettings { FailedAttempts = 3, LockedOutUntilUtc = Now.AddSeconds(30) };

        Assert.Equal(TimeSpan.FromSeconds(30), PinLockout.Remaining(settings, Now.AddHours(-1)));
    }

    [Fact]
    public void CountdownSecondsRoundUp()
    {
        Assert.Equal(30, PinLockout.Seconds(TimeSpan.FromSeconds(30)));
        Assert.Equal(1, PinLockout.Seconds(TimeSpan.FromMilliseconds(10)));
        Assert.Equal(0, PinLockout.Seconds(TimeSpan.Zero));
        Assert.Equal("Yanlış PIN. 300 saniye bekleyin.", AppLockText.Wait(TimeSpan.FromMinutes(5)));
        Assert.Equal("Yanlış PIN, tekrar deneyin.", AppLockText.AfterFailure(TimeSpan.Zero));
        Assert.Equal("Yanlış PIN. 30 saniye bekleyin.", AppLockText.AfterFailure(TimeSpan.FromSeconds(30)));
    }

    // ---- Idle ----

    [Fact]
    public void IdleTimeSurvivesTickWrap()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), IdleLock.IdleTime(15_000, 10_000));
        Assert.Equal(TimeSpan.FromMilliseconds(1_000), IdleLock.IdleTime(500, uint.MaxValue - 499));
        Assert.Equal(TimeSpan.Zero, IdleLock.IdleTime(42, 42));
    }

    [Fact]
    public void IdleLockIsDueAfterTheChosenMinutesOnly()
    {
        Assert.False(IdleLock.IsDue(TimeSpan.FromHours(5), 0)); // Kapalı
        Assert.False(IdleLock.IsDue(TimeSpan.FromMinutes(9.9), 10));
        Assert.True(IdleLock.IsDue(TimeSpan.FromMinutes(10), 10));
        Assert.True(IdleLock.IsDue(TimeSpan.FromMinutes(31), 30));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 5)]
    [InlineData(10, 10)]
    [InlineData(30, 30)]
    [InlineData(7, 10)]
    [InlineData(-1, 10)]
    public void IdleMinutesOutsideTheOptionsReadAsDefault(int stored, int expected) => Assert.Equal(expected, IdleLock.Normalize(stored));

    // ---- Storage ----

    [Fact]
    public void DefaultsAreOffAndTenMinutes()
    {
        var settings = new AppSettings();

        Assert.False(settings.AppLock.Enabled);
        Assert.Null(settings.AppLock.PinHash);
        Assert.Equal(10, settings.AppLock.IdleMinutes);
        Assert.Equal(0, settings.AppLock.FailedAttempts);
        Assert.Null(settings.AppLock.LockedOutUntilUtc);
    }

    [Fact]
    public void OldSettingsWithoutLockLoadWithLockOff()
    {
        var path = Path.Combine(Path.GetTempPath(), $"spvision-{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(path, """{ "Devices": [], "Theme": "Dark", "AppLock": null }""");

            var loaded = new SettingsStore(path).Load();

            Assert.False(loaded.AppLock.Enabled);
            Assert.Equal(10, loaded.AppLock.IdleMinutes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PinHashIsDpapiProtectedOnDiskAndLockoutSurvivesRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"spvision-{Guid.NewGuid()}.json");
        try
        {
            var store = new SettingsStore(path);
            var settings = new AppSettings();
            settings.AppLock.Enabled = true;
            settings.AppLock.PinHash = Stored.Value;
            settings.AppLock.IdleMinutes = 30;
            PinLockout.RecordFailure(settings.AppLock, Now);
            PinLockout.RecordFailure(settings.AppLock, Now);
            PinLockout.RecordFailure(settings.AppLock, Now);

            store.Save(settings);
            var json = File.ReadAllText(path);
            var loaded = store.Load();

            var onDisk = JsonDocument.Parse(json).RootElement.GetProperty("AppLock");
            Assert.StartsWith("dpapi:", onDisk.GetProperty("PinHash").GetString());
            Assert.DoesNotContain(Stored.Value.Split('$')[3], json); // neither the hash
            Assert.DoesNotContain(Stored.Value.Split('$')[2], json); // nor the salt in clear
            Assert.DoesNotContain("258147", json);
            Assert.Equal(Stored.Value, settings.AppLock.PinHash); // in-memory copy untouched

            Assert.True(loaded.AppLock.Enabled);
            Assert.Equal(Stored.Value, loaded.AppLock.PinHash);
            Assert.Equal(30, loaded.AppLock.IdleMinutes);
            Assert.Equal(3, loaded.AppLock.FailedAttempts);
            Assert.Equal(TimeSpan.FromSeconds(30), PinLockout.Remaining(loaded.AppLock, Now));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UnreadablePinHashKeepsTheLockOn()
    {
        var path = Path.Combine(Path.GetTempPath(), $"spvision-{Guid.NewGuid()}.json");
        try
        {
            // Settings copied from another Windows account: DPAPI cannot open the hash.
            File.WriteAllText(path, """{ "AppLock": { "Enabled": true, "PinHash": "dpapi:AAAA" } }""");

            var loaded = new SettingsStore(path).Load();

            Assert.True(loaded.AppLock.Enabled);
            Assert.Null(loaded.AppLock.PinHash);
            Assert.False(PinHasher.Verify("258147", loaded.AppLock.PinHash));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- Reset ("PIN'i unuttum") ----

    [Fact]
    public void ResetForgetsDevicesViewsAndLockButKeepsPreferences()
    {
        var device = new Device
        {
            Name = "NVR",
            Host = "192.168.1.2",
            Password = "secret",
            StreamKey = "ABCDEF",
            Certificate = new CertificatePin { SpkiSha256 = "00" },
            Cameras = [new Camera { Channel = 1, Name = "KAPI", StreamKey = "QWERTY" }],
        };
        var view = new SavedView { Layout = 4, Tiles = [new CameraRef(device.Id, 1)] };
        var settings = new AppSettings
        {
            Devices = [device],
            Views = [view],
            LastView = view,
            LastPlaybackView = view,
            Theme = AppTheme.Dark,
            HardwareDecoding = false,
            LiveQuality = StreamQuality.Sub,
            ShowMotionBoxes = false,
            RestoreLastView = false,
            StartFullScreen = true,
            SnapshotFolder = @"D:\Resimler",
            RecordingFolder = @"D:\Videolar",
            SdkLogLevel = 3,
            Update = new UpdateState { LatestManifest = "m" },
        };
        settings.AppLock = new AppLockSettings
        {
            Enabled = true, PinHash = "x", IdleMinutes = 30, FailedAttempts = 7, LockedOutUntilUtc = Now,
        };

        AppReset.ForgetDevices(settings);

        Assert.Empty(settings.Devices);
        Assert.Empty(settings.Views);
        Assert.Null(settings.LastView);
        Assert.Null(settings.LastPlaybackView);
        Assert.False(settings.AppLock.Enabled);
        Assert.Null(settings.AppLock.PinHash);
        Assert.Equal(10, settings.AppLock.IdleMinutes);
        Assert.Equal(0, settings.AppLock.FailedAttempts);
        Assert.Null(settings.AppLock.LockedOutUntilUtc);

        Assert.Equal(AppTheme.Dark, settings.Theme);
        Assert.False(settings.HardwareDecoding);
        Assert.Equal(StreamQuality.Sub, settings.LiveQuality);
        Assert.False(settings.ShowMotionBoxes);
        Assert.False(settings.RestoreLastView);
        Assert.True(settings.StartFullScreen);
        Assert.Equal(@"D:\Resimler", settings.SnapshotFolder);
        Assert.Equal(@"D:\Videolar", settings.RecordingFolder);
        Assert.Equal(3, settings.SdkLogLevel);
        Assert.Equal("m", settings.Update?.LatestManifest);
    }

    [Fact]
    public void ResetLeavesNoSecretInTheSavedFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"spvision-{Guid.NewGuid()}.json");
        try
        {
            var settings = new AppSettings
            {
                Devices = [new Device { Name = "NVR-ADI", Host = "10.1.2.3", UserName = "kullanici", Password = "secret", StreamKey = "ABCDEF" }],
            };
            settings.AppLock = new AppLockSettings { Enabled = true, PinHash = Stored.Value };

            AppReset.ForgetDevices(settings);
            new SettingsStore(path).Save(settings);
            var json = File.ReadAllText(path);

            Assert.DoesNotContain("NVR-ADI", json);
            Assert.DoesNotContain("10.1.2.3", json);
            Assert.DoesNotContain("kullanici", json);
            Assert.DoesNotContain("dpapi:", json);
            Assert.DoesNotContain("PinHash", json);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
