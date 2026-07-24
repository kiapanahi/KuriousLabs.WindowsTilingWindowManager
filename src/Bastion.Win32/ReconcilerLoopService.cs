using System.Diagnostics.CodeAnalysis;
using Bastion.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bastion.Win32;

/// <summary>
/// Hosts <see cref="Reconciler.RunAsync"/> — DESIGN.md §3.4's heartbeat + wake-driven convergence
/// loop — as its own hosted service (GitHub issue #10's composition-root wiring).
/// </summary>
/// <remarks>
/// <para>
/// <b>Hosting shape.</b> A <see cref="BackgroundService"/>, not a raw <see cref="IHostedService"/> +
/// dedicated <see cref="Thread"/> — <see cref="Reconciler.RunAsync"/> is an ordinary
/// <see langword="await"/>-based loop (a heartbeat <see cref="PeriodicTimer"/> racing a channel wait,
/// per <see cref="Reconciler"/>'s own remarks) with no <c>GetMessage</c> loop or hook registration of
/// its own, matching <see cref="Coalescer"/>/<see cref="ReconcilerIntentPump"/>'s identical reasoning
/// (docs/engineering/daemon-architecture.md §2).
/// </para>
/// <para>
/// <b>No additional exception handling here.</b> <see cref="Reconciler.RunAsync"/> already catches
/// and swallows its own expected shutdown <see cref="OperationCanceledException"/> internally (see
/// its own remarks) and returns normally rather than rethrowing — so this method needs no matching
/// <see langword="catch"/> for that case. A genuinely unexpected exception from a convergence pass
/// (a real bug, not a routine race — those are already handled as ordinary outcomes inside the
/// pipeline) is deliberately left to propagate and stop the host, the documented
/// <see cref="BackgroundService"/>/Generic Host default: DESIGN.md §3.4's "must never strand a
/// window" posture is a reason to <em>harden</em> the convergence pass itself against that class of
/// bug (GitHub issue #30 tracks exactly this), not a reason to swallow it silently here and leave
/// placements broken with no signal anything is wrong.
/// </para>
/// </remarks>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "Registered via AddHostedService<ReconcilerLoopService>() in Bastion.Daemon's " +
        "composition root (GitHub issue #10). Same documented CA1812 false-positive shape as " +
        "Coalescer/ReconcilerIntentPump/WinEventPumpService.")]
internal sealed partial class ReconcilerLoopService(Reconciler reconciler, ILogger<ReconcilerLoopService> logger) : BackgroundService
{
    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted();
        await reconciler.RunAsync(stoppingToken).ConfigureAwait(false);
        LogStopped();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconciler convergence loop started (heartbeat + wake-driven).")]
    private partial void LogStarted();

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconciler convergence loop stopped.")]
    private partial void LogStopped();
}
