using Bastion.Win32;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bastion.Daemon;

/// <summary>
/// Registers docs/engineering/daemon-architecture.md §2's required-order item 3 ("monitor topology
/// service") for GitHub issue #10's composition root — a stub only, since the real service (GitHub
/// issue #16) is not yet built. See <see cref="MonitorTopologyPlaceholderService"/>'s own remarks
/// for why a stub, not an omission, holds this slot.
/// </summary>
/// <remarks>
/// A one-line registration, factored into its own extension method (rather than inlined in
/// <c>Program.cs</c>) purely so <c>Bastion.Daemon.Tests</c>' composition-root smoke test can
/// reproduce <c>Program.cs</c>'s exact registration surface without itself needing a direct
/// <c>Bastion.Win32</c> project reference (this project already has one; the test project does
/// not, and should not need one just to prove the DI graph resolves).
/// </remarks>
internal static class BastionMonitorTopologyServiceCollectionExtensions
{
    /// <summary>Registers the monitor-topology stub.</summary>
    public static IServiceCollection AddBastionMonitorTopologyStub(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IHostedService, MonitorTopologyPlaceholderService>();

        return services;
    }
}
