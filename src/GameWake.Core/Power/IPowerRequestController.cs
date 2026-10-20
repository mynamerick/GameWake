namespace GameWake.Core.Power;

public interface IPowerRequestController : IDisposable
{
    bool IsHeld { get; }

    DateTimeOffset? HeldSince { get; }

    /// <summary>Queues a hold or release. Returns immediately; requests are applied in order.</summary>
    void SetRequested(bool hold, string reason);

    /// <summary>Completes once every previously queued request has been applied.</summary>
    Task FlushAsync();

    event EventHandler<PowerRequestChangedEventArgs>? Changed;
}

public sealed class PowerRequestChangedEventArgs(bool isHeld, string reason, DateTimeOffset at) : EventArgs
{
    public bool IsHeld { get; } = isHeld;

    public string Reason { get; } = reason;

    public DateTimeOffset At { get; } = at;
}
