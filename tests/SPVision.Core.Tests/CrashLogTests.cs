using System.Text;
using SPVision.Core;

namespace SPVision.Core.Tests;

/// <summary>Secret scrubbing (crash log, report zip) and logs\crash.log writing/rolling.</summary>
public class CrashLogTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "spvision-crashlog-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("rtsp://admin:Gizli123@192.168.1.2:554/Streaming/Channels/101", "rtsp://***@192.168.1.2:554/Streaming/Channels/101")]
    [InlineData("Açılamadı: http://admin:p@ss@cihaz/ISAPI hata", "Açılamadı: http://***@cihaz/ISAPI hata")]
    [InlineData("https://user@host/x", "https://***@host/x")]
    [InlineData("rtsp://192.168.1.2:554/a", "rtsp://192.168.1.2:554/a")]
    public void UrlCredentialsAreMasked(string input, string expected) => Assert.Equal(expected, LogScrubber.Scrub(input));

    [Theory]
    [InlineData("password=Gizli123", "password=***")]
    [InlineData("login pwd=abc&user=admin", "login pwd=***&user=admin")]
    [InlineData("Password: hunter2 sonra", "Password: *** sonra")]
    [InlineData("user_password = 'a b c'", "user_password = ***")]
    [InlineData("{\"Password\": \"dpapi:AQAAANCM\", \"Host\": \"x\"}", "{\"Password\": ***, \"Host\": \"x\"}")]
    [InlineData("<password>Gizli</password>", "<password>***</password>")]
    [InlineData("<StreamKey>ABCDEF</StreamKey>", "<StreamKey>***</StreamKey>")]
    [InlineData("streamKey=ABCDEF", "streamKey=***")]
    [InlineData("Authorization: Digest username=\"admin\", response=\"9f8e\"\nnext", "Authorization: ***\nnext")]
    [InlineData("PWD=x;Server=y", "PWD=***;Server=y")]
    public void PasswordLikeValuesAreMasked(string input, string expected) => Assert.Equal(expected, LogScrubber.Scrub(input));

    [Theory]
    [InlineData("passwordless giriş yok")]
    [InlineData("Şifre yanlış (401)")]
    [InlineData("compass=north")]
    [InlineData("bypass: yes")]
    [InlineData("")]
    public void OrdinaryTextIsUntouched(string input) => Assert.Equal(input, LogScrubber.Scrub(input));

    [Fact]
    public void NullScrubsToEmpty() => Assert.Equal("", LogScrubber.Scrub(null));

    [Fact]
    public void EntryHasTimeVersionOsAndTheWholeException()
    {
        Exception ex;
        try
        {
            throw new InvalidOperationException("rtsp://admin:Gizli@10.0.0.5/ açılamadı", new IOException("iç hata"));
        }
        catch (Exception e)
        {
            ex = e;
        }

        var entry = CrashLog.Entry(new DateTimeOffset(2026, 10, 22, 14, 3, 11, 123, TimeSpan.FromHours(3)),
            "DispatcherUnhandledException", "1.0.0", "Windows 10.0.26300", ".NET 10.0.0", ex.ToString());

        Assert.StartsWith("==== 2026-10-22 14:03:11.123 +03:00 · DispatcherUnhandledException ====", entry);
        Assert.Contains("SPVision 1.0.0 · Windows 10.0.26300 · .NET 10.0.0", entry);
        Assert.Contains("System.InvalidOperationException", entry);
        Assert.Contains("iç hata", entry);                // inner exception
        Assert.Contains(nameof(EntryHasTimeVersionOsAndTheWholeException), entry); // stack trace
        Assert.Contains("rtsp://***@10.0.0.5/", entry);
        Assert.DoesNotContain("Gizli", entry);
    }

    [Fact]
    public void AppendCreatesTheFolderAndScrubs()
    {
        Assert.True(CrashLog.Append(_dir, "a password=x b\n"));

        Assert.Equal("a password=*** b\n", File.ReadAllText(Path.Combine(_dir, CrashLog.FileName)));
    }

    [Fact]
    public void RollsAtTheLimitKeepingTwoFiles()
    {
        var line = new string('x', 99) + "\n"; // 100 bytes
        for (var i = 0; i < 25; i++)
        {
            Assert.True(CrashLog.Append(_dir, line, maxBytes: 1000));
        }

        var current = new FileInfo(Path.Combine(_dir, "crash.log"));
        var old = new FileInfo(Path.Combine(_dir, "crash.log.1"));
        Assert.True(current.Exists && old.Exists);
        Assert.True(current.Length <= 1000);
        Assert.Equal(1000, old.Length);
        Assert.Equal(2, Directory.GetFiles(_dir).Length);
        Assert.Equal(500, current.Length); // 25 lines = 10 + 10 (rolled away, dropped) + 5
    }

    [Fact]
    public void OversizedSingleEntryIsStillWritten()
    {
        var big = new string('y', 3000);
        Assert.True(CrashLog.Append(_dir, big, maxBytes: 1000));
        Assert.True(CrashLog.Append(_dir, "z", maxBytes: 1000));

        Assert.Equal(3000, new FileInfo(Path.Combine(_dir, "crash.log.1")).Length);
        Assert.Equal("z", File.ReadAllText(Path.Combine(_dir, "crash.log")));
    }

    [Fact]
    public void AppendNeverThrows()
    {
        // A file where the folder should be: the write fails quietly.
        File.WriteAllText(_dir, "not a folder");
        try
        {
            Assert.False(CrashLog.Append(_dir, "entry"));
        }
        finally
        {
            File.Delete(_dir);
        }
    }

    [Fact]
    public void UnicodeEntriesAreUtf8()
    {
        CrashLog.Append(_dir, "Ayarlar okunamadı: ğüşiöç\n");
        Assert.Equal("Ayarlar okunamadı: ğüşiöç\n", File.ReadAllText(Path.Combine(_dir, "crash.log"), Encoding.UTF8));
    }
}
