using Bastion.Core;
using Bastion.Win32;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// Pure-logic unit tests for <see cref="RuleKeyResolver"/> -- the GitHub issue #6 stand-in mapping
/// from a resolved <see cref="WindowIdentity"/> to a <see cref="RuleKey"/>. No real HWND/identity
/// resolution involved; every case is a synthetic <see cref="WindowIdentity"/> value.
/// </summary>
public sealed class RuleKeyResolverTests
{
    [Fact]
    public void AumidIdentityProducesAnAumidPrefixedRuleKey()
    {
        var identity = new WindowIdentity(WindowIdentityKind.Aumid, "Contoso.Example_1.0.0.0_x64__abcdef");

        RuleKey ruleKey = RuleKeyResolver.Resolve(identity, className: "SomeClass");

        Assert.Equal(new RuleKey("aumid:Contoso.Example_1.0.0.0_x64__abcdef"), ruleKey);
    }

    [Fact]
    public void ExePathIdentityProducesAnExePrefixedRuleKey()
    {
        var identity = new WindowIdentity(WindowIdentityKind.ExePath, "C:\\Program Files\\Example\\example.exe");

        RuleKey ruleKey = RuleKeyResolver.Resolve(identity, className: "SomeClass");

        Assert.Equal(new RuleKey("exe:C:\\Program Files\\Example\\example.exe"), ruleKey);
    }

    [Fact]
    public void UnknownIdentityFallsBackToTheClassNamePrefixedRuleKey()
    {
        RuleKey ruleKey = RuleKeyResolver.Resolve(WindowIdentity.Unknown, className: "Notepad");

        Assert.Equal(new RuleKey("class:Notepad"), ruleKey);
    }

    [Fact]
    public void UnknownIdentityWithNoClassNameCollapsesToASingleSharedRuleKeyRatherThanThrowing()
    {
        RuleKey ruleKey = RuleKeyResolver.Resolve(WindowIdentity.Unknown, className: string.Empty);

        Assert.Equal(new RuleKey("class:"), ruleKey);
    }

    [Fact]
    public void AnAumidAndAnExePathWithTheSameRawValueNeverCollide()
    {
        RuleKey aumidKey = RuleKeyResolver.Resolve(new WindowIdentity(WindowIdentityKind.Aumid, "same-value"), className: "irrelevant");
        RuleKey exeKey = RuleKeyResolver.Resolve(new WindowIdentity(WindowIdentityKind.ExePath, "same-value"), className: "irrelevant");

        Assert.NotEqual(aumidKey, exeKey);
    }
}
