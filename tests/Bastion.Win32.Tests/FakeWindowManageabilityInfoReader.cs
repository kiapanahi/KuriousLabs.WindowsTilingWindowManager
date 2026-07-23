using Bastion.Win32;
using Windows.Win32.Foundation;

namespace Bastion.Win32.Tests;

/// <summary>Configurable <see cref="IWindowManageabilityInfoReader"/> fake for <see cref="WindowRegistryTests"/>.</summary>
internal sealed class FakeWindowManageabilityInfoReader : IWindowManageabilityInfoReader
{
    private static readonly WindowManageabilityInfo s_defaultManageableInfo = new(
        IsRootWindow: true,
        IsVisible: true,
        IsCloaked: false,
        HasOwner: false,
        HasToolWindowStyle: false,
        HasAppWindowStyle: false,
        HasNoActivateStyle: false,
        HasEmptyRect: false,
        IsShellWindow: false,
        ClassName: "SomeOrdinaryApp");

    private readonly Dictionary<HWND, WindowManageabilityInfo> _infoByHwnd = [];

    /// <summary>Every unconfigured HWND reads as manageable, unless overridden.</summary>
    public WindowManageabilityInfo Default { get; set; } = s_defaultManageableInfo;

    public void SetInfo(HWND hwnd, WindowManageabilityInfo info) => _infoByHwnd[hwnd] = info;

    public WindowManageabilityInfo Read(HWND hwnd) => _infoByHwnd.GetValueOrDefault(hwnd, Default);
}
