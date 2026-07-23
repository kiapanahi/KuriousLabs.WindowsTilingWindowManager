using Bastion.Win32;
using Windows.Win32.Foundation;

namespace Bastion.Win32.Tests;

/// <summary>Configurable <see cref="IWindowIdentityResolver"/> fake for <see cref="WindowRegistryTests"/>.</summary>
internal sealed class FakeWindowIdentityResolver : IWindowIdentityResolver
{
    public WindowIdentity Result { get; set; } = WindowIdentity.Unknown;

    public int CallCount { get; private set; }

    /// <summary>
    /// Awaited before returning <see cref="Result"/> — defaults to an already-completed task.
    /// Set to a controllable <see cref="TaskCompletionSource"/>'s <see cref="Task"/> to hold every
    /// concurrent caller at this exact point, deterministically exercising
    /// <see cref="WindowRegistry"/>'s race-on-admission path (see <c>WindowRegistryTests</c>).
    /// </summary>
    public Task Gate { get; set; } = Task.CompletedTask;

    public async Task<WindowIdentity> ResolveAsync(HWND hwnd, uint pid, CancellationToken cancellationToken = default)
    {
        CallCount++;
        await Gate.ConfigureAwait(false);
        return Result;
    }
}
