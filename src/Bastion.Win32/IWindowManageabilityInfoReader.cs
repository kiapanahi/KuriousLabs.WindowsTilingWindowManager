using Windows.Win32.Foundation;

namespace Bastion.Win32;

/// <summary>
/// Seam over gathering a live window's <see cref="WindowManageabilityInfo"/> snapshot, so
/// <see cref="WindowRegistry"/>'s admission logic is unit-testable with a fake (see
/// <c>Bastion.Win32.Tests</c>'s <c>FakeWindowManageabilityInfoReader</c>) without a real window —
/// the same "extract a small testable seam" pattern <c>WinEventFilter</c>/
/// <c>WinEventRootNormalizer</c> use for the WinEvent pump (GitHub issue #1).
/// </summary>
internal interface IWindowManageabilityInfoReader
{
    /// <summary>
    /// Reads every field <see cref="WindowManageabilityFilter"/> needs from the live window
    /// identified by <paramref name="hwnd"/>.
    /// </summary>
    WindowManageabilityInfo Read(HWND hwnd);
}
