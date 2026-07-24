using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Bastion.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bastion.Win32;

/// <summary>
/// Drains <see cref="Reconciler.PlacementPlanReader"/> and hands each plan to
/// <see cref="PlacementExecutor.ApplyAsync"/> — the final hop of the pipeline DESIGN.md §3 diagrams
/// (event ingest -&gt; Coalescer -&gt; Reconciler -&gt; Placement Executor -&gt; Win32), wired by
/// GitHub issue #10's composition root.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hosting shape.</b> A <see cref="BackgroundService"/>: an ordinary
/// <see langword="await foreach"/> channel-drain loop with no message pump or hook registration of
/// its own, matching <see cref="Coalescer"/>/<see cref="ReconcilerIntentPump"/>'s identical reasoning
/// (docs/engineering/daemon-architecture.md §2).
/// </para>
/// <para>
/// <b>No per-plan exception guard, deliberately.</b> <see cref="PlacementExecutor.ApplyAsync"/>
/// already resolves every routine race (a vanished window, a hang, a clamp) into an ordinary
/// <see cref="PlacementOutcome"/> rather than throwing — see its own remarks. A genuine exception
/// escaping it would be an unanticipated bug, and letting it propagate and stop the host (the
/// documented <see cref="BackgroundService"/> default) surfaces that loudly rather than silently
/// leaving placements broken for the rest of the daemon's life with nothing observing it — the same
/// design choice <see cref="ReconcilerLoopService"/> makes for the convergence loop itself, for the
/// identical reason (see that type's own remarks).
/// </para>
/// </remarks>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "Registered via AddHostedService<PlacementExecutionPump>() in Bastion.Daemon's " +
        "composition root (GitHub issue #10). Same documented CA1812 false-positive shape as " +
        "Coalescer/ReconcilerIntentPump/WinEventPumpService.")]
internal sealed partial class PlacementExecutionPump(
    ChannelReader<ImmutableArray<PlacementInstruction>> planReader,
    PlacementExecutor executor,
    ILogger<PlacementExecutionPump> logger) : BackgroundService
{
    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (ImmutableArray<PlacementInstruction> plan in planReader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            ImmutableArray<PlacementOutcome> outcomes = await executor.ApplyAsync(plan, stoppingToken).ConfigureAwait(false);
            LogApplied(plan.Length, outcomes.Length);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Applied a placement plan: {InstructionCount} instruction(s), {OutcomeCount} outcome(s).")]
    private partial void LogApplied(int instructionCount, int outcomeCount);
}
