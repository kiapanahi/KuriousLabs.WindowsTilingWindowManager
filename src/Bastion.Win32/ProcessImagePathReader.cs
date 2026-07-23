using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.System.Threading;

namespace Bastion.Win32;

/// <summary>
/// Real <see cref="IProcessImagePathReader"/>, backed by <c>OpenProcess</c> +
/// <c>QueryFullProcessImageNameW</c>.
/// </summary>
/// <remarks>
/// DOCUMENTED CONTRACT (verified against
/// https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-queryfullprocessimagenamew and
/// https://learn.microsoft.com/windows/win32/procthread/process-security-and-access-rights):
/// <c>BOOL QueryFullProcessImageNameW(HANDLE hProcess, DWORD dwFlags, LPWSTR lpExeName, PDWORD
/// lpdwSize)</c>, needing <c>PROCESS_QUERY_LIMITED_INFORMATION</c> (or the broader
/// <c>PROCESS_QUERY_INFORMATION</c>); <c>dwFlags = 0</c> selects the Win32 path format (the
/// alternative, <c>PROCESS_NAME_NATIVE</c>, is the NT-device-path format Bastion never wants
/// here). Unlike <c>GetApplicationUserModelId</c>, the docs do not describe a probe-for-required-size
/// failure mode for this API — a fixed, generously-sized buffer is called once. 4096 characters
/// comfortably covers every practical Win32 executable path (well beyond the classic
/// <c>MAX_PATH</c> of 260) without resorting to an unbounded/attacker-sized allocation.
/// </remarks>
internal sealed class ProcessImagePathReader : IProcessImagePathReader
{
    private const int MaxImagePathLength = 4096;

    /// <inheritdoc/>
    public string? TryGetImagePath(uint pid)
    {
        using SafeFileHandle processHandle = PInvoke.OpenProcess_SafeHandle(
            PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, bInheritHandle: false, pid);
        if (processHandle.IsInvalid)
        {
            return null;
        }

        var buffer = new char[MaxImagePathLength];
        uint size = MaxImagePathLength;
        bool succeeded = PInvoke.QueryFullProcessImageName(
            processHandle, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, ref size);
        if (!succeeded || size == 0)
        {
            return null;
        }

        return new string(buffer, 0, (int)size);
    }
}
