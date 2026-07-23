using Bastion.Core;
using Xunit;

namespace Bastion.Core.Tests;

/// <summary>Tier 1 unit tests (docs/engineering/testing.md §3) for the opaque <see cref="WindowId"/>.</summary>
public sealed class WindowIdTests
{
    [Fact]
    public void SameOpaqueValueProducesEqualWindowId()
    {
        var first = WindowId.FromOpaqueValue(42);
        var second = WindowId.FromOpaqueValue(42);

        Assert.Equal(first, second);
    }

    [Fact]
    public void DifferentOpaqueValuesProduceUnequalWindowIds()
    {
        var first = WindowId.FromOpaqueValue(1);
        var second = WindowId.FromOpaqueValue(2);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ToStringIncludesTheOpaqueValue()
    {
        var windowId = WindowId.FromOpaqueValue(7);

        Assert.Equal("WindowId(7)", windowId.ToString());
    }
}
