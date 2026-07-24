using Bastion.Core;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Bastion.Win32;

/// <summary>
/// The Win32-facing seam <see cref="PlacementExecutor"/> depends on for every actual syscall
/// DESIGN.md §3.6 names — hang probe, state reads, <c>SetWindowPlacement</c>, the
/// <c>BeginDeferWindowPos</c>/<c>DeferWindowPos</c>/<c>EndDeferWindowPos</c> triad, and the
/// per-window <c>SetWindowPos</c> fallback — plus the one piece of identity resolution the executor
/// itself needs (<see cref="TryResolveHwnd"/>). Matches docs/engineering/testing.md §5's Tier-2
/// seam shape ("the fake implements the same adapter-facing interface production code compiles
/// against... above CsWin32/COM, not inside a COM shim"): <see cref="PlacementSystemAdapter"/> is
/// the real implementation; <c>Bastion.Win32.Tests</c>' <c>FakePlacementSystem</c> is the fake that
/// exercises <see cref="PlacementExecutor"/>'s own batching/quarantine/fallback logic with zero real
/// HWNDs — including the acceptance-criteria-mandated Defer-batch-failure-falls-back-to-per-window
/// test.
/// </summary>
internal interface IPlacementSystem
{
    /// <summary>
    /// Resolves <paramref name="windowId"/> to its current live <c>HWND</c> (the identity
    /// <c>WindowRegistry</c> owns, DESIGN.md §3.3). Returns <see langword="false"/> if the window
    /// has vanished since the Reconciler produced its plan — a routine race, not exceptional.
    /// </summary>
    bool TryResolveHwnd(WindowId windowId, out HWND hwnd);

    /// <summary>
    /// DESIGN.md §3.6a's hang probe: <c>SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG, timeout)</c>.
    /// Returns <see langword="true"/> if the window is (or appears) hung — a bare zero-return check,
    /// deliberately never inspecting <c>GetLastError</c> afterward (verified against
    /// learn.microsoft.com: <c>SendMessageTimeout</c> "does not always call SetLastError on
    /// failure," so a zero return with <c>ERROR_SUCCESS</c> must be treated as a generic failure
    /// regardless — the hang/no-hang distinction this method needs doesn't require the reason).
    /// </summary>
    bool ProbeIsHung(HWND hwnd, TimeSpan timeout);

    /// <summary>Reads the live state <see cref="PlacementExecutor"/> needs to pick its placement branch (DESIGN.md §3.6b).</summary>
    WindowPlacementState ReadPlacementState(HWND hwnd);

    /// <summary>
    /// Reads <paramref name="hwnd"/>'s current <c>GetWindowRect</c> and
    /// <c>DWMWA_EXTENDED_FRAME_BOUNDS</c> fresh (DESIGN.md §3.6c: "never cached per-class").
    /// Returns <see langword="false"/> if the window vanished before this specific call — a routine
    /// race, not exceptional (matching <c>WindowSystemAdapter</c>'s own established handling).
    /// </summary>
    bool TryReadGeometry(HWND hwnd, out Rect windowRect, out Rect frameBounds);

    /// <summary>
    /// The primary monitor's work area, in screen coordinates (<c>SystemParametersInfo(
    /// SPI_GETWORKAREA)</c> — documented to "always return[] the work area of the primary
    /// monitor," per learn.microsoft.com's Multiple Monitor System Metrics page) — the origin
    /// <c>WINDOWPLACEMENT.rcNormalPosition</c>'s workspace coordinates are relative to (DESIGN.md
    /// §3.6b). Queried once per <see cref="PlacementExecutor.Apply"/> pass, not per window: it
    /// cannot change mid-batch.
    /// </summary>
    Rect ReadPrimaryWorkArea();

    /// <summary>
    /// DESIGN.md §3.6b's direct restore-into-tile: <c>SetWindowPlacement</c> with
    /// <paramref name="rcNormalPosition"/> preset to the target,
    /// <c>flags = WPF_ASYNCWINDOWPLACEMENT</c>, <c>showCmd = SW_SHOWNOACTIVATE</c>. The caller has
    /// already converted <paramref name="rcNormalPosition"/> to the correct coordinate space
    /// (workspace vs. screen) via <see cref="PlacementCoordinateConverter"/>.
    /// </summary>
    PlacementCallResult ApplyWindowPlacement(HWND hwnd, Rect rcNormalPosition);

    /// <summary>DESIGN.md §3.6d: <c>BeginDeferWindowPos</c>. A <see langword="null"/>-equivalent (<c>HDWP.IsNull</c>) return means allocation failed; the caller must not proceed to <see cref="TryDefer"/>.</summary>
    HDWP BeginDefer(int windowCount);

    /// <summary>
    /// DESIGN.md §3.6d: one <c>DeferWindowPos</c> call, <c>SWP_NOACTIVATE | SWP_NOZORDER</c>
    /// baked in. Returns <see langword="null"/> on failure — per <c>DeferWindowPos</c>'s own
    /// documented contract, the caller must then abandon <paramref name="batch"/> and never call
    /// <see cref="EndDefer"/> on it. On success, returns the (possibly different — see
    /// <c>DeferWindowPos</c>'s own remarks) <c>HDWP</c> to pass to the next call.
    /// </summary>
    HDWP? TryDefer(HDWP batch, HWND hwnd, Rect screenBounds);

    /// <summary>DESIGN.md §3.6d: <c>EndDeferWindowPos</c> — applies the whole batch in one repaint cycle.</summary>
    bool EndDefer(HDWP batch);

    /// <summary>
    /// DESIGN.md §3.6d's fallback: per-window <c>SetWindowPos</c> with
    /// <c>SWP_ASYNCWINDOWPOS | SWP_NOACTIVATE | SWP_NOZORDER</c> — the standing mode for any window
    /// ever seen hung, and the abandon-the-batch fallback for a <c>DeferWindowPos</c> failure.
    /// </summary>
    PlacementCallResult ApplyWindowPosFallback(HWND hwnd, Rect screenBounds);
}
