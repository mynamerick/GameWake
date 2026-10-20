using System.Text.Json;

namespace GameWake.Providers.Epic;

/// <summary>The fields GameWake reads from an Epic <c>*.item</c> manifest (JSON).</summary>
public sealed record EpicManifest
{
    public required string ManifestPath { get; init; }

    public required string AppName { get; init; }

    public string? DisplayName { get; init; }

    public string? InstallLocation { get; init; }

    public string? StagingLocation { get; init; }

    public bool IsIncompleteInstall { get; init; }

    public long? InstallSize { get; init; }

    public string? CatalogItemId { get; init; }

    public DateTime LastWriteTime { get; init; }

    public static EpicManifest Parse(string json, string manifestPath, DateTime lastWriteTime)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = 32,
        });

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("the manifest is not a JSON object");
        }

        var appName = GetString(root, "AppName") ?? GetString(root, "MainGameAppName");
        if (string.IsNullOrWhiteSpace(appName))
        {
            throw new FormatException("the manifest has no AppName");
        }

        return new EpicManifest
        {
            ManifestPath = manifestPath,
            AppName = appName,
            DisplayName = NullIfBlank(GetString(root, "DisplayName")),
            InstallLocation = NormalizePath(GetString(root, "InstallLocation")),
            StagingLocation = NormalizePath(GetString(root, "StagingLocation")),
            IsIncompleteInstall = root.TryGetProperty("bIsIncompleteInstall", out var incomplete) && incomplete.ValueKind == JsonValueKind.True,
            InstallSize = root.TryGetProperty("InstallSize", out var size) && size.ValueKind == JsonValueKind.Number && size.TryGetInt64(out var bytes) && bytes > 0
                ? bytes
                : null,
            CatalogItemId = NullIfBlank(GetString(root, "CatalogItemId")),
            LastWriteTime = lastWriteTime,
        };
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizePath(string? path)
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
}

/// <summary>Parses <c>*.item</c> files in a folder, re-reading a file only when it changes.</summary>
public sealed class EpicManifestCache
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<EpicManifest> Refresh(string folder, ICollection<string> problems)
    {
        List<string> files;
        try
        {
            if (!Directory.Exists(folder))
            {
                _entries.Clear();
                return [];
            }

            files = Directory.EnumerateFiles(folder, "*.item", SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"Could not list {folder}: {ex.Message}");
            return _entries.Values.Select(e => e.Manifest).OfType<EpicManifest>().ToList();
        }

        foreach (var stale in _entries.Keys.Except(files, StringComparer.OrdinalIgnoreCase).ToList())
        {
            _entries.Remove(stale);
        }

        foreach (var file in files)
        {
            RefreshFile(file, problems);
        }

        return _entries.Values.Select(e => e.Manifest).OfType<EpicManifest>().ToList();
    }

    private void RefreshFile(string file, ICollection<string> problems)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(file);
            if (!info.Exists)
            {
                _entries.Remove(file);
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (_entries.TryGetValue(file, out var cached) && cached.LastWriteUtc == info.LastWriteTimeUtc && cached.Length == info.Length)
        {
            if (cached.Problem is not null)
            {
                problems.Add(cached.Problem);
            }

            return;
        }

        try
        {
            var manifest = EpicManifest.Parse(Common.SharedFile.ReadAllText(file, 1024 * 1024), file, info.LastWriteTime);
            _entries[file] = new Entry(info.LastWriteTimeUtc, info.Length, manifest, null);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            var problem = $"{Path.GetFileName(file)} could not be parsed: {ex.Message}";
            _entries[file] = new Entry(info.LastWriteTimeUtc, info.Length, null, problem);
            problems.Add(problem);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{Path.GetFileName(file)} could not be read this time: {ex.Message}");
        }
    }

    private sealed record Entry(DateTime LastWriteUtc, long Length, EpicManifest? Manifest, string? Problem);
}
