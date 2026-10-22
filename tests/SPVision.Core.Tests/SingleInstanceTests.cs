using System.Diagnostics;
using SPVision.Core;

namespace SPVision.Core.Tests;

/// <summary>
/// One SPVision per session (named mutex + activation event). A mutex is re-entrant on its owning thread, so every
/// "process" here is its own thread; names are unique per test.
/// </summary>
public class SingleInstanceTests
{
    static string NewName() => "SPVision.Test." + Guid.NewGuid().ToString("N");

    /// <summary>Runs Acquire on a dedicated thread that keeps the mutex until told to release it (or to die without releasing).</summary>
    sealed class Owner : IDisposable
    {
        readonly Thread _thread;
        readonly ManualResetEventSlim _ready = new();
        readonly ManualResetEventSlim _finish = new();
        bool _release = true;

        public SingleInstance? Instance { get; private set; }

        public Owner(string name, Func<bool>? onActivate = null)
        {
            _thread = new Thread(() =>
            {
                Instance = SingleInstance.Acquire(name, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(300));
                if (onActivate is not null)
                {
                    Instance?.Listen(onActivate);
                }
                _ready.Set();
                _finish.Wait();
                if (_release)
                {
                    Instance?.Dispose();
                }
            }) { IsBackground = true };
            _thread.Start();
            Assert.True(_ready.Wait(TimeSpan.FromSeconds(5)));
        }

        /// <summary>Ends the owner thread; <paramref name="release"/> false leaves the mutex abandoned.</summary>
        public void End(bool release = true)
        {
            _release = release;
            _finish.Set();
            Assert.True(_thread.Join(TimeSpan.FromSeconds(5)));
        }

        public void Dispose()
        {
            if (_thread.IsAlive)
            {
                End();
            }
        }
    }

    static SingleInstance? AcquireOnThread(string name, TimeSpan acknowledge, TimeSpan handover, out TimeSpan took)
    {
        SingleInstance? result = null;
        var watch = Stopwatch.StartNew();
        var thread = new Thread(() =>
        {
            result = SingleInstance.Acquire(name, acknowledge, handover);
            result?.Dispose(); // released on the acquiring thread
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        took = watch.Elapsed;
        return result;
    }

    [Fact]
    public void FirstLaunchOwnsTheInstance()
    {
        using var owner = new Owner(NewName());
        Assert.NotNull(owner.Instance);
    }

    [Fact]
    public void SecondLaunchBringsTheRunningOneToFrontAndExits()
    {
        var name = NewName();
        var activated = 0;
        using var owner = new Owner(name, () =>
        {
            Interlocked.Increment(ref activated);
            return true;
        });

        var second = AcquireOnThread(name, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), out var took);

        Assert.Null(second);
        Assert.Equal(1, Volatile.Read(ref activated));
        Assert.True(took < TimeSpan.FromSeconds(5), $"waited {took}"); // answered at once, no handover wait
    }

    [Fact]
    public void ClosingInstanceHandsOverWhenItReleases()
    {
        // The updater case: the old process is shutting down (does not come to the front) and lets go a little later.
        var name = NewName();
        using var owner = new Owner(name, () => false);
        var release = new Thread(() =>
        {
            Thread.Sleep(500);
            owner.End();
        });
        release.Start();

        var second = AcquireOnThread(name, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(10), out _);

        Assert.True(release.Join(TimeSpan.FromSeconds(10)));
        Assert.NotNull(second);
    }

    [Fact]
    public void AbandonedMutexCountsAsAcquired()
    {
        // Owner ended by Environment.Exit / killed: the mutex is never released.
        var name = NewName();
        var owner = new Owner(name);
        owner.End(release: false);

        var second = AcquireOnThread(name, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(2), out _);

        Assert.NotNull(second);
    }

    [Fact]
    public void HungInstanceIsLeftAloneAfterTheHandoverWait()
    {
        var name = NewName();
        using var owner = new Owner(name); // never listens: no answer, never releases

        var second = AcquireOnThread(name, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(400), out var took);

        Assert.Null(second);
        Assert.True(took >= TimeSpan.FromMilliseconds(450), $"waited only {took}");
    }

    [Fact]
    public void RequestMadeBeforeListeningIsDeliveredOnListen()
    {
        // The second launch came while the first was still starting (window not created yet).
        var name = NewName();
        var activated = new ManualResetEventSlim();
        using var owner = new Owner(name);
        Assert.Null(AcquireOnThread(name, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100), out _));

        owner.Instance!.Listen(() =>
        {
            activated.Set();
            return true;
        });

        Assert.True(activated.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void ReleasedInstanceCanBeTakenAgain()
    {
        var name = NewName();
        var owner = new Owner(name);
        owner.End();

        Assert.NotNull(AcquireOnThread(name, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100), out var took));
        Assert.True(took < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void DefaultsMatchTheShutdownWatchdog()
    {
        Assert.Equal("SPVision.SingleInstance", SingleInstance.DefaultName);
        Assert.True(SingleInstance.HandoverWait >= TimeSpan.FromSeconds(8 + 1)); // App.ExitLimit 8 s, plus margin
    }
}
