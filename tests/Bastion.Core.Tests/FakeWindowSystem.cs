using System.Collections.Immutable;
using Bastion.Core;

namespace Bastion.Core.Tests;

/// <summary>
/// Test double for <see cref="IWindowSystem"/> — a fully in-memory, zero-interop-type window set a
/// test controls directly, matching docs/engineering/testing.md §5's "the fake adapter touches
/// zero interop types" (the seam sits above CsWin32/COM, not inside a COM shim).
/// </summary>
internal sealed class FakeWindowSystem : IWindowSystem
{
    /// <summary>
    /// The windows <see cref="ReadAllAsync"/> returns on its next call. Mutate directly between
    /// convergence passes to simulate the desktop changing (a window appearing, vanishing,
    /// becoming cloaked, moving, etc.).
    /// </summary>
    public List<ObservedWindow> Windows { get; } = [];

    /// <summary>Test-observable: how many times <see cref="ReadAllAsync"/> has been called.</summary>
    public int ReadAllCallCount { get; private set; }

    public Task<ImmutableArray<ObservedWindow>> ReadAllAsync(CancellationToken cancellationToken)
    {
        ReadAllCallCount++;
        return Task.FromResult(Windows.ToImmutableArray());
    }
}
