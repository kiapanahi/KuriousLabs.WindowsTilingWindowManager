---
name: win32-interop
description: AOT-safe checklist for writing or reviewing any code in Bastion.Win32 (P/Invoke, hook callbacks, source-generated COM). Use whenever touching the adapter ring.
---

Purpose: this SKILL turns `docs/engineering/interop.md` into an executable review
procedure so every adapter-ring change is checked against the same AOT/COM/hook
rules every time. The doc holds the rationale and citations; this file holds the
steps. Do not re-derive the rules from first principles — read the doc section,
then apply the matching step below.

## When to use this skill

Trigger on ANY of the following, whether you are authoring new code or reviewing
a diff:

- Creating, modifying, or reviewing a file under `src/Bastion.Win32/`.
- Touching `NativeMethods.txt` or `NativeMethods.json` (CsWin32 config).
- A diff that contains any of: `LibraryImport`, `GeneratedComInterface`,
  `GeneratedComClass`, `UnmanagedCallersOnly`, `delegate*`, `HWND`, `HHOOK`,
  `HWINEVENTHOOK`, `CoCreateInstance`, `IVirtualDesktopManager`, `ITaskbarList`,
  `ITaskService`, `IPropertyStore`.
- Any PR/change description mentioning WinEvent hooks, keyboard hooks, or shell
  COM integration.

If none of the above apply, this skill is not needed.

## Procedure

1. **Read the relevant doc sections first.** Do not proceed from memory.
   - P/Invoke / CsWin32 config changes -> `docs/engineering/interop.md` §1.
   - Handle-type modeling (HWND/HHOOK/HWINEVENTHOOK, SafeHandle boundaries) -> §2.
   - Hook callbacks (WinEventProc / LowLevelKeyboardProc) -> §3.
   - COM (GeneratedComInterface, ComWrappers, shell interfaces) -> §4 and §5.
   - P/Invoke error handling (GetLastError / SetLastError under
     DisableRuntimeMarshalling) -> §6.

2. **Boundary check — no Win32 types leak out of Bastion.Win32.**
   Only the opaque `WindowId` (and other Core-defined value types) may cross into
   Bastion.Core / Bastion.Layout. Grep the diff for `HWND`, `RECT`, `HWINEVENTHOOK`,
   `HHOOK`, and any `Windows.Win32.*`/CsWin32-generated namespace appearing outside
   `src/Bastion.Win32/`. Any hit outside the adapter ring is a hard stop — push the
   translation to a Win32 type back into Bastion.Win32 and cross the boundary with
   the opaque type instead.

3. **Config check — only if `NativeMethods.txt`/`NativeMethods.json` changed.**
   Confirm all of the following are present (not just one or two):
   - `<CsWin32RunAsBuildTask>true</CsWin32RunAsBuildTask>` and
     `<DisableRuntimeMarshalling>true</DisableRuntimeMarshalling>` in the csproj.
   - `"comInterop": { "useComSourceGenerators": true }` in `NativeMethods.json`
     — `CsWin32RunAsBuildTask` alone does NOT enable COM source generation; set
     this explicitly whenever the change touches a COM interface.
   - `useSafeHandles` left at its default (`true`) globally — governs closeable
     kernel handles, not HWND/HHOOK/HWINEVENTHOOK (interop.md §2).
   - Any new wildcard entry (e.g. `User32.*`) reviewed for silently-dropped
     arch-specific members — a wildcard entry **silently drops** members that
     are only available for a specific CPU architecture, with **no warning
     emitted** for the omitted subset. This cannot be caught by the build; you
     must diff the generated output against what you expected (interop.md
     §1.1).
   - Any new **explicit** (non-wildcard) request for an arch-specific API on
     an `AnyCPU` compilation reviewed for build warning `PInvoke005` ("This
     API is only available when targeting a specific CPU architecture.
     AnyCPU cannot generate this API."); Bastion always publishes with an
     explicit RID, so this is low-risk but still worth a glance
     (interop.md §1.1).

4. **Hook-callback checklist — only if the diff adds/touches an
   `[UnmanagedCallersOnly]` method (WinEventProc, LowLevelKeyboardProc, or any
   future hook).** Every item must hold; treat any failure as a hard stop:
   - Method is `static`, non-generic, with `CallConvs = [typeof(CallConvStdcall)]`
     (or the CsWin32-generated equivalent) and a fully blittable signature.
   - The **entire method body** is wrapped in a single catch-all `try`/`catch
     (Exception)` that logs and swallows — never rethrows, never lets an
     exception cross back into native hook-dispatch code. This is non-negotiable:
     an escaping exception here is undefined behavior under AOT, not a catchable
     .NET exception.
   - The native callback is registered as a `delegate* unmanaged[Stdcall]<...>`
     function pointer, not a managed delegate handed through
     `Marshal.GetFunctionPointerForDelegate`. Inspect the actual CsWin32-generated
     parameter type for `SetWinEventHook`/`SetWindowsHookExW` before writing the
     pointer signature — do not assume parameter order or calling convention.
   - Per-hook managed context (layout engine reference, keybinding table, etc.)
     is passed via a `GCHandle`-based context registry keyed by the hook handle
     (static/process-wide dictionary, since `SetWinEventHook`/`SetWindowsHookExW`
     provide no user-data slot) — never via a captured closure (not legal for
     `UnmanagedCallersOnly` targets in the first place).
   - The `GCHandle` is freed only after the matching `UnhookWinEvent`/
     `UnhookWindowsHookEx` call succeeds, in a `Dispose`-path that pairs 1:1 with
     registration. Verify there is no path where the hook is unregistered without
     freeing the handle, or freed while the hook is still live.
   - Zero heap allocation in the callback body beyond the single bounded-channel
     write that hands the event to the reconciler ingest path (per
     `docs/engineering/concurrency-performance.md`).
   - The internal event envelope written to the channel is a `readonly record
     struct` — do not over-constrain it to be blittable; only the raw
     HWND/handle/message fields inside the callback cross the native boundary.

5. **Banned-API scan.** Grep the diff for every symbol/pattern below. Any hit is
   either a bug to fix or requires an explicit, cited justification against
   `docs/engineering/interop.md`:
   - `DllImport`, `[ComImport]`
   - `Marshal.GetDelegateForFunctionPointer`, `Marshal.GetFunctionPointerForDelegate`
   - `Marshal.GetObjectForIUnknown`, `Marshal.GetIUnknownForObject`
   - `new` activation of a CoClass (e.g. `new TaskbarListCoClass()`)
   - `SafeHandle`-wrapping of `HWND`, `HHOOK`, or `HWINEVENTHOOK`

   Only the first three bullets above — the six concrete `DllImport`/
   `[ComImport]`/`Marshal.*` symbols — are literal `BannedSymbols.txt`
   DocCommentId candidates (this is exactly the seed list shown in
   `docs/engineering/quality-gates.md` §5). Confirm `BannedSymbols.txt` lists
   all six by exact `DocumentationCommentId` so `RS0030` catches regressions —
   if one is missing, add it as part of this change.

   The last two bullets are code-review-time grep/inspection patterns, **not**
   analyzer-enforceable `BannedSymbols.txt` entries, and must not be "fixed" by
   adding them to that file:
   - CoClass activation has no stable `DocumentationCommentId` until a
     specific CsWin32-generated CoClass type exists in the codebase, and
     `BannedApiAnalyzers` requires an exact DocCommentId per symbol — no
     wildcard/pattern matching — with a wrong one silently failing to match
     anything (quality-gates.md §5).
   - `SafeHandle`-wrapping of HWND/HHOOK/HWINEVENTHOOK cannot be banned as a
     symbol without banning `System.Runtime.InteropServices.SafeHandle`
     itself, which would contradict interop.md §2 and quality-gates.md:
     `SafeHandle` remains the correct tool for genuinely closeable kernel
     handles (registry/process/thread/file) elsewhere in Bastion.Win32.

6. **COM checklist — only if the diff adds/touches a `[GeneratedComInterface]` or
   `[GeneratedComClass]` type** (IVirtualDesktopManager, ITaskbarList family,
   ITaskService, IPropertyStore, or any future shell surface):
   - Interface is `partial`, carries `[Guid("...")]`; the inheritance chain
     (e.g. `ITaskbarList3 : ITaskbarList2 : ITaskbarList`) uses plain C# interface
     inheritance with **no `new`-shadowing**, and exactly **one**
     `[GeneratedComInterface]`-attributed interface in the chain.
   - `StringMarshalling.Utf16` (or per-parameter `[MarshalUsing]`) is explicit on
     any method with `string` parameters/returns — verify per-method whether the
     native field is BSTR (needs a BSTR-aware marshaller) vs. plain LPWSTR before
     picking one. Never leave string marshalling unset.
   - Output/mutated parameters use C# `in`/`out`/`ref` modifiers, not `[In]`/
     `[Out]` attributes (the generator only accepts those on arrays) — e.g.
     `ref Guid desktopId` on `MoveWindowToDesktop`.
   - `[PreserveSig]` is applied where a failure HRESULT is an expected,
     branch-on-value outcome rather than exceptional — the canonical case is
     `IVirtualDesktopManager.MoveWindowToDesktop` returning `E_ACCESSDENIED` for a
     foreign HWND (interop.md §4.4). Confirm the caller checks the returned `int`
     with `FAILED()`/`SUCCEEDED()` — a discarded PreserveSig return is a
     silent-failure bug.
   - Activation goes through `CoCreateInstance` +
     `StrategyBasedComWrappers.Instance.GetOrCreateObjectForComInstance`, never
     `new SomeCoClass()`. The wrapper is cached per underlying `IUnknown` identity
     and re-created only on the documented signal (explorer.exe restart /
     taskbar recreation) — never on a timer, never speculatively per call.

7. **Threading check.** For every shell-COM call site touched by the diff, trace
   the call path back to its entry point and confirm it provably executes on the
   single dedicated STA/message-pump thread that owns Bastion's hidden/bar HWND.
   The COM source generator assumes all objects are free-threaded and provides
   **no** apartment marshaling — a call from a thread-pool thread or `Task.Run`
   continuation will not be marshaled for you and can corrupt state or fail with
   `RPC_E_WRONG_THREAD`. Confirm there is no `await`/`Task.Run` hop between
   `CoCreateInstance` and the method call that uses the resulting object.

8. **Build/publish verification.**
   - `dotnet build` on the affected project(s) must show **zero** IL2xxx/IL3xxx
     warnings. If any appear, do not suppress with a bare pragma — use
     `[UnconditionalSuppressMessage]` only after confirming the path is genuinely
     safe (pragma/`SuppressMessage` do not survive into AOT analysis).
   - If the change plausibly affects publish-time trimming/AOT behavior (new
     reflection-adjacent API, new NativeMethods entry, new COM interface), run
     `dotnet publish -r win-x64` (or `win-arm64` as applicable) — never
     `dotnet build -t:Publish`, which does not exercise the same analysis path.

9. **Hard stop — unresearched Win32 API.** If this change introduces a Windows
   API, COM interface, or relies on documented behavior you have not already
   verified in this session (a new hook type, a new shell interface, a new
   struct with arch-specific layout, etc.), **stop** and run the
   `verify-windows-api` skill first. Do not write interop code against an API
   surface you have not confirmed against official Microsoft documentation.

## Non-negotiables (repeat, for scanning)

- No `[ComImport]`, no `DllImport`, no `Marshal.GetDelegateForFunctionPointer`/
  `GetFunctionPointerForDelegate` anywhere in Bastion.Win32.
- No exception may escape an `[UnmanagedCallersOnly]` method — ever.
- No `SafeHandle` wrapping of HWND/HHOOK/HWINEVENTHOOK.
- No Win32 type crosses out of Bastion.Win32.
- No shell-COM call off the dedicated STA thread.
- Validate AOT/trim only via `dotnet publish`, never `dotnet build -t:Publish`.
