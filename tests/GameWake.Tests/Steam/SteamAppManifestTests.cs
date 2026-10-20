using GameWake.Providers.Steam;

namespace GameWake.Tests.Steam;

public sealed class SteamAppManifestTests
{
    [Fact]
    public void Reads_name_state_and_saved_progress()
    {
        const string text = """
            "AppState"
            {
            	"appid"		"570"
            	"name"		"Dota 2"
            	"StateFlags"		"516"
            	"installdir"		"dota 2 beta"
            	"BytesToDownload"		"4000"
            	"BytesDownloaded"		"1000"
            	"TargetBuildID"		"99"
            }
            """;

        var manifest = SteamAppManifest.FromVdf(VdfParser.Parse(text), "appmanifest_570.acf", @"D:\Games", DateTimeOffset.Now);
        Assert.NotNull(manifest);
        Assert.Equal(570u, manifest!.AppId);
        Assert.Equal("Dota 2", manifest.Name);
        Assert.True(manifest.StateFlags.HasFlag(SteamAppStateFlags.FullyInstalled));
        Assert.True(manifest.StateFlags.HasFlag(SteamAppStateFlags.UpdatePaused));
        Assert.Equal(4000, manifest.BytesToDownload);
        Assert.Equal(1000, manifest.BytesDownloaded);
        Assert.Equal(99, manifest.TargetBuildId);
    }

    [Fact]
    public void Returns_null_when_appid_is_missing()
    {
        const string text = """
            "AppState"
            {
            	"name"		"Broken"
            }
            """;
        Assert.Null(SteamAppManifest.FromVdf(VdfParser.Parse(text), "x.acf", @"C:\Steam", DateTimeOffset.Now));
    }

    [Fact]
    public void Returns_null_when_root_is_not_AppState()
    {
        Assert.Null(SteamAppManifest.FromVdf(VdfParser.Parse("\"other\" { \"appid\" \"1\" }"), "x.acf", @"C:\Steam", DateTimeOffset.Now));
    }
}
