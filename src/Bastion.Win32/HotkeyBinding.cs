using System.Runtime.InteropServices;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Bastion.Win32;

/// <summary>
/// One entry in <see cref="DefaultHotkeyBindings"/>'s default table: the <c>RegisterHotKey</c>
/// chord (<paramref name="Modifiers"/> + <paramref name="VirtualKey"/>), the app-assigned
/// <paramref name="Id"/> its own documented contract requires in the <c>0x0000</c>-<c>0xBFFF</c>
/// application range, and the logical <paramref name="Command"/> it maps to once <c>WM_HOTKEY</c>
/// fires (DESIGN.md §7).
/// </summary>
/// <param name="Id">
/// The <c>RegisterHotKey</c>/<c>UnregisterHotKey</c> identifier. Must be unique within
/// <see cref="DefaultHotkeyBindings.All"/> — see <c>DefaultHotkeyBindingsTests</c>.
/// </param>
/// <param name="Modifiers">
/// The chord's modifier keys, always including <see cref="HOT_KEY_MODIFIERS.MOD_NOREPEAT"/>
/// (DESIGN.md §7) and never a <em>bare</em> <see cref="HOT_KEY_MODIFIERS.MOD_WIN"/> (Win held with
/// no companion Shift/Ctrl) — see <see cref="DefaultHotkeyBindings"/>'s remarks for why.
/// </param>
/// <param name="VirtualKey">The chord's non-modifier key.</param>
/// <param name="Command">The logical command this chord invokes once registered and fired.</param>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct HotkeyBinding(int Id, HOT_KEY_MODIFIERS Modifiers, VIRTUAL_KEY VirtualKey, HotkeyCommand Command);
