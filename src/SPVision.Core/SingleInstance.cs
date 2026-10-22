namespace SPVision.Core;

/// <summary>
/// One SPVision per Windows session. The first process owns a named mutex (<c>Local\SPVision.SingleInstance</c>) for its
/// whole life; a second launch asks it through a named event to come to the front, waits briefly for its answer and then
/// exits quietly. No answer means the owner is closing (the in-app updater: Inno Setup relaunches the app while the old
/// process may still be inside its 8 s shutdown watchdog) or hung, so the newcomer waits up to <see cref="HandoverWait"/>
/// for the mutex and becomes the instance if it gets it. An abandoned mutex (owner killed or ended by
/// <c>Environment.Exit</c> without releasing) counts as acquired. Unit tested (SingleInstanceTests).
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public const string DefaultName = "SPVision.SingleInstance";

    /// <summary>How long a newcomer waits for the running instance to confirm it came to the front.</summary>
    public static readonly TimeSpan AcknowledgeWait = TimeSpan.FromSeconds(2);

    /// <summary>How long a newcomer waits for a closing instance to release the mutex (watchdog 8 s + margin).</summary>
    public static readonly TimeSpan HandoverWait = TimeSpan.FromSeconds(10);

    readonly Mutex _mutex;
    readonly EventWaitHandle _activate;
    readonly EventWaitHandle _acknowledge;
    RegisteredWaitHandle? _listening;
    int _disposed;

    SingleInstance(Mutex mutex, EventWaitHandle activate, EventWaitHandle acknowledge)
    {
        _mutex = mutex;
        _activate = activate;
        _acknowledge = acknowledge;
    }

    /// <summary>
    /// The owning instance, or null when another instance runs (it has been asked to come to the front). Blocks for up to
    /// <paramref name="acknowledgeWait"/> + <paramref name="handoverWait"/> only when another process holds the mutex.
    /// Must be disposed on the same thread (a mutex belongs to the thread that acquired it).
    /// </summary>
    public static SingleInstance? Acquire(string name = DefaultName, TimeSpan? acknowledgeWait = null, TimeSpan? handoverWait = null)
    {
        var mutex = new Mutex(false, $@"Local\{name}");
        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.Activate");
        var acknowledge = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.Activated");
        if (TryTake(mutex, TimeSpan.Zero))
        {
            return new SingleInstance(mutex, activate, acknowledge);
        }

        // Another instance holds it: ask it to come to the front. A stale answer of an earlier launch is dropped first.
        acknowledge.Reset();
        activate.Set();
        if (!acknowledge.WaitOne(acknowledgeWait ?? AcknowledgeWait) && TryTake(mutex, handoverWait ?? HandoverWait))
        {
            activate.Reset(); // our own request, nobody was there to take it
            return new SingleInstance(mutex, activate, acknowledge);
        }
        activate.Dispose();
        acknowledge.Dispose();
        mutex.Dispose();
        return null;
    }

    static bool TryTake(Mutex mutex, TimeSpan wait)
    {
        try
        {
            return mutex.WaitOne(wait);
        }
        catch (AbandonedMutexException)
        {
            return true; // the previous owner died without releasing it; the mutex is ours now
        }
    }

    /// <summary>
    /// Every later launch calls <paramref name="activate"/> on a thread-pool thread; returning true (window shown) tells
    /// the newcomer to exit, false (closing) lets it wait for the mutex. A request made before this call is delivered at once.
    /// </summary>
    public void Listen(Func<bool> activate)
    {
        _listening?.Unregister(null);
        _listening = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) =>
        {
            bool shown;
            try
            {
                shown = activate();
            }
            catch (Exception)
            {
                shown = false;
            }
            if (shown && Volatile.Read(ref _disposed) == 0)
            {
                try
                {
                    _acknowledge.Set();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Stops listening and releases the mutex (on the acquiring thread; elsewhere the OS releases it at exit).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _listening?.Unregister(null);
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // not the owning thread: released by Windows when the process ends (the next instance sees it abandoned)
        }
        _mutex.Dispose();
        _activate.Dispose();
        _acknowledge.Dispose();
    }
}
