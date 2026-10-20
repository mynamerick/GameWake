using GameWake.Core.Models;
using GameWake.Providers.Steam;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace GameWake.Tests.Steam;

public sealed class SteamDownloadProviderTests
{
    [Fact]
    public async Task Not_installed_when_steam_folder_is_missing()
    {
        var provider = new SteamDownloadProvider(new FakeClock(), NullLogger<SteamDownloadProvider>.Instance, new FakeRegistry(), new FakeProcessInspector(), () => null);
        var snapshot = await provider.PollAsync(CancellationToken.None);
        Assert.False(snapshot.IsInstalled);
        Assert.Equal(ActivityState.NotRunning, snapshot.SummaryState);
        Assert.Empty(snapshot.Operations);
    }

    [Fact]
    public async Task Running_without_downloads_does_not_report_activity()
    {
        var root = CreateSteamTree();
        try
        {
            var processes = new FakeProcessInspector();
            processes.Processes.Add(new GameWake.Providers.Common.ProcessInfo(100, "steam", new DateTime(2026, 10, 20, 16, 0, 0)));
            var registry = new FakeRegistry();
            registry.Set(RegistryHive.CurrentUser, SteamLocator.ActiveProcessKey, "pid", 100);

            var provider = new SteamDownloadProvider(new FakeClock(), NullLogger<SteamDownloadProvider>.Instance, registry, processes, () => root);
            var snapshot = await provider.PollAsync(CancellationToken.None);
            Assert.True(snapshot.IsInstalled);
            Assert.True(snapshot.IsRunning);
            Assert.Equal(ActivityState.Idle, snapshot.SummaryState);
            Assert.Empty(snapshot.Operations);
            Assert.Null(snapshot.Error);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Confirmed_download_from_content_log()
    {
        var root = CreateSteamTree();
        try
        {
            File.WriteAllText(Path.Combine(root, "logs", "content_log.txt"), """
                [2026-10-20 16:22:21] AppID 570 state changed : Update Required,Update Running,
                [2026-10-20 16:22:23] AppID 570 App update changed : Running Update,Downloading,
                [2026-10-20 16:23:36] Current download rate: 12.5 Mbps

                """);
            File.WriteAllText(Path.Combine(root, "steamapps", "appmanifest_570.acf"), """
                "AppState"
                {
                	"appid"		"570"
                	"name"		"Dota 2"
                	"StateFlags"		"6"
                }
                """);

            var processes = new FakeProcessInspector();
            processes.Processes.Add(new GameWake.Providers.Common.ProcessInfo(100, "steam", new DateTime(2026, 10, 20, 16, 0, 0)));
            var registry = new FakeRegistry();
            registry.Set(RegistryHive.CurrentUser, SteamLocator.ActiveProcessKey, "pid", 100);
            registry.Set(RegistryHive.CurrentUser, SteamLocator.AppsKey + @"\570", "Updating", 1);
            registry.AddSubKey(RegistryHive.CurrentUser, SteamLocator.AppsKey, "570");

            var provider = new SteamDownloadProvider(new FakeClock(), NullLogger<SteamDownloadProvider>.Instance, registry, processes, () => root);
            var snapshot = await provider.PollAsync(CancellationToken.None);
            var op = Assert.Single(snapshot.Operations, o => o.Key == "steam:570");
            Assert.Equal("Dota 2", op.GameTitle);
            Assert.Equal(ActivityState.Downloading, op.State);
            Assert.Equal(DetectionConfidence.Confirmed, op.Confidence);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateSteamTree()
    {
        var root = Path.Combine(Path.GetTempPath(), "GameWake-steam-prov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "steamapps"));
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        File.WriteAllText(Path.Combine(root, "steam.exe"), string.Empty);
        return root;
    }
}
