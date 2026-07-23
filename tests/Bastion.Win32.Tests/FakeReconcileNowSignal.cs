using Bastion.Win32;

namespace Bastion.Win32.Tests;

/// <summary>
/// Test double for <see cref="IReconcileNowSignal"/> — counts invocations instead of driving any
/// real reconciliation (the Reconciler itself does not exist yet; GitHub issue #4).
/// </summary>
internal sealed class FakeReconcileNowSignal : IReconcileNowSignal
{
    private int _requestCount;

    public int RequestCount => _requestCount;

    public void RequestReconcileNow() => Interlocked.Increment(ref _requestCount);
}
