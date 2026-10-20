using GameWake.Core.Models;
using GameWake.Core.Settings;
using GameWake.Providers.Common;
using GameWake.Providers.Simulation;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameWake.Tests.Providers;

public sealed class IncrementalLogAndSimulationTests
{
    [Fact]
    public void Incremental_reader_returns_only_new_complete_lines()
    {
        var path = Path.Combine(Path.GetTempPath(), "GameWake-log-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(path, "one\n");
            var reader = new IncrementalLogReader(path);
            var first = reader.ReadNewLines();
            Assert.Equal(["one"], first.Lines);

            File.AppendAllText(path, "two\npartial");
            var second = reader.ReadNewLines();
            Assert.Equal(["two"], second.Lines);

            File.AppendAllText(path, " line\n");
            var third = reader.ReadNewLines();
            Assert.Equal(["partial line"], third.Lines);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Incremental_reader_reports_missing_file()
    {
        var reader = new IncrementalLogReader(Path.Combine(Path.GetTempPath(), "GameWake-missing-" + Guid.NewGuid().ToString("N") + ".log"));
        var result = reader.ReadNewLines();
        Assert.True(result.FileMissing);
        Assert.Empty(result.Lines);
    }

    [Fact]
    public void Runtime_options_parse_simulation_switches()
    {
        var options = RuntimeOptions.Parse(["--simulate", "--autostart"]);
        Assert.True(options.Simulation);
        Assert.True(options.LaunchedAtStartup);
        Assert.False(options.SimulationCanPreventSleep);

        var allow = RuntimeOptions.Parse(["--simulate-prevent-sleep"]);
        Assert.True(allow.Simulation);
        Assert.True(allow.SimulationCanPreventSleep);
    }

    [Fact]
    public async Task Simulation_script_covers_ui_states_and_marks_every_operation()
    {
        var provider = new SimulationProvider(new FakeClock(), NullLogger<SimulationProvider>.Instance);
        var snapshot = await provider.PollAsync(CancellationToken.None);
        Assert.True(snapshot.IsSimulated);
        Assert.All(snapshot.Operations, o => Assert.True(o.IsSimulated));

        var downloading = SimulationProvider.Script(10);
        Assert.Contains(downloading, o => o.State == ActivityState.Downloading && o.Confidence == DetectionConfidence.Confirmed);
        Assert.Contains(downloading, o => o.State == ActivityState.Queued);

        var paused = SimulationProvider.Script(300);
        Assert.Contains(paused, o => o.State == ActivityState.Paused);
        Assert.Contains(paused, o => o.State == ActivityState.Verifying);

        var unknown = SimulationProvider.Script(340);
        Assert.Contains(unknown, o => o.State == ActivityState.Unknown && o.GameTitle is null);
    }
}
