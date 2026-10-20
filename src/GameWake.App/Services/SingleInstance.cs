namespace GameWake.App.Services;

/// <summary>
/// One GameWake per user session. A second launch signals the first one to show its window and
/// then exits. Both objects live in the session-local namespace, so other users are unaffected.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\GameWake.SingleInstance.6F1C2B4E";
    private const string ActivateEventName = @"Local\GameWake.Activate.6F1C2B4E";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private RegisteredWaitHandle? _registration;

    private SingleInstance(Mutex mutex, EventWaitHandle activate)
    {
        _mutex = mutex;
        _activate = activate;
    }

    /// <summary>Returns null when another instance already owns the session; that instance is asked to show itself.</summary>
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            try
            {
                using var existing = EventWaitHandle.OpenExisting(ActivateEventName);
                existing.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }

            return null;
        }

        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        return new SingleInstance(mutex, activate);
    }

    /// <summary>Invokes <paramref name="onActivate"/> (on a thread-pool thread) whenever a second launch is attempted.</summary>
    public void ListenForActivation(Action onActivate)
    {
        _registration = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _activate.Dispose();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }

        _mutex.Dispose();
    }
}
