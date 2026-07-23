using Bastion.Win32;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>Tier 1 unit tests (docs/engineering/testing.md §3) for <see cref="WindowClassBlocklist"/>.</summary>
public sealed class WindowClassBlocklistTests
{
    [Theory]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    [InlineData("Shell_TrayWnd")]
    [InlineData("Shell_SecondaryTrayWnd")]
    public void DefaultBlocklistContainsShellChromeClasses(string className)
    {
        Assert.True(WindowClassBlocklist.Default.Contains(className));
    }

    [Fact]
    public void DefaultBlocklistDoesNotContainAnOrdinaryAppClassName()
    {
        Assert.False(WindowClassBlocklist.Default.Contains("SomeOrdinaryApp"));
    }

    [Fact]
    public void ContainsIsOrdinalCaseSensitive()
    {
        Assert.False(WindowClassBlocklist.Default.Contains("progman"));
    }

    [Fact]
    public void InjectedBlocklistIsNotBoundToDefault()
    {
        var blocklist = new WindowClassBlocklist(new HashSet<string>(StringComparer.Ordinal) { "CustomShellClass" });

        Assert.True(blocklist.Contains("CustomShellClass"));
        Assert.False(blocklist.Contains("Progman"));
    }
}
