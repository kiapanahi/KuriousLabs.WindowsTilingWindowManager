namespace Bastion.Win32;

/// <summary>
/// Receives a logical <see cref="HotkeyCommand"/> whenever <see cref="InputPumpService"/>'s pump
/// thread resolves a fired <c>WM_HOTKEY</c> back to one of <see cref="DefaultHotkeyBindings"/>'
/// entries.
/// </summary>
/// <remarks>
/// No production command implementation exists yet — wiring these to actual Reconciler-driven
/// layout commands needs a live daemon composition root (GitHub issue #10); this issue only proves
/// the registration/probing/dispatch pipeline end to end. <see cref="LoggingHotkeyDispatchTarget"/>
/// is the interim, pre-composition-root implementation.
/// </remarks>
internal interface IHotkeyDispatchTarget
{
    /// <summary>
    /// Invoked synchronously on the pump thread itself (DESIGN.md §7's dedicated input pump) —
    /// implementations must return quickly and must never block, exactly like a
    /// <c>WinEventProc</c>/<c>LowLevelKeyboardProc</c> callback body must not (interop.md §3), even
    /// though this call is not itself an <c>[UnmanagedCallersOnly]</c> native callback.
    /// </summary>
    void OnHotkeyInvoked(HotkeyCommand command);
}
