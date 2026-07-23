using Windows.Win32.Foundation;

namespace Bastion.Win32;

/// <summary>
/// Seam over reading a window's owning process id, so <see cref="WindowRegistry"/>'s own
/// bookkeeping logic — admission idempotency, HWND-recycling eviction, purge — is unit-testable
/// with a fake (see <c>Bastion.Win32.Tests</c>'s <c>FakeWindowProcessIdReader</c>) without a real
/// window, the same rationale as <see cref="IWindowManageabilityInfoReader"/>.
/// </summary>
internal interface IWindowProcessIdReader
{
    /// <summary>
    /// Returns the process id that owns <paramref name="hwnd"/>, or <see langword="null"/> if
    /// <paramref name="hwnd"/> is no longer a valid window handle.
    /// </summary>
    uint? TryReadProcessId(HWND hwnd);
}
