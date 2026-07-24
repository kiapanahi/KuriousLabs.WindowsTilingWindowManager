using System.Runtime.InteropServices;
using Bastion.Core;
using Windows.Win32.Foundation;

namespace Bastion.Win32;

/// <summary>
/// One window's result from a <see cref="PlacementExecutor.Apply"/> pass — the per-window outcome
/// GitHub issue #5's design guidance asks for, deliberately independent of both the not-yet-built
/// UIPI elevated-window classification (issue #18) and the not-yet-built learned effective-min-size
/// constraint cache (issue #6): this type only <em>surfaces</em> the data both future consumers
/// need (a preserved error code; a clamped verified size), it classifies neither.
/// </summary>
/// <remarks>
/// <b>Causation-ID scope call (see the design guidance's explicit ask to state this):</b> DESIGN.md
/// §3.4's full causation-ID event log (every intent/decision/effect carrying the id of the intent
/// that produced it) is GitHub issue #23, not yet built. "Verify-after-move re-reads frame bounds
/// under the same causation ID" is satisfied here structurally rather than via an explicit id field:
/// <see cref="PlacementExecutor.Apply"/> is synchronous, and every outcome's verify-after-move
/// readback (<see cref="VerifiedBounds"/>) happens immediately after — and is returned alongside —
/// the very <see cref="Bastion.Core.PlacementInstruction"/> that produced it, one outcome per
/// instruction, in the same call. That 1:1, same-call pairing <em>is</em> the correlation; a
/// synthetic causation-id field would carry no information issue #23's real event log doesn't
/// already own more completely.
/// </remarks>
/// <param name="WindowId">The window this outcome describes.</param>
/// <param name="Kind">What happened.</param>
/// <param name="VerifiedBounds">
/// The fresh post-move frame-bounds readback (DESIGN.md §3.6e), when <paramref name="Kind"/> is
/// <see cref="PlacementOutcomeKind.Moved"/> and the window was still alive to read back from.
/// <see langword="null"/> for every other <paramref name="Kind"/>, and also <see langword="null"/>
/// on a <see cref="PlacementOutcomeKind.Moved"/> outcome whose window vanished between the move and
/// the verify read (a routine race, not exceptional — matching <c>WindowSystemAdapter</c>'s own
/// established handling of the identical race).
/// </param>
/// <param name="ClampedTo">
/// Non-<see langword="null"/> exactly when <see cref="VerifiedBounds"/>'s width or height differs
/// from the originally-requested <see cref="Bastion.Core.PlacementInstruction.TargetBounds"/> by
/// more than <see cref="PlacementExecutorOptions.SizeToleranceDevicePixels"/> — i.e. the app refused
/// to size to what the layout asked for. This is exactly, and only, the signal DESIGN.md §3.6e asks
/// this issue to surface ("a clamped result is recorded as the window's effective minimum size,
/// feeding the layout constraint cache"): GitHub issue #6 owns turning it into a persisted,
/// decaying, per-rule-key cache entry and GitHub issue #6/the Reconciler own the actual re-layout;
/// this field is only the data.
/// </param>
/// <param name="ErrorCode">
/// The Win32 error code from the failing call, when <paramref name="Kind"/> is
/// <see cref="PlacementOutcomeKind.Failed"/> and the underlying <c>SetWindowPlacement</c>/
/// <c>SetWindowPos</c> call itself failed (captured via <c>Marshal.GetLastPInvokeError()</c> — see
/// <see cref="PlacementSystemAdapter"/>'s remarks for why that API, not <c>GetLastWin32Error</c> or
/// a hand-rolled <c>GetLastError</c> P/Invoke, is correct here). <see langword="null"/> when the
/// failure was instead "this <see cref="Bastion.Core.WindowId"/> no longer resolves to any live
/// <c>HWND</c>" (the window vanished before any Win32 call was even attempted) — there is no error
/// code to preserve in that case. Deliberately preserved, never swallowed, so a future caller (the
/// UIPI elevated-window classification of GitHub issue #18) can inspect it; this issue does not
/// itself classify it.
/// </param>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct PlacementOutcome(
    WindowId WindowId,
    PlacementOutcomeKind Kind,
    Rect? VerifiedBounds,
    Rect? ClampedTo,
    WIN32_ERROR? ErrorCode)
{
    /// <summary>Creates a <see cref="PlacementOutcomeKind.Untiled"/> outcome.</summary>
    public static PlacementOutcome Untiled(WindowId windowId) =>
        new(windowId, PlacementOutcomeKind.Untiled, null, null, null);

    /// <summary>Creates a <see cref="PlacementOutcomeKind.QuarantinedHung"/> outcome.</summary>
    public static PlacementOutcome QuarantinedHung(WindowId windowId) =>
        new(windowId, PlacementOutcomeKind.QuarantinedHung, null, null, null);

    /// <summary>Creates a <see cref="PlacementOutcomeKind.Moved"/> outcome.</summary>
    public static PlacementOutcome Moved(WindowId windowId, Rect? verifiedBounds, Rect? clampedTo) =>
        new(windowId, PlacementOutcomeKind.Moved, verifiedBounds, clampedTo, null);

    /// <summary>Creates a <see cref="PlacementOutcomeKind.Failed"/> outcome.</summary>
    public static PlacementOutcome Failed(WindowId windowId, WIN32_ERROR? errorCode) =>
        new(windowId, PlacementOutcomeKind.Failed, null, null, errorCode);
}
