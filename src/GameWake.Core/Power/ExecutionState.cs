using System.Runtime.InteropServices;

namespace GameWake.Core.Power;

[Flags]
public enum ExecutionStateFlags : uint
{
    None = 0,
    SystemRequired = 0x00000001,
    DisplayRequired = 0x00000002,
    AwayModeRequired = 0x00000040,
    Continuous = 0x80000000,
}

public interface IExecutionStateApi
{
    /// <summary>Returns the thread's previous state, or <see cref="ExecutionStateFlags.None"/> if the call failed.</summary>
    ExecutionStateFlags SetThreadExecutionState(ExecutionStateFlags flags);
}

public sealed class Win32ExecutionStateApi : IExecutionStateApi
{
    public ExecutionStateFlags SetThreadExecutionState(ExecutionStateFlags flags) =>
        (ExecutionStateFlags)NativeMethods.SetThreadExecutionState((uint)flags);

    private static class NativeMethods
    {
        [DllImport("kernel32.dll")]
        public static extern uint SetThreadExecutionState(uint esFlags);
    }
}

/// <summary>
/// Reads the system-wide execution state (all processes combined) via CallNtPowerInformation.
/// Used for diagnostics only: it cannot tell which process holds a request.
/// </summary>
public static class SystemExecutionState
{
    private const int SystemExecutionStateLevel = 16;

    public static ExecutionStateFlags? TryRead()
    {
        try
        {
            var status = NativeMethods.CallNtPowerInformation(SystemExecutionStateLevel, IntPtr.Zero, 0, out var state, sizeof(uint));
            return status == 0 ? (ExecutionStateFlags)state : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static class NativeMethods
    {
        [DllImport("powrprof.dll")]
        public static extern uint CallNtPowerInformation(int informationLevel, IntPtr inputBuffer, uint inputBufferLength, out uint outputBuffer, uint outputBufferLength);
    }
}
