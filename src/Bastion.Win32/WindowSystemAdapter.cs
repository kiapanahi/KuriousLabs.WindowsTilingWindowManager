using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Bastion.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;

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

            builder.Add(ReadObservedWindow(id, hwnd));
        }

        return builder.ToImmutable();
    }

    private ObservedWindow ReadObservedWindow(WindowId windowId, HWND hwnd)
    {
        _ = WindowProbe.TryGetBounds(hwnd, out RECT windowRect);
        RECT frameBoundsRect = TryGetExtendedFrameBounds(hwnd, fallback: windowRect);

        return new ObservedWindow(
            windowId,
            ToRect(frameBoundsRect),
            ToRect(windowRect),
            IsCloaked: cloakStateReader.IsCloaked(hwnd),
            IsIconic: PInvoke.IsIconic(hwnd),
            IsZoomed: PInvoke.IsZoomed(hwnd));
    }

    /// <summary>
    /// DOCUMENTED CONTRACT (verified against
    /// https://learn.microsoft.com/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute and
    /// https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getwindowrect):
    /// <c>DWMWA_EXTENDED_FRAME_BOUNDS</c> "retrieves the extended frame bounds rectangle in screen
    /// space[, of type] RECT," and <c>GetWindowRect</c>'s own remarks point here explicitly to get
    /// "the visible window bounds, not including the invisible resize borders" — noting the two
    /// readings are <em>not</em> both DPI-virtualized ("unlike the Window Rect, the DWM Extended
    /// Frame Bounds are not adjusted for DPI"), a fact DESIGN.md §8 already accounts for via
    /// PerMonitorV2. Falls back to <paramref name="fallback"/> (the raw <c>GetWindowRect</c>
    /// reading) on a failing <c>HRESULT</c> (e.g. the window was destroyed between enumeration and
    /// this call — a routine race, not exceptional), matching <see cref="DwmCloakStateReader"/>'s
    /// own conservative-default pattern for this exact call family.
    /// </summary>
    private static RECT TryGetExtendedFrameBounds(HWND hwnd, RECT fallback)
    {
        // RECT is four sequential 4-byte LONG (int32) fields, 16 bytes total, matching
        // DwmCloakStateReader's own "size the stackalloc buffer to the documented pvAttribute
        // type" convention (interop.md §1).
        Span<byte> buffer = stackalloc byte[sizeof(int) * 4];

        HRESULT hr;
        unsafe
        {
            // The Span<byte> overload's generated signature is itself `unsafe` (it takes the
            // address of the span internally) even though nothing in this call site needs a raw
            // pointer of our own — CsWin32 emits it this way (interop.md §1: "confirm by
            // inspecting the generated partial"), matching DwmCloakStateReader's identical call
            // shape for DWMWA_CLOAKED.
            hr = PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, buffer);
        }

        return hr.Succeeded ? MemoryMarshal.Read<RECT>(buffer) : fallback;
    }

    private static Rect ToRect(RECT rect) => new(rect.left, rect.top, rect.right, rect.bottom);
}
