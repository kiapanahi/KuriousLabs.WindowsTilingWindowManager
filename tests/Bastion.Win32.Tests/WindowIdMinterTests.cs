using Bastion.Core;
using Bastion.Win32;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>Tier 1 unit tests (docs/engineering/testing.md §3) for <see cref="WindowIdMinter"/>.</summary>
public sealed class WindowIdMinterTests
{
    [Fact]
    public void SuccessiveMintsProduceDistinctIds()
    {
        var minter = new WindowIdMinter();

        WindowId first = minter.Mint();
        WindowId second = minter.Mint();

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void MintedIdIsNeverTheDefaultWindowId()
    {
        var minter = new WindowIdMinter();

        WindowId minted = minter.Mint();

        Assert.NotEqual(default, minted);
    }

    [Fact]
    public void ConcurrentMintsAreAllDistinct()
    {
        var minter = new WindowIdMinter();
        const int mintCount = 1000;

        var results = new WindowId[mintCount];
        Parallel.For(0, mintCount, i => results[i] = minter.Mint());

        Assert.Equal(mintCount, results.Distinct().Count());
    }
}
