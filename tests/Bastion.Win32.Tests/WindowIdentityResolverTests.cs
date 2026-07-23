using Bastion.Win32;
using Windows.Win32.Foundation;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// Tier 1 unit tests (docs/engineering/testing.md §3) for <see cref="WindowIdentityResolver"/>'s
/// DESIGN.md §3.3 fallthrough chain — every rung faked, no real HWND/process/COM call anywhere in
/// this file (the acceptance criterion's "fake IPropertyStore-touching step" is
/// <see cref="FakePropertyStoreAumidReader"/>).
/// </summary>
public sealed class WindowIdentityResolverTests
{
    private static readonly HWND s_someWindow = new((IntPtr)1);
    private const uint SomePid = 4242;

    private readonly FakeUwpAttributionProvider _uwp = new();
    private readonly FakePropertyStoreAumidReader _propertyStore = new();
    private readonly FakeProcessAumidReader _processAumid = new();
    private readonly FakeProcessImagePathReader _processImagePath = new();

    private WindowIdentityResolver CreateResolver() =>
        new(_uwp, _propertyStore, _processAumid, _processImagePath);

    [Fact]
    public async Task UwpAttributionSuccessShortCircuitsTheRestOfTheChain()
    {
        _uwp.Result = "Contoso.App_abc123!App";

        WindowIdentity identity = await CreateResolver().ResolveAsync(s_someWindow, SomePid, TestContext.Current.CancellationToken);

        Assert.Equal(WindowIdentityKind.Aumid, identity.Kind);
        Assert.Equal("Contoso.App_abc123!App", identity.Value);
        Assert.Equal(0, _propertyStore.CallCount);
        Assert.Equal(0, _processAumid.CallCount);
        Assert.Equal(0, _processImagePath.CallCount);
    }

    [Fact]
    public async Task PropertyStoreAumidIsTriedWhenUwpAttributionFails()
    {
        _propertyStore.Result = "Contoso.App_abc123!Window";

        WindowIdentity identity = await CreateResolver().ResolveAsync(s_someWindow, SomePid, TestContext.Current.CancellationToken);

        Assert.Equal(WindowIdentityKind.Aumid, identity.Kind);
        Assert.Equal("Contoso.App_abc123!Window", identity.Value);
        Assert.Equal(1, _uwp.CallCount);
        Assert.Equal(0, _processAumid.CallCount);
        Assert.Equal(0, _processImagePath.CallCount);
    }

    [Fact]
    public async Task ProcessAumidIsTriedWhenUwpAndPropertyStoreBothFail()
    {
        _processAumid.Result = "Contoso.App_abc123!Default";

        WindowIdentity identity = await CreateResolver().ResolveAsync(s_someWindow, SomePid, TestContext.Current.CancellationToken);

        Assert.Equal(WindowIdentityKind.Aumid, identity.Kind);
        Assert.Equal("Contoso.App_abc123!Default", identity.Value);
        Assert.Equal(0, _processImagePath.CallCount);
    }

    [Fact]
    public async Task ExePathIsTheLastResortWhenNoAumidRungSucceeds()
    {
        _processImagePath.Result = @"C:\Program Files\Contoso\App.exe";

        WindowIdentity identity = await CreateResolver().ResolveAsync(s_someWindow, SomePid, TestContext.Current.CancellationToken);

        Assert.Equal(WindowIdentityKind.ExePath, identity.Kind);
        Assert.Equal(@"C:\Program Files\Contoso\App.exe", identity.Value);
    }

    [Fact]
    public async Task UnknownIsReturnedWhenEveryRungFails()
    {
        WindowIdentity identity = await CreateResolver().ResolveAsync(s_someWindow, SomePid, TestContext.Current.CancellationToken);

        Assert.Equal(WindowIdentity.Unknown, identity);
    }

    [Fact]
    public async Task CancellationBeforeThePropertyStoreRungThrows()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => CreateResolver().ResolveAsync(s_someWindow, SomePid, cts.Token));
    }
}
