using GameWake.Core.Infrastructure;
using GameWake.Core.Models;
using GameWake.Core.Providers;
using GameWake.Providers.Common;
using GameWake.Providers.Epic;
using GameWake.Providers.Gog;
using GameWake.Providers.Simulation;
using GameWake.Providers.Steam;
using GameWake.Providers.Xbox;
using Microsoft.Extensions.Logging;

namespace GameWake.Providers;

public sealed class LauncherProviderFactory(
    IClock clock,
    ILoggerFactory loggerFactory,
    IProcessInspector? processes = null,
    IRegistryReader? registry = null) : ILauncherProviderFactory
{
    private readonly IProcessInspector _processes = processes ?? new ProcessInspector();
    private readonly IRegistryReader _registry = registry ?? WindowsRegistryReader.Instance;

    public ILauncherDownloadProvider Create(LauncherKind kind) => kind switch
    {
        LauncherKind.Steam => new SteamDownloadProvider(clock, loggerFactory.CreateLogger<SteamDownloadProvider>(), _registry, _processes),
        LauncherKind.Epic => new EpicDownloadProvider(clock, loggerFactory.CreateLogger<EpicDownloadProvider>(), _registry, _processes),
        LauncherKind.Gog => new GogDownloadProvider(clock, loggerFactory.CreateLogger<GogDownloadProvider>(), _registry, _processes),
        LauncherKind.Xbox => new XboxDownloadProvider(clock, loggerFactory.CreateLogger<XboxDownloadProvider>(), _processes),
        LauncherKind.Simulation => new SimulationProvider(clock, loggerFactory.CreateLogger<SimulationProvider>()),
        _ => throw new NotSupportedException($"No provider for {kind}"),
    };
}
