using System.Collections.Immutable;

namespace Bastion.Win32;

/// <summary>
/// Resolves a fired <c>WM_HOTKEY</c> message's <c>wParam</c> id back to the <see cref="HotkeyCommand"/>
/// it was registered under.
/// </summary>
/// <remarks>
/// Extracted out of <see cref="InputPumpService"/>'s pump-thread message loop as a small,
/// directly-callable, pure lookup — a synthetic <see cref="ImmutableArray{T}"/> of
/// <see cref="HotkeyRegistrationResult"/> and an already-known id are enough to exercise it, no real
/// thread/message/hotkey required (<c>HotkeyDispatchTests</c>).
/// </remarks>
internal static class HotkeyDispatch
{
    /// <summary>
    /// Looks up <paramref name="id"/> — recovered from <c>WM_HOTKEY</c>'s <c>wParam</c>, per
    /// https://learn.microsoft.com/windows/win32/inputdev/wm-hotkey: "wParam ... The identifier of
    /// the hot key that generated the message" — among <paramref name="registrations"/>'s
    /// successfully-registered entries.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> and the matching <paramref name="command"/> if <paramref name="id"/>
    /// matches a registered (<see cref="HotkeyRegistrationResult.Registered"/>) binding;
    /// <see langword="false"/> otherwise — defensive; should not happen in practice, since this pump
    /// only ever registers its own ids and only a successful registration could have produced a live
    /// <c>WM_HOTKEY</c> in the first place, mirroring <c>WinEventPumpService.OnWinEvent</c>'s
    /// identical defensive handling of an unrecognized hook handle.
    /// </returns>
    public static bool TryResolveCommand(
        ImmutableArray<HotkeyRegistrationResult> registrations,
        int id,
        out HotkeyCommand command)
    {
        foreach (HotkeyRegistrationResult registration in registrations)
        {
            if (registration.Registered && registration.Binding.Id == id)
            {
                command = registration.Binding.Command;
                return true;
            }
        }

        command = default;
        return false;
    }
}
