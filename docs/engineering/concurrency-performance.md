# Concurrency, Threading & Hot-Path Performance

Owns the event-pipeline mechanics for `bastiond`: the WinEvent ingest channel's exact
configuration, dedicated-thread vs. task-pool decisions, STA thread construction, GC/allocation
policy, testable time, and production diagnostics. This is the authority for *how the pipeline
runs*, not *what it decides* (that's the Reconciler's domain, DESIGN.md §3.4) or *how it talks to
Win32/COM* (that's [interop.md](./interop.md)).

Baseline: .NET 10, C# 14. Anything requiring a newer runtime/language version is called out
explicitly; nothing here does.

---

## 1. The WinEvent ingest channel (exact configuration)

DESIGN.md §3.1 requires the WinEvent pump to enqueue `(hwnd, event, dwmsEventTime)` into a
bounded channel where **overflow is not an error** — it sets a reconcile-now flag and drops
deltas, trusting the Reconciler's 5 s heartbeat (§3.4) and authoritative reads to recover ground
truth. The channel configuration is what makes that guarantee real.

### Always the explicit `BoundedChannelOptions` constructor

Never use the `Channel.CreateBounded<T>(int capacity)` convenience overload. It silently defaults
`FullMode` to `BoundedChannelFullMode.Wait`, which makes `WriteAsync` block the writer until space
frees up. On a WinEvent-pump-driven queue, a blocking writer stalls delivery of subsequent OS
callbacks — exactly the failure mode §3.1's "overflow is not an error" design exists to prevent.
Always construct `BoundedChannelOptions` explicitly and use the `Channel.CreateBounded<T>(options,
itemDropped)` overload so the drop path is a first-class, observable callback, not an implicit
block.

```csharp
// Bastion.Win32 — WinEvent ingest channel
private static readonly BoundedChannelOptions IngestChannelOptions = new(capacity: 4096)
{
    FullMode = BoundedChannelFullMode.DropWrite,
    SingleWriter = true,   // only the WinEvent pump thread ever writes
    SingleReader = true,   // only the Coalescer ever reads
    AllowSynchronousContinuations = false,
};

private readonly Channel<WinEvent> _ingest = Channel.CreateBounded<WinEvent>(
    IngestChannelOptions,
    itemDropped: static _ => ReconcileNowFlag.Set());
```

### `SingleWriter` / `SingleReader`

Set both `true`. Exactly one thread writes (the WinEvent pump, per §3.1's dedicated
`GetMessage`/`DispatchMessage` thread) and exactly one thread reads (the Coalescer, per §3.2).
Declaring this unlocks the channel's lock-free single-producer/single-consumer fast path — do not
set either `false` "just in case"; a second writer or reader is itself an architecture violation of
the single-threaded-actor design (DESIGN.md §3, §3.4) and should fail review, not be
accommodated by the channel config.

### The `itemDropped` callback IS the overflow contract

`Channel.CreateBounded<T>(BoundedChannelOptions, Action<T>? itemDropped)` invokes the delegate
synchronously, on the writer's thread, whenever an item is evicted due to a full channel. This is
the literal mechanism for DESIGN.md §3.1/§3.4's "queue overflow sets a reconcile-now flag" —
**do not poll channel depth** (`Channel<T>.Reader.Count` or similar) to detect backpressure; that
is racy, wasteful, and reinvents what the callback already gives you deterministically. The
callback body must be trivial and allocation-free (set a flag, `Interlocked.Exchange` a bool, or
signal an existing `ManualResetEventSlim`) — it runs inline on the OS-callback-adjacent pump
thread, so anything more than a flag-set risks becoming the hot-path cost DESIGN.md is designed
to avoid.

### `FullMode`: DropWrite vs. DropOldest

Both are defensible for this queue, because **dropped deltas are recovered by authoritative reads**
(the 5 s heartbeat and distrust escalation, DESIGN.md §3.4) — that recovery guarantee is what
makes the choice non-load-bearing correctness-wise. The two options differ only in which item is
sacrificed:

- **`DropWrite`** (recommended default) — the *incoming* item is dropped; every already-queued,
  not-yet-processed intent is left untouched. This means a shed arrival never mutates the shape of
  what the Coalescer is about to drain, and exactly one `itemDropped` invocation happens per shed
  event — simplest to reason about and to unit-test.
- **`DropOldest`** — the oldest *queued* item is evicted to make room for the new write. Also
  correct here, since the periodic full re-sync (heartbeat, every 5 s per §3.4) already treats
  stale deltas as no more authoritative than fresh ones once a reconcile-now flag is set.

Pick one, record the rationale in a code comment at the channel construction site (not just in
this doc), and keep the `itemDropped` → reconcile-now-flag semantics identical regardless of which
you pick. Default recommendation: **`DropWrite`**, revisited with real `dotnet-trace`/event-log
data (§6) if field traces show a different mode would reduce reconcile churn.

---

## 2. Thread topology

### Message pumps and anything STA or Win32-marshaling: a raw dedicated `Thread`

`Thread.SetApartmentState(ApartmentState)` **must** be called before `Start()` — it throws
`ThreadStateException` once the thread has started, and a brand-new thread defaults to MTA if
apartment state isn't set first. Thread-pool threads — including `Task.Run` and
`TaskCreationOptions.LongRunning` tasks — are always MTA and are already running by the time your
code touches them; there is no supported hook to call `SetApartmentState` on a pool thread before
it begins executing. **`Task.Run` can never substitute for a dedicated STA thread.**

```csharp
var pumpThread = new Thread(WinEventPump.Run)
{
    IsBackground = false,
};
pumpThread.SetApartmentState(ApartmentState.STA); // before Start() — throws afterward
pumpThread.Name = "Bastion.WinEventPump";           // see "Name every dedicated thread" below
pumpThread.Start();
```

This applies to every thread in `bastiond` that runs a `GetMessage`/`DispatchMessage` loop (the
WinEvent pump, §3.1) or performs shell-COM calls that the source-generated COM interop layer
cannot marshal across apartments for you (the reconciler/message-pump thread hosting
`ITaskbarList`/`IVirtualDesktopManager` calls — apartment-affinity details live in
[interop.md](./interop.md) §on apartment discipline). Exactly which calls must land on that
specific thread is defined there, not here; this section only owns *how the thread is
constructed*.

### `TaskCreationOptions.LongRunning`: acceptable only for pure managed drain loops

`TaskCreationOptions.LongRunning` is a legitimate, documented signal telling the scheduler not to
use a normal pool thread for a long-lived operation — but the thread it produces is still MTA. Use
it only for a loop that:

- does an `await foreach` (or equivalent) drain over a `ChannelReader<T>`,
- performs no Win32 calls that require message-pump semantics, and
- performs no COM calls requiring apartment affinity.

The moment that loop needs to call `SendMessageTimeout`, `DeferWindowPos`/`BeginDeferWindowPos`, or
touch any STA-affine shell COM object (§3.4, §3.6 of DESIGN.md), move it to the dedicated-`Thread`
+ `SetApartmentState(STA)` pattern above. Do not let a `LongRunning` task's convenience justify
smuggling a Win32 marshaling call into it "just this once."

### Name every dedicated thread

Set `Thread.Name` immediately after construction, before `Start()`, on every dedicated thread:
the WinEvent pump, the reconciler/message-pump thread, and the low-level-keyboard-hook thread
(DESIGN.md §7 Tier 2). This is the only practical way to distinguish Bastion's several long-lived,
native-adjacent threads in `dotnet-trace`/EventPipe captures, `dotnet-dump` reports, or a Visual
Studio Threads window when diagnosing a hang (e.g. a stuck `SendMessageTimeout` probe, §3.6).
Unnamed thread-pool-style `Thread #17` entries are useless for that triage.

---

## 3. GC policy

### Baseline: Workstation GC, default Interactive latency

`bastiond` is a single-process, background desktop daemon, not a multi-core request-throughput
server — **Workstation GC** (the default for non-ASP.NET console/worker apps) with concurrent
(background) GC and the default `GCLatencyMode.Interactive` is the correct baseline. **Never**
enable Server GC for this daemon: Server GC is tuned for per-core-heap request throughput, which is
the wrong trade for a UI-latency-sensitive, mostly-idle background process that should stay light
on working set.

### `GCLatencyMode.SustainedLowLatency`: scoped, never process-wide

Reach for `GCLatencyMode.SustainedLowLatency` only around a **proven-hot** window — concretely, the
Placement Executor's `DeferWindowPos` batch (DESIGN.md §3.6d) — and only after a `dotnet-trace`/
`dotnet-counters` capture (§6) shows a gen2 collection coinciding with visible placement jank. Set
it immediately before the batch, restore the previous mode immediately after:

```csharp
var previous = GCSettings.LatencyMode;
GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
try
{
    // BeginDeferWindowPos / DeferWindowPos / EndDeferWindowPos batch (DESIGN.md §3.6d)
}
finally
{
    GCSettings.LatencyMode = previous;
}
```

Never set this process-wide, and never call `GC.Collect()` anywhere in business logic — pool
buffers (§5) rather than churn objects that would otherwise force a collection.

### Check DATAS before hand-tuning any heap knob

DATAS (Dynamic Adaptation To Application Sizes, `System.GC.DynamicAdaptationMode`) has been on by
default since .NET 9 and continues in .NET 10. It dynamically resizes generation budgets to reduce
working set — precisely what a mostly-idle background daemon wants. Before adding any manual
`GCHeapHardLimit`, gen0 budget override, or other heap-tuning knob to `bastiond`'s
`runtimeconfig.json`, verify DATAS's current behavior for the targeted .NET 10 servicing release
first; in most cases no manual override is needed at all.

---

## 4. Testable time

Every debounce/heartbeat constant DESIGN.md defines — the ~75 ms Coalescer window (§3.2), the
~150 ms admission grace (§3.2, §5), the ~500 ms display-settle debounce (§8), and the Reconciler's
5 s heartbeat (§3.4) — must be driven by an injected `TimeProvider`, never by `DateTime.Now` or a
bare `Task.Delay`. These constants are "engineering practice, not documented constants" per §3.2 —
they live in config and must be independently, deterministically testable without real-time
sleeps.

```csharp
internal sealed class Coalescer(TimeProvider timeProvider, TimeSpan coalesceWindow)
{
    // never: DateTime.UtcNow, Task.Delay(TimeSpan) — always the injected TimeProvider
}
```

### Heartbeat loop: `PeriodicTimer(TimeSpan, TimeProvider)`

Use the `PeriodicTimer(TimeSpan period, TimeProvider timeProvider)` constructor for the
Reconciler's 5 s heartbeat, so tests can drive it via `Microsoft.Extensions.TimeProvider.Testing`'s
`FakeTimeProvider` instead of waiting on a wall-clock timer.

```csharp
private readonly PeriodicTimer _heartbeat = new(TimeSpan.FromSeconds(5), timeProvider);

await foreach (var _ in TimerAsyncEnumerable(_heartbeat, cancellationToken))
{
    // full re-sync per DESIGN.md §3.4
}
```

Note `PeriodicTimer.WaitForNextTickAsync` supports only a single concurrent consumer — fine here,
since the Reconciler is the single-threaded actor DESIGN.md §3.4 already mandates; do not fan a
second consumer out over the same `PeriodicTimer` instance.

Test-side `FakeTimeProvider` usage (including its `SynchronizationContext`-clearing gotcha when
advancing time under an awaited continuation) is operationalized in
[testing.md](./testing.md) — this document only owns the production-code injection point.

---

## 5. Allocation-free hot paths

### Event envelope: `readonly record struct`

Represent the WinEvent tuple as a `readonly record struct`, giving value semantics and
compiler-generated equality with no heap allocation for the envelope itself:

```csharp
internal readonly record struct WinEvent(nint Hwnd, uint EventId, uint DwmsEventTimeMs);
```

This is safe here specifically because every field is a primitive/`nint` — record equality
compares members via `Object.Equals`, so a record holding a `List<T>` or array would compare by
*reference*, not value; that pitfall doesn't apply to `WinEvent` but keep it in mind if the type
ever grows a collection-typed field. Boxing still occurs if the struct is later stored as `object`
or passed through a non-generic delegate/`params object[]` — keep it flowing through generic
`ChannelWriter<WinEvent>`/`ChannelReader<WinEvent>` APIs, never cast to `object`.

Note the blittability rule that `[UnmanagedCallersOnly]` imposes (CS8894 — every parameter/return
of the native-facing callback method must be blittable) applies only to the `WinEventProc`/
`LowLevelKeyboardProc` entry-point signature itself, documented in
[interop.md](./interop.md). Once inside that method, packing primitives into `WinEvent` for
`ChannelWriter<WinEvent>.TryWrite` is ordinary managed code and is not itself subject to that
constraint — don't over-constrain the internal record struct's shape trying to satisfy a rule that
doesn't apply to it.

### No per-event closures on the ingest path

Do not allocate a fresh lambda per WinEvent — e.g. capturing loop-local state in a closure handed
to `Task.Run`/`ContinueWith`/a per-item callback. Reuse a single `static` (preferred) or instance
delegate for the channel-drain loop's per-item handling:

```csharp
// Wrong: allocates a closure per drained item
await foreach (var evt in reader.ReadAllAsync(ct))
{
    _ = Task.Run(() => Handle(evt, someLoopLocal)); // captures someLoopLocal — allocates
}

// Right: one reusable delegate, no per-item closure
await foreach (var evt in reader.ReadAllAsync(ct))
{
    HandleEvent(evt); // static or instance method, no capture
}
```

### C# 14 implicit `Span` conversions

C# 14's broader set of implicit conversions between `Span<T>`/`ReadOnlySpan<T>` and arrays is
available for hook-path code that needs to pass buffers without an intermediate array allocation.
The full catalogue of C# 14 features adopted repo-wide (extension blocks, the `field` keyword,
implicit span conversions, etc.) is tracked in [quality-gates.md](./quality-gates.md) — this
document only calls out that the feature is safe and applicable on the ingest hot path.

---

## 6. Diagnostics & benchmarking

### Production diagnosis: `dotnet-trace` + `dotnet-counters`

Use `dotnet-trace` (built on EventPipe) and `dotnet-counters` as the default production
diagnostics for `bastiond`'s hot paths. Both attach to a running process by ID, require no
administrator privilege, and are cross-platform. `dotnet-counters` gives live gen0/1/2 collection
counts, thread-pool queue length, and exception counts useful for watching the Reconciler under
load without a full capture; `dotnet-trace collect --providers <EventSource names>` produces a
`.nettrace` file viewable in PerfView, Visual Studio, or converted to speedscope/Chromium trace
format.

Reach for **ETW/PerfView only when native call-stack resolution is required** — e.g. resolving
frames inside a hung `SendMessageTimeout` call (DESIGN.md §3.6's hang probe) that EventPipe cannot
symbolicate. ETW requires elevation and is Windows-only, which is an acceptable cost here since
Bastion is Windows-only, but it should be the escalation, not the default.

### Benchmarking: BenchmarkDotNet, only for proven-hot paths

Micro-benchmark the coalescing/window-diffing hot paths with **BenchmarkDotNet** in Release —
`[MemoryDiagnoser]` to catch allocation regressions, a `[Benchmark(Baseline = true)]` for
comparison, and `[Params]` sweeps for realistic event-storm sizes:

```csharp
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0)]
public class CoalescerBenchmarks
{
    [Params(10, 100, 1000)]
    public int EventCount;

    [Benchmark(Baseline = true)]
    public void CoalesceStorm() { /* ... */ }
}
```

**Never optimize a path that has not shown up as hot in a `dotnet-trace` capture.** BenchmarkDotNet
is community-maintained, not Microsoft-docs-hosted — cross-check its own current documentation
(package versions, attribute surface, runtime-moniker names) when writing the actual harness rather
than relying on memorized API shapes.

---

## Forbidden

- `Channel.CreateBounded<T>(int)` for the WinEvent ingest queue — silently defaults to
  `FullMode.Wait`, which can block the OS-callback-driven writer.
- Polling channel depth (`Reader.Count` or similar) to detect overflow instead of using the
  `itemDropped` callback.
- Hosting a message pump, STA COM call, or any Win32-marshaling loop on `Task.Run` or
  `TaskCreationOptions.LongRunning` — both produce MTA thread-pool threads that cannot have
  `SetApartmentState` applied.
- Calling `Thread.SetApartmentState` after `Start()` — throws `ThreadStateException`.
- `GC.Collect()` anywhere in business logic.
- Process-wide `GCLatencyMode.SustainedLowLatency`/`LowLatency` left set instead of scoped
  tightly around one proven-hot critical section with the previous mode restored afterward.
- Server GC for `bastiond`.
- `DateTime.Now`/`DateTime.UtcNow` or bare `Task.Delay` inside the Coalescer or Reconciler instead
  of an injected `TimeProvider`.
- Allocating a per-event closure/lambda on the ingest hot path.
- Optimizing any path — coalescing, diffing, ingest — without a `dotnet-trace`/BenchmarkDotNet
  result showing it is actually hot.

---

## Cross-references

- [interop.md](./interop.md) — CsWin32 configuration, `[UnmanagedCallersOnly]` callback bodies,
  function-pointer callback types, handle modeling, and which specific calls require the STA
  thread constructed in §2 above.
- [daemon-architecture.md](./daemon-architecture.md) — Generic Host wiring, why the WinEvent
  pump/keyboard-hook threads must be raw `IHostedService`s rather than `BackgroundService`s (the
  .NET 10 `BackgroundService.ExecuteAsync` thread-pool change), logging, and single-instance
  enforcement.
- [testing.md](./testing.md) — `FakeTimeProvider` usage patterns and the xUnit
  `SynchronizationContext` gotcha when advancing virtual time under an awaited continuation.
- [quality-gates.md](./quality-gates.md) — the full C# 14 feature adoption list, analyzer
  configuration, and AOT/trim property placement referenced in passing above.
