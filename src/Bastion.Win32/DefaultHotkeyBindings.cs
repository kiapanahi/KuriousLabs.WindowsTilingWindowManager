using System.Collections.Immutable;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Bastion.Win32;

/// <summary>
/// DESIGN.md §7's Tier 1 default keybinding set: Alt-based and Win+Shift/Win+Ctrl chords, every
/// entry carrying <see cref="HOT_KEY_MODIFIERS.MOD_NOREPEAT"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never a bare <see cref="HOT_KEY_MODIFIERS.MOD_WIN"/> chord.</b> The Win-key reservation is
/// advisory, not a failure mode: <c>RegisterHotKey(MOD_WIN, ...)</c> may <em>succeed</em> even for a
/// combo the shell already owns (DESIGN.md §7), so Bastion cannot rely on registration
/// success/failure to detect OS-claimed Win chords. Every binding below that uses
/// <see cref="HOT_KEY_MODIFIERS.MOD_WIN"/> also carries <see cref="HOT_KEY_MODIFIERS.MOD_SHIFT"/>
/// or <see cref="HOT_KEY_MODIFIERS.MOD_CONTROL"/> — enforced by <c>DefaultHotkeyBindingsTests</c>,
/// not just convention.
/// </para>
/// <para>
/// <b>Ids are small and sequential</b> (well within <c>RegisterHotKey</c>'s documented application
/// range of <c>0x0000</c>-<c>0xBFFF</c>) because this is a small, fixed, compile-time table — no
/// dynamic id allocator is needed the way <c>WindowIdMinter</c> is for the unbounded, runtime-grown
/// set of managed windows.
/// </para>
/// </remarks>
internal static class DefaultHotkeyBindings
{
    /// <summary>The full default table, in registration order.</summary>
    public static ImmutableArray<HotkeyBinding> All { get; } =
    [
        // Focus movement: Alt+hjkl (vi-style directions), matching the dwindle/tree/master-stack
        // engines' left/right/up/down tile adjacency (DESIGN.md §6).
        new(1, HOT_KEY_MODIFIERS.MOD_ALT | HOT_KEY_MODIFIERS.MOD_NOREPEAT, VIRTUAL_KEY.VK_H, HotkeyCommand.FocusLeft),
        new(2, HOT_KEY_MODIFIERS.MOD_ALT | HOT_KEY_MODIFIERS.MOD_NOREPEAT, VIRTUAL_KEY.VK_L, HotkeyCommand.FocusRight),
        new(3, HOT_KEY_MODIFIERS.MOD_ALT | HOT_KEY_MODIFIERS.MOD_NOREPEAT, VIRTUAL_KEY.VK_J, HotkeyCommand.FocusDown),
        new(4, HOT_KEY_MODIFIERS.MOD_ALT | HOT_KEY_MODIFIERS.MOD_NOREPEAT, VIRTUAL_KEY.VK_K, HotkeyCommand.FocusUp),

        // Window movement: Alt+Shift+hjkl, the same directions with Shift added.
        new(5, HOT_KEY_MODIFIERS.MOD_ALT | HOT_KEY_MODIFIERS.MOD_SHIFT | HOT_KEY_MODIFIERS.MOD_NOREPEAT, VIRTUAL_KEY.VK_H, HotkeyCommand.MoveWindowLeft),
        new(6, HOT_KEY_MODIFIERS.MOD_ALT | HOT_KEY_MODIFIERS.MOD_SHIFT | HOT_KEY_MODIFIERS.MOD_NOREPEAT, VIRTUAL_KEY.VK_L, HotkeyCommand.MoveWindowRight),
        new(7, HOT_KEY_MODIFIERS.MOD_ALT | HOT_KEY_MODIFIERS.MOD_SHIFT | HOT_KEY_MODIFIERS.MOD_NOREPEAT, VIRTUAL_KEY.VK_J, HotkeyCommand.MoveWindowDown),
        new(8, HOT_KEY_MODIFIERS.MOD_ALT | HOT_KEY_MODIFIERS.MOD_SHIFT | HOT_KEY_MODIFIERS.MOD_NOREPEAT, VIRTUAL_KEY.VK_K, HotkeyCommand.MoveWindowUp),

        // Layout control: Alt-only.
        new(9, HOT_KEY_MODIFIERS.MOD_ALT | HOT_KEY_MODIFIERS.MOD_NOREPEAT, VIRTUAL_KEY.VK_RETURN, HotkeyCommand.ToggleFloating),
        new(10, HOT_KEY_MODIFIERS.MOD_ALT | HOT_KEY_MODIFIERS.MOD_NOREPEAT, VIRTUAL_KEY.VK_SPACE, HotkeyCommand.CycleLayoutEngine),

        // Daemon-level commands: Win+Ctrl / Win+Shift, never bare Win — see remarks above.
        new(11, HOT_KEY_MODIFIERS.MOD_WIN | HOT_KEY_MODIFIERS.MOD_CONTROL | HOT_KEY_MODIFIERS.MOD_NOREPEAT, VIRTUAL_KEY.VK_R, HotkeyCommand.ReloadConfig),
        new(12, HOT_KEY_MODIFIERS.MOD_WIN | HOT_KEY_MODIFIERS.MOD_SHIFT | HOT_KEY_MODIFIERS.MOD_NOREPEAT, VIRTUAL_KEY.VK_Q, HotkeyCommand.QuitBastiond),
    ];
}
