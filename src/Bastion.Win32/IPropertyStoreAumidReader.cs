using Windows.Win32.Foundation;

namespace Bastion.Win32;

/// <summary>
/// First rung of DESIGN.md §3.3's identity chain: the window's own <c>PKEY_AppUserModel_ID</c>
/// property, via <c>SHGetPropertyStoreForWindow</c>. Async because the underlying
/// <c>IPropertyStore</c> call must run on <see cref="ShellComThread"/> (interop.md §5) — not
/// because the work itself is long-running.
/// </summary>
internal interface IPropertyStoreAumidReader
{
    /// <summary>
    /// Returns the window's explicit AUMID, or <see langword="null"/> if it has none or the read
    /// fails.
    /// </summary>
    Task<string?> TryGetAumidAsync(HWND hwnd, CancellationToken cancellationToken = default);
}
