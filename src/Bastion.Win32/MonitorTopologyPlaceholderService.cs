using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bastion.Win32;

/// <summary>
/// Holds the composition root's required Monitor Topology Service startup-order slot
/// (docs/engineering/daemon-architecture.md §2: "... 3. Monitor topology service ...") without
/// doing any actual topology work — the real service (DESIGN.md §8: <c>EnumDisplayMonitors</c>,
/// <c>StableMonitorId</c>/EDID-keyed persistence, dock/undock migrate-home) is GitHub issue #16,
/// not yet built.
/// </summary>
/// <remarks>
/// A deliberate, explicit stub rather than omitting the slot entirely (GitHub issue #10's own
/// acceptance criteria sanctions either choice) — this way the required four-item startup order is
/// observably complete in the hosted-service list and in logs/<c>bastion doctor</c>-style
/// diagnostics, rather than silently absent. Nothing downstream depends on this doing real work yet:
/// v0.1 has no monitor topology at all (<see cref="WindowSystemAdapter"/>'s own remarks), and the
/// Reconciler's only workspace-assignment policy today is the single hardcoded
/// <see cref="Bastion.Core.WorkspaceKey.Default"/> destination.
/// </remarks>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "Registered via AddSingleton<IHostedService, MonitorTopologyPlaceholderService>() " +
        "in Bastion.Daemon's composition root (GitHub issue #10). Same documented CA1812 " +
        "false-positive shape as JournalRestoreOnShutdownService/WinEventPumpService.")]
internal sealed partial class MonitorTopologyPlaceholderService(ILogger<MonitorTopologyPlaceholderService> logger) : IHostedService
{
    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        LogNotYetImplemented();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Monitor topology service is not yet implemented (GitHub issue #16); multi-monitor topology changes are not tracked.")]
    private partial void LogNotYetImplemented();
}
