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
/// TODO(DESIGN.md §3.4): the adapter ring's identity-minting strategy (monotonic counter vs.
/// content-derived key) is not yet finalized; <see cref="Value"/>'s only contract here is
/// "opaque, stable, equatable" — do not assign meaning to its numeric value outside
/// <c>Bastion.Win32</c>.
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
