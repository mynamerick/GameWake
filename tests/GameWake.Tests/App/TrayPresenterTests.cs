using GameWake.App.Services;
using GameWake.Core.Models;

namespace GameWake.Tests.App;

public sealed class TrayPresenterTests
{
    [Fact]
    public void Idle_and_active_tooltips_match_the_spec()
    {
        var idle = TrayPresenter.Describe(new MonitorSnapshot
        {
            Status = MonitorStatus.Idle,
            UpdatedAt = DateTimeOffset.Now,
        }, TrayState.Downloading);
        Assert.Equal(TrayState.Idle, idle.State);
        Assert.Equal("GameWake — No active downloads", idle.ToolTip);

        var one = TrayPresenter.Describe(new MonitorSnapshot
        {
            Status = MonitorStatus.SleepPrevented,
            UpdatedAt = DateTimeOffset.Now,
            QualifyingOperations = [Ops.Steam("1", ActivityState.Downloading, DetectionConfidence.Confirmed, "X")],
        }, TrayState.Idle);
        Assert.Equal(TrayState.Downloading, one.State);
        Assert.Equal("GameWake — Steam downloading", one.ToolTip);

        var two = TrayPresenter.Describe(new MonitorSnapshot
        {
            Status = MonitorStatus.SleepPrevented,
            UpdatedAt = DateTimeOffset.Now,
            QualifyingOperations =
            [
                Ops.Steam("1", ActivityState.Downloading, DetectionConfidence.Confirmed, "A"),
                Ops.Steam("2", ActivityState.Downloading, DetectionConfidence.Confirmed, "B"),
            ],
        }, TrayState.Idle);
        Assert.Equal("GameWake — 2 active downloads", two.ToolTip);
    }

    [Fact]
    public void Tooltip_is_truncated_to_notify_icon_limit()
    {
        var snapshot = new MonitorSnapshot
        {
            Status = MonitorStatus.Unavailable,
            UpdatedAt = DateTimeOffset.Now,
            IsSimulationActive = true,
        };
        var text = TrayPresenter.Describe(snapshot, TrayState.Idle).ToolTip;
        Assert.True(text.Length <= TrayPresenter.MaxToolTipLength);
    }
}
