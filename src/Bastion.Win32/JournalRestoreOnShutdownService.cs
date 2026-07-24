using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Hosting;

namespace Bastion.Win32;

/// <summary>
/// Clean-daemon-shutdown half of GitHub issue #8's acceptance criteria: "Clean daemon shutdown
/// restores all currently-journaled windows before exiting" (DESIGN.md §3.7). A plain
/// <see cref="IHostedService"/> whose <see cref="StopAsync"/> force-restores the journal via the
/// same <see cref="HwndJournalRestorer"/> <c>bastionc restore-windows</c> uses.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope call (explicit, per this issue's own design guidance): not registered anywhere yet.</b>
/// Wiring this into <c>Bastion.Daemon</c>'s actual composition root — <c>Program.cs</c>'s
/// <c>Host.CreateApplicationBuilder</c> call, and <c>BastiondService.cs</c>'s eventual real
/// implementation — is GitHub issue #10, explicitly separate and not yet done as of this change
/// (<c>BastiondService.cs</c> is still the documented placeholder that only proves the host
/// composition builds and runs). This type is built to the same "real component, registered later"
/// shape every other not-yet-composition-rooted <see cref="IHostedService"/> in this assembly
/// already uses (<c>WinEventPumpService</c>, <c>Coalescer</c>) — issue #10 registers it via
/// <c>builder.Services.AddHostedService&lt;JournalRestoreOnShutdownService&gt;()</c> (or an
/// equivalent DI registration) alongside those.
/// </para>
/// <para>
/// <b>Ordering note for issue #10.</b> <see cref="IHostedService.StopAsync"/> is called on
/// registered hosted services in <em>reverse</em> registration order by the Generic Host, so
/// registering this service <em>first</em> (before the WinEvent/input pumps and the IPC server)
/// makes it the <em>last</em> one stopped — i.e. the journal restore runs only after every other
/// pump has already stopped producing new hide/move-away operations, avoiding a restore racing a
/// still-in-flight hide. This is a note for issue #10's actual registration order, not something
/// enforceable from within this type alone.
/// </para>
/// <para>
/// A failed restore attempt (e.g. an unreadable/corrupt journal) is logged
/// (<see cref="JournalDiagnostics.LogRestoreOnShutdownFailed"/>) and swallowed rather than allowed
/// to propagate — a diagnostic-logging concern must never block the host from actually shutting
/// down (DESIGN.md §1's must-not-strand-a-window principle is about windows, not about the shutdown
/// sequence itself stalling indefinitely).
/// </para>
/// </remarks>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "Constructed by tests via InternalsVisibleTo today, and intended to be " +
        "registered as an IHostedService once Bastion.Daemon's composition root is wired (GitHub " +
        "issue #10) — not yet wired as of this change. Same documented CA1812 false-positive shape " +
        "as WinEventPumpService/Coalescer/PlacementExecutor/BastiondService.")]
internal sealed class JournalRestoreOnShutdownService(HwndJournalRestorer restorer) : IHostedService
{
    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            ImmutableArray<JournalRestoreOutcome> outcomes = await restorer.RestoreAllAsync(cancellationToken).ConfigureAwait(false);
            LogSummary(outcomes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // See this type's remarks: a diagnostic-logging concern must never block shutdown.
            JournalDiagnostics.LogRestoreOnShutdownFailed(ex);
        }
    }

    private static void LogSummary(ImmutableArray<JournalRestoreOutcome> outcomes)
    {
        if (outcomes.IsEmpty)
        {
            return;
        }

        int restored = 0;
        int skipped = 0;
        int failed = 0;
        foreach (JournalRestoreOutcome outcome in outcomes)
        {
            switch (outcome.Kind)
            {
                case JournalRestoreOutcomeKind.Restored:
                    restored++;
                    break;
                case JournalRestoreOutcomeKind.Failed:
                    failed++;
                    break;
                default:
                    skipped++;
                    break;
            }
        }

        JournalDiagnostics.LogRestoreSummary(restored, skipped, failed);
    }
}
