using System.Runtime.InteropServices;
using Windows.Win32.Foundation;

namespace Bastion.Win32;

/// <summary>
/// The result of one <c>RegisterHotKey</c> call: success, or a preserved Win32 error code. Mirrors
/// <see cref="PlacementCallResult"/>'s shape for the identical reason:
/// <c>Marshal.GetLastPInvokeError()</c>, captured immediately on failure, is the correct way to
/// obtain <see cref="ErrorCode"/> — confirmed against the actual CsWin32-generated
/// <c>RegisterHotKey</c> partial while implementing GitHub issue #7 (see
/// <see cref="HotkeyRegistrationSystemAdapter"/>'s remarks), which carries the identical
/// <c>Marshal.SetLastSystemError(0)</c>/<c>Marshal.SetLastPInvokeError(...)</c> bridge
/// <c>SetWindowPos</c> et al. do (docs/engineering/interop.md §6).
/// </summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct HotkeyCallResult(bool Success, WIN32_ERROR? ErrorCode)
{
    /// <summary>The shared success instance — no error code to carry.</summary>
    public static HotkeyCallResult Ok { get; } = new(true, null);

    /// <summary>Creates a failure result carrying <paramref name="errorCode"/>.</summary>
    public static HotkeyCallResult Fail(WIN32_ERROR errorCode) => new(false, errorCode);
}
