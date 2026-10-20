using System.Text.Json;
using GameWake.Core.Infrastructure;
using Microsoft.Extensions.Logging;

namespace GameWake.Core.Settings;

public sealed class SettingsStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _path;
    private readonly ILogger<SettingsStore> _logger;
    private readonly object _gate = new();
    private AppSettings _current = new();

    public SettingsStore(string path, ILogger<SettingsStore> logger)
    {
        _path = path;
        _logger = logger;
    }

    public event EventHandler<AppSettings>? Changed;

    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public string FilePath => _path;

    /// <summary>Loads settings; a missing or corrupted file yields defaults and the bad file is kept for inspection.</summary>
    public AppSettings Load()
    {
        AppSettings loaded;
        try
        {
            if (!File.Exists(_path))
            {
                _logger.LogInformation("No settings file at {Path}; using defaults", _path);
                loaded = new AppSettings();
            }
            else
            {
                var json = File.ReadAllText(_path);
                loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning(ex, "Settings file {Path} could not be read; using defaults", _path);
            PreserveCorruptFile();
            loaded = new AppSettings();
        }

        loaded = loaded.Normalize();
        lock (_gate)
        {
            _current = loaded;
        }

        return loaded;
    }

    public void Save(AppSettings settings)
    {
        settings = settings.Normalize();
        lock (_gate)
        {
            _current = settings;
        }

        try
        {
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not save settings to {Path}", _path);
        }

        Changed?.Invoke(this, settings);
    }

    public AppSettings Reset()
    {
        var defaults = new AppSettings();
        Save(defaults);
        return defaults;
    }

    private void PreserveCorruptFile()
    {
        try
        {
            if (File.Exists(_path))
            {
                var backup = $"{_path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
                File.Copy(_path, backup, overwrite: true);
                _logger.LogWarning("Kept unreadable settings file as {Backup}", backup);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not back up unreadable settings file");
        }
    }
}
