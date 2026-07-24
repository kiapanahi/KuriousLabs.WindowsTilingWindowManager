using System.Reflection;
using Bastion.Daemon;
using Bastion.Win32;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Single-instance enforcement (GitHub issue #10, docs/engineering/daemon-architecture.md §7) --
// must run, and must exit cleanly if another instance already owns it, before any hosted service
// is registered. See SingleInstanceGuard's own remarks for the naming discipline.
using Mutex? singleInstanceMutex = SingleInstanceGuard.TryAcquire();
if (singleInstanceMutex is null)
{
    // Another bastiond instance already owns the mutex for this user/session -- exit cleanly
    // rather than fight it for hooks/hotkeys/the IPC pipe. No ILogger exists yet at this point (the
    // host isn't built), so this uses the same Console.Error fallback Bastion.Win32.HookDiagnostics
    // already establishes for "no logging pipeline reachable yet" -- the async overload, since
    // top-level statements with a top-level `await` compile into an async Main (CA1849).
    await Console.Error.WriteLineAsync("bastiond is already running for this user session; exiting.").ConfigureAwait(false);
    return;
}

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// Registered first so its StopAsync -- the meaningful half of JournalRestoreOnShutdownService,
// StartAsync is a no-op -- runs LAST: hosted services stop in reverse registration order, so every
// pump below has already stopped producing new hide/move-away operations by the time this runs
// (see BastionHwndJournalServiceCollectionExtensions' own remarks).
builder.Services.AddBastionHwndJournal();

// GitHub issue #9's config-loading subsystem (JSONC rules, hot-reload, schema publishing) --
// AddWindowRulesConfiguration bundles every piece behind this one call (see its own remarks).
builder.Services.AddWindowRulesConfiguration();

// docs/engineering/daemon-architecture.md §2's required startup order, item 1 ("event ingest
// pump") -- expanded to its full downstream pipeline (Coalescer -> Reconciler -> Placement
// Executor) rather than the bare WinEvent pump alone; see
// BastionEventPipelineServiceCollectionExtensions' own remarks for why that expansion belongs
// here, contiguous with the event-ingest slot, rather than left unwired.
builder.Services.AddBastionEventAndReconciliationPipeline();

// Required order, item 2: input pump (GitHub issue #7's Tier-1 RegisterHotKey service).
builder.Services.AddBastionInputPipeline();

// Required order, item 3: monitor topology service. Stub only -- GitHub issue #16 (Monitor
// Topology Service: StableMonitorId/EDID persistence, dock/undock migrate-home) is not yet built;
// see MonitorTopologyPlaceholderService's own remarks for why an explicit stub, not an omission,
// holds this slot.
builder.Services.AddBastionMonitorTopologyStub();

// Required order, item 4: the named-pipe IPC command/broadcast server. GitHub issue #12 (blocked
// by this one) owns the real implementation -- deliberately not stubbed here (unlike the monitor
// topology slot above): issue #12 does not exist yet as running code of any shape, so inventing a
// placeholder would be pure invention with nothing to anchor it to, versus the monitor topology
// stub which at least holds a slot for a fully-specified (DESIGN.md §8), merely-not-yet-built
// service. This is the slot issue #12 fills.

using IHost host = builder.Build();

// GitHub issue #14: HookDiagnostics.LogCallbackFault is called from inside [UnmanagedCallersOnly]
// hook callbacks (WinEventPumpService, WindowProbe, ApplicationFrameUwpAttributionProvider), where
// resolving anything from the DI container is unsafe/unavailable -- see that method's own remarks.
// Hand it a real ILogger exactly once, here, via a static one-time handoff rather than DI
// resolution inside the callback itself. Must run before host.RunAsync() starts the WinEvent pump
// -- a hook could fire the moment that hosted service's StartAsync returns.
// HookDiagnostics is a static class, so ILogger<HookDiagnostics> (a generic type argument) is not
// legal C# (CS0718) -- ILoggerFactory.CreateLogger(Type) is the documented non-generic equivalent,
// deriving the identical "Bastion.Win32.HookDiagnostics" category from the given Type value instead.
HookDiagnostics.Initialize(host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(HookDiagnostics)));

// GitHub issue #48/PR #49: log the MinVer-derived running version once the host (and therefore
// real ILogger-based logging) is up, ahead of every hosted service's own startup messages -- the
// equivalent of the deleted BastiondService's identical startup-version log. Read the identical
// way bastionc's own PrintAssemblyVersionAction does, rather than leaning on any framework default.
string version = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    ?? "unknown";
host.Services.GetRequiredService<ILogger<Program>>().DaemonStarting(version);

await host.RunAsync().ConfigureAwait(false);
