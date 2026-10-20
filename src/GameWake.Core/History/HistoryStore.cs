using System.Text.Json;
using System.Text.Json.Serialization;
using GameWake.Core.Infrastructure;
using Microsoft.Extensions.Logging;

namespace GameWake.Core.History;

public sealed class HistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly ILogger<HistoryStore> _logger;

    public HistoryStore(string path, ILogger<HistoryStore> logger)
    {
        _path = path;
        _logger = logger;
    }

    public IReadOnlyList<ActivityHistoryEntry> Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            var entries = JsonSerializer.Deserialize<List<ActivityHistoryEntry>>(File.ReadAllText(_path), JsonOptions) ?? [];
            return entries.Where(e => e.StartedAt != default).ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning(ex, "Activity history at {Path} could not be read; starting with an empty history", _path);
            try
            {
                File.Copy(_path, $"{_path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
            }
            catch (Exception copyEx) when (copyEx is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(copyEx, "Could not back up unreadable history file");
            }

            return [];
        }
    }

    public void Save(IEnumerable<ActivityHistoryEntry> entries)
    {
        try
        {
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(entries.Where(e => !e.IsSimulated).ToList(), JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not save activity history to {Path}", _path);
        }
    }
}
