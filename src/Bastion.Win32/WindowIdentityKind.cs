namespace Bastion.Win32;

/// <summary>Which rung of DESIGN.md §3.3's identity-resolution chain produced a <see cref="WindowIdentity"/>.</summary>
internal enum WindowIdentityKind
{
    /// <summary>
    /// No rung of the chain succeeded — <c>IPropertyStore</c>, process AUMID, and exe path all
    /// failed (e.g. a protected process denying <c>OpenProcess</c>). Total, expected-possible
    /// failure; callers must handle this rather than assume identity resolution always succeeds.
    /// </summary>
    Unknown,

    /// <summary>
    /// <see cref="WindowIdentity.Value"/> is an Application User Model ID, resolved via UWP
    /// attribution, the window's own <c>IPropertyStore</c>, or the owning process's AUMID.
    /// </summary>
    Aumid,

    /// <summary>
    /// <see cref="WindowIdentity.Value"/> is a Win32 executable path, resolved via
    /// <c>QueryFullProcessImageNameW</c> — the chain's last-resort rung.
    /// </summary>
    ExePath,
}
