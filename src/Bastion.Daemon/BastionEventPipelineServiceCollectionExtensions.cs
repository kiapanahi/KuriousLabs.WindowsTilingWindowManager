using Bastion.Core;
using Bastion.Layout;
using Bastion.Win32;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Bastion.Daemon;

/// <summary>
/// Registers the full event-ingest-to-placement pipeline DESIGN.md §3 diagrams — WinEvent pump -&gt;
/// Coalescer -&gt; Reconciler -&gt; Placement Executor -&gt; Win32 — for GitHub issue #10's
/// composition root: the Window Registry's identity-resolution chain, the <see cref="IWindowSystem"/>/
/// <see cref="IPlacementSystem"/> Win32 adapters, <see cref="Reconciler"/> and
/// <see cref="PlacementExecutor"/> themselves, and every hosted service that drives them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope call: this is the composition root's expansion of
/// docs/engineering/daemon-architecture.md §2's required-order item 1 ("event ingest pump"), not a
/// competing top-level item.</b> That doc names four slots (event ingest pump, input pump, monitor
/// topology, IPC server); <c>BastiondService</c>'s own placeholder doc comment additionally names
/// the Coalescer, Reconciler, and Placement Executor as things it needs to "become or hand off to,"
/// and no other GitHub issue owns wiring them into a running daemon. This method therefore registers
/// the WinEvent pump's own full downstream consumer chain — Coalescer, <see cref="ReconcilerIntentPump"/>,
/// <see cref="ReconcilerLoopService"/>, <see cref="PlacementExecutionPump"/> — contiguously with it,
/// all still ordered strictly <em>before</em> the input pump (<c>Program.cs</c> calls
/// <see cref="AddBastionInputPipeline"/> afterward), preserving the documented relative order of the
/// four named items while giving the event-ingest slot its complete, currently-buildable pipeline
/// rather than leaving the Reconciler/Executor genuinely inert.
/// </para>
/// <para>
/// <b>Dual concrete + <see cref="IHostedService"/> registration, only where something downstream
/// needs the producer's own concrete type.</b> <see cref="WinEventPumpService"/> and
/// <see cref="Coalescer"/> each need a manual factory anyway (a raw <see cref="TimeSpan"/> pair for
/// <see cref="Coalescer"/>'s constructor; reading <c>.IngestReader</c>/<c>.IntentReader</c> off the
/// producer for the next stage), so each is registered once as its own concrete singleton and once
/// as <see cref="IHostedService"/> via a factory that resolves that same instance — never two
/// independently-constructed instances of either. This is the identical pattern
/// <c>WindowRulesConfigServiceCollectionExtensions</c> already establishes for
/// <c>PublishedWindowRulesConfig</c>/<c>IPublishedWindowRulesConfig</c> (see its own remarks). Every
/// other hosted service registered here (<see cref="ReconcilerIntentPump"/>,
/// <see cref="ReconcilerLoopService"/>, <see cref="PlacementExecutionPump"/>) needs no such
/// dual-registration: nothing else resolves their concrete type, so the standard
/// <see cref="ServiceCollectionHostedServiceExtensions.AddHostedService{THostedService}"/> sugar
/// applies directly once the plain services their constructors need (a
/// <see cref="System.Threading.Channels.ChannelReader{T}"/> registered as its own service, pulled
/// from whichever producer exposes it) are registered.
/// </para>
/// <para>
/// <b>Out of scope, deliberately (see the GitHub issue #10 PR description for the full
/// reasoning).</b> Window-rules-driven float/ignore admission decisions are not wired here —
/// <see cref="Reconciler"/>'s own desired-window-set sync auto-admits every eligible window into
/// <see cref="WorkspaceKey.Default"/> unconditionally today; consulting the loaded
/// <c>IPublishedWindowRulesConfig</c> during admission is a distinct, not-yet-filed feature, not
/// composition-root wiring. The startup <c>EnumWindows</c> adoption pass already happens for free —
/// every convergence pass (including the very first one, whenever it fires) enumerates and admits
/// all currently-visible windows via <see cref="IWindowSystem.ReadAllAsync"/> — but journaling each
/// adopted window's <em>pre-management</em> placement at admission time (as opposed to at hide
/// time, which is all <see cref="HwndJournalWriter"/>'s existing contract covers) is a genuinely new
/// integration surface with no existing issue or precise DESIGN.md-level specification, so it is not
/// built here either; flagged as a gap for a future issue.
/// </para>
/// </remarks>
internal static class BastionEventPipelineServiceCollectionExtensions
{
    /// <summary>Registers every service the event-ingest-to-placement pipeline needs. See this type's remarks for the registration-order rationale.</summary>
    public static IServiceCollection AddBastionEventAndReconciliationPipeline(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);

        // Window Registry & identity-resolution chain (DESIGN.md §3.3).
        services.TryAddSingleton<ShellComThread>();
        services.TryAddSingleton<IWindowProcessIdReader, WindowProcessIdReader>();
        services.TryAddSingleton<IProcessAumidReader, ProcessAumidReader>();
        services.TryAddSingleton<IProcessImagePathReader, ProcessImagePathReader>();
        services.TryAddSingleton<IPropertyStoreAumidReader, PropertyStoreAumidReader>();
        services.TryAddSingleton<IUwpAttributionProvider, ApplicationFrameUwpAttributionProvider>();
        services.TryAddSingleton<IWindowIdentityResolver, WindowIdentityResolver>();
        services.TryAddSingleton<IWindowManageabilityInfoReader, WindowManageabilityInfoReader>();
        services.TryAddSingleton(WindowClassBlocklist.Default);
        services.TryAddSingleton<WindowIdMinter>();
        services.TryAddSingleton<WindowRegistry>();

        // Win32-facing seams the Reconciler/Placement Executor pipeline depends on.
        services.TryAddSingleton<ICloakStateReader, DwmCloakStateReader>();
        services.TryAddSingleton<IWindowSystem, WindowSystemAdapter>();
        services.TryAddSingleton<IPlacementSystem, PlacementSystemAdapter>();
        services.TryAddSingleton<IReconcileNowSignal, ReconcileNowSignal>();

        // Bastion.Core: pure reconciliation, fed the one shipped layout engine (DESIGN.md §6, §12 v0.1).
        services.TryAddSingleton<ILayoutEngine, DwindleLayoutEngine>();
        services.TryAddSingleton(ReconcilerOptions.Default);
        services.TryAddSingleton<Reconciler>();

        // Bastion.Win32: placement execution.
        services.TryAddSingleton(PlacementExecutorOptions.Default);
        services.TryAddSingleton<PlacementExecutor>();

        // The pipeline's own hosted services, registered (and therefore started) in DESIGN.md §3's
        // pipeline order: WinEvent pump -> Coalescer -> ReconcilerIntentPump -> ReconcilerLoopService
        // -> PlacementExecutionPump. See this type's own remarks for why this whole block occupies
        // the "event ingest pump" required-order slot rather than competing with it.
        services.TryAddSingleton<WinEventPumpService>();
        services.AddSingleton<IHostedService>(static sp => sp.GetRequiredService<WinEventPumpService>());

        services.TryAddSingleton(static sp => new Coalescer(
            sp.GetRequiredService<WinEventPumpService>().IngestReader,
            sp.GetRequiredService<ICloakStateReader>(),
            sp.GetRequiredService<IReconcileNowSignal>(),
            sp.GetRequiredService<TimeProvider>(),
            Coalescer.DefaultCoalesceWindow,
            Coalescer.DefaultAdmissionGrace));
        services.AddSingleton<IHostedService>(static sp => sp.GetRequiredService<Coalescer>());

        services.TryAddSingleton(static sp => sp.GetRequiredService<Coalescer>().IntentReader);
        services.AddHostedService<ReconcilerIntentPump>();

        services.AddHostedService<ReconcilerLoopService>();

        services.TryAddSingleton(static sp => sp.GetRequiredService<Reconciler>().PlacementPlanReader);
        services.AddHostedService<PlacementExecutionPump>();

        return services;
    }
}
