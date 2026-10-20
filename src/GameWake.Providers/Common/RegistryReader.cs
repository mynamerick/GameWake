using System.Security;
using Microsoft.Win32;

namespace GameWake.Providers.Common;

/// <summary>Read-only registry access. Missing keys and access-denied both yield null.</summary>
public interface IRegistryReader
{
    object? GetValue(RegistryHive hive, string subKey, string valueName);

    IReadOnlyList<string> GetSubKeyNames(RegistryHive hive, string subKey);
}

public sealed class WindowsRegistryReader : IRegistryReader
{
    public static WindowsRegistryReader Instance { get; } = new();

    public object? GetValue(RegistryHive hive, string subKey, string valueName)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(subKey, writable: false);
            return key?.GetValue(valueName);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public IReadOnlyList<string> GetSubKeyNames(RegistryHive hive, string subKey)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(subKey, writable: false);
            return key?.GetSubKeyNames() ?? [];
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }
}

public static class RegistryReaderExtensions
{
    public static string? GetString(this IRegistryReader registry, RegistryHive hive, string subKey, string valueName) =>
        registry.GetValue(hive, subKey, valueName) as string;

    public static int? GetInt32(this IRegistryReader registry, RegistryHive hive, string subKey, string valueName) =>
        registry.GetValue(hive, subKey, valueName) switch
        {
            int value => value,
            long value when value is >= int.MinValue and <= int.MaxValue => (int)value,
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => null,
        };
}
