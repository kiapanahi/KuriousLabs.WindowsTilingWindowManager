using System.Threading.Channels;
using Bastion.Win32;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// Exercises the WinEvent ingest channel's overflow/drop contract — GitHub issue #1's mandatory
/// acceptance test ("A test exists exercising the channel's overflow/drop behavior (does not
/// require a real window)"). See docs/engineering/concurrency-performance.md §1.
/// </summary>
public sealed class WinEventChannelFactoryTests
{
    private const int Capacity = 4096;

    [Fact]
    public void WritesUpToCapacitySucceedWithoutSignalingReconcile()
    {
        var reconcileSignal = new FakeReconcileNowSignal();
        Channel<WinEvent> channel = WinEventChannelFactory.CreateIngestChannel(reconcileSignal);

        for (var i = 0; i < Capacity; i++)
        {
            Assert.True(channel.Writer.TryWrite(new WinEvent((nint)i, EventId: 1, DwmsEventTimeMs: 0)));
        }

        Assert.Equal(0, reconcileSignal.RequestCount);
    }

    [Fact]
    public void OverflowingWriteIsDroppedWithoutDisturbingAlreadyQueuedItems()
    {
        var reconcileSignal = new FakeReconcileNowSignal();
        Channel<WinEvent> channel = WinEventChannelFactory.CreateIngestChannel(reconcileSignal);

        for (var i = 0; i < Capacity; i++)
        {
            Assert.True(channel.Writer.TryWrite(new WinEvent((nint)i, EventId: 1, DwmsEventTimeMs: 0)));
        }

        // Verified against learn.microsoft.com/dotnet/api/system.threading.channels.channelwriter-1.trywrite
        // plus this test failing before this comment was added: TryWrite's documented "false
        // immediately" behavior on a full channel is called out *only* for BoundedChannelFullMode.Wait.
        // Under DropWrite (and DropOldest/DropNewest), the write always succeeds from TryWrite's
        // perspective — something is always incorporated into the channel — and which item is
        // sacrificed is signaled exclusively via the itemDropped callback, never via TryWrite's
        // return value.
        bool overflowWriteAccepted =
            channel.Writer.TryWrite(new WinEvent((nint)Capacity, EventId: 2, DwmsEventTimeMs: 1));

        Assert.True(overflowWriteAccepted);
        Assert.Equal(1, reconcileSignal.RequestCount);

        // FullMode.DropWrite (concurrency-performance.md §1): the *incoming* item is sacrificed,
        // every already-queued item is left untouched — assert the channel still drains
        // oldest-first starting at Hwnd 0, not shifted the way DropOldest would leave it.
        Assert.True(channel.Reader.TryRead(out WinEvent first));
        Assert.Equal(0, first.Hwnd);
    }

    [Fact]
    public void ManyOverflowingWritesEachRequestReconcileExactlyOnce()
    {
        var reconcileSignal = new FakeReconcileNowSignal();
        Channel<WinEvent> channel = WinEventChannelFactory.CreateIngestChannel(reconcileSignal);

        for (var i = 0; i < Capacity; i++)
        {
            _ = channel.Writer.TryWrite(new WinEvent((nint)i, EventId: 1, DwmsEventTimeMs: 0));
        }

        const int OverflowCount = 10;
        for (var i = 0; i < OverflowCount; i++)
        {
            _ = channel.Writer.TryWrite(new WinEvent((nint)(Capacity + i), EventId: 2, DwmsEventTimeMs: 0));
        }

        Assert.Equal(OverflowCount, reconcileSignal.RequestCount);
    }

    [Fact]
    public void CreateIngestChannelRejectsANullReconcileNowSignal()
    {
        Assert.Throws<ArgumentNullException>(static () => WinEventChannelFactory.CreateIngestChannel(null!));
    }
}
