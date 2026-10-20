using GameWake.Providers.Steam;

namespace GameWake.Tests.Steam;

public sealed class SteamContentLogParserTests
{
    [Fact]
    public void Parses_state_phase_start_finish_and_rate_lines()
    {
        var state = Assert.IsType<SteamStateChanged>(SteamContentLogParser.ParseLine(
            "[2026-10-20 16:22:21] AppID 5280930 state changed : Update Required,Update Queued,Update Running,"));
        Assert.Equal(5280930u, state.AppId);
        Assert.Contains("Update Running", state.Flags);

        var phase = Assert.IsType<SteamPhaseChanged>(SteamContentLogParser.ParseLine(
            "[2026-10-20 16:22:23] AppID 5280930 App update changed : Running Update,Downloading,Staging,"));
        Assert.Equal(SteamUpdateChannel.App, phase.Channel);
        Assert.Contains("Downloading", phase.Phases);

        var started = Assert.IsType<SteamUpdateStarted>(SteamContentLogParser.ParseLine(
            "[2026-10-20 16:22:23] AppID 5280930 update started : download 0/40938995792, store 0/0, reuse 0/0, delta 0/0, stage 0/47128058401"));
        Assert.Equal(40938995792, started.DownloadTotal);

        var rate = Assert.IsType<SteamDownloadRate>(SteamContentLogParser.ParseLine(
            "[2026-10-20 16:23:36] Current download rate: 58.964 Mbps"));
        Assert.Equal(58.964, rate.MegabitsPerSecond, 3);

        var finished = Assert.IsType<SteamUpdateFinished>(SteamContentLogParser.ParseLine(
            "[2026-10-15 21:12:41] AppID 1867240 finished update, 1 mounted depots (BuildID 25937608) : done"));
        Assert.Equal(1867240u, finished.AppId);

        var paused = Assert.IsType<SteamStateChanged>(SteamContentLogParser.ParseLine(
            "[2026-10-20 16:22:51] AppID 1172710 state changed : Update Required,Fully Installed,Update Paused,"));
        Assert.Contains("Update Paused", paused.Flags);

        var canceled = Assert.IsType<SteamUpdateCanceled>(SteamContentLogParser.ParseLine(
            "[2026-10-15 18:32:45] AppID 1144200 update canceled : Disabled (Suspended)"));
        Assert.Contains("Suspended", canceled.Reason);
    }

    [Fact]
    public void Ignores_unknown_and_malformed_lines()
    {
        Assert.Null(SteamContentLogParser.ParseLine("not a steam log line"));
        Assert.Null(SteamContentLogParser.ParseLine("[2026-10-20 16:22:21] something else happened"));
        Assert.Null(SteamContentLogParser.ParseLine("[bad] AppID 1 state changed : Update Running,"));
    }

    [Fact]
    public void Session_ignores_events_from_before_steam_started()
    {
        var session = new SteamSession(1, new DateTime(2026, 10, 20, 16, 0, 0));
        session.Apply(SteamContentLogParser.ParseLine("[2026-10-19 12:00:00] AppID 570 state changed : Update Running,")!);
        session.Apply(SteamContentLogParser.ParseLine("[2026-10-20 16:01:00] AppID 570 state changed : Update Running,")!);
        Assert.Equal(1, session.IgnoredEvents);
        Assert.Equal(1, session.AppliedEvents);
        Assert.True(session.Apps[570].HasFlag("Update Running"));
    }

    [Fact]
    public void Transient_running_update_does_not_replace_effective_phase()
    {
        var session = new SteamSession(1, new DateTime(2026, 10, 20, 16, 0, 0));
        session.Apply(SteamContentLogParser.ParseLine("[2026-10-20 16:01:00] AppID 570 App update changed : Running Update,Downloading,Staging,")!);
        session.Apply(SteamContentLogParser.ParseLine("[2026-10-20 16:01:05] AppID 570 App update changed : Running Update,")!);
        var channel = session.Apps[570].Channels[SteamUpdateChannel.App];
        Assert.Contains("Downloading", channel.EffectivePhases);
        Assert.DoesNotContain("Downloading", channel.Phases);
    }
}
