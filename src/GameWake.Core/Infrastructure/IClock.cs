namespace GameWake.Core.Infrastructure;

public interface IClock
{
    DateTimeOffset Now { get; }
}

public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();

    public DateTimeOffset Now => DateTimeOffset.Now;
}
