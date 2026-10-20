using GameWake.Core.Models;
using GameWake.Core.Policy;
using GameWake.Core.Settings;

namespace GameWake.Tests.Engine;

public sealed class SleepDecisionAndPolicyTests
{
    [Fact]
    public void Activity_acquires_immediately_and_release_waits_for_grace()
    {
        var machine = new SleepDecisionMachine();
        var start = new DateTimeOffset(2026, 10, 20, 16, 0, 0, TimeSpan.Zero);
        var grace = TimeSpan.FromSeconds(120);

        var holding = machine.Evaluate(true, start, grace);
        Assert.Equal(SleepPhase.Holding, holding.Phase);
        Assert.True(holding.ShouldHold);

        var still = machine.Evaluate(false, start.AddSeconds(119), grace);
        Assert.Equal(SleepPhase.Grace, still.Phase);
        Assert.True(still.ShouldHold);
        Assert.Equal(start.AddSeconds(120), still.GraceEndsAt);

        var released = machine.Evaluate(false, start.AddSeconds(120), grace);
        Assert.Equal(SleepPhase.Released, released.Phase);
        Assert.False(released.ShouldHold);
    }

    [Fact]
    public void Activity_during_grace_returns_to_holding()
    {
        var machine = new SleepDecisionMachine();
        var start = new DateTimeOffset(2026, 10, 20, 16, 0, 0, TimeSpan.Zero);
        machine.Evaluate(true, start, TimeSpan.FromSeconds(60));
        machine.Evaluate(false, start.AddSeconds(10), TimeSpan.FromSeconds(60));
        var again = machine.Evaluate(true, start.AddSeconds(20), TimeSpan.FromSeconds(60));
        Assert.Equal(SleepPhase.Holding, again.Phase);
        Assert.Null(again.GraceEndsAt);
    }

    [Fact]
    public void Reset_releases_immediately()
    {
        var machine = new SleepDecisionMachine();
        var now = DateTimeOffset.Now;
        machine.Evaluate(true, now, TimeSpan.FromMinutes(5));
        machine.Reset();
        var after = machine.Evaluate(false, now, TimeSpan.FromMinutes(5));
        Assert.Equal(SleepPhase.Released, after.Phase);
    }

    [Fact]
    public void Confirmed_download_qualifies_probable_does_not_by_default()
    {
        var settings = new AppSettings();
        var confirmed = Ops.Steam("1", ActivityState.Downloading, DetectionConfidence.Confirmed, "A");
        var probable = Ops.Steam("2", ActivityState.Downloading, DetectionConfidence.Probable, "B");
        var paused = Ops.Steam("3", ActivityState.Paused, DetectionConfidence.Inactive, "C");
        var unknown = Ops.Steam("4", ActivityState.Downloading, DetectionConfidence.Unknown, "D");

        Assert.True(SleepPolicy.Qualifies(confirmed, settings, false));
        Assert.False(SleepPolicy.Qualifies(probable, settings, false));
        Assert.True(SleepPolicy.Qualifies(probable, settings with { IncludeProbableActivity = true }, false));
        Assert.False(SleepPolicy.Qualifies(paused, settings, false));
        Assert.False(SleepPolicy.Qualifies(unknown, settings, false));
    }

    [Fact]
    public void Simulated_activity_never_qualifies_unless_explicitly_allowed()
    {
        var settings = new AppSettings();
        var simulated = Ops.Steam("s", ActivityState.Downloading, DetectionConfidence.Confirmed, "Fake") with { IsSimulated = true };
        Assert.False(SleepPolicy.Qualifies(simulated, settings, simulationCanPreventSleep: false));
        Assert.True(SleepPolicy.Qualifies(simulated, settings, simulationCanPreventSleep: true));
    }

    [Fact]
    public void User_can_opt_out_of_individual_working_states()
    {
        var settings = new AppSettings { PreventSleepDuringVerification = false };
        var verifying = Ops.Steam("1", ActivityState.Verifying, DetectionConfidence.Confirmed);
        Assert.False(SleepPolicy.Qualifies(verifying, settings, false));
        Assert.True(SleepPolicy.Qualifies(Ops.Steam("1", ActivityState.Downloading, DetectionConfidence.Confirmed), settings, false));
    }
}
