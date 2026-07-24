using System.Runtime.InteropServices;
using Windows.Win32.Foundation;

namespace Bastion.Win32;

/// <summary>
/// The result of one <c>SetWindowPlacement</c>/<c>SetWindowPos</c>/<c>DeferWindowPos</c>/
/// <c>EndDeferWindowPos</c> call: success, or a preserved Win32 error code. See
/// <see cref="PlacementSystemAdapter"/>'s remarks for why <c>Marshal.GetLastPInvokeError()</c>,
/// captured immediately on failure, is the correct way to obtain <see cref="ErrorCode"/> for every
/// one of these calls under this project's <c>DisableRuntimeMarshalling</c> configuration.
/// </summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct PlacementCallResult(bool Success, WIN32_ERROR? ErrorCode)
{
    /// <summary>The shared success instance — no error code to carry.</summary>
    public static PlacementCallResult Ok { get; } = new(true, null);

    /// <summary>Creates a failure result carrying <paramref name="errorCode"/>.</summary>
    public static PlacementCallResult Fail(WIN32_ERROR errorCode) => new(false, errorCode);
}
