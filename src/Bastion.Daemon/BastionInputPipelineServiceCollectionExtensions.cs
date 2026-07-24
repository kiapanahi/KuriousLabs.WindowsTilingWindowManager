using Bastion.Win32;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Bastion.Daemon;

/// <summary>
/// Registers GitHub issue #7's Tier-1 input service (DESIGN.md §7: <c>RegisterHotKey</c> on a
/// dedicated pump thread) for GitHub issue #10's composition root —
/// docs/engineering/daemon-architecture.md §2's required-order item 2 ("input pump").
/// </summary>
/// <remarks>
/// <see cref="LoggingHotkeyDispatchTarget"/> is the only <see cref="IHotkeyDispatchTarget"/>
/// registered here — issue #10's own scope explicitly stops at proving the
/// registration/probing/dispatch pipeline end to end; wiring fired hotkeys to real
/// Reconciler-driven layout commands (<see cref="HotkeyCommand.FocusLeft"/> and friends) is a
/// distinct, not-yet-filed feature this issue does not build.
/// </remarks>
internal static class BastionInputPipelineServiceCollectionExtensions
{
    /// <summary>Registers the Tier-1 hotkey input chain.</summary>
    public static IServiceCollection AddBastionInputPipeline(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IHotkeyRegistrationSystem, HotkeyRegistrationSystemAdapter>();
        services.TryAddSingleton<IHotkeyDispatchTarget, LoggingHotkeyDispatchTarget>();
        services.AddHostedService<InputPumpService>();

        return services;
    }
}
