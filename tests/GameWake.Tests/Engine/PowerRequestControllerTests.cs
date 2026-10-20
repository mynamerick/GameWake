using GameWake.Core.Power;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameWake.Tests.Engine;

public sealed class PowerRequestControllerTests
{
    [Fact]
    public async Task Hold_and_release_run_on_the_same_dedicated_thread()
    {
        var api = new RecordingExecutionState();
        using var controller = new PowerRequestController(api, new FakeClock(), NullLogger<PowerRequestController>.Instance);
        controller.SetRequested(true, "download");
        await controller.FlushAsync();
        Assert.True(controller.IsHeld);
        controller.SetRequested(false, "done");
        await controller.FlushAsync();
        Assert.False(controller.IsHeld);

        Assert.Equal(2, api.Calls.Count);
        Assert.All(api.Calls, c => Assert.Equal(controller.OwnerThreadId, c.ThreadId));
        Assert.Equal(PowerRequestController.HoldFlags, api.Calls[0].Flags);
        Assert.Equal(PowerRequestController.ReleaseFlags, api.Calls[1].Flags);
        Assert.DoesNotContain(api.Calls, c => c.Flags.HasFlag(ExecutionStateFlags.DisplayRequired));
    }

    [Fact]
    public async Task Dispose_releases_if_still_held()
    {
        var api = new RecordingExecutionState();
        var controller = new PowerRequestController(api, new FakeClock(), NullLogger<PowerRequestController>.Instance);
        controller.SetRequested(true, "download");
        await controller.FlushAsync();
        controller.Dispose();
        Assert.Contains(api.Calls, c => c.Flags == PowerRequestController.ReleaseFlags);
    }

    private sealed class RecordingExecutionState : IExecutionStateApi
    {
        public List<(int ThreadId, ExecutionStateFlags Flags)> Calls { get; } = [];

        public ExecutionStateFlags SetThreadExecutionState(ExecutionStateFlags flags)
        {
            Calls.Add((Environment.CurrentManagedThreadId, flags));
            return ExecutionStateFlags.Continuous;
        }
    }
}
