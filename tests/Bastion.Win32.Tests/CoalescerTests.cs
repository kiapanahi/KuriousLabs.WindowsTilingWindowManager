using System.Threading.Channels;
using Bastion.Win32;
using Microsoft.Extensions.Time.Testing;
using Windows.Win32;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// Exercises the Coalescer (DESIGN.md §3.2, GitHub issue #2) via <see cref="Coalescer.OnEvent"/> —
/// the same directly-callable seam docs/engineering/testing.md §4 illustrates — so every coalescing
/// behavior is deterministic under <see cref="FakeTimeProvider"/>, with no real sleeps.
/// </summary>
public sealed class CoalescerTests
{
    // Exercising the real shipped default (rather than an independently-chosen TimeSpan) also
    // means these tests would catch a change to DefaultCoalesceWindow's value, not just to the
    // mechanism around it.
    private static readonly TimeSpan s_coalesceWindow = Coalescer.DefaultCoalesceWindow;

    private static Coalescer CreateCoalescer(FakeTimeProvider time, ICloakStateReader? cloakStateReader = null) =>
        new(Channel.CreateUnbounded<WinEvent>().Reader, cloakStateReader ?? new FakeCloakStateReader(), time, s_coalesceWindow);

    // --- Core coalescing mechanism ---------------------------------------------------------

    [Fact]
    public void BurstOfShowEventsWithinTheWindowCollapsesToOneWindowAppeared()
    {
        // docs/engineering/testing.md §4's documented gotcha: clear the sync context before
        // advancing the fake provider's timers, or an awaited continuation may not observe the
        // timer callback synchronously.
        SynchronizationContext.SetSynchronizationContext(null);
        var time = new FakeTimeProvider();
        using Coalescer coalescer = CreateCoalescer(time);
        const nint Hwnd = 0x1000;

        coalescer.OnEvent(new WinEvent(Hwnd, PInvoke.EVENT_OBJECT_SHOW, DwmsEventTimeMs: 1_000));
        coalescer.OnEvent(new WinEvent(Hwnd, PInvoke.EVENT_OBJECT_SHOW, DwmsEventTimeMs: 1_020));
        coalescer.OnEvent(new WinEvent(Hwnd, PInvoke.EVENT_OBJECT_SHOW, DwmsEventTimeMs: 1_040));

        time.Advance(s_coalesceWindow);

        Assert.True(coalescer.IntentReader.TryRead(out CoalescedIntent? intent));
        WindowAppeared appeared = Assert.IsType<WindowAppeared>(intent);
        Assert.Equal(Hwnd, appeared.Hwnd);
        Assert.False(coalescer.IntentReader.TryRead(out _)); // exactly one intent, not three
    }

    [Fact]
    public void EventsSpacedFurtherApartThanTheWindowAreNotCoalesced()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var time = new FakeTimeProvider();
        using Coalescer coalescer = CreateCoalescer(time);
        const nint Hwnd = 0x2000;

        coalescer.OnEvent(new WinEvent(Hwnd, PInvoke.EVENT_OBJECT_SHOW, DwmsEventTimeMs: 1_000));
        time.Advance(s_coalesceWindow);
        Assert.True(coalescer.IntentReader.TryRead(out CoalescedIntent? first));
        Assert.IsType<WindowAppeared>(first);

        // Second event's DwmsEventTimeMs is well outside the coalescing window from the first —
        // a genuinely separate occurrence, not a continuation of the same burst.
        coalescer.OnEvent(new WinEvent(Hwnd, PInvoke.EVENT_OBJECT_SHOW, DwmsEventTimeMs: 5_000));
        time.Advance(s_coalesceWindow);
        Assert.True(coalescer.IntentReader.TryRead(out CoalescedIntent? second));
        Assert.IsType<WindowAppeared>(second);

        Assert.False(coalescer.IntentReader.TryRead(out _)); // still just the two, not merged/tripled
    }

    [Fact]
    public void AWideDwmsEventTimeGapFlushesTheStaleBatchImmediatelyRatherThanMergingIt()
    {
        // Distinguishes the two clocks docs/engineering/concurrency-performance.md §4 calls out:
        // the merge decision is keyed on dwmsEventTime, not on how much *real*/FakeTimeProvider
        // time this test has advanced — so a wide dwmsEventTime gap flushes synchronously, with
        // no Advance() call at all yet.
        SynchronizationContext.SetSynchronizationContext(null);
        var time = new FakeTimeProvider();
        using Coalescer coalescer = CreateCoalescer(time);
        const nint Hwnd = 0x3000;

        coalescer.OnEvent(new WinEvent(Hwnd, PInvoke.EVENT_OBJECT_SHOW, DwmsEventTimeMs: 1_000));
        coalescer.OnEvent(new WinEvent(Hwnd, PInvoke.EVENT_OBJECT_SHOW, DwmsEventTimeMs: 5_000));

        Assert.True(coalescer.IntentReader.TryRead(out CoalescedIntent? first));
        Assert.IsType<WindowAppeared>(first);
        Assert.False(coalescer.IntentReader.TryRead(out _)); // the second batch is still pending its own timer

        time.Advance(s_coalesceWindow);
        Assert.True(coalescer.IntentReader.TryRead(out CoalescedIntent? second));
        Assert.IsType<WindowAppeared>(second);
    }

    [Fact]
    public void ABurstThatKeepsArrivingExtendsTheFlushDeadlineUntilItGoesQuiet()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var time = new FakeTimeProvider();
        using Coalescer coalescer = CreateCoalescer(time);
        const nint Hwnd = 0x3800;

        coalescer.OnEvent(new WinEvent(Hwnd, PInvoke.EVENT_OBJECT_SHOW, DwmsEventTimeMs: 1_000));

        // Advance almost to the deadline, then merge another event in — this should push the
        // flush deadline out again rather than letting the original one fire.
        time.Advance(TimeSpan.FromMilliseconds(50));
        coalescer.OnEvent(new WinEvent(Hwnd, PInvoke.EVENT_OBJECT_SHOW, DwmsEventTimeMs: 1_050));
        time.Advance(TimeSpan.FromMilliseconds(50));
        Assert.False(coalescer.IntentReader.TryRead(out _)); // would have fired by now if not extended

        time.Advance(s_coalesceWindow);
        Assert.True(coalescer.IntentReader.TryRead(out CoalescedIntent? intent));
        Assert.IsType<WindowAppeared>(intent);
    }

    // --- MOVESIZESTART/END drag bracketing ---------------------------------------------------

    [Fact]
    public void LocationChangeIsSuppressedDuringADragAndDragEndEmitsBothDragEndedAndGeometryDrift()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var time = new FakeTimeProvider();
        using Coalescer coalescer = CreateCoalescer(time);
        const nint Hwnd = 0x4000;

        coalescer.OnEvent(new WinEvent(Hwnd, PInvoke.EVENT_SYSTEM_MOVESIZESTART, DwmsEventTimeMs: 1_000));
        coalescer.OnEvent(new WinEvent(Hwnd, PInvoke.EVENT_OBJECT_LOCATIONCHANGE, DwmsEventTimeMs: 1_010));
        coalescer.OnEvent(new WinEvent(Hwnd, PInvoke.EVENT_OBJECT_LOCATIONCHANGE, DwmsEventTimeMs: 1_020));

        time.Advance(s_coalesceWindow);
        Assert.False(coalescer.IntentReader.TryRead(out _)); // suppressed — nothing pending, nothing to flush

        coalescer.OnEvent(new WinEvent(Hwnd, PInvoke.EVENT_SYSTEM_MOVESIZEEND, DwmsEventTimeMs: 1_200));

        Assert.True(coalescer.IntentReader.TryRead(out CoalescedIntent? first));
        DragEnded dragEnded = Assert.IsType<DragEnded>(first);
        Assert.Equal(Hwnd, dragEnded.Hwnd);

        Assert.True(coalescer.IntentReader.TryRead(out CoalescedIntent? second));
        GeometryDrift drift = Assert.IsType<GeometryDrift>(second);
        Assert.Equal(Hwnd, drift.Hwnd);
        Assert.Equal(1_200u, drift.DwmsEventTimeMs);

        Assert.False(coalescer.IntentReader.TryRead(out _)); // exactly those two, immediately, not debounced
    }

    [Fact]
    public void ALocationChangeBatchPendingBeforeADragStartsIsDroppedRatherThanEmittedMidDrag()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var time = new FakeTimeProvider();
        using Coalescer coalescer = CreateCoalescer(time);
        const nint Hwnd = 0x4800;

        coalescer.OnEvent(new WinEvent(Hwnd, PInvoke.EVENT_OBJECT_LOCATIONCHANGE, DwmsEventTimeMs: 1_000));
        coalescer.OnEvent(new WinEvent(Hwnd, PInvoke.EVENT_SYSTEM_MOVESIZESTART, DwmsEventTimeMs: 1_010));

        // The pre-drag GeometryDrift batch's own timer would otherwise still fire here.
        time.Advance(s_coalesceWindow);
        Assert.False(coalescer.IntentReader.TryRead(out _));
    }

    [Fact]
    public void LocationChangeAfterADragEndsIsCoalescedNormallyAgain()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var time = new FakeTimeProvider();
        using Coalescer coalescer = CreateCoalescer(time);
        const nint Hwnd = 0x4A00;

        coalescer.OnEvent(new WinEvent(Hwnd, PInvoke.EVENT_SYSTEM_MOVESIZESTART, DwmsEventTimeMs: 1_000));
        coalescer.OnEvent(new WinEvent(Hwnd, PInvoke.EVENT_SYSTEM_MOVESIZEEND, DwmsEventTimeMs: 1_100));
        Assert.True(coalescer.IntentReader.TryRead(out _)); // DragEnded
        Assert.True(coalescer.IntentReader.TryRead(out _)); // GeometryDrift

        coalescer.OnEvent(new WinEvent(Hwnd, PInvoke.EVENT_OBJECT_LOCATIONCHANGE, DwmsEventTimeMs: 1_150));
        time.Advance(s_coalesceWindow);

        Assert.True(coalescer.IntentReader.TryRead(out CoalescedIntent? intent));
        GeometryDrift drift = Assert.IsType<GeometryDrift>(intent);
        Assert.Equal(1_150u, drift.DwmsEventTimeMs);
    }

    // --- CLOAKED/UNCLOAKED burst -> DesktopSwitchSuspected heuristic -------------------------

    [Fact]
    public void CloakBurstOnAWindowThatReadsCloakedCollapsesToOneDesktopSwitchSuspected()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var time = new FakeTimeProvider();
        var cloakStateReader = new FakeCloakStateReader { IsCloakedResult = true };
        using Coalescer coalescer = CreateCoalescer(time, cloakStateReader);
        const nint Hwnd = 0x5000;

        coalescer.OnEvent(new WinEvent(Hwnd, PInvoke.EVENT_OBJECT_CLOAKED, DwmsEventTimeMs: 1_000));
        coalescer.OnEvent(new WinEvent(Hwnd, PInvoke.EVENT_OBJECT_UNCLOAKED, DwmsEventTimeMs: 1_020));
        coalescer.OnEvent(new WinEvent(Hwnd, PInvoke.EVENT_OBJECT_CLOAKED, DwmsEventTimeMs: 1_040));

        time.Advance(s_coalesceWindow);

        Assert.True(coalescer.IntentReader.TryRead(out CoalescedIntent? intent));
        DesktopSwitchSuspected suspected = Assert.IsType<DesktopSwitchSuspected>(intent);
        Assert.Equal(Hwnd, suspected.Hwnd);
        Assert.False(coalescer.IntentReader.TryRead(out _)); // one intent for the whole burst
    }

    [Fact]
    public void UncloakedEventWhenGenuinelyNotCloakedMapsToWindowAppeared()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var time = new FakeTimeProvider();
        var cloakStateReader = new FakeCloakStateReader { IsCloakedResult = false };
        using Coalescer coalescer = CreateCoalescer(time, cloakStateReader);
        const nint Hwnd = 0x5800;

        coalescer.OnEvent(new WinEvent(Hwnd, PInvoke.EVENT_OBJECT_UNCLOAKED, DwmsEventTimeMs: 1_000));
        time.Advance(s_coalesceWindow);

        Assert.True(coalescer.IntentReader.TryRead(out CoalescedIntent? intent));
        Assert.IsType<WindowAppeared>(intent);
    }

    [Fact]
    public void CloakedEventWhenGenuinelyNotCloakedEmitsNoIntent()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var time = new FakeTimeProvider();
        var cloakStateReader = new FakeCloakStateReader { IsCloakedResult = false };
        using Coalescer coalescer = CreateCoalescer(time, cloakStateReader);
        const nint Hwnd = 0x5A00;

        coalescer.OnEvent(new WinEvent(Hwnd, PInvoke.EVENT_OBJECT_CLOAKED, DwmsEventTimeMs: 1_000));
        time.Advance(TimeSpan.FromSeconds(1)); // well past any plausible window

        Assert.False(coalescer.IntentReader.TryRead(out _));
    }

    // --- WindowVanished: always immediate, never debounced -----------------------------------

    [Fact]
    public void DestroyEmitsWindowVanishedImmediatelyWithNoAdvanceNeeded()
    {
        var time = new FakeTimeProvider();
        using Coalescer coalescer = CreateCoalescer(time);
        const nint Hwnd = 0x6000;

        coalescer.OnEvent(new WinEvent(Hwnd, PInvoke.EVENT_OBJECT_DESTROY, DwmsEventTimeMs: 1_000));

        Assert.True(coalescer.IntentReader.TryRead(out CoalescedIntent? intent));
        WindowVanished vanished = Assert.IsType<WindowVanished>(intent);
        Assert.Equal(Hwnd, vanished.Hwnd);
    }

    [Fact]
    public void DestroyCancelsAnyPendingBatchForTheSameWindowWithoutEmittingIt()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var time = new FakeTimeProvider();
        using Coalescer coalescer = CreateCoalescer(time);
        const nint Hwnd = 0x6800;

        coalescer.OnEvent(new WinEvent(Hwnd, PInvoke.EVENT_OBJECT_LOCATIONCHANGE, DwmsEventTimeMs: 1_000));
        coalescer.OnEvent(new WinEvent(Hwnd, PInvoke.EVENT_OBJECT_DESTROY, DwmsEventTimeMs: 1_010));

        Assert.True(coalescer.IntentReader.TryRead(out CoalescedIntent? intent));
        Assert.IsType<WindowVanished>(intent);

        time.Advance(s_coalesceWindow);
        Assert.False(coalescer.IntentReader.TryRead(out _)); // the pre-destroy GeometryDrift never fires
    }

    // --- NAMECHANGE rate-limited re-evaluation -> WindowAppeared ------------------------------

    [Fact]
    public void NameChangeMapsToWindowAppearedAndIsRateLimitedLikeAnyOtherBurst()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var time = new FakeTimeProvider();
        using Coalescer coalescer = CreateCoalescer(time);
        const nint Hwnd = 0x7000;

        coalescer.OnEvent(new WinEvent(Hwnd, PInvoke.EVENT_OBJECT_NAMECHANGE, DwmsEventTimeMs: 1_000));
        coalescer.OnEvent(new WinEvent(Hwnd, PInvoke.EVENT_OBJECT_NAMECHANGE, DwmsEventTimeMs: 1_020));

        time.Advance(s_coalesceWindow);

        Assert.True(coalescer.IntentReader.TryRead(out CoalescedIntent? intent));
        Assert.IsType<WindowAppeared>(intent);
        Assert.False(coalescer.IntentReader.TryRead(out _)); // both NAMECHANGEs collapsed into one
    }

    // --- Events with no typed-intent mapping in this issue's scope ---------------------------

    [Theory]
    [InlineData(0x8000u)] // EVENT_OBJECT_CREATE
    [InlineData(0x8003u)] // EVENT_OBJECT_HIDE
    [InlineData(0x0016u)] // EVENT_SYSTEM_MINIMIZESTART
    [InlineData(0x0017u)] // EVENT_SYSTEM_MINIMIZEEND
    public void EventsWithNoTypedIntentMappingEmitNothing(uint eventId)
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var time = new FakeTimeProvider();
        using Coalescer coalescer = CreateCoalescer(time);

        coalescer.OnEvent(new WinEvent(0x9000, eventId, DwmsEventTimeMs: 1_000));
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.False(coalescer.IntentReader.TryRead(out _));
    }

    // --- Intent channel overflow -------------------------------------------------------------

    [Fact]
    public void OverflowingTheIntentChannelDropsWritesAndCountsThem()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var time = new FakeTimeProvider();
        using Coalescer coalescer = CreateCoalescer(time);
        const int OverflowBy = 10;

        // Distinct Hwnds never coalesce with each other, so each of these becomes its own pending
        // batch with its own flush timer, all due at the same instant — enough of them to exceed
        // Coalescer.IntentChannelCapacity in a single FakeTimeProvider.Advance.
        for (nint hwnd = 1; hwnd <= Coalescer.IntentChannelCapacity + OverflowBy; hwnd++)
        {
            coalescer.OnEvent(new WinEvent(hwnd, PInvoke.EVENT_OBJECT_SHOW, DwmsEventTimeMs: 1_000));
        }

        Assert.Equal(0, coalescer.DroppedIntentCount);
        time.Advance(s_coalesceWindow);

        Assert.Equal(OverflowBy, coalescer.DroppedIntentCount);

        var read = 0;
        while (coalescer.IntentReader.TryRead(out _))
        {
            read++;
        }

        Assert.Equal(Coalescer.IntentChannelCapacity, read);
    }

    // --- Disposal safety ----------------------------------------------------------------------

    [Fact]
    public void DisposeStopsPendingBatchesFromEmittingLaterIntents()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var time = new FakeTimeProvider();
        Coalescer coalescer = CreateCoalescer(time);

        coalescer.OnEvent(new WinEvent(0xA000, PInvoke.EVENT_OBJECT_SHOW, DwmsEventTimeMs: 1_000));
        coalescer.Dispose();

        time.Advance(TimeSpan.FromSeconds(1));

        Assert.False(coalescer.IntentReader.TryRead(out _));
    }

    // --- Constructor validation -----------------------------------------------------------

    [Fact]
    public void ConstructorRejectsANullIngestReader()
    {
        var time = new FakeTimeProvider();
        Assert.Throws<ArgumentNullException>(() =>
            new Coalescer(null!, new FakeCloakStateReader(), time, s_coalesceWindow));
    }

    [Fact]
    public void ConstructorRejectsANullCloakStateReader()
    {
        var time = new FakeTimeProvider();
        Assert.Throws<ArgumentNullException>(() =>
            new Coalescer(Channel.CreateUnbounded<WinEvent>().Reader, null!, time, s_coalesceWindow));
    }

    [Fact]
    public void ConstructorRejectsANullTimeProvider()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new Coalescer(Channel.CreateUnbounded<WinEvent>().Reader, new FakeCloakStateReader(), null!, s_coalesceWindow));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-75)]
    public void ConstructorRejectsANonPositiveCoalesceWindow(double milliseconds)
    {
        var time = new FakeTimeProvider();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Coalescer(Channel.CreateUnbounded<WinEvent>().Reader, new FakeCloakStateReader(), time, TimeSpan.FromMilliseconds(milliseconds)));
    }

    // --- BackgroundService hosting wiring ----------------------------------------------------

    [Fact]
    public async Task ExecuteAsyncDrainsTheIngestChannelIntoTheSameCoalescingPipeline()
    {
        var time = new FakeTimeProvider();
        var ingest = Channel.CreateUnbounded<WinEvent>();
        using var coalescer = new Coalescer(ingest.Reader, new FakeCloakStateReader(), time, s_coalesceWindow);
        const nint Hwnd = 0xB000;

        await coalescer.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.True(ingest.Writer.TryWrite(new WinEvent(Hwnd, PInvoke.EVENT_OBJECT_SHOW, DwmsEventTimeMs: 1_000)));

            // ExecuteAsync's `await foreach` continuation runs asynchronously relative to this
            // write (real thread-pool scheduling), unlike every coalescing-*duration* assertion
            // above, which stays fully deterministic on OnEvent + FakeTimeProvider.Advance
            // directly. Poll briefly on real wall-clock time only for "has the drain loop picked
            // the write up yet" — advancing FakeTimeProvider on every attempt is safe regardless
            // of how many attempts that takes, since a freshly-armed batch's due time is always
            // relative to whenever CreateTimer actually ran.
            CoalescedIntent? intent = null;
            for (var attempt = 0; attempt < 200 && intent is null; attempt++)
            {
                time.Advance(s_coalesceWindow);
                if (coalescer.IntentReader.TryRead(out intent))
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);
            }

            Assert.IsType<WindowAppeared>(intent);
        }
        finally
        {
            await coalescer.StopAsync(TestContext.Current.CancellationToken);
        }
    }
}
