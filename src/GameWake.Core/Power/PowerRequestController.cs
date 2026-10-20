using System.Collections.Concurrent;
using GameWake.Core.Infrastructure;
using Microsoft.Extensions.Logging;

namespace GameWake.Core.Power;

/// <summary>
/// Owns GameWake's sleep-prevention request.
/// <para>
/// <c>SetThreadExecutionState</c> is per-thread, so every call is made from one dedicated,
/// long-lived thread: the request is set and cleared on the same thread, and Windows drops it
/// automatically if that thread ever exits. Only <c>ES_SYSTEM_REQUIRED</c> is requested, so the
/// display can still turn off, and explicit sleep or shutdown by the user is never blocked.
/// </para>
/// </summary>
public sealed class PowerRequestController : IPowerRequestController
{
    public const ExecutionStateFlags HoldFlags = ExecutionStateFlags.Continuous | ExecutionStateFlags.SystemRequired;
    public const ExecutionStateFlags ReleaseFlags = ExecutionStateFlags.Continuous;

    private readonly BlockingCollection<Command> _commands = new();
    private readonly IExecutionStateApi _api;
    private readonly IClock _clock;
    private readonly ILogger<PowerRequestController> _logger;
    private readonly Thread _thread;
    private readonly object _stateLock = new();
    private bool _isHeld;
    private DateTimeOffset? _heldSince;
    private int _disposed;

    public PowerRequestController(IExecutionStateApi api, IClock clock, ILogger<PowerRequestController> logger)
    {
        _api = api;
        _clock = clock;
        _logger = logger;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "GameWake power request",
        };
        _thread.Start();
    }

    public event EventHandler<PowerRequestChangedEventArgs>? Changed;

    public bool IsHeld
    {
        get
        {
            lock (_stateLock)
            {
                return _isHeld;
            }
        }
    }

    public DateTimeOffset? HeldSince
    {
        get
        {
            lock (_stateLock)
            {
                return _heldSince;
            }
        }
    }

    /// <summary>Managed id of the thread that issues every SetThreadExecutionState call.</summary>
    public int OwnerThreadId => _thread.ManagedThreadId;

    public void SetRequested(bool hold, string reason)
    {
        TryEnqueue(new Command(hold ? CommandKind.Hold : CommandKind.Release, reason, null));
    }

    public Task FlushAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!TryEnqueue(new Command(CommandKind.Flush, "flush", completion)))
        {
            completion.TrySetResult();
        }

        return completion.Task;
    }

    private bool TryEnqueue(Command command)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        try
        {
            _commands.Add(command);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void Run()
    {
        try
        {
            foreach (var command in _commands.GetConsumingEnumerable())
            {
                switch (command.Kind)
                {
                    case CommandKind.Hold:
                        Apply(hold: true, command.Reason);
                        break;
                    case CommandKind.Release:
                        Apply(hold: false, command.Reason);
                        break;
                    case CommandKind.Flush:
                        command.Completion?.TrySetResult();
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Power request thread failed");
        }
        finally
        {
            Apply(hold: false, "power request thread exiting");
        }
    }

    private void Apply(bool hold, string reason)
    {
        bool currentlyHeld;
        lock (_stateLock)
        {
            currentlyHeld = _isHeld;
        }

        if (currentlyHeld == hold)
        {
            return;
        }

        var previous = _api.SetThreadExecutionState(hold ? HoldFlags : ReleaseFlags);
        if (previous == ExecutionStateFlags.None)
        {
            _logger.LogError("SetThreadExecutionState({Flags}) failed while trying to {Action} sleep prevention ({Reason})",
                hold ? HoldFlags : ReleaseFlags, hold ? "acquire" : "release", reason);
            return;
        }

        var now = _clock.Now;
        lock (_stateLock)
        {
            _isHeld = hold;
            _heldSince = hold ? now : null;
        }

        if (hold)
        {
            _logger.LogInformation("Sleep prevention acquired: {Reason}", reason);
        }
        else
        {
            _logger.LogInformation("Sleep prevention released: {Reason}", reason);
        }

        try
        {
            Changed?.Invoke(this, new PowerRequestChangedEventArgs(hold, reason, now));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Power state change handler failed");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _commands.Add(new Command(CommandKind.Release, "shutting down", null));
        }
        catch (InvalidOperationException)
        {
        }

        _commands.CompleteAdding();
        if (!_thread.Join(TimeSpan.FromSeconds(5)))
        {
            _logger.LogWarning("Power request thread did not stop within 5 seconds");
        }
    }

    private enum CommandKind
    {
        Hold,
        Release,
        Flush,
    }

    private sealed record Command(CommandKind Kind, string Reason, TaskCompletionSource? Completion);
}
