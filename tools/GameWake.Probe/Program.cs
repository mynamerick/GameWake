// Development tool: polls the real launcher providers and prints exactly what each one detects.
// Usage: dotnet run --project tools/GameWake.Probe -- [steam|epic|gog|xbox ...] [--count N] [--interval SECONDS]
using System.Globalization;
using GameWake.Core.Infrastructure;
using GameWake.Core.Models;
using GameWake.Core.Providers;
using GameWake.Providers;
using Microsoft.Extensions.Logging.Abstractions;

var kinds = new List<LauncherKind>();
var count = 1;
var interval = TimeSpan.FromSeconds(5);
for (var i = 0; i < args.Length; i++)
{
    switch (args[i].ToLowerInvariant())
    {
        case "--count":
            count = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--interval":
            interval = TimeSpan.FromSeconds(double.Parse(args[++i], CultureInfo.InvariantCulture));
            break;
        case var name when Enum.TryParse<LauncherKind>(name, ignoreCase: true, out var kind):
            kinds.Add(kind);
            break;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            return 2;
    }
}

if (kinds.Count == 0)
{
    kinds.AddRange(LauncherKindExtensions.SupportedLaunchers);
}

var factory = new LauncherProviderFactory(SystemClock.Instance, NullLoggerFactory.Instance);
var providers = kinds.Select(factory.Create).ToList();
try
{
    for (var round = 1; round <= count; round++)
    {
        if (round > 1)
        {
            await Task.Delay(interval);
        }

        Console.WriteLine($"=== Poll {round} at {DateTime.Now:HH:mm:ss} ===");
        foreach (var provider in providers)
        {
            var started = DateTime.UtcNow;
            var snapshot = await provider.PollAsync(CancellationToken.None);
            Print(snapshot, DateTime.UtcNow - started);
        }
    }
}
finally
{
    foreach (var provider in providers)
    {
        provider.Dispose();
    }
}

return 0;

static void Print(LauncherSnapshot s, TimeSpan elapsed)
{
    Console.WriteLine($"[{s.DisplayName}] installed={s.IsInstalled} running={s.IsRunning} summary={s.SummaryState}/{s.SummaryConfidence} ({elapsed.TotalMilliseconds:0} ms)");
    Console.WriteLine($"  status: {s.StatusDetail}");
    if (s.Error is not null)
    {
        Console.WriteLine($"  ERROR: {s.Error}");
    }

    if (s.TotalBytesPerSecond is { } total)
    {
        Console.WriteLine($"  launcher speed: {total / 1_000_000:0.00} MB/s ({s.TotalSpeedNote})");
    }

    foreach (var op in s.Operations)
    {
        Console.WriteLine($"  - {op.Key} '{op.DisplayTitle}': {op.State} / {op.Confidence} ({op.EvidenceKind}) {op.StateDetail}");
        if (op.TitleNote is not null)
        {
            Console.WriteLine($"      title: {op.TitleNote}");
        }

        var progress = op.Progress is { } p ? $"{p:P1}" : "unavailable";
        var size = op.TotalBytes is { } b ? $"{b / 1_000_000_000.0:0.00} GB" : "unknown";
        var speed = op.BytesPerSecond is { } bps ? $"{bps / 1_000_000:0.00} MB/s ({op.SpeedNote})" : "n/a";
        Console.WriteLine($"      progress: {progress} ({op.ProgressNote}); size: {size}; speed: {speed}");
        foreach (var evidence in op.Evidence)
        {
            Console.WriteLine($"      evidence: {evidence}");
        }
    }

    foreach (var limitation in s.Limitations)
    {
        Console.WriteLine($"  limitation: {limitation}");
    }

    foreach (var line in s.Diagnostics)
    {
        Console.WriteLine($"  diag: {line}");
    }
}
