using System.Runtime.InteropServices;

namespace Bastion.Win32;

/// <summary>
/// Allocation-free envelope for a single filtered, normalized WinEvent, carried from
/// <see cref="WinEventPumpService"/>'s <c>[UnmanagedCallersOnly]</c> hook callback into the bounded
/// ingest channel that the future Coalescer (GitHub issue #2) will drain.
/// </summary>
/// <param name="Hwnd">
/// The event's target window, already normalized to its root ancestor via <c>GetAncestor(GA_ROOT)</c>
/// (DESIGN.md §3.1) — never a child/owned window.
/// </param>
/// <param name="EventId">The raw <c>EVENT_*</c> constant reported by <c>WinEventProc</c>.</param>
/// <param name="DwmsEventTimeMs">
/// The event's <c>dwmsEventTime</c>, the timestamp the future Coalescer will key its ~75 ms
/// per-HWND coalescing window on (DESIGN.md §3.2).
/// </param>
/// <remarks>
/// Every field is a primitive/<see langword="nint"/>, so compiler-generated record equality (which
/// compares members via <see cref="object.Equals(object?)"/>) is value-correct here — see
/// docs/engineering/concurrency-performance.md §5's note that this would <em>not</em> hold if this
/// type ever grew a collection-typed field. The blittability rules <c>[UnmanagedCallersOnly]</c>
/// imposes on <see cref="WinEventPumpService"/>'s native callback signature apply only to that
/// callback's own parameter list, not to this type — packing primitives into this struct is
/// ordinary managed code, not itself subject to that constraint (interop.md §3.4).
/// </remarks>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct WinEvent(nint Hwnd, uint EventId, uint DwmsEventTimeMs);
