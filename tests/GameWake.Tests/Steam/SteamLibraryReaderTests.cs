using GameWake.Providers.Steam;

namespace GameWake.Tests.Steam;

public sealed class SteamLibraryReaderTests
{
    [Fact]
    public void Reads_multiple_libraries_and_always_includes_steam_root()
    {
        var root = CreateSteamRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "steamapps", "libraryfolders.vdf"), """
                "libraryfolders"
                {
                	"0"
                	{
                		"path"		"C:\\Program Files (x86)\\Steam"
                	}
                	"1"
                	{
                		"path"		"D:\\Games"
                	}
                }
                """);

            var libraries = SteamLibraryReader.Read(root, out var problem);
            Assert.Null(problem);
            Assert.Contains(root, libraries, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(@"D:\Games", libraries, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Missing_libraryfolders_returns_only_steam_root()
    {
        var root = CreateSteamRoot();
        try
        {
            var libraries = SteamLibraryReader.Read(root, out var problem);
            Assert.NotNull(problem);
            Assert.Single(libraries);
            Assert.Equal(root, libraries[0]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Corrupt_libraryfolders_does_not_throw()
    {
        var root = CreateSteamRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "steamapps", "libraryfolders.vdf"), "\"libraryfolders\" { \"0\" {");
            var libraries = SteamLibraryReader.Read(root, out var problem);
            Assert.Contains("could not be read", problem, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(root, Assert.Single(libraries));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Manifest_cache_skips_corrupt_acf_and_keeps_valid_ones()
    {
        var root = CreateSteamRoot();
        try
        {
            var steamapps = Path.Combine(root, "steamapps");
            File.WriteAllText(Path.Combine(steamapps, "appmanifest_570.acf"), """
                "AppState"
                {
                	"appid"		"570"
                	"name"		"Dota 2"
                	"StateFlags"		"4"
                }
                """);
            File.WriteAllText(Path.Combine(steamapps, "appmanifest_1.acf"), "\"AppState\" { \"name\" \"broken");

            var cache = new SteamManifestCache();
            var problems = new List<string>();
            var manifests = cache.Refresh([root], problems);
            Assert.True(manifests.ContainsKey(570));
            Assert.Equal("Dota 2", manifests[570].Name);
            Assert.Contains(problems, p => p.Contains("appmanifest_1.acf", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Manifest_cache_reports_missing_library_folder()
    {
        var cache = new SteamManifestCache();
        var problems = new List<string>();
        var manifests = cache.Refresh([Path.Combine(Path.GetTempPath(), "GameWake-missing-lib-" + Guid.NewGuid().ToString("N"))], problems);
        Assert.Empty(manifests);
        Assert.Contains(problems, p => p.Contains("not found", StringComparison.OrdinalIgnoreCase));
    }

    private static string CreateSteamRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "GameWake-steam-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "steamapps"));
        return root;
    }
}
