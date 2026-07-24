namespace Bastion.Win32;

/// <summary>
/// Logical command identifiers <see cref="DefaultHotkeyBindings"/>' default chords map to. No
/// implementation exists for any of these yet — GitHub issue #7 proves the
/// registration/probing/<c>WM_HOTKEY</c>-dispatch pipeline end to end; wiring these to actual
/// Reconciler-driven layout commands needs a live daemon composition root (GitHub issue #10) and is
/// explicitly out of scope here.
/// </summary>
internal enum HotkeyCommand
{
    /// <summary>Move focus to the tile left of the currently focused window.</summary>
    FocusLeft,

    /// <summary>Move focus to the tile right of the currently focused window.</summary>
    FocusRight,

    /// <summary>Move focus to the tile below the currently focused window.</summary>
    FocusDown,

    /// <summary>Move focus to the tile above the currently focused window.</summary>
    FocusUp,

    /// <summary>Swap the focused window with the tile to its left.</summary>
    MoveWindowLeft,

    /// <summary>Swap the focused window with the tile to its right.</summary>
    MoveWindowRight,

    /// <summary>Swap the focused window with the tile below it.</summary>
    MoveWindowDown,

    /// <summary>Swap the focused window with the tile above it.</summary>
    MoveWindowUp,

    /// <summary>Toggle the focused window between tiled and floating.</summary>
    ToggleFloating,

    /// <summary>Cycle the focused workspace's layout engine (dwindle/tree/master-stack/monocle, DESIGN.md §6).</summary>
    CycleLayoutEngine,

    /// <summary>Force an immediate JSONC config reload (DESIGN.md §3.9), bypassing the debounce.</summary>
    ReloadConfig,

    /// <summary>Request a graceful <c>bastiond</c> shutdown (DESIGN.md §3.10's restore-on-exit path).</summary>
    QuitBastiond,
}
