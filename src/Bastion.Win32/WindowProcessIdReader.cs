using Windows.Win32;
using Windows.Win32.Foundation;

namespace Bastion.Win32;

/// <summary>
/// Real <see cref="IWindowProcessIdReader"/>, backed by <c>GetWindowThreadProcessId</c>.
/// </summary>
/// <remarks>
/// DOCUMENTED CONTRACT (verified against
/// https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getwindowthreadprocessid):
/// "If the window handle is invalid, the return value is zero" — the thread id, not the process id
/// written through <c>lpdwProcessId</c>, but a zero thread id is exactly the signal that no valid
/// process id was written either, so both are treated as the single "window no longer valid"
/// outcome.
/// </remarks>
internal sealed class WindowProcessIdReader : IWindowProcessIdReader
{
    /// <inheritdoc/>
    public uint? TryReadProcessId(HWND hwnd) =>
        PInvoke.GetWindowThreadProcessId(hwnd, out uint pid) == 0 ? null : pid;
}
