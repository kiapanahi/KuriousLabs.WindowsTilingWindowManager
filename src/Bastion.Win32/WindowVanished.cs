namespace Bastion.Win32;

/// <summary>A window was destroyed (<c>EVENT_OBJECT_DESTROY</c>).</summary>
/// <param name="Hwnd">The window that was destroyed.</param>
/// <remarks>
/// DESIGN.md §5: "EVENT_OBJECT_DESTROY purges the registry entry and journal row; HIDE/CLOAKED
/// keep the slot." Like <see cref="DragEnded"/>, an emission of this one is never debounced by
/// <see cref="Coalescer"/> — an HWND can only be destroyed once, so there is no burst to collapse,
/// and delaying the purge behind the coalescing window would buy nothing.
/// </remarks>
internal sealed record WindowVanished(nint Hwnd) : CoalescedIntent;
