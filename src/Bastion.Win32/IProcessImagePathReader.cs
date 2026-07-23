namespace Bastion.Win32;

/// <summary>Third and last rung of DESIGN.md §3.3's identity chain: the owning process's exe path.</summary>
internal interface IProcessImagePathReader
{
    /// <summary>
    /// Returns the full Win32-format executable path for the process identified by
    /// <paramref name="pid"/>, or <see langword="null"/> if it cannot be read (e.g. a protected
    /// process denying <c>OpenProcess</c>).
    /// </summary>
    string? TryGetImagePath(uint pid);
}
