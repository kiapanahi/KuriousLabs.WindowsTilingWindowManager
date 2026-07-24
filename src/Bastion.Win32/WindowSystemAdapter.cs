using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Bastion.Core;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Bastion.Win32;

/// <summary>
/// The real <see cref="IWindowSystem"/>: DESIGN.md §3.4's heartbeat read, "<c>EnumWindows</c> +
/// per-window <c>GetWindowRect</c> + <c>DWMWA_EXTENDED_FRAME_BOUNDS</c> + <c>DWMWA_CLOAKED</c> +
/// <c>IsIconic</c>/<c>IsZoomed</c>," wrapping <see cref="WindowRegistry"/> for admission/identity.
/// </summary>
/// <remarks>
/// <para>
/// <b>Admission is sequential, never concurrent, by construction.</b> <see cref="ReadAllAsync"/>
/// awaits <see cref="WindowRegistry.TryAdmitAsync"/> once per enumerated window, one at a time, in
/// a plain <see langword="foreach"/> loop — never <c>Task.WhenAll</c> across the whole enumeration.
/// This is deliberate, not merely convenient: every admission that needs identity resolution
/// funnels through the single dedicated <c>ShellComThread</c> regardless (interop.md §5), so
/// firing concurrent <see cref="Task"/>s here would only queue up on that same thread for zero
/// parallelism benefit, while adding real risk — two overlapping admissions for windows that
/// happen to share a recycled <c>HWND</c> mid-enumeration is exactly the race
/// <see cref="WindowRegistry"/>'s own remarks describe defending against with its lock-and-recheck
/// pattern, and staying sequential here is the simplest way to never manufacture that race in the
/// first place from this call site.
/// </para>
/// <para>
/// <b>GetWindowDesktopId is deliberately not read here.</b> DESIGN.md §3.3/§3.4 name
/// <c>GetWindowDesktopId</c> (<c>IVirtualDesktopManager</c>) among the heartbeat's reads, but the
/// method returns only a desktop <see cref="Guid"/> with no documented way to obtain "the current
/// desktop's GUID" to compare it against (DESIGN.md §4: "no current-desktop query" exists) — the
/// method that actually answers "is this window on the desktop the user is looking at right now,"
/// <c>IsWindowOnCurrentVirtualDesktop</c>, is a <em>different</em>, unlisted method. The only thing
/// that makes a bare desktop-GUID reading actionable is DESIGN.md §4's "independent, persistent
/// layout tree per (native-desktop GUID × monitor)" partition model — GitHub issue #26's explicit,
/// named deliverable, not yet built. Since v0.1 (DESIGN.md §12) has no multi-desktop partitioning
/// at all, a window's desktop membership is not yet consulted by any decision regardless of
/// whether it is read — so deferring the read changes no runtime behavior today and avoids
/// speculatively wiring a second COM interface (<c>IVirtualDesktopManager</c>, alongside issue #3's
/// <c>IPropertyStore</c>) and a field nothing yet consumes ahead of its first real use. Issue #26
/// is the natural single owner of both the read and its consumption together.
/// </para>
/// </remarks>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "Constructed by tests via InternalsVisibleTo today, and intended to be " +
        "registered as the production IWindowSystem once Bastion.Daemon's composition root is " +
        "wired (GitHub issue #10) — not yet wired as of this change. Same documented CA1812 " +
        "false-positive shape as Coalescer/WinEventPumpService/BastiondService.")]
internal sealed class WindowSystemAdapter(WindowRegistry registry, ICloakStateReader cloakStateReader) : IWindowSystem
{
    /// <inheritdoc/>
    public async Task<ImmutableArray<ObservedWindow>> ReadAllAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<HWND> visibleWindows = WindowProbe.EnumerateVisibleTopLevelWindows();
        ImmutableArray<ObservedWindow>.Builder builder = ImmutableArray.CreateBuilder<ObservedWindow>(visibleWindows.Count);

        foreach (HWND hwnd in visibleWindows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Sequential await, never concurrent — see this type's remarks.
            WindowId? windowId = await registry.TryAdmitAsync(hwnd, cancellationToken).ConfigureAwait(false);
            if (windowId is not { } id)
            {
                // Not manageable, or vanished between enumeration and this call — a routine race
                // (WindowRegistry.TryAdmitAsync's own doc remarks), not exceptional.
                continue;
            }

            if (TryReadObservedWindow(id, hwnd, out ObservedWindow observedWindow))
            {
                builder.Add(observedWindow);
            }
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// Reads <paramref name="hwnd"/>'s geometry/state into <paramref name="observedWindow"/>.
    /// Returns <see langword="false"/> (and <paramref name="observedWindow"/> is
    /// <see langword="default"/>) if <c>GetWindowRect</c> itself fails — e.g. the window was
    /// destroyed between enumeration/admission and this call, a routine race, not exceptional
    /// (matching <see cref="WindowProbe.TryGetBounds"/>'s own doc remarks). This window is skipped
    /// entirely for this tick rather than reported with degenerate <c>(0,0,0,0)</c> bounds, which
    /// would otherwise drive a spurious <see cref="PlacementAction.Move"/>/<see cref="PlacementAction.Untile"/>
    /// decision for a window that is already gone (Copilot review finding on this PR) — the next
    /// convergence pass naturally forgets it once <c>EnumWindows</c> stops reporting it at all.
    /// </summary>
    /// <remarks>
    /// DOCUMENTED CONTRACT for <c>IsIconic</c>/<c>IsZoomed</c> (verified against
    /// https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-isiconic and
    /// https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-iszoomed): "Determines
    /// whether the specified window is minimized"/"whether a window is maximized" — a nonzero
    /// <c>BOOL</c> when true, zero otherwise. Neither documents a distinct failure mode for an
    /// already-destroyed handle the way <c>GetWindowRect</c>/<c>DwmGetWindowAttribute</c> do, so no
    /// separate fallback handling applies to these two calls specifically.
    /// </remarks>
    private bool TryReadObservedWindow(WindowId windowId, HWND hwnd, out ObservedWindow observedWindow)
    {
        if (!WindowProbe.TryGetBounds(hwnd, out RECT windowRect))
        {
            observedWindow = default;
            return false;
        }

        RECT frameBoundsRect = TryGetExtendedFrameBounds(hwnd, fallback: windowRect);
        observedWindow = new ObservedWindow(
            windowId,
            ToRect(frameBoundsRect),
            ToRect(windowRect),
            IsCloaked: cloakStateReader.IsCloaked(hwnd),
            IsIconic: PInvoke.IsIconic(hwnd),
            IsZoomed: PInvoke.IsZoomed(hwnd));
        return true;
    }

    /// <summary>
    /// Falls back to <paramref name="fallback"/> (the raw <c>GetWindowRect</c> reading) when
    /// <see cref="WindowProbe.TryGetExtendedFrameBounds"/> fails (e.g. the window was destroyed
    /// between enumeration and this call — a routine race, not exceptional), matching
    /// <see cref="DwmCloakStateReader"/>'s own conservative-default pattern for this exact call
    /// family. See <see cref="WindowProbe.TryGetExtendedFrameBounds"/> for the documented-contract
    /// citation (shared with <see cref="PlacementSystemAdapter"/>, GitHub issue #5, rather than
    /// duplicated here a second time).
    /// </summary>
    private static RECT TryGetExtendedFrameBounds(HWND hwnd, RECT fallback) =>
        WindowProbe.TryGetExtendedFrameBounds(hwnd, out RECT frameBounds) ? frameBounds : fallback;

    private static Rect ToRect(RECT rect) => new(rect.left, rect.top, rect.right, rect.bottom);
}
