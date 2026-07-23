# Daemon Architecture: Hosting, Logging, Config, State & Resilience

Owns `bastiond`'s process-level architecture: Generic Host wiring, hosted-service lifetime
rules (including the .NET 10 `BackgroundService` breaking change), AOT-safe logging and
configuration binding, immutable state-snapshot data structures, must-not-die exception
policy, and single-instance enforcement. For CsWin32/COM/`[UnmanagedCallersOnly]` interop
rules see `docs/engineering/interop.md`; for channel/thread/GC mechanics behind the pumps
described here see `docs/engineering/concurrency-performance.md`; for CI/analyzer gates see
`docs/engineering/quality-gates.md`; for test tooling see `docs/engineering/testing.md`; for
`JsonSerializerContext` layout, JSONC/schema, and named-pipe IPC mechanics see
`docs/engineering/json-ipc-config.md`.

---

## 1. Host wiring

- Entry point is `Host.CreateApplicationBuilder(args)` (`HostApplicationBuilder`) — the
  modern, minimal-reflection Generic Host builder that `dotnet new worker --aot` itself
  scaffolds. It is the correct entry point for `bastiond` today.
- **There is no non-web "slim" builder.** `WebApplication.CreateSlimBuilder` is
  ASP.NET Core-specific; `bastiond` has no HTTP surface (IPC is named pipes, §4 of
  DESIGN.md §3.9), so it does not apply here. AOT footprint reduction for `bastiond` comes
  from `PublishAot`/trimming settings and pruning unnecessary DI registrations, not from
  picking a different builder.
- **Never** use the legacy `Host.CreateDefaultBuilder()` callback-based `IHostBuilder` for
  new code — it is the pre-minimal-hosting-model API and pulls in more reflection-based
  wiring than `HostApplicationBuilder` needs.
- Reference: https://learn.microsoft.com/dotnet/core/extensions/generic-host

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<IHostedService, WinEventPumpService>();
builder.Services.AddSingleton<IHostedService, InputPumpService>();
builder.Services.AddHostedService<TopologyService>();   // fine as an ordinary async hosted service
builder.Services.AddHostedService<IpcServerService>();

var host = builder.Build();
await host.RunAsync();
```

---

## 2. Hosted services & the .NET 10 breaking change

**The load-bearing fact for this whole section:** as of .NET 10, `BackgroundService.ExecuteAsync`
runs its *entire* body — including any synchronous code before the first `await` — on a
thread-pool thread. In .NET 9 and earlier, the portion of `ExecuteAsync` before the first
`await` ran inline during `StartAsync`. This is a breaking change and it matters for
`bastiond` specifically: the WinEvent ingest pump and the keyboard-hook input pump each need
one **stable, dedicated, foreground OS thread that lives for the entire process lifetime**
(a documented requirement for out-of-context hooks — DESIGN.md §3.1, §7). A thread-pool
thread is the wrong identity and lifetime model for that job even if it happens to run the
right code.

- Reference: https://learn.microsoft.com/dotnet/api/microsoft.extensions.hosting.backgroundservice
- Reference: https://learn.microsoft.com/dotnet/api/microsoft.extensions.hosting.ihostedservice

**Rule: pumps are raw `IHostedService`, never `BackgroundService`.**

The WinEvent ingest pump (§3.1) and the input service pump (`RegisterHotKey`/`WH_KEYBOARD_LL`,
§7) must each be implemented as a plain `IHostedService` that owns its own `Thread`:

```csharp
internal sealed class WinEventPumpService : IHostedService
{
    private Thread? _pumpThread;
    private volatile bool _stopRequested;
    private readonly ManualResetEventSlim _threadReady = new(initialState: false);
    private uint _pumpThreadId;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _pumpThread = new Thread(PumpLoop)
        {
            Name = "Bastion.WinEventPump",
            IsBackground = false, // outlives GC-driven finalization concerns; we own shutdown
        };
        _pumpThread.Start();

        // Wait for the pump to install its hook(s) and record its thread id before
        // StartAsync returns, so registration-order guarantees (below) hold.
        _threadReady.Wait(cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stopRequested = true;
        // PostThreadMessage(WM_QUIT) unblocks a GetMessage loop from another thread —
        // this is the documented way to end a message pump you don't own control flow of.
        PInvoke.PostThreadMessage(_pumpThreadId, PInvoke.WM_QUIT, default, default);
        return _pumpThread is { } t && !t.Join(TimeSpan.FromSeconds(2))
            ? Task.FromException(new TimeoutException("WinEvent pump thread did not exit."))
            : Task.CompletedTask;
    }

    private void PumpLoop()
    {
        _pumpThreadId = PInvoke.GetCurrentThreadId();
        // SetWinEventHook(...) registration happens here, on this thread, per DESIGN.md §3.1.
        _threadReady.Set();

        while (!_stopRequested && PInvoke.GetMessage(out var msg, HWND.Null, 0, 0))
        {
            PInvoke.TranslateMessage(msg);
            PInvoke.DispatchMessage(msg);
        }
    }
}
```

Key points embedded in the skeleton above:

- `StartAsync` spins up `new Thread(...)` — **never** `Task.Run`, `Task.Factory.StartNew`,
  or `TaskCreationOptions.LongRunning`. Thread-pool-backed threads (including
  `LongRunning` tasks) are always MTA and already running by the time you'd want to
  configure them; there is no supported hook to set apartment state or guarantee a stable
  dedicated thread identity on them. `LongRunning` is an acceptable choice only for a pure
  managed channel-drain loop with no message pump / STA COM requirement (see
  `concurrency-performance.md` §2).
- `StopAsync` signals exit cooperatively (`PostThreadMessage(WM_QUIT)` for a `GetMessage`
  loop, or a checked exit flag for a manual poll loop) and `Join`s with a **bounded**
  timeout — never an unbounded `Join()`, which would hang host shutdown if the pump thread
  is stuck.
- Set `Thread.Name` immediately — it is the only practical way to distinguish `bastiond`'s
  several long-lived native-adjacent threads (WinEvent pump, input pump, reconciler/message
  pump) in `dotnet-trace`/ETW captures and a hang dump.

**Rule: keep every `StartAsync` fast.** Hosted services registered via
`IServiceCollection.AddHostedService`/`AddSingleton<IHostedService>` are started **sequentially,
in registration order**, and a slow `StartAsync` blocks every subsequent service from
starting. This directly maps onto `bastiond`'s required ordered startup:

```
1. Event ingest pump   (WinEvent hooks installed, thread confirmed running)
2. Input pump          (RegisterHotKey / WH_KEYBOARD_LL)
3. Monitor topology service
4. IPC server
```

Put unavoidable heavy *synchronous* setup in the constructor (DI already resolves
constructors before any `StartAsync` runs), or — when you need precise ordering guarantees
beyond registration order, e.g. "topology must be warm before the IPC server accepts
connections" — implement `IHostedLifecycleService` instead of layering ad hoc readiness
flags on top of `IHostedService`.

- Reference: https://learn.microsoft.com/dotnet/core/extensions/dependency-injection/overview#scope-scenarios
- Reference: https://learn.microsoft.com/dotnet/api/microsoft.extensions.hosting.ihostedservice

Ordinary async, non-pump hosted services (the topology service, the IPC server) may
continue to use `BackgroundService` — the .NET 10 change affects *where the code runs*, not
whether `BackgroundService` is a usable base class; it is simply the wrong base class for
anything that needs a specific, stable OS thread.

---

## 3. Logging (AOT-safe)

- Use **`[LoggerMessage]` source-generated partial methods** on every hot path: event
  ingest, reconciler tick, IPC dispatch. This generates non-reflective, non-boxing logging
  code, is fully AOT/trim-safe, and directly addresses CA1848.
- Message templates are **constant**, with named `{PascalCase}` placeholders. Never build
  the message via string interpolation or concatenation (CA2254) — that defeats
  structured logging and, under `[LoggerMessage]`, won't even compile as intended.
- Guard genuinely expensive argument evaluation with `logger.IsEnabled(LogLevel.X)` before
  computing it; `[LoggerMessage]`-generated methods already skip formatting when the level
  is disabled, but they still evaluate their arguments eagerly at the call site.

```csharp
internal static partial class Log
{
    [LoggerMessage(EventId = 1001, Level = LogLevel.Debug,
        Message = "Reconciler tick {TickNumber}: {ManagedWindowCount} managed windows, {DriftCount} drifted")]
    public static partial void ReconcilerTick(ILogger logger, long tickNumber, int managedWindowCount, int driftCount);

    [LoggerMessage(EventId = 2001, Level = LogLevel.Warning,
        Message = "WinEvent ingest queue overflowed; dropped event for window {WindowId}, forcing reconcile-now")]
    public static partial void IngestQueueOverflow(ILogger logger, WindowId windowId);
}
```

- Reference: https://learn.microsoft.com/dotnet/core/extensions/logging/high-performance-logging

**No built-in rolling-file provider exists.** Microsoft's own docs are explicit: the
framework "doesn't include a logging provider for writing logs to files." The Worker/Generic
Host default provider set from `Host.CreateApplicationBuilder` is Console, Debug,
EventSource, and EventLog (Windows-only) — no file provider among them.

- Reference: https://learn.microsoft.com/dotnet/core/extensions/logging/providers#built-in-logging-providers

**Plan for `bastiond`:** a minimal, hand-rolled `StreamWriter`-based `ILoggerProvider` for
rolling-file output (reflection-free, trivially AOT-safe — it is plain managed code with no
dynamic dispatch). This is deliberately small in scope: buffered/flushed writes, size- or
day-based roll, no third-party dependency.

- **[uncertain] Serilog's Native AOT compatibility was not found documented on
  learn.microsoft.com** — Microsoft Learn is silent on third-party logging libraries'
  AOT status. **Verify before relying**: check Serilog's own repository/release notes
  directly (https://github.com/serilog/serilog) before ever adopting it in `bastiond`.
  Until that verification happens, prefer the custom `ILoggerProvider` above; it has a
  known, small AOT surface by construction.

---

## 4. Configuration & options (JSONC, AOT-safe)

DESIGN.md §3.9 specifies JSONC with a published JSON Schema, hot-reloaded via directory
watch with debounce, parse-errors-keep-old-config semantics, and `System.Text.Json`
source-gen. The AOT-safe binding path for that:

- Set **`<EnableConfigurationBindingGenerator>true</EnableConfigurationBindingGenerator>`**.
  This is required, not optional, once `PublishAot=true` is set on `Bastion.Daemon`:
  without it, any `ConfigurationBinder.Get<T>()`/`.Bind()` call raises **IL2026** under
  full AOT trim analysis. The generator is interceptor-based — it replaces the reflective
  binder with generated code at the call sites, transparently.
  - Reference: https://learn.microsoft.com/dotnet/core/extensions/configuration-generator
- Use the **`[OptionsValidator]` source generator**, not `ValidateDataAnnotations()`, for
  startup validation of every bound options type. `ValidateDataAnnotations()` is
  reflection-based; `[OptionsValidator]` generates an `IValidateOptions<T>` with no
  reflection, keeping the validation path AOT-safe.
  - Pair it with **`AddOptionsWithValidateOnStart<T>()`** so a malformed JSONC config
    fails `bastiond` at startup rather than surfacing lazily at first use — this matches
    DESIGN.md §3.9's posture that parse errors must be caught decisively at a boundary
    (note: DESIGN.md's *hot-reload* parse-error path deliberately keeps the *old*
    in-memory config and raises a bar notification instead of crashing a running daemon;
    `AddOptionsWithValidateOnStart` governs the *startup* boundary specifically — the very
    first load has no "old config" to fall back to, so failing fast there is correct and
    consistent, not contradictory).
  - Reference: https://learn.microsoft.com/dotnet/core/extensions/options

```csharp
public sealed class LayoutOptions
{
    [Range(0, 64)]
    public int GapPx { get; init; } = 8;

    public required string DefaultEngine { get; init; }
}

[OptionsValidator]
public sealed partial class LayoutOptionsValidator : IValidateOptions<LayoutOptions>;

builder.Services.AddOptions<LayoutOptions>()
    .Bind(builder.Configuration.GetSection("layout"))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<LayoutOptions>, LayoutOptionsValidator>();
```

- System.Text.Json source-gen for the IPC schema and config DTOs follows directly from
  DESIGN.md §10's stack decision. The concrete `JsonSerializerContext` layout, polymorphic
  IPC command shape, JSONC parsing/hot-reload mechanics, and published JSON Schema generation
  are documented in full in `docs/engineering/json-ipc-config.md` — that doc, not this
  section, is the source of truth for that surface.

---

## 5. Observed-state snapshots (data-structure policy)

DESIGN.md §3.4 has the Reconciler rebuild an observed-state snapshot every 5-second
heartbeat tick (`EnumWindows` + per-window reads). The documented fit for that shape of
data is **`ImmutableArray<T>`**, rebuilt fresh each tick via `ImmutableArray.Create(...)`
or an `ImmutableArray<T>.Builder`:

- `System.Collections.Immutable`'s own guidance recommends `ImmutableArray<T>` when
  "updating the data is rare or the number of elements is quite small (<16 items)," when
  fast indexed iteration in performance-critical code matters, and for many short-lived
  instances that can't afford a tree-backed structure — an exact match for a per-tick
  window-state snapshot.
  - Reference: https://learn.microsoft.com/dotnet/api/system.collections.immutable.immutablearray-1

**Never `FrozenDictionary`/`FrozenSet` for per-tick data.** Frozen collections are
documented for data "created once ... used throughout the remainder of the life of the
application" — i.e., a high one-time construction cost amortized over many reads. Rebuilding
one every 5 seconds pays that construction cost repeatedly for none of the amortized
benefit. **Reserve Frozen collections for genuinely static, build-once startup data**: the
shipped community-rules blocklist (DESIGN.md §9), class-name blocklists (`Progman`/
`WorkerW`/`Shell_TrayWnd`, DESIGN.md §3.3).

**Footguns to guard against explicitly:**

- `default(ImmutableArray<T>)` (and `new ImmutableArray<T>()`) wraps a **null** backing
  array. Touching `.Length` or any member on it null-derefs at runtime with no
  compile-time warning. Always construct via `.Empty` or `.Create(...)`/a `Builder` —
  never let a diffing routine produce a default-initialized snapshot field.
  - Reference: https://learn.microsoft.com/visualstudio/extensibility/roslyn-analyzers-and-code-aware-library-for-immutablearrays
- `ImmutableArray<T>.Add` is **O(n)** (copies the whole backing array), versus
  `ImmutableList<T>`'s O(log n). This is irrelevant when the snapshot is replaced wholesale
  every tick via `Create`/`Builder` — but if any part of Bastion's state model needs
  frequent one-at-a-time incremental mutation over a long lifetime (rather than full-
  snapshot replacement), use `ImmutableList<T>` or a plain `List<T>` converted to
  `ImmutableArray<T>` only at the snapshot boundary.

```csharp
public readonly record struct ObservedWindow(WindowId Id, RECT FrameBounds, bool IsIconic, bool IsZoomed);

// Every tick: rebuild wholesale, never mutate in place, never leave a field at default.
var builder = ImmutableArray.CreateBuilder<ObservedWindow>(initialCapacity: previousCount);
foreach (var hwnd in EnumerateManagedWindows())
{
    builder.Add(ReadObservedWindow(hwnd));
}
ObservedState = builder.ToImmutable(); // ImmutableArray<ObservedWindow>, never default
```

---

## 6. Must-not-die exception policy

`bastiond` must never strand a window (DESIGN.md §1, principle 3), which means the process
itself must be extremely hard to kill via an unhandled exception. Three concrete .NET
behaviors shape this policy:

**`TaskScheduler.UnobservedTaskException` is diagnostics-only on modern .NET — it is not a
safety net and does not crash the process.** This differs from legacy .NET Framework 4.0
default behavior. Do not treat it as your error-recovery mechanism. Every genuinely
fire-and-forget `Task` in the daemon (reconciler-triggered work, IPC response tasks) must
have an **explicit owner/tracker** that observes its result, logs failure, and can trigger
distrust escalation (DESIGN.md §3.4) — never a dropped `Task` reference relying on this
event to notice trouble.

- **[uncertain, verify before relying]** whether the legacy opt-in
  `<ThrowUnobservedTaskExceptions>` app-config-era knob has any bearing under .NET 10's
  Generic Host could not be conclusively re-verified — don't depend on it either way.

**`AppDomain.UnhandledException` fires for an unhandled exception on any thread** —
including the WinEvent pump thread and the keyboard-hook thread — **but is never raised for
`StackOverflowException`**, and `[HandleProcessCorruptedStateExceptionsAttribute]`
explicitly has **no effect** for that specific exception. A `StackOverflowException` "cannot
be caught with a try/catch block, and the corresponding process is terminated by default" —
by CLR design, since no code, including a handler, can be guaranteed to run safely on an
overflowed stack.

- Reference: https://learn.microsoft.com/dotnet/api/system.appdomain.unhandledexception
- Reference: https://learn.microsoft.com/dotnet/api/system.stackoverflowexception

**Practical consequence: exception handling cannot contain runaway recursion.** Every
recursive traversal in `bastiond` — layout tree solving/diffing (`Bastion.Layout`), window-
tree reconciliation diffing (`Bastion.Core`) — must carry an **explicit depth or iteration
cap**, enforced structurally (a counter parameter, a `checked` bound before recursing), not
guarded by a `try`/`catch`. A stack overflow there kills `bastiond.exe` unconditionally,
regardless of how `AppDomain.UnhandledException` is wired.

```csharp
private const int MaxLayoutTreeDepth = 64; // generous vs. any real user config; a hard backstop

private static RECT[] Solve(SplitNode node, RECT bounds, int depth)
{
    if (depth > MaxLayoutTreeDepth)
    {
        throw new LayoutTreeTooDeepException(depth); // fails the tick loudly; does not overflow the stack
    }
    // ... recurse with depth + 1 ...
}
```

(Hook-callback catch-all policy — the `try`/catch-log-swallow requirement inside every
`[UnmanagedCallersOnly]` `WinEventProc`/`LowLevelKeyboardProc` body — is a *different* rule
about the native call boundary, not the managed-exception policy above. See
`docs/engineering/interop.md` §3.)

---

## 7. Single-instance enforcement

Enforce single-instance via a named `Mutex`, but the naming discipline matters:

```csharp
string mutexName = $@"Local\Bastion.Daemon.{WindowsIdentity.GetCurrent().User!.Value}";
using var mutex = new Mutex(initiallyOwned: true, name: mutexName, createdNew: out bool createdNew);
if (!createdNew)
{
    // Another bastiond instance already owns the mutex for this user/session — exit.
    return;
}
```

- **Scope the name to the interactive user/session** — a `Local\` prefix and/or a name
  incorporating the user SID, as above — never a fixed, predictable, global string like
  `"BastionDaemon"`. Microsoft's own remarks on `CreateMutex` warn explicitly that "a
  malicious user can create this mutex before you do and prevent your application from
  starting" when the name is fixed and predictable — this is a documented local-DoS
  vector, not a theoretical one.
  - Reference: https://learn.microsoft.com/windows/win32/api/synchapi/nf-synchapi-createmutexw#remarks
- **No `MutexSecurity`/`GetAccessControl`/`SetAccessControl` on .NET Core/.NET 5+.** That
  ACL API surface is documented as available only on .NET Framework. Do not attempt to
  lock down the mutex object itself on .NET 10 — the security boundary for `bastiond` is
  the named pipe's `PipeSecurity` ACL (DESIGN.md §3.9), not the mutex.
  - Reference: https://learn.microsoft.com/dotnet/standard/threading/mutexes#local-and-system-mutexes
- **`bastionc`/`bastion-bar`'s daemon-presence probe uses
  `Mutex.TryOpenExisting(name, out mutex)`** — a non-throwing existence check —
  **never** `Mutex.OpenExisting(...)` wrapped in a `try`/`catch(WaitHandleCannotBeOpenedException)`.
  (A `TryOpenExisting(string, MutexRights, out Mutex)` overload exists, but it is
  `netframework-4.5`–`netframework-4.8.1` only and unavailable on .NET 10; the new
  `.NET 10` overload, `TryOpenExisting(string, NamedWaitHandleOptions, out Mutex?)`, takes
  an `options` parameter that controls current-user/current-session scope, not ACL rights.)
  `TryOpenExisting` returns `false` for a missing or inaccessible mutex instead of throwing,
  which is the appropriate shape for a CLI/tray-app pre-flight check that runs on every
  invocation.
  - Reference: https://learn.microsoft.com/dotnet/api/system.threading.mutex.tryopenexisting

```csharp
// bastionc.exe / bastion-bar.exe presence probe — cheap, non-throwing, run on every invocation.
string mutexName = $@"Local\Bastion.Daemon.{WindowsIdentity.GetCurrent().User!.Value}";
bool daemonRunning = Mutex.TryOpenExisting(mutexName, out var existing);
existing?.Dispose();
```

---

## Forbidden

- Do not use `Host.CreateDefaultBuilder()` or any legacy callback-based `IHostBuilder` for
  `bastiond` — use `Host.CreateApplicationBuilder(args)`.
- Do not implement the WinEvent ingest pump or the keyboard-hook input pump as a
  `BackgroundService` — since .NET 10, all of `ExecuteAsync` (including pre-`await`
  synchronous code) runs on a thread-pool thread, which is the wrong lifetime/identity
  model for a Win32 message pump that must own one specific OS thread for the process's
  entire life. Use a raw `IHostedService` owning a dedicated `Thread` instead.
- Do not spin up a pump thread via `Task.Run` or `TaskCreationOptions.LongRunning` —
  thread-pool-backed threads (including `LongRunning`) are always MTA and already running;
  there is no supported way to control their apartment state or guarantee dedicated
  identity.
- Do not perform slow synchronous work inside `IHostedService.StartAsync` — hosted services
  start sequentially in registration order and a slow one blocks every later service. Move
  heavy setup into the constructor, or use `IHostedLifecycleService` for explicit ordering.
- Do not build log messages via string interpolation or concatenation (CA2254) — use
  `[LoggerMessage]` with constant templates and named `{PascalCase}` placeholders.
- Do not assume a built-in rolling-file `ILoggerProvider` exists — it does not; use a
  hand-rolled `StreamWriter`-based provider or an independently AOT-verified third-party
  package.
- Do not adopt Serilog in `bastiond` on the assumption it is AOT-compatible — this is
  unverified against Microsoft Learn; check Serilog's own release notes first.
- Do not call `ConfigurationBinder.Get<T>()`/`.Bind()` under `PublishAot=true` without
  `EnableConfigurationBindingGenerator=true` — it raises IL2026.
- Do not use `ValidateDataAnnotations()` for startup options validation — it is
  reflection-based and not AOT-safe; use the `[OptionsValidator]` source generator.
- Do not rebuild a `FrozenDictionary`/`FrozenSet` on every 5-second reconciliation tick —
  use `ImmutableArray<T>` for the observed-state snapshot; reserve Frozen collections for
  genuinely static, build-once data (the shipped rules blocklist, class-name lists).
- Do not default-construct or leave an `ImmutableArray<T>` at `default` and then touch a
  member on it — it wraps a null backing array and null-derefs at runtime; always use
  `.Empty` or `.Create(...)`/a `Builder`.
- Do not use `ImmutableArray<T>.Add` for state that is incrementally mutated element-by-
  element over a long lifetime — it is O(n) per add; use `ImmutableList<T>` or a `List<T>`
  converted at snapshot boundaries instead.
- Do not treat `TaskScheduler.UnobservedTaskException` as an error-recovery mechanism — it
  is diagnostics-only and does not terminate the process by default. Give every
  fire-and-forget `Task` an explicit owner that logs and can trigger distrust escalation.
- Do not assume `[HandleProcessCorruptedStateExceptionsAttribute]` or any global handler can
  catch and recover from a `StackOverflowException` — it explicitly has no effect for that
  exception, and the process is unconditionally terminated. Guard every recursive traversal
  (layout solving, window-tree diffing) with an explicit depth/iteration cap instead.
- Do not pick a fixed, predictable, unscoped named `Mutex` string for single-instance
  enforcement — it is a documented local-DoS vector (another process can pre-create it and
  permanently block `bastiond` from starting). Scope the name to the user/session.
- Do not call `MutexSecurity`/`GetAccessControl`/`SetAccessControl` on a named `Mutex` under
  .NET 10 — that API surface exists only on .NET Framework.
- Do not use `Mutex.OpenExisting(...)` wrapped in `try`/`catch` for a daemon-presence probe
  — use the non-throwing `Mutex.TryOpenExisting(...)` instead.
