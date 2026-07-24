using Bastion.Win32;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Bastion.Daemon;

/// <summary>
/// Registers GitHub issue #8's write-ahead HWND journal chain (DESIGN.md §3.7) — the store, the
/// cross-process lock, the writer, the restorer, and the daemon-shutdown restore hook — for
/// GitHub issue #10's composition root.
/// </summary>
/// <remarks>
/// <para>
/// <b>Registered first, deliberately (see <c>Program.cs</c>).</b> <see cref="JournalRestoreOnShutdownService"/>'s
/// own remarks: hosted services stop in <em>reverse</em> registration order, so registering it before
/// every pump/IPC service makes it the <em>last</em> one stopped — the journal restore runs only
/// after every other pump has already stopped producing new hide/move-away operations, avoiding a
/// restore racing a still-in-flight hide.
/// </para>
/// <para>
/// <b><paramref name="journalFilePath"/> is optional</b> for the identical reason
/// <c>WindowRulesConfigServiceCollectionExtensions.AddWindowRulesConfiguration</c>'s own
/// <c>paths</c> parameter is: so <c>Bastion.Daemon.Tests</c> can call this exact production
/// registration path pointed at a temporary file instead of the real
/// <c>%LOCALAPPDATA%\Bastion\hwnd-journal.json</c>.
/// </para>
/// <para>
/// <b><see cref="HwndJournalWriter"/> has no production caller registered by this issue.</b> Its own
/// remarks scope its first live caller to GitHub issue #15's future Workspace Manager (Bastion-owned
/// workspaces, explicitly out of scope here) — it is registered anyway, alongside every other real,
/// tested component in this chain, so issue #15 only has to resolve it rather than also wire it.
/// </para>
/// </remarks>
internal static class BastionHwndJournalServiceCollectionExtensions
{
    /// <summary>Registers every service the HWND journal chain needs. See this type's remarks for the registration-order rationale.</summary>
    public static IServiceCollection AddBastionHwndJournal(this IServiceCollection services, string? journalFilePath = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IWindowProcessIdReader, WindowProcessIdReader>();
        services.TryAddSingleton<IHwndJournalLock, HwndJournalLock>();
        services.TryAddSingleton<IJournalPlacementSystem, JournalPlacementSystemAdapter>();
        services.TryAddSingleton<IHwndJournalStore>(
            _ => new HwndJournalStore(journalFilePath ?? HwndJournalStore.DefaultJournalFilePath));

        services.TryAddSingleton<HwndJournalWriter>();
        services.TryAddSingleton<HwndJournalRestorer>();

        // JournalRestoreOnShutdownService's constructor (HwndJournalRestorer restorer) is fully
        // DI-resolvable via the registration above, with no raw-value parameters -- the standard
        // AddHostedService<T>() sugar applies directly; nothing downstream needs this service's own
        // concrete type, only its place in the IHostedService collection.
        services.AddHostedService<JournalRestoreOnShutdownService>();

        return services;
    }
}
