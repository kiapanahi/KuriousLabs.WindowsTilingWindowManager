---
name: pure-core
description: Purity rules and property-test procedure for working on Bastion.Core and Bastion.Layout (the Linux-CI-tested, Win32-free libraries). Use whenever touching src/Bastion.Core or src/Bastion.Layout.
---

# pure-core

Bastion's entire testability strategy rests on one fact: `Bastion.Core` and `Bastion.Layout`
have zero Win32 dependencies and run on Linux CI. Every change you make here either preserves
that boundary or breaks the reason the pure/adapter split exists at all (DESIGN.md §3, §10, §11).
Treat purity as a hard gate, not a style preference.

## When to use this skill

Any time you are about to add, edit, or review code under `src/Bastion.Core/`,
`src/Bastion.Layout/`, or their paired test projects. If a change touches both a pure project
and `Bastion.Win32`, apply this skill to the pure half and `docs/engineering/interop.md` to the
adapter half — do not let interop concerns leak backward into this checklist.

## Before writing any code: purity checklist

Walk this list before touching a file. If any item fails, the code belongs in `Bastion.Win32`,
not here — move it, don't work around it.

1. **No Win32/COM surface at all.** No CsWin32-generated types, no `HWND`, no P/Invoke, no
   `[GeneratedComInterface]`/`ComWrappers` usage, no registry/file/named-pipe I/O. State-bearing
   code only ever sees the opaque `WindowId` (DESIGN.md §3, §10) — never an `HWND`, PID, or
   window handle. If you find yourself reaching for a handle "just to compare identity," that's
   the adapter ring's job; push it there.
2. **No wall-clock or ambient time.** No `DateTime.Now`/`UtcNow`, `Task.Delay`, `Stopwatch.StartNew`,
   or raw `Timer`/`PeriodicTimer` construction. Every timing dependency is an injected
   `TimeProvider`. Every timing constant (75 ms coalesce, 150 ms admission grace, 500 ms
   display-settle debounce, the 5 s heartbeat — DESIGN.md §3.2) is a config value passed in, never
   a literal baked into logic. If you add a new debounce/timeout, it must arrive as a parameter
   or config-bound field, never a magic number.
3. **No allocation-per-event on hot paths.** The reconciler loop and layout solve run once per
   coalesced intent or heartbeat tick — do not allocate a new collection, LINQ closure, or boxed
   value per window per tick where a pooled/reused buffer or `ImmutableArray` builder will do.
   Snapshots are built via `ImmutableArray.CreateBuilder<T>()`, never `default(ImmutableArray<T>)`
   (a default-constructed `ImmutableArray<T>` is not empty, it's uninitialized — see
   `docs/engineering/daemon-architecture.md` §5 for the full data-structure policy). Verify with
   the allocation profiler or `ClrHeapAllocationAnalyzer` output before asserting a path is
   allocation-free; don't just eyeball it.
4. **Every recursive traversal carries an explicit depth cap.** Tree diffing, tree walking, and
   any structural recursion over the split tree must bound depth explicitly and fail soft (return
   an error/degraded result) rather than recurse unbounded — a `StackOverflowException` is
   uncatchable and takes the whole daemon process down with it
   (`docs/engineering/daemon-architecture.md` §6). Never proceed with an unbounded recursive
   helper "because the tree is usually shallow" — bound it anyway.
5. **Deterministic, referentially transparent functions.** Same `(tree, workArea, constraints,
   gaps)` in, same `[(WindowId, RECT)]` out, every time, no hidden state. If a function needs
   anything beyond its parameters and injected `TimeProvider` to produce its result, that's a
   purity leak — thread the missing input through explicitly instead.
6. **`IsAotCompatible=true` and warning-free on Linux.** Both projects must build clean under
   `dotnet build` on the Linux CI leg. Never add a conditional `#if WINDOWS` escape hatch to sneak
   Win32 code past this — that defeats the entire cross-platform test story
   (`docs/engineering/quality-gates.md` for the AOT/analyzer property placement rules this build
   depends on).

**Hard stop condition:** if you cannot satisfy all six items for a piece of logic, stop and
relocate that logic to `Bastion.Win32` (or the daemon composition root) rather than adding a
suppression, an `#if`, or a "temporary" Win32 reference here. A purity violation that ships even
once means the Linux CI leg is no longer testing what it claims to test.

## Test procedure for layout / reconciler changes

Apply in order; do not skip a step because "it's a small change" — small layout changes are
exactly where subtle invariant breaks hide.

1. **Add or extend property tests for every new/changed layout algorithm.** Use FsCheck's
   `[Property]` attribute (see `docs/engineering/testing.md` §3 for why FsCheck was chosen over
   CsCheck for Bastion). Required invariants, at minimum:
   - no-overlap (pairwise rect intersection is empty),
   - full-coverage (union of leaf rects == `workArea` minus configured gaps),
   - min-size respect,
   - determinism (same input twice → identical output).
2. **Add the subtree-locality metamorphic test for any tree-shape change.** Generate a tree, run
   `Layout`, perturb exactly one leaf (insert or remove), run `Layout` again, and assert every
   *other* leaf's rect is byte-identical across the two runs. This is the single property that
   catches accidental global re-layout from a local edit — never skip it for a change that touches
   tree restructuring.
3. **Write generators over the input space, not hand-computed pixel examples.** Vary tree shape,
   split ratios, `workArea`, gaps, and constraints in the generator; do not assert against a fixed
   set of pixel values where a property assertion covers the same ground more robustly. If you
   catch yourself hand-computing an expected rect, ask whether a property test already subsumes it.
4. **Use Verify only for whole-solution regression snapshots**, e.g. asserting an entire
   `[(WindowId, RECT)]` solution shape across a refactor. Register explicit scrubbers/converters
   for `WindowId` and rect DTOs before committing a `*.verified.*` file — Verify's built-in
   scrubbing only normalizes GUIDs, `DateTime`s, and paths automatically; a custom rect/ID type
   left unscrubbed will produce spurious diffs on every run (`docs/engineering/testing.md` §6).
   Never assume the default scrubber set covers a project-defined type.
5. **Route every time-dependent Core test through `FakeTimeProvider`**
   (`Microsoft.Extensions.TimeProvider.Testing`), injected the same way production code receives
   `TimeProvider`. If a test awaits work gated on the fake clock, call
   `SynchronizationContext.SetSynchronizationContext(null)` immediately before `Advance(...)` —
   otherwise the awaited continuation may not observe the timer callback synchronously
   (`docs/engineering/testing.md` §4). Never use a real `Task.Delay`/sleep to "wait for" a
   debounce in a test; that reintroduces the exact flakiness the fake clock exists to remove.
6. **Reconciler behavior changes get a Tier-2 replay test.** Add or extend a recorded-trace replay
   through the `IWindowSystem` fake adapter (`docs/engineering/testing.md` §5) rather than relying
   solely on unit-level asserts — Tier 2 is designated as carrying the project's main regression
   burden, and a reconciler change with no Tier-2 coverage is not done.
7. **If you strengthened an invariant or added a new property, sanity-check it kills mutants.**
   Run a scoped Stryker.NET pass against just the changed project with `--test-runner mtp`
   (`docs/engineering/testing.md` §9). This is a spot-check, not a full-suite gate on every PR —
   use it when you're unsure whether the new tests actually constrain behavior or merely restate
   the implementation.

## Verification before calling a change done

- `dotnet build` the affected project(s) on the Linux CI leg (or locally under Linux/WSL if
  available) — a warning here is a purity or AOT-compatibility regression, not noise.
- `dotnet test` the pure test projects and confirm the property tests, the subtree-locality test,
  and any updated Tier-2 replay test all pass — never report green without having seen the actual
  run output.
- Grep the diff for `HWND`, `DateTime.Now`, `DateTime.UtcNow`, `Task.Delay`, `Stopwatch`, and CsWin32
  namespace usages before committing; any hit inside `Bastion.Core`/`Bastion.Layout` is a blocking
  finding, not a suggestion.
- If a Verify snapshot changed, open the diff and confirm the change is the intended layout
  behavior shift, not scrubber drift or nondeterministic ID leakage — never bulk-accept `.received.*`
  files without reading them.
