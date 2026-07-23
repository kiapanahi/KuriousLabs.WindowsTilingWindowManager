# Win32 & COM Interop under NativeAOT

**Scope.** This is the single authority for `Bastion.Win32` — the adapter ring
described in DESIGN.md §3/§10. It covers CsWin32 configuration, handle-type
modeling, `[UnmanagedCallersOnly]` hook callbacks, function-pointer callback
declarations, and source-generated COM (`[GeneratedComInterface]`). Every rule
here exists to keep the adapter ring compilable and correct under
`PublishAot=true` (see `quality-gates.md` for where that property is actually
set). If you are about to write a P/Invoke signature, a hook callback, or a
COM interface declaration in `Bastion.Win32`, the rule is here — do not
improvise from memory of classic (`[DllImport]`/`[ComImport]`) interop, which
DESIGN.md §10 explicitly rules out.

Sibling docs, for anything not owned here:
- `concurrency-performance.md` — bounded-channel configuration, dedicated
  vs. pool threads, STA thread *construction* mechanics, GC/allocation policy.
- `daemon-architecture.md` — hosted-service lifetime, AOT-safe logging/config,
  process-level resilience.
- `testing.md` — Tier 2 fake-adapter replay seam, Tier 5 behavior canaries
  for the undocumented-but-relied-on behaviors flagged throughout this doc.
- `quality-gates.md` — `IsAotCompatible`/`PublishAot` placement per project,
  `dotnet publish` validation gate, BannedApiAnalyzers wiring.

---

## 1. The CsWin32 configuration contract (verbatim, do not deviate)

Bastion needs COM **interfaces** (`IVirtualDesktopManager`, `ITaskbarList`
family, `ITaskService`, `IPropertyStore`), not just P/Invokes. CsWin32
documents two distinct AOT-safe modes, and only one produces interfaces.

**`Bastion.Win32.csproj`:**

```xml
<PropertyGroup>
  <CsWin32RunAsBuildTask>true</CsWin32RunAsBuildTask>
  <DisableRuntimeMarshalling>true</DisableRuntimeMarshalling>
</PropertyGroup>
```

**`NativeMethods.json`:**

```jsonc
{
  "$schema": "https://raw.githubusercontent.com/microsoft/CsWin32/main/src/Microsoft.Windows.CsWin32/settings.schema.json",
  "allowMarshaling": false,
  "comInterop": {
    "useComSourceGenerators": true
  },
  "useSafeHandles": true
}
```

Rules, in the order you'll trip over them:

- **`CsWin32RunAsBuildTask=true` is necessary but not sufficient for COM
  interfaces.** It switches CsWin32 from a Roslyn source generator to an
  MSBuild task, which lets its output be post-processed by the
  `[LibraryImport]`/`[GeneratedComInterface]` source generators. Without it,
  CsWin32 output can't chain into those generators at all.
- **`comInterop.useComSourceGenerators=true` must be set explicitly.** The
  schema documents it as "only has an effect with
  `<CsWin32RunAsBuildTask>true</CsWin32RunAsBuildTask>`" and defaults to
  `false`. Setting `CsWin32RunAsBuildTask` alone and expecting COM source
  generation to "just turn on" is the single most common misconfiguration —
  it silently doesn't happen.
- **`allowMarshaling: false` alone is not the same commitment.** It switches
  CsWin32's *own* projections off the runtime COM marshaler, which is
  necessary groundwork, but by itself it is documented to emit COM **structs**
  (vtable-shaped value types), not `[GeneratedComInterface]`-attributed
  interfaces. DESIGN.md §10 commits to the interface-shaped surface
  (`IVirtualDesktopManager`, `ITaskbarList`, etc.), so the load-bearing
  combination is all three settings above together:
  `CsWin32RunAsBuildTask=true` + `DisableRuntimeMarshalling=true` (csproj) +
  `comInterop.useComSourceGenerators=true` (NativeMethods.json). Treat
  `allowMarshaling=false` as a precondition for that combination, not a
  substitute for it.
- **`useSafeHandles=true` stays at its default** for genuinely closeable
  kernel handles requested via `NativeMethods.txt` (file, process, thread,
  registry, event handles, etc.).
  **Correction (verified against the actual generated output while implementing
  issue #1):** this setting is *not* inert for every HWND-family API the way
  the previous wording here claimed. For at least `SetWinEventHook`, CsWin32
  generates a *second*, `SafeHandle`-wrapping overload
  (`UnhookWinEventSafeHandle`-returning) alongside the original raw
  `HWINEVENTHOOK`-returning one — both exist simultaneously; `useSafeHandles`
  adds the wrapped overload, it does not replace the raw one. §2's rule is
  therefore about *which overload to call*, not about `useSafeHandles` having
  zero effect: always call the raw-handle-returning overload for
  `HWND`/`HHOOK`/`HWINEVENTHOOK`-family APIs, never the `SafeHandle`-wrapping
  one CsWin32 also emits, for the reasons in §2 (no correct universal
  `ReleaseHandle` for a resource Bastion doesn't own the lifetime of). Verify
  this per-API on the actual generated partial before assuming either
  direction — do not assume every HWND-family API gets a wrapped overload, and
  do not assume none do.
- **Record the final resolved settings here** once `Bastion.Win32.csproj` and
  `NativeMethods.json` exist in the repo, including any deviation and why.

### 1.1 Architecture-specific requests

NativeAOT publish is always RID-specific (`win-x64` / `win-arm64`, never
`AnyCPU`) — `dotnet publish -r <RID>` is mandatory for AOT anyway, so this is
low-risk for Bastion in practice, but review it once:

- A wildcard `NativeMethods.txt` entry (e.g. `User32.*`) **silently drops**
  members that are only available for a specific CPU architecture — no
  warning is emitted for the omitted subset.
- An **explicit** request for an arch-specific API on an `AnyCPU` compilation
  produces build warning `PInvoke005` ("This API is only available when
  targeting a specific CPU architecture. AnyCPU cannot generate this API.").
- Action: when adding a wildcard request that might reach arch-specific
  structs (low-level memory/process APIs are the usual suspects), diff the
  generated output against what you expected rather than assuming full
  coverage.

### 1.2 `DisableRuntimeMarshalling` consequences

Setting `DisableRuntimeMarshallingAttribute` at the assembly level (which
`<DisableRuntimeMarshalling>true</DisableRuntimeMarshalling>` emits) changes
the blittability rules for every P/Invoke, delegate, and function pointer in
`Bastion.Win32`:

- All value types that are C# `unmanaged` types (no reference-type fields,
  recursively) are blittable; nothing else is.
- Value types with any field marked `[StructLayout(LayoutKind.Auto)]` are
  disallowed from interop.
- **All reference types are disallowed in interop signatures.** Handle
  wrappers are blittable structs (`readonly struct HWND(nint Value)`-shaped),
  never classes.
- `SetLastError`, `varargs`, and `LCIDConversionAttribute` support are all
  disabled — see §6.

---

## 2. Handle modeling: HWND is not a kernel handle

**Never wrap `HWND`, `HHOOK`, or `HWINEVENTHOOK` in a `SafeHandle`.** The
documented `CloseHandle` object list (access tokens, files, mutexes,
processes, threads, events, etc.) conspicuously omits windows — a window is
destroyed via `DestroyWindow`, owned by whichever thread/process created it,
not released via `CloseHandle`. Hooks are released via
`UnhookWindowsHookExW`/`UnhookWinEvent`, again not `CloseHandle`. There is no
correct universal `ReleaseHandle` implementation for these types:

- Either the wrapper has nothing to legitimately release (the window/hook
  belongs to whatever owns it), or
- Worse, a finalizer fires and calls `DestroyWindow`/`UnhookWinEvent` on a
  resource Bastion never owned in the first place — a crash-on-GC bug that
  only manifests under memory pressure.

`SafeHandle` remains the right tool for genuinely closeable handles requested
via `NativeMethods.txt` elsewhere in `Bastion.Win32` (registry, process,
thread, file handles) — `useSafeHandles=true` (§1) legitimately governs those.
It does not govern HWND-family types.

**Consequence for the Core boundary:** HWND stays a raw blittable value
(`HWND`/`HHOOK`/`HWINEVENTHOOK` structs, effectively `nint` wrappers) entirely
inside `Bastion.Win32`. Only the opaque `WindowId` (DESIGN.md §3, §10) crosses
into `Bastion.Core`. HWND recycling, PID association, and first-seen
timestamps are adapter concerns; state-bearing code never sees an HWND.

---

## 3. Hook callbacks: WinEventProc / LowLevelKeyboardProc

### 3.1 Declaration shape

```csharp
[UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
private static void OnWinEvent(
    HWINEVENTHOOK hWinEventHook,
    uint eventId,
    HWND hwnd,
    int idObject,
    int idChild,
    uint idEventThread,
    uint dwmsEventTime)
{
    try
    {
        // filter, normalize (GetAncestor(GA_ROOT)), enqueue — DESIGN.md §3.1
    }
    catch (Exception ex)
    {
        // log and swallow — never rethrow, see §3.3
        HookDiagnostics.LogCallbackFault(ex);
    }
}
```

Requirements, all enforced by the compiler for CS8893–CS8896 plus one that
isn't:

- **Static, non-generic, ordinary method** — `UnmanagedCallersOnly` cannot be
  applied to instance, abstract, virtual, or generic methods, and the method
  cannot be declared inside a generic type (CS8895/CS8896).
- **Fully blittable parameters and return type** (CS8894) — no `string`,
  `object`, delegate, or managed reference type anywhere in the signature.
- **Exact calling convention match** — `CallConvs = [typeof(CallConvStdcall)]`
  for `WINAPI`/`__stdcall` callbacks (`WinEventProc`, `HOOKPROC`). Confirm the
  exact generated parameter list from the CsWin32 output for the specific
  `SetWinEventHook`/`SetWindowsHookExW` overload in use — do not hand-copy a
  signature from documentation prose; parameter ordering and types can differ
  subtly from what CsWin32 emits.
- **Mandatory catch-all around the entire body** (not just enforced by the
  compiler — this is a hard project rule, see §3.3).

### 3.2 Context passing: GCHandle registry, never closures

`[UnmanagedCallersOnly]` methods cannot be instance methods or lambdas, so
there is no closure to capture managed state in. `SetWinEventHook` has no
user-data parameter; `SetWindowsHookExW`'s `HOOKPROC` likewise has no
`lpParam`. Pattern:

1. At hook-registration time: `GCHandle.Alloc(managedState, GCHandleType.Normal)`.
2. Stash `GCHandle.ToIntPtr(handle)` in a static registry keyed by the
   returned hook handle (`HWINEVENTHOOK`/`HHOOK`) — there is nowhere else to
   put it, since neither registration API accepts user data.
3. Inside the static callback: `GCHandle.FromIntPtr(ptr).Target` to recover
   the managed object.
4. **Free the `GCHandle` only after a successful unhook**
   (`UnhookWinEvent`/`UnhookWindowsHookExW` returned success), in a matching
   `Dispose` path. Never let the handle outlive the hook registration, and
   never free it speculatively before unhook completes — a callback could
   still be in flight.

### 3.3 The catch-all is mandatory, not defensive style

An exception that escapes an `[UnmanagedCallersOnly]` method, or otherwise
propagates across the managed/native boundary, is undefined behavior. Windows'
hook-dispatch code (the message loop, the hook chain) is not exception-safe;
under NativeAOT there is no guarantee of a catchable unwind back through it.
**[uncertain — verify before relying]** the exact fail-fast semantics on
Windows (does it crash the process immediately, corrupt the hook chain, or
something else) were not independently confirmed this session (see §7) — that
uncertainty is precisely why the catch-all is a hard rule rather than a
"nice to have": wrap the **entire** callback body in try/catch, log, and
swallow. Never rethrow.

### 3.4 Blittability scope is the native-facing signature only

The blittability constraint applies to the `[UnmanagedCallersOnly]` method's
parameters and return type — nothing else. Once inside the method body,
packing the event into a `readonly record struct` for the channel is ordinary
managed code with no interop restrictions. Do not over-constrain the internal
event/intent type to satisfy blittability rules that don't apply to it; that
type's shape is owned by `concurrency-performance.md`, not this document.

### 3.5 Function-pointer callback types

Declare native callback fields/parameters using C# function-pointer syntax,
matching whatever CsWin32 generates for the specific `SetWinEventHook`
parameter — confirm by inspecting the generated partial, don't guess:

```csharp
delegate* unmanaged[Stdcall]<HWINEVENTHOOK, uint, HWND, int, int, uint, uint, void> callback = &OnWinEvent;
```

`delegate* unmanaged[CallingConvention]<...>` values are themselves blittable
function pointers with no marshaling thunk and no GC-tracked delegate object
— they sidestep both the allocation of a `Marshal.GetFunctionPointerForDelegate`
call and the classic "delegate got collected while native code still held the
callback pointer" bug class that affects the `static readonly` delegate
keep-alive pattern.

---

## 4. Source-generated COM (`[GeneratedComInterface]`) rules

### 4.1 Declaring and consuming an interface

```csharp
[GeneratedComInterface]
[Guid("...")]
internal partial interface IVirtualDesktopManager
{
    [PreserveSig]
    int MoveWindowToDesktop(HWND topLevelWindow, ref Guid desktopId);

    // ...
}
```

- Every consumed interface (`IVirtualDesktopManager`, `ITaskbarList`/`2`/`3`/`4`,
  `ITaskService`, `IPropertyStore`) gets `[GeneratedComInterface]` + `[Guid]`
  on a `partial` interface with `internal` or `public` visibility.
- Implementations (managed sinks exposed *to* native code, e.g. a callback
  interface) get `[GeneratedComClass]` on a `partial` class.
- **CsWin32 friction:** CsWin32-generated nested `.Interface` types are not
  `partial`. Expect to hand-declare the specific shell interfaces Bastion
  consumes rather than relying on CsWin32's own projection for them.

### 4.2 Activation

- **Correction (verified during GitHub issue #3's implementation):** `StrategyBasedComWrappers`
  has **no static `Instance` member** — confirmed against
  https://learn.microsoft.com/dotnet/api/system.runtime.interopservices.marshalling.strategybasedcomwrappers,
  whose full member list is exactly one public parameterless constructor plus instance
  methods/properties. An earlier revision of this doc incorrectly referenced
  `StrategyBasedComWrappers.Instance` — construct and reuse your own instance instead (e.g. a
  `private static readonly StrategyBasedComWrappers` field per consumer, as
  `src/Bastion.Win32/PropertyStoreAumidReader.cs` does), never a nonexistent singleton accessor.
- Activate via `CoCreateInstance` (CsWin32's `PInvoke.CoCreateInstance<T>`, or
  a hand-written P/Invoke) followed by your own `StrategyBasedComWrappers` instance's
  `GetOrCreateObjectForComInstance(pUnknown, CreateObjectFlags.Unwrap)`
  to obtain the managed interface.
- **`new SomeCoClass()` activation syntax is unsupported** with source-generated
  COM — always go through the `CoCreateInstance` + `ComWrappers` path.
- `ComWrappers.GetOrCreateObjectForComInstance` already performs identity
  caching per underlying `IUnknown` **and per `ComWrappers` instance it was called
  on** — don't build a second cache on top of it, but do plan for exactly one
  re-creation on the documented signal (e.g. `TaskbarCreated` broadcast after an
  Explorer restart, DESIGN.md §9), never speculatively on a timer or on every
  call. For a genuinely per-call target (no long-lived object to cache — e.g. a
  distinct window's `IPropertyStore` on every call), pass
  `CreateObjectFlags.UniqueInstance` instead and deterministically `Dispose()`
  the returned wrapper (it implements `IDisposable` only when constructed with
  this flag) immediately after use rather than leaving it for the GC.
- To expose a managed sink to native code (e.g. a progress/notification
  callback), use `GetOrCreateComInterfaceForObject` instead.

### 4.3 Derived interfaces

```csharp
[GeneratedComInterface, Guid("...")]
internal partial interface ITaskbarList { /* ... */ }

[GeneratedComInterface, Guid("...")]
internal partial interface ITaskbarList2 : ITaskbarList { /* ITaskbarList2-only members */ }

[GeneratedComInterface, Guid("...")]
internal partial interface ITaskbarList3 : ITaskbarList2 { /* ITaskbarList3-only members */ }
```

- Plain C# interface inheritance — the generator lays out the vtable slots to
  match. **Do not redeclare inherited methods and do not shadow with `new`.**
  This is the opposite of classic `[ComImport]` interop, which required
  redeclaring every base method.
- Exactly **one** `[GeneratedComInterface]`-attributed base interface per
  derived interface — multiple attributed bases are unsupported.
- Cross-assembly base interfaces are unsupported in .NET 8 but supported in
  .NET 9+, provided the base and derived interfaces target the same TFM and
  neither shadows members. Rebuild after any change to generated virtual
  method offsets in a cross-assembly base — stale offsets are not detected
  automatically.

### 4.4 `[PreserveSig]`

Without `[PreserveSig]`, the generator hides the HRESULT, converts the last
`out` parameter into the C# return value, and throws on any failing HRESULT.
Use `[PreserveSig]` when a "failure" HRESULT is an expected branch, not an
exceptional condition — the canonical Bastion case:

```csharp
[PreserveSig]
int MoveWindowToDesktop(HWND topLevelWindow, ref Guid desktopId);
```

`IVirtualDesktopManager.MoveWindowToDesktop` is reported — not officially
documented; see DESIGN.md §2's attribution to an archived WinSDK blog and
field reports — to return `E_ACCESSDENIED` for foreign HWNDs. That's a
routine, expected result Bastion must branch on, not catch as a
`COMException`. With
`[PreserveSig]`, the method returns the raw `int` HRESULT and **you own
`FAILED()`/`SUCCEEDED()` checking** — never discard the returned value. Leave
`[PreserveSig]` off everywhere else so genuine failures surface as normal
exceptions.

### 4.5 String marshalling

```csharp
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("...")]
internal partial interface ISomeShellInterface { /* string-taking methods */ }
```

- `GeneratedComInterfaceAttribute.StringMarshalling` (or
  `StringMarshallingCustomType`) applies to **every** `string`-typed
  parameter and return on the interface unless a parameter has its own
  `[MarshalUsing]`/`[MarshalAs]` override.
- If interface B derives from interface A, both must agree on these settings.
- Windows shell COM is UTF-16-native — `StringMarshalling.Utf16` is the
  default correct choice for a plain null-terminated UTF-16 string. **Verify
  per-method whether a given string parameter is actually a BSTR** (needs
  `SysAllocString`/`SysFreeString` ownership semantics) before picking the
  built-in UTF-16 marshaller — a BSTR marshalled as plain UTF-16 is a
  use-after-free/leak bug waiting to happen. Use `StringMarshallingCustomType`
  with a BSTR-aware marshaller where the method contract calls for BSTR.

### 4.6 Marshalling defaults differ from built-in COM

- In built-in COM, all types are implicitly `[In]` except arrays of blittable
  elements, which are implicitly `[In, Out]`. **In source-generated COM,
  everything — including blittable arrays — is `[In]`-only.**
- `[In]`/`[Out]` attributes are accepted **only on arrays.** For any
  non-array parameter that needs `[Out]` or `[In, Out]` semantics, use C#'s
  `in`/`out`/`ref` parameter modifiers instead. `MoveWindowToDesktop`'s
  `ref Guid desktopId` (§4.1, §4.4) is exactly this case — the `ref` modifier
  is what expresses the in/out contract, not an attribute.

### 4.7 Explicitly unsupported by the generator

Do not attempt these against `[GeneratedComInterface]`:

- IDispatch-based interfaces.
- IInspectable-based interfaces.
- COM properties.
- COM events.

Not directly relevant to `IVirtualDesktopManager`/`ITaskbarList`/`IPropertyStore`
(all plain vtable interfaces), but relevant if Bastion ever touches a Shell
extension surface (e.g. `IShellItem`/`IPropertyStore` companion interfaces)
that expresses something as a COM property.

---

## 5. Apartment discipline — the biggest source-gen COM gap

**The COM source generator has no apartment-affinity support at all.** Every
COM object is assumed free-threaded; a call goes straight through the vtable
pointer from whatever thread issues it — there is no cross-apartment
marshaling/proxying the way built-in `[ComImport]` interop provided
automatically. A violation gets you `RPC_E_WRONG_THREAD` at best, silent
corruption at worst.

**Rule:** exactly **one** dedicated STA thread — initialized with
`CoInitializeEx(COINIT_APARTMENTTHREADED)`, owning Bastion's own HWNDs
(bar window, hidden owner window) and pumping its own message loop — performs
**every** shell-COM call: the `CoCreateInstance`, and every subsequent method
call, for `IVirtualDesktopManager`, `ITaskbarList`/`ITaskbarList3`, and
`IPropertyStore`. Never from `Task.Run`, the thread pool, or any other thread.
Thread-construction mechanics (how that STA thread is started and how work is
marshaled onto it) belong to `concurrency-performance.md` §2 — this section
owns only the COM-side rule that the thread must exist and must be the sole
caller.

- `ITaskbarList` additionally: create and use it only after receiving the
  registered `TaskbarButtonCreated` message on that same thread, per
  Microsoft's own sample pattern.
- **[strong — status: convention-supported, not an explicit Microsoft Learn
  statement]** STA-affinity of these specific shell interfaces is the
  consistent convention across Microsoft samples and every mature WM
  (komorebi, GlazeWM, Whim), not something stated as a hard requirement on
  the interfaces' own doc pages. Flagged in §7; verify empirically on an STA
  thread before shipping (Tier 5 canary).
- **Escape hatch, if apartment marshaling is ever genuinely required** (e.g.
  a future `IGlobalInterfaceTable`-based sharing need): implement a custom
  `ComWrappers`/`StrategyBasedComWrappers` strategy
  (`IIUnknownInterfaceDetailsStrategy`/`IIUnknownStrategy`). This is the
  documented, forward-compatible mechanism. Never fall back to
  `Marshal`-based interop or `[ComImport]` to get apartment behavior back —
  for the four interfaces currently in scope, single-threaded call discipline
  is simpler and sufficient; only reach for a custom strategy if that
  constraint becomes untenable.

---

## 6. Error handling at the P/Invoke boundary

**[uncertain — verify before relying]** The interaction of `SetLastError`
semantics with CsWin32-generated wrappers under `DisableRuntimeMarshalling`
was not independently re-verified against a real generated sample this
session. What's confirmed generally: `DisableRuntimeMarshallingAttribute`
documents that it disables built-in `SetLastError` support for P/Invokes
outright (§1.2), and `LibraryImportAttribute` carries its own `SetLastError`
property that must be set explicitly per-method to opt back in to
`Marshal.GetLastWin32Error()` capturing the right value. Before writing
error-handling code in `Bastion.Win32` that reads `GetLastError()` after a
CsWin32-generated call (e.g. the UIPI detection path in DESIGN.md §3.6, which
depends on `GetLastError() == ERROR_ACCESS_DENIED` after a failed
`SetWindowPos`/`ShowWindow`): open the actual generated partial method for
that specific API and confirm whether `[LibraryImport(SetLastError = true)]`
is present, rather than assuming DllImport-era `[DllImport(SetLastError = true)]`
behavior carries over unchanged.

---

## 7. Uncertain — never assert as fact in code comments or docs

These are named explicitly so nobody upgrades them to "documented fact" in a
code comment, a user-facing message, or a future PR description. Each is a
canary-test candidate (`testing.md` Tier 5) before it's load-bearing:

1. **`[UnmanagedCallersOnly]` escape = fail-fast on Windows.** The exact
   failure mode (immediate process termination vs. corrupted hook chain vs.
   something else) was not independently confirmed against Microsoft Learn
   this session. Mitigation is unconditional regardless of the exact
   semantics: the mandatory catch-all (§3.3).
2. **`GeneratedComInterface`/`ComWrappers` apartment specifics beyond "no
   support."** Confirmed: free-threaded assumption, no marshaling. Not
   confirmed: any finer-grained behavior beyond that blanket statement.
3. **An explicit documented STA requirement for `ITaskbarList`/`ITaskbarList3`.**
   Convention-supported (§5), not stated as a hard requirement on the
   interface's own doc page — verify empirically on an STA thread before
   shipping.
4. **`SetLastError` nuances under `DisableRuntimeMarshalling` + CsWin32
   wrappers** (§6) — spot-check the generated code before relying on it.

---

## Forbidden

The following are banned in `Bastion.Win32`. Each maps to a specific failure
mode, not a style preference:

- **`Marshal.GetDelegateForFunctionPointer` / `Marshal.GetFunctionPointerForDelegate`.**
  The commonly-used Type/Delegate-based overloads of both are
  `[RequiresDynamicCode]`; the generic `<TDelegate>` overloads avoid that
  attribute but are documented as unsupported for interop scenarios (generics
  not supported) — avoid all overloads of both APIs regardless. The API docs
  recommend function pointers + `[UnmanagedCallersOnly]` instead ("more
  efficient, easier to use correctly, and supported in all environments").
  Fails AOT publish analysis or throws at runtime. Use
  `delegate* unmanaged[...]<...>` (§3.5) and static `[UnmanagedCallersOnly]`
  methods (§3.1) instead.
- **Letting an exception escape a `WinEventProc`/`LowLevelKeyboardProc`
  `[UnmanagedCallersOnly]` method.** Undefined behavior across the native
  boundary; wrap the entire body in try/catch-all (§3.3).
- **`SafeHandle`-wrapping `HWND`/`HHOOK`/`HWINEVENTHOOK`.** No correct
  universal `ReleaseHandle`; risks destroying/unhooking a resource Bastion
  doesn't own (§2).
- **`CsWin32RunAsBuildTask=true` without `comInterop.useComSourceGenerators=true`**
  when `[GeneratedComInterface]`-based COM is needed. The build-task flag is
  necessary but not sufficient (§1).
- **`allowMarshaling=false` as a substitute for the full
  `CsWin32RunAsBuildTask` + `DisableRuntimeMarshalling` + `useComSourceGenerators`
  combination** when COM interfaces are required — produces COM structs, not
  the interface projections DESIGN.md commits to (§1).
- **Validating AOT/trim compatibility via `dotnet build -t:Publish`.** Only
  `dotnet publish` exercises real trimming/AOT analysis; see
  `quality-gates.md` for the exact gate command.
- **Capturing closure state in a lambda and handing it to
  `SetWinEventHook`/`SetWindowsHookExW`.** `[UnmanagedCallersOnly]` forbids
  instance methods and closures entirely — use the `GCHandle` registry
  pattern (§3.2).
- **`[ComImport]` / `[DllImport]`-based COM interop** anywhere in
  `Bastion.Win32`. Generates a runtime IL stub, which is unsupported under
  NativeAOT/trimming and is exactly what DESIGN.md §10 rules out.
- **`Marshal.GetObjectForIUnknown` / `Marshal.GetIUnknownForObject`** on
  objects obtained through the `ComWrappers` pipeline. Incompatible with
  source-generated COM (SYSLIB1099-class failures) — use
  `StrategyBasedComWrappers.GetOrCreateObjectForComInstance` /
  `GetOrCreateComInterfaceForObject` (§4.2).
- **Calling shell-COM objects (`IVirtualDesktopManager`, `ITaskbarList`,
  `IPropertyStore`) from a thread-pool thread, `Task.Run`, or any thread other
  than the single dedicated STA thread.** The generator provides zero
  apartment safety net (§5).
- **`new SomeCoClass()` activation syntax.** Unsupported with source-generated
  COM — always `CoCreateInstance` + `ComWrappers` (§4.2).
- **`new`-shadowing base interface methods, or attributing more than one base
  interface with `[GeneratedComInterface]`** in a derived interface chain
  (§4.3).
- **Discarding a `[PreserveSig]` method's returned HRESULT.** You took on
  `FAILED()`/`SUCCEEDED()` responsibility by opting out of exception
  translation — never ignore the return value (§4.4).
- **Declaring IDispatch-based interfaces, IInspectable-based interfaces, COM
  properties, or COM events** as `[GeneratedComInterface]` targets — all
  explicitly unsupported by the generator (§4.7).
