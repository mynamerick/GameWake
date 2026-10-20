using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using GameWake.Core.Models;
using GameWake.Core.Power;
using GameWake.Core.Settings;

namespace GameWake.Core.Diagnostics;

/// <summary>
/// Builds a plain-text report for troubleshooting. It contains detection evidence, settings and
/// recent log lines; it never contains account names, credentials or launcher tokens.
/// </summary>
public static class DiagnosticsReport
{
    public static string Build(
        MonitorSnapshot snapshot,
        AppSettings settings,
        bool powerRequestHeld,
        DateTimeOffset? powerHeldSince,
        IReadOnlyList<string> recentLog,
        string appVersion)
    {
        var builder = new StringBuilder();
        var invariant = CultureInfo.InvariantCulture;

        builder.AppendLine("GameWake diagnostics");
        builder.AppendLine(new string('=', 60));
        builder.AppendLine(invariant, $"Generated:        {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine(invariant, $"GameWake version: {appVersion}");
        builder.AppendLine(invariant, $"OS:               {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        builder.AppendLine(invariant, $".NET:             {RuntimeInformation.FrameworkDescription}");
        builder.AppendLine();

        builder.AppendLine("Power");
        builder.AppendLine(new string('-', 60));
        builder.AppendLine(invariant, $"Monitor status:            {snapshot.Status}");
        builder.AppendLine(invariant, $"GameWake request held:     {powerRequestHeld}{(powerHeldSince is { } since ? $" (since {since:HH:mm:ss})" : string.Empty)}");
        var system = SystemExecutionState.TryRead();
        builder.AppendLine(invariant, $"Windows execution state:   {(system is null ? "unavailable" : $"{system} (all processes combined)")}");
        builder.AppendLine(invariant, $"Grace period ends:         {snapshot.GraceEndsAt?.ToString("HH:mm:ss", invariant) ?? "-"}");
        builder.AppendLine();

        builder.AppendLine("Launchers");
        builder.AppendLine(new string('-', 60));
        foreach (var launcher in snapshot.Launchers)
        {
            builder.AppendLine(invariant, $"[{launcher.DisplayName}] enabled={launcher.IsEnabled} installed={launcher.IsInstalled} running={launcher.IsRunning} state={launcher.SummaryState} confidence={launcher.SummaryConfidence}");
            builder.AppendLine(invariant, $"  checked={launcher.CheckedAt:HH:mm:ss} lastSuccess={launcher.LastSuccessfulDetection?.ToString("HH:mm:ss", invariant) ?? "never"}");
            if (launcher.StatusDetail is not null)
            {
                builder.AppendLine(invariant, $"  status: {launcher.StatusDetail}");
            }

            if (launcher.Error is not null)
            {
                builder.AppendLine(invariant, $"  ERROR: {launcher.Error}");
            }

            foreach (var line in launcher.Diagnostics)
            {
                builder.AppendLine(invariant, $"  {line}");
            }

            foreach (var limitation in launcher.Limitations)
            {
                builder.AppendLine(invariant, $"  limitation: {limitation}");
            }

            foreach (var operation in launcher.Operations)
            {
                builder.AppendLine(invariant, $"  - {operation.Key} \"{operation.DisplayTitle}\" state={operation.State} confidence={operation.Confidence} evidence={operation.EvidenceKind}{(operation.IsSimulated ? " SIMULATED" : string.Empty)}");
                if (operation.StateDetail is not null)
                {
                    builder.AppendLine(invariant, $"      detail: {operation.StateDetail}");
                }

                foreach (var evidence in operation.Evidence)
                {
                    builder.AppendLine(invariant, $"      * {evidence}");
                }
            }

            builder.AppendLine();
        }

        builder.AppendLine("Settings");
        builder.AppendLine(new string('-', 60));
        builder.AppendLine(JsonSerializer.Serialize(settings, SettingsStore.JsonOptions));
        builder.AppendLine();

        builder.AppendLine(invariant, $"Recent log ({recentLog.Count} lines)");
        builder.AppendLine(new string('-', 60));
        foreach (var line in recentLog)
        {
            builder.AppendLine(line);
        }

        return builder.ToString();
    }
}
