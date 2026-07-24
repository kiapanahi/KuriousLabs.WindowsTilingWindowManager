using System.Collections.Immutable;
using Bastion.Core;

namespace Bastion.Win32.Tests;

/// <summary>
/// Minimal <see cref="IWindowSystem"/> test double for <see cref="ReconcileNowSignalTests"/>/
/// <see cref="ReconcilerIntentPumpTests"/> — these only need to observe <em>that</em> a
/// convergence pass happened (a call count), not exercise <see cref="Reconciler"/>'s own
/// convergence logic, which <c>Bastion.Core.Tests</c>' own <c>FakeWindowSystem</c> already covers
/// thoroughly.
/// </summary>
internal sealed class FakeWindowSystem : IWindowSystem
{
    public int ReadAllCallCount { get; private set; }

    public Task<ImmutableArray<ObservedWindow>> ReadAllAsync(CancellationToken cancellationToken)
    {
        ReadAllCallCount++;
        return Task.FromResult(ImmutableArray<ObservedWindow>.Empty);
    }
}
