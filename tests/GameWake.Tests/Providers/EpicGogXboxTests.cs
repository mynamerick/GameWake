using System.Buffers.Binary;
using System.Text;
using GameWake.Core.Models;
using GameWake.Providers.Epic;
using GameWake.Providers.Gog;
using GameWake.Providers.Xbox;

namespace GameWake.Tests.Providers;

public sealed class EpicGogXboxTests
{
    [Fact]
    public void Epic_manifest_reads_display_name_and_paths()
    {
        const string json = """
            {
              "AppName": "Fortnite",
              "DisplayName": "Fortnite",
              "InstallLocation": "D:/Epic/Fortnite",
              "StagingLocation": "D:/Epic/Fortnite/.egstore/bps",
              "bIsIncompleteInstall": true,
              "InstallSize": 1234
            }
            """;
        var manifest = EpicManifest.Parse(json, "fortnite.item", DateTime.Now);
        Assert.Equal("Fortnite", manifest.DisplayName);
        Assert.True(manifest.IsIncompleteInstall);
        Assert.Equal(1234, manifest.InstallSize);
        Assert.NotNull(manifest.InstallLocation);
    }

    [Fact]
    public void Epic_manifest_requires_app_name()
    {
        Assert.Throws<FormatException>(() => EpicManifest.Parse("{\"DisplayName\":\"X\"}", "x.item", DateTime.Now));
    }

    [Fact]
    public void Epic_manifest_cache_skips_corrupt_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "GameWake-epic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "good.item"), """{"AppName":"A","DisplayName":"Alpha"}""");
            File.WriteAllText(Path.Combine(dir, "bad.item"), "{ nope");
            var problems = new List<string>();
            var manifests = new EpicManifestCache().Refresh(dir, problems);
            Assert.Single(manifests);
            Assert.Contains(problems, p => p.Contains("bad.item", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Epic_locator_extracts_quoted_executable()
    {
        Assert.Equal(@"C:\Epic\EpicGamesLauncher.exe", EpicLocator.ExtractExecutable(@"""C:\Epic\EpicGamesLauncher.exe"" %1"));
        Assert.Null(EpicLocator.ExtractExecutable(null));
    }

    [Fact]
    public void Gog_parses_job_and_target_lines()
    {
        var job = Assert.IsType<GogJobChanged>(GogClientLogParser.ParseLine(
            "2024-03-01 12:34:56 [Information] Job 'ProductInstall (1207658924) id: 3 state: Running' state changed"));
        Assert.Equal("ProductInstall", job.JobType);
        Assert.Equal("1207658924", job.ProductId);
        Assert.Equal(GogJobStatus.Working, GogClientLogParser.ClassifyState(job.StateWord));
        Assert.Equal(ActivityState.Downloading, GogClientLogParser.ClassifyJobType(job.JobType));

        var target = Assert.IsType<GogTargetPath>(GogClientLogParser.ParseLine(
            "Processing update request for product ID: 1207658924, target path: D:\\GOG Games\\X"));
        Assert.Equal(@"D:\GOG Games\X", target.Path);

        Assert.Equal(GogJobStatus.Unrecognized, GogClientLogParser.ClassifyState("Reticulating"));
        Assert.Null(GogClientLogParser.ClassifyJobType("ProductUninstall"));
    }

    [Fact]
    public void Gog_tracker_ignores_pre_session_lines_and_keeps_latest_job()
    {
        var tracker = new GogJobTracker(new DateTime(2026, 10, 20, 16, 0, 0));
        tracker.Apply("2026-10-19 12:00:00 Job 'ProductInstall (1) id: 1 state: Running' state changed", DateTime.Now, false);
        tracker.Apply("2026-10-20 16:01:00 Job 'ProductInstall (1) id: 1 state: Running' state changed", DateTime.Now, false);
        tracker.Apply("2026-10-20 16:02:00 Job 'ProductInstall (1) id: 1 state: Finished' state changed", DateTime.Now, false);
        Assert.True(tracker.IgnoredLines >= 1);
        var job = Assert.Single(tracker.CurrentJobs(new DateTime(2026, 10, 20, 16, 02, 30), TimeSpan.FromMinutes(2)));
        Assert.Equal(GogJobStatus.Finished, job.Status);
    }

    [Fact]
    public void Xbox_game_folder_is_derived_from_content_path()
    {
        var folder = XboxActivityClassifier.GameFolderOf(@"D:\XboxGames\Halo\Content", [@"D:\XboxGames"]);
        Assert.Equal(@"D:\XboxGames\Halo", folder);
        Assert.Equal("Halo", XboxDownloadProvider.ContentBucket(@"Halo\Content\data.bin"));
        Assert.Null(XboxDownloadProvider.ContentBucket(@"Halo\Saves\slot1"));
    }

    [Fact]
    public void Xbox_catalog_event_for_a_game_folder_is_confirmed()
    {
        var now = new DateTimeOffset(2026, 10, 20, 16, 30, 0, TimeSpan.Zero);
        var activity = new PackageActivity(
            "Halo_1.0.0.0_x64__8wekyb3d8bbwe",
            "Halo_8wekyb3d8bbwe",
            "Halo Infinite",
            @"D:\XboxGames\Halo\Content",
            PackageActivityKind.Staging,
            0.4,
            false,
            null,
            now.AddSeconds(-10));
        var op = Assert.Single(XboxActivityClassifier.Classify([activity], [], [@"D:\XboxGames"], new Dictionary<string, DateTimeOffset>(), now));
        Assert.Equal(ActivityState.Downloading, op.State);
        Assert.Equal(DetectionConfidence.Confirmed, op.Confidence);
        Assert.Equal(0.4, op.Progress);
        Assert.Equal("Halo Infinite", op.GameTitle);
    }

    [Fact]
    public void Xbox_store_package_without_game_folder_is_probable()
    {
        var now = DateTimeOffset.Now;
        var activity = new PackageActivity("Store_1.0_x64__8wekyb3d8bbwe", "Store_8wekyb3d8bbwe", "Store App", @"C:\Program Files\WindowsApps\Store", PackageActivityKind.Installing, 0.1, false, null, now);
        var op = Assert.Single(XboxActivityClassifier.Classify([activity], [], [@"D:\XboxGames"], new Dictionary<string, DateTimeOffset>(), now));
        Assert.Equal(DetectionConfidence.Probable, op.Confidence);
        Assert.Contains("not identified as a game", op.StateDetail);
    }

    [Fact]
    public void Xbox_folder_writes_without_catalog_events_are_probable()
    {
        var now = DateTimeOffset.Now;
        var writes = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase)
        {
            [@"D:\XboxGames\Starfield"] = now.AddSeconds(-5),
        };
        var op = Assert.Single(XboxActivityClassifier.Classify([], [], [@"D:\XboxGames"], writes, now));
        Assert.Equal(DetectionConfidence.Probable, op.Confidence);
        Assert.Equal("Starfield", op.GameTitle);
        Assert.Equal(EvidenceKind.Inferred, op.EvidenceKind);
    }

    [Fact]
    public void GamingRoot_parses_rgbx_payload()
    {
        var name = "XboxGames\0";
        var nameBytes = Encoding.Unicode.GetBytes(name);
        var data = new byte[8 + nameBytes.Length];
        data[0] = (byte)'R';
        data[1] = (byte)'G';
        data[2] = (byte)'B';
        data[3] = (byte)'X';
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 1);
        nameBytes.CopyTo(data.AsSpan(8));
        var folders = GamingRoot.Parse(data, @"D:\");
        Assert.Equal(Path.Combine(@"D:\", "XboxGames"), Assert.Single(folders));
    }

    [Fact]
    public void GamingRoot_rejects_bad_header()
    {
        Assert.Throws<FormatException>(() => GamingRoot.Parse("nope"u8, @"D:\"));
    }
}
