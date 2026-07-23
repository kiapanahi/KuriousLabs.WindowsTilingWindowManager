using Bastion.Win32;
using Windows.Win32.Foundation;

namespace Bastion.Win32.Tests;

/// <summary>Configurable <see cref="IWindowProcessIdReader"/> fake for <see cref="WindowRegistryTests"/>.</summary>
internal sealed class FakeWindowProcessIdReader : IWindowProcessIdReader
{
    private readonly Dictionary<HWND, uint> _pidsByHwnd = [];

    /// <summary>Sets the pid <see cref="TryReadProcessId"/> returns for <paramref name="hwnd"/>.</summary>
    public void SetPid(HWND hwnd, uint pid) => _pidsByHwnd[hwnd] = pid;

    /// <summary>Removes any pid previously set for <paramref name="hwnd"/> — simulates a vanished window.</summary>
    public void RemovePid(HWND hwnd) => _pidsByHwnd.Remove(hwnd);

    public uint? TryReadProcessId(HWND hwnd) => _pidsByHwnd.TryGetValue(hwnd, out uint pid) ? pid : null;
}
