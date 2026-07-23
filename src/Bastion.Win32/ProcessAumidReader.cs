using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;

namespace Bastion.Win32;

/// <summary>
/// Real <see cref="IProcessAumidReader"/>, backed by <c>GetApplicationUserModelId</c>.
/// </summary>
/// <remarks>
/// DOCUMENTED CONTRACT (verified against
/// https://learn.microsoft.com/windows/win32/api/appmodel/nf-appmodel-getapplicationusermodelid):
/// <c>LONG GetApplicationUserModelId(HANDLE hProcess, UINT32* applicationUserModelIdLength, PWSTR
/// applicationUserModelId)</c>; the handle needs <c>PROCESS_QUERY_LIMITED_INFORMATION</c>. Two-call
/// pattern per the docs' own C example: a probe call with a zero-length buffer returns
/// <c>ERROR_INSUFFICIENT_BUFFER</c> and writes the required length (including the null
/// terminator) back through <paramref name="pid"/>'s length parameter; a second call with a
/// buffer of that size retrieves the value. <c>APPMODEL_ERROR_NO_APPLICATION</c> means "this is an
/// ordinary desktop process with no AUMID" — an expected, non-error outcome to fall through on;
/// this reader does not special-case it by name, since <em>any</em> non-<c>ERROR_INSUFFICIENT_BUFFER</c>
/// probe result (including that one) is already the correct signal to stop and return
/// <see langword="null"/>.
/// </remarks>
internal sealed class ProcessAumidReader : IProcessAumidReader
{
    /// <inheritdoc/>
    public string? TryGetAumid(uint pid)
    {
        using SafeFileHandle processHandle = PInvoke.OpenProcess_SafeHandle(
            PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, bInheritHandle: false, pid);
        if (processHandle.IsInvalid)
        {
            return null;
        }

        uint length = 0;
        WIN32_ERROR probeResult = PInvoke.GetApplicationUserModelId(processHandle, ref length, default);
        if (probeResult != WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER || length == 0)
        {
            // Covers APPMODEL_ERROR_NO_APPLICATION (ordinary desktop process, no AUMID — expected)
            // and every other unexpected outcome alike: none of them leave anything worth a
            // second call.
            return null;
        }

        var buffer = new char[length];
        WIN32_ERROR result = PInvoke.GetApplicationUserModelId(processHandle, ref length, buffer);
        if (result != WIN32_ERROR.ERROR_SUCCESS || length == 0)
        {
            return null;
        }

        // length includes the null terminator on success, per the documented [in, out] contract.
        int contentLength = (int)length - 1;
        return contentLength <= 0 ? null : new string(buffer, 0, contentLength);
    }
}
