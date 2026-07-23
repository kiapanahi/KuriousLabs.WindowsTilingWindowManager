using Bastion.Win32;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// Tier 1 unit tests (docs/engineering/testing.md §3) for <see cref="WindowManageabilityFilter"/>'s
/// pure predicate — DESIGN.md §3.3's filter, exercised entirely against a synthetic
/// <see cref="WindowManageabilityInfo"/>. No real HWND is required or used anywhere in this file.
/// </summary>
public sealed class WindowManageabilityFilterTests
{
    private static readonly WindowClassBlocklist s_blocklist = WindowClassBlocklist.Default;

    private static readonly WindowManageabilityInfo s_manageable = new(
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

    [Fact]
    public void FullyManageableWindowIsManageable()
    {
        Assert.True(WindowManageabilityFilter.IsManageable(s_manageable, s_blocklist));
    }

    [Fact]
    public void NonRootWindowIsNotManageable()
    {
        WindowManageabilityInfo info = s_manageable with { IsRootWindow = false };

        Assert.False(WindowManageabilityFilter.IsManageable(info, s_blocklist));
    }

    [Fact]
    public void InvisibleWindowIsNotManageable()
    {
        WindowManageabilityInfo info = s_manageable with { IsVisible = false };

        Assert.False(WindowManageabilityFilter.IsManageable(info, s_blocklist));
    }

    [Fact]
    public void CloakedWindowIsNotManageable()
    {
        // Not admitted *this call* — DESIGN.md §3.3's "any nonzero cloak value -> keep tracked,
        // never tile, never forget" governs an *already-registered* window (WindowRegistry's own
        // job), not this predicate.
        WindowManageabilityInfo info = s_manageable with { IsCloaked = true };

        Assert.False(WindowManageabilityFilter.IsManageable(info, s_blocklist));
    }

    [Fact]
    public void ToolWindowWithoutAppWindowStyleIsNotManageable()
    {
        WindowManageabilityInfo info = s_manageable with { HasToolWindowStyle = true };

        Assert.False(WindowManageabilityFilter.IsManageable(info, s_blocklist));
    }

    [Fact]
    public void ToolWindowWithAppWindowStyleIsManageable()
    {
        // WS_EX_APPWINDOW is the documented override for WS_EX_TOOLWINDOW — DESIGN.md §3.3.
        WindowManageabilityInfo info = s_manageable with { HasToolWindowStyle = true, HasAppWindowStyle = true };

        Assert.True(WindowManageabilityFilter.IsManageable(info, s_blocklist));
    }

    [Fact]
    public void OwnedWindowWithoutAppWindowStyleIsNotManageable()
    {
        WindowManageabilityInfo info = s_manageable with { HasOwner = true };

        Assert.False(WindowManageabilityFilter.IsManageable(info, s_blocklist));
    }

    [Fact]
    public void OwnedWindowWithAppWindowStyleIsManageable()
    {
        WindowManageabilityInfo info = s_manageable with { HasOwner = true, HasAppWindowStyle = true };

        Assert.True(WindowManageabilityFilter.IsManageable(info, s_blocklist));
    }

    [Fact]
    public void NoActivateWindowIsNotManageable()
    {
        WindowManageabilityInfo info = s_manageable with { HasNoActivateStyle = true };

        Assert.False(WindowManageabilityFilter.IsManageable(info, s_blocklist));
    }

    [Fact]
    public void EmptyRectWindowIsNotManageable()
    {
        WindowManageabilityInfo info = s_manageable with { HasEmptyRect = true };

        Assert.False(WindowManageabilityFilter.IsManageable(info, s_blocklist));
    }

    [Fact]
    public void ShellWindowIsNotManageable()
    {
        WindowManageabilityInfo info = s_manageable with { IsShellWindow = true };

        Assert.False(WindowManageabilityFilter.IsManageable(info, s_blocklist));
    }

    [Fact]
    public void BlocklistedClassNameIsNotManageable()
    {
        WindowManageabilityInfo info = s_manageable with { ClassName = "Progman" };

        Assert.False(WindowManageabilityFilter.IsManageable(info, s_blocklist));
    }

    [Fact]
    public void ClassNameBlocklistIsCaseSensitive()
    {
        // Ordinal, per docs/engineering/quality-gates.md §7 — a differently-cased class name is
        // not the same shell window and must not match.
        WindowManageabilityInfo info = s_manageable with { ClassName = "progman" };

        Assert.True(WindowManageabilityFilter.IsManageable(info, s_blocklist));
    }
}
