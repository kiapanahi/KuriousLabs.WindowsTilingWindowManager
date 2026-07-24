using System.Diagnostics.CodeAnalysis;
using Bastion.Core;
using Microsoft.Extensions.Hosting;

namespace Bastion.Win32;

/// <summary>
/// Seeds <see cref="Reconciler"/>'s single default workspace (DESIGN.md §12 v0.1: "single workspace
/// per monitor") with the primary monitor's work area before reconciliation begins.
/// </summary>
/// <remarks>
/// <para>
/// <b>A genuine gap a Codex review finding on this PR caught.</b> <see cref="Reconciler"/> is
/// constructed with <see cref="DesiredState.Empty"/> — no workspace registered — and
/// <c>Reconciler</c>'s own private desired-window-set sync only auto-admits an observed window into
/// <see cref="WorkspaceKey.Default"/> if that workspace <em>already exists</em>
/// (<see cref="Reconciler.SetWorkspace"/>'s own remarks: "v0.1 has no monitor topology service
/// (GitHub issue #16) to discover this automatically, so callers (the eventual composition root,
/// GitHub issue #10; tests here) supply it directly"). Without this service, nothing in production
/// ever calls <see cref="Reconciler.SetWorkspace"/> at all: every convergence pass would solve zero
/// workspaces and produce a permanently empty plan, leaving the entire wired pipeline —
/// <c>PlacementExecutor</c> included — inert despite compiling, resolving, and running cleanly.
/// </para>
/// <para>
/// <b>A plain <see cref="IHostedService"/>, not a <see cref="BackgroundService"/>.</b>
/// <see cref="StartAsync"/> performs one synchronous <see cref="Reconciler.SetWorkspace"/> call and
/// returns — there is no loop to run and nothing to await. Registered before every event-producing
/// pump (<c>BastionEventPipelineServiceCollectionExtensions</c>' own remarks) so the default
/// workspace exists before anything could possibly trigger a premature convergence pass — hosted
/// services start sequentially in registration order (docs/engineering/daemon-architecture.md §2),
/// so this one completing first is a structural guarantee, not a timing assumption.
/// </para>
/// <para>
/// <b>One-time seed, not re-evaluated on monitor/work-area changes.</b> v0.1 has no monitor
/// topology service (GitHub issue #16, DESIGN.md §8) to react to <c>WM_DISPLAYCHANGE</c>/taskbar
/// resize — that limitation exists regardless of how the initial value is seeded, and reacting to
/// those changes is squarely issue #16's job, not this stub's.
/// </para>
/// </remarks>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "Registered via AddHostedService<DefaultWorkspaceSeedingService>() in " +
        "Bastion.Daemon's composition root (GitHub issue #10). Same documented CA1812 " +
        "false-positive shape as JournalRestoreOnShutdownService/ReconcilerLoopService.")]
internal sealed class DefaultWorkspaceSeedingService(Reconciler reconciler, IPlacementSystem placementSystem) : IHostedService
{
    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        reconciler.SetWorkspace(WorkspaceKey.Default, placementSystem.ReadPrimaryWorkArea());
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
