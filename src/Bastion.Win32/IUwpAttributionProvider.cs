using Windows.Win32.Foundation;

namespace Bastion.Win32;

/// <summary>
/// Seam isolating UWP identity attribution (DESIGN.md §3.3/§9's ApplicationFrameHost row):
/// <c>ApplicationFrameWindow → EnumChildWindows for the Windows.UI.Core.CoreWindow child → child
/// PID → AUMID</c>. Sits in front of <see cref="WindowIdentityResolver"/>'s
/// <c>IPropertyStore</c>/process-AUMID/exe-path chain specifically for this case, because the
/// ApplicationFrameWindow's <em>own</em> owning process is the shared <c>ApplicationFrameHost.exe</c>
/// host — not the actual per-app process — so resolving identity from the frame window's own PID
/// (the chain's later rungs) would attribute every running UWP app to the same host process.
/// </summary>
/// <remarks>
/// This hosting structure is <b>observed behavior, not a documented contract</b> — DESIGN.md §9's
/// own framing, and the reason this is its own seam rather than folded into
/// <see cref="WindowIdentityResolver"/> directly: a servicing change to how UWP frame hosting works
/// degrades gracefully, since <see cref="TryGetAumid"/> returning <see langword="null"/> here is
/// exactly what makes <see cref="WindowIdentityResolver"/> fall through to the exe-path identity of
/// the frame window's own (host) process — degraded, but never a hard failure. Per DESIGN.md §3.3's
/// own scoping, this seam's Tier-5 canary is tracked separately in the DESIGN.md §13 risk-register
/// issue, not duplicated here.
/// </remarks>
internal interface IUwpAttributionProvider
{
    /// <summary>
    /// Returns the AUMID attributed to <paramref name="hwnd"/> via its <c>CoreWindow</c> child's
    /// owning process, or <see langword="null"/> if <paramref name="hwnd"/> is not an
    /// ApplicationFrameWindow, has no such child, or the child's process has no AUMID.
    /// </summary>
    string? TryGetAumid(HWND hwnd);
}
