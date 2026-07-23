using Bastion.Win32;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// Construction-only coverage for <see cref="WinEventPumpService"/>. The pump thread lifecycle
/// itself — <c>StartAsync</c> registering real, system-wide <c>SetWinEventHook</c> ranges,
/// <c>StopAsync</c>'s <c>PostThreadMessage</c>/<c>Thread.Join</c> handshake — is intentionally not
/// exercised here: that is Tier 3 (<c>Bastion.TestWindows</c>) territory per GitHub issue #13, not
/// this unit-test project.
/// </summary>
public sealed class WinEventPumpServiceTests
{
    [Fact]
    public void ConstructorWiresIngestReaderToAFreshEmptyChannel()
    {
        var reconcileSignal = new FakeReconcileNowSignal();
        using var pump = new WinEventPumpService(reconcileSignal);

        Assert.NotNull(pump.IngestReader);
        Assert.False(pump.IngestReader.TryRead(out _));
    }
}
