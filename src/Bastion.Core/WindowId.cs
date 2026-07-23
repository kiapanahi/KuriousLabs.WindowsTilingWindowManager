namespace Bastion.Core;

/// <summary>
/// Opaque identifier for a managed window, stable across HWND recycling.
/// </summary>
/// <remarks>
/// No <c>HWND</c> ever crosses into <c>Bastion.Core</c> or <c>Bastion.Layout</c> — the adapter
/// ring (<c>Bastion.Win32</c>) is solely responsible for minting a <see cref="WindowId"/> per
/// live window, for HWND recycling detection, and for PID/first-seen-timestamp bookkeeping.
/// See DESIGN.md §3, §10.
///
/// Minting strategy (DESIGN.md §3.4, GitHub issue #3): a process-lifetime monotonic counter,
/// implemented by <c>Bastion.Win32</c>'s <c>WindowIdMinter</c> and called exactly once per
/// <em>new</em> Window Registry entry — never per HWND value, since HWNDs recycle and a
/// re-admitted window after a missed <c>EVENT_OBJECT_DESTROY</c> still gets a fresh id. The
/// opaque numeric value carries no meaning outside <c>Bastion.Win32</c> beyond this file's own
/// "stable, equatable" contract — do not derive ordering, persistence, or cross-run identity from
/// it.
/// </remarks>
public readonly record struct WindowId
{
    private readonly ulong _value;

    private WindowId(ulong value) => _value = value;

    /// <summary>Creates a <see cref="WindowId"/> from an adapter-minted opaque value.</summary>
    /// <remarks>
    /// Only <c>Bastion.Win32</c> should call this in practice; it is public because the seam
    /// (DESIGN.md §3, §10 — no HWND in Core/Layout, but the opaque value must be constructible
    /// from the adapter ring in a different assembly) requires it.
    /// </remarks>
    public static WindowId FromOpaqueValue(ulong value) => new(value);

    public override string ToString() => $"WindowId({_value})";
}
