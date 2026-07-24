namespace Bastion.Win32;

/// <summary>What actually happened when the <see cref="PlacementExecutor"/> tried to apply one <see cref="Bastion.Core.PlacementInstruction"/>.</summary>
internal enum PlacementOutcomeKind
{
    /// <summary>
    /// <see cref="Bastion.Core.PlacementAction.Untile"/> — there was nothing to apply; the window keeps
    /// whatever position it currently has (DESIGN.md §3.6's remarks: "leaving the window alone").
    /// </summary>
    Untiled,

    /// <summary>
    /// The hang probe (<c>SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG)</c>) found the window
    /// unresponsive, or it is still within an active quarantine backoff from a previous hang
    /// (DESIGN.md §3.6a, §9's "Hung app" row) — skipped this pass rather than stalling the batch.
    /// </summary>
    QuarantinedHung,

    /// <summary>
    /// Applied — either via <c>SetWindowPlacement</c> (state normalization), a successful
    /// <c>DeferWindowPos</c> batch entry, or the per-window <c>SetWindowPos</c> fallback — and
    /// verified via a fresh post-move frame-bounds readback (DESIGN.md §3.6e). See
    /// <see cref="PlacementOutcome.ClampedTo"/> for whether the verified size matched what was
    /// requested.
    /// </summary>
    Moved,

    /// <summary>
    /// The underlying Win32 call failed (or the window's <c>WindowId</c> no longer resolves to a
    /// live <c>HWND</c> at all — it vanished between the Reconciler's plan and this pass). See
    /// <see cref="PlacementOutcome.ErrorCode"/> — deliberately preserved, never swallowed, so a
    /// future caller (the UIPI elevated-window classification of GitHub issue #18, explicitly out
    /// of scope here) can inspect it.
    /// </summary>
    Failed,
}
