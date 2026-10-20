using GameWake.Core.Infrastructure;
using GameWake.Core.Models;
using Microsoft.Extensions.Logging;

namespace GameWake.Core.Providers;

/// <summary>
/// Shared error handling and bookkeeping: exceptions become an error snapshot instead of
/// escaping, and the last successful detection time is tracked per provider.
/// </summary>
public abstract class LauncherProviderBase : ILauncherDownloadProvider
{
    private DateTimeOffset? _lastSuccess;
    private string? _lastLoggedError;

    protected LauncherProviderBase(IClock clock, ILogger logger)
    {
        Clock = clock;
        Logger = logger;
    }

    public abstract LauncherKind Launcher { get; }

    protected IClock Clock { get; }

    protected ILogger Logger { get; }

    public async ValueTask<LauncherSnapshot> PollAsync(CancellationToken cancellationToken)
    {
        var now = Clock.Now;
        try
        {
            var snapshot = await PollCoreAsync(now, cancellationToken).ConfigureAwait(false);
            if (snapshot.Error is null)
            {
                _lastSuccess = now;
                if (_lastLoggedError is not null)
                {
                    Logger.LogInformation("{Launcher} detection recovered", Launcher);
                    _lastLoggedError = null;
                }
            }

            return snapshot with { LastSuccessfulDetection = _lastSuccess };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_lastLoggedError != ex.Message)
            {
                Logger.LogError(ex, "{Launcher} detection failed", Launcher);
                _lastLoggedError = ex.Message;
            }

            return new LauncherSnapshot
            {
                Launcher = Launcher,
                CheckedAt = now,
                IsInstalled = true,
                Error = $"Detection failed: {ex.Message}",
                LastSuccessfulDetection = _lastSuccess,
            };
        }
    }

    protected abstract ValueTask<LauncherSnapshot> PollCoreAsync(DateTimeOffset now, CancellationToken cancellationToken);

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
    }
}
