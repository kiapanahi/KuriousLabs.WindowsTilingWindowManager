namespace Bastion.Win32;

/// <summary>
/// Seam over the single DWM read <see cref="Coalescer"/> needs for the CLOAKED/UNCLOAKED-burst
/// heuristic (DESIGN.md §3.2/§4): whether a window's <c>DWMWA_CLOAKED</c> attribute currently reads
/// nonzero. Extracted so the heuristic is unit-testable with a fake (see
/// <c>Bastion.Win32.Tests</c>'s <c>FakeCloakStateReader</c>) without a real window, matching the
/// same "extract a small testable seam" pattern <see cref="WinEventFilter"/>/
/// <see cref="WinEventRootNormalizer"/> use for the WinEvent pump (GitHub issue #1).
/// </summary>
internal interface ICloakStateReader
{
    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="hwnd"/>'s <c>DWMWA_CLOAKED</c> attribute
    /// currently reads nonzero (cloaked, for any of <c>DWM_CLOAKED_APP</c>/<c>_SHELL</c>/
    /// <c>_INHERITED</c> — the specific reason is deliberately never inspected, per DESIGN.md
    /// §3.3/§4's "Bastion deliberately does not depend on the specific reason flag"), or
    /// <see langword="false"/> if it reads zero <em>or</em> the underlying read fails.
    /// </summary>
    bool IsCloaked(nint hwnd);
}
