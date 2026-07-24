using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Bastion.Win32;

/// <summary>
/// The Win32-facing seam <see cref="HotkeyRegistrar"/> depends on for the two actual syscalls
/// DESIGN.md §7 names — <c>RegisterHotKey</c> and <c>UnregisterHotKey</c>, both associated with the
/// calling thread (<c>hWnd = NULL</c>) per their own documented "posted to the message queue of the
/// calling thread"/"freed ... by the calling thread" contracts.
/// </summary>
/// <remarks>
/// Matches docs/engineering/testing.md §5's Tier-2 seam shape ("the fake implements the same
/// adapter-facing interface production code compiles against ... above CsWin32/COM, not inside a COM
/// shim"): <see cref="HotkeyRegistrationSystemAdapter"/> is the real implementation;
/// <c>Bastion.Win32.Tests</c>' <c>FakeHotkeyRegistrationSystem</c> is the fake that exercises
/// <see cref="HotkeyRegistrar"/>'s conflict-detection logic with zero real hotkeys — a real
/// OS-level registration conflict cannot be reliably forced from a unit test (it would need a
/// second process racing to claim the same chord first), so this seam is exactly what makes that
/// logic testable at all, the same way <see cref="IPlacementSystem"/> makes the Placement
/// Executor's Defer-batch-failure branch testable without a real HWND.
/// </remarks>
internal interface IHotkeyRegistrationSystem
{
    /// <summary>
    /// <c>RegisterHotKey(HWND.Null, id, modifiers, virtualKeyCode)</c>. Per DESIGN.md §7: a failed
    /// registration is a conflict regardless of the specific
    /// <see cref="HotkeyCallResult.ErrorCode"/> value — callers must never branch on the error code
    /// to decide whether a failure "counts."
    /// </summary>
    HotkeyCallResult Register(int id, HOT_KEY_MODIFIERS modifiers, uint virtualKeyCode);

    /// <summary>
    /// <c>UnregisterHotKey(HWND.Null, id)</c> — frees a hotkey previously registered by the calling
    /// thread. Must be called from the same thread that registered <paramref name="id"/>, exactly
    /// like <c>UnhookWinEvent</c> must run on <c>SetWinEventHook</c>'s registering thread.
    /// </summary>
    bool Unregister(int id);
}
