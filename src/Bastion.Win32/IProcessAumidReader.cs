namespace Bastion.Win32;

/// <summary>
/// Second rung of DESIGN.md §3.3's identity chain: the owning process's Application User Model ID,
/// via <c>GetApplicationUserModelId</c>. Also reused by <see cref="IUwpAttributionProvider"/>
/// against a UWP <c>CoreWindow</c> child's process id, so a real implementation must accept an
/// arbitrary PID rather than assume "the window's own process."
/// </summary>
internal interface IProcessAumidReader
{
    /// <summary>
    /// Returns the AUMID for the process identified by <paramref name="pid"/>, or
    /// <see langword="null"/> if the process has no application identity
    /// (<c>APPMODEL_ERROR_NO_APPLICATION</c> — an expected, non-error outcome for an ordinary
    /// desktop app) or the read otherwise fails.
    /// </summary>
    string? TryGetAumid(uint pid);
}
