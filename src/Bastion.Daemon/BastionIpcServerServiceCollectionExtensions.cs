using Bastion.Core;
using Bastion.Win32;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Bastion.Daemon;

/// <summary>
/// Registers docs/engineering/daemon-architecture.md §2's required-order item 4 ("the named-pipe
/// IPC command/broadcast server") for GitHub issue #10's composition root — GitHub issues #11/#12,
/// the slot <c>Program.cs</c>'s own comment named as "This is the slot issue #12 fills."
/// </summary>
/// <remarks>
/// <para>
/// <b>Dual concrete + interface/<see cref="IHostedService"/> registration for
/// <see cref="IpcBroadcastServerPump"/> only</b> — the identical pattern
/// <c>BastionEventPipelineServiceCollectionExtensions</c> already establishes for
/// <see cref="Bastion.Win32.WinEventPumpService"/>/<see cref="Bastion.Win32.Coalescer"/> (see that
/// type's own remarks): something downstream (a future <see cref="IIpcBroadcastPublisher"/>
/// consumer) needs to resolve the <em>same</em> singleton instance the host also runs as a pump, so
/// it is registered once as its own concrete type and exposed as both
/// <see cref="IHostedService"/> and <see cref="IIpcBroadcastPublisher"/> via factories that resolve
/// that one instance -- never two independently-constructed instances.
/// <see cref="IpcCommandServerPump"/> needs no such dual registration: nothing else resolves its
/// concrete type, so the standard <see cref="ServiceCollectionHostedServiceExtensions.AddHostedService{THostedService}"/>
/// sugar applies directly.
/// </para>
/// <para>
/// <b>Pump classes live in <c>Bastion.Win32</c>, not here.</b> Named pipes are plain,
/// Windows-portable BCL I/O, not CsWin32/Win32-specific — but every other pump in this repo
/// (<see cref="Bastion.Win32.WinEventPumpService"/>, <see cref="Bastion.Win32.Coalescer"/>,
/// <see cref="Bastion.Win32.ReconcilerLoopService"/>, <see cref="Bastion.Win32.PlacementExecutionPump"/>,
/// even the currently-stubbed <see cref="Bastion.Win32.MonitorTopologyPlaceholderService"/>) lives
/// in <c>Bastion.Win32</c> regardless of whether its own implementation happens to touch genuine
/// Win32 surface, with the registration <em>extension method</em> living here in
/// <c>Bastion.Daemon</c> — this method matches that established convention rather than inventing a
/// different placement for this one pump pair.
/// </para>
/// </remarks>
internal static class BastionIpcServerServiceCollectionExtensions
{
    /// <summary>Registers the IPC command/broadcast server chain. See this type's remarks for the registration-shape rationale.</summary>
    /// <param name="daemonVersion">
    /// The running <c>bastiond</c>'s version string, threaded into <see cref="IpcCommandProcessor"/>
    /// so a <see cref="StatusReply"/> reports it — the same MinVer-derived string <c>Program.cs</c>
    /// already computes for its own startup log.
    /// </param>
    public static IServiceCollection AddBastionIpcServer(this IServiceCollection services, string daemonVersion)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(daemonVersion);

        services.TryAddSingleton(_ => new IpcCommandProcessor(daemonVersion));
        services.AddHostedService<IpcCommandServerPump>();

        services.TryAddSingleton<IpcBroadcastServerPump>();
        services.AddSingleton<IHostedService>(static sp => sp.GetRequiredService<IpcBroadcastServerPump>());
        services.TryAddSingleton<IIpcBroadcastPublisher>(static sp => sp.GetRequiredService<IpcBroadcastServerPump>());

        return services;
    }
}
