using Windows.Win32.Foundation;

namespace Bastion.Win32;

/// <summary>
/// Seam over DESIGN.md §3.3's full identity-resolution chain, so <see cref="WindowRegistry"/>'s
/// own tests can substitute a trivial fake rather than wiring up the chain's four real
/// dependencies (see <c>Bastion.Win32.Tests</c>'s <c>FakeWindowIdentityResolver</c>).
/// </summary>
internal interface IWindowIdentityResolver
{
    /// <summary>
    /// Resolves the best available identity for the window <paramref name="hwnd"/>, owned by
    /// process <paramref name="pid"/>.
    /// </summary>
    Task<WindowIdentity> ResolveAsync(HWND hwnd, uint pid, CancellationToken cancellationToken = default);
}
