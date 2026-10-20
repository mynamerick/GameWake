using GameWake.Providers.Common;
using Microsoft.Win32;

namespace GameWake.Providers.Steam;

public static class SteamLocator
{
    public const string SteamKey = @"Software\Valve\Steam";
    public const string ActiveProcessKey = @"Software\Valve\Steam\ActiveProcess";
    public const string AppsKey = @"Software\Valve\Steam\Apps";

    public static string? Locate(IRegistryReader registry)
    {
        var candidates = new[]
        {
            registry.GetString(RegistryHive.CurrentUser, SteamKey, "SteamPath"),
            registry.GetString(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            registry.GetString(RegistryHive.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var path = SteamLibraryReader.NormalizePath(candidate);
            if (path is not null && Directory.Exists(Path.Combine(path, "steamapps")))
            {
                return path;
            }
        }

        return null;
    }
}

public static class SteamLibraryReader
{
    /// <summary>
    /// Returns every library root (always including the Steam folder itself). Supports the current
    /// format (<c>"0" { "path" "D:\\Games" ... }</c>) and the pre-2021 flat format (<c>"1" "D:\\Games"</c>).
    /// </summary>
    public static IReadOnlyList<string> Read(string steamRoot, out string? problem)
    {
        problem = null;
        var libraries = new List<string>();
        Add(libraries, steamRoot);

        var file = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(file))
        {
            problem = "libraryfolders.vdf not found; only the main Steam library is checked";
            return libraries;
        }

        VdfNode document;
        try
        {
            document = VdfParser.Parse(SharedFile.ReadAllText(file));
        }
        catch (Exception ex) when (ex is VdfParseException or IOException or UnauthorizedAccessException)
        {
            problem = $"libraryfolders.vdf could not be read ({ex.Message}); only the main Steam library is checked";
            return libraries;
        }

        var root = document["libraryfolders"];
        if (root is null || !root.IsObject)
        {
            problem = "libraryfolders.vdf has an unexpected layout; only the main Steam library is checked";
            return libraries;
        }

        foreach (var entry in root.Children)
        {
            if (!entry.Key.All(char.IsAsciiDigit))
            {
                continue;
            }

            Add(libraries, entry.IsObject ? entry.GetString("path") : entry.Value);
        }

        return libraries;
    }

    public static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim().Replace('/', '\\')));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static void Add(List<string> libraries, string? path)
    {
        var normalized = NormalizePath(path);
        if (normalized is not null && !libraries.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            libraries.Add(normalized);
        }
    }
}

/// <summary>Keeps parsed manifests and re-reads a file only when its size or timestamp changes.</summary>
public sealed class SteamManifestCache
{
    private readonly Dictionary<string, CachedManifest> _cache = new(StringComparer.OrdinalIgnoreCase);

    public int CachedFileCount => _cache.Count;

    public IReadOnlyDictionary<uint, SteamAppManifest> Refresh(IEnumerable<string> libraries, ICollection<string> problems)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in libraries)
        {
            var steamapps = Path.Combine(library, "steamapps");
            IEnumerable<string> files;
            try
            {
                if (!Directory.Exists(steamapps))
                {
                    problems.Add($"Library folder not found: {steamapps}");
                    continue;
                }

                files = Directory.EnumerateFiles(steamapps, "appmanifest_*.acf").ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"Could not list {steamapps}: {ex.Message}");
                continue;
            }

            foreach (var file in files)
            {
                seen.Add(file);
                RefreshFile(file, library, problems);
            }
        }

        foreach (var stale in _cache.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _cache.Remove(stale);
        }

        var result = new Dictionary<uint, SteamAppManifest>();
        foreach (var manifest in _cache.Values.Select(c => c.Manifest).OfType<SteamAppManifest>())
        {
            if (!result.TryGetValue(manifest.AppId, out var existing) || manifest.LastWriteTime > existing.LastWriteTime)
            {
                result[manifest.AppId] = manifest;
            }
        }

        return result;
    }

    private void RefreshFile(string file, string library, ICollection<string> problems)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(file);
            if (!info.Exists)
            {
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (_cache.TryGetValue(file, out var cached) && cached.LastWriteUtc == info.LastWriteTimeUtc && cached.Length == info.Length)
        {
            if (cached.Problem is not null)
            {
                problems.Add(cached.Problem);
            }

            return;
        }

        try
        {
            var text = SharedFile.ReadAllText(file);
            var manifest = SteamAppManifest.FromVdf(VdfParser.Parse(text), file, library, info.LastWriteTime);
            var problem = manifest is null ? $"{Path.GetFileName(file)} is not a valid app manifest" : null;
            _cache[file] = new CachedManifest(info.LastWriteTimeUtc, info.Length, manifest, problem);
            if (problem is not null)
            {
                problems.Add(problem);
            }
        }
        catch (VdfParseException ex)
        {
            var problem = $"{Path.GetFileName(file)} is corrupted: {ex.Message}";
            _cache[file] = new CachedManifest(info.LastWriteTimeUtc, info.Length, null, problem);
            problems.Add(problem);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Steam may be replacing the file right now; keep the previous parse and retry next poll.
            problems.Add($"{Path.GetFileName(file)} could not be read this time: {ex.Message}");
        }
    }

    private sealed record CachedManifest(DateTime LastWriteUtc, long Length, SteamAppManifest? Manifest, string? Problem);
}
