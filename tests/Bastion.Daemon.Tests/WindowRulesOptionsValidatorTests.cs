using Bastion.Core;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bastion.Daemon.Tests;

/// <summary>
/// Direct, isolated proof that <see cref="WindowRulesOptionsValidator"/>'s <c>[OptionsValidator]</c>
/// -generated code genuinely validates <see cref="Bastion.Core.WindowRule.Name"/>'s
/// <c>[Required]</c> attribute, constructed by hand rather than through
/// <see cref="WindowRulesConfigLoader"/> or a full <see cref="Microsoft.Extensions.Hosting.IHost"/>.
/// </summary>
/// <remarks>
/// This exists because <see cref="WindowRulesConfigServiceCollectionExtensionsTests"/>'s
/// end-to-end host tests can no longer observe this validator actually firing: once
/// <see cref="WindowRulesConfigLoader.LoadMerged"/> also enforces the same "non-empty name"
/// invariant (so hot-reload rejects it too, not just startup — see that type's remarks), the
/// loader always throws before the Configure delegate finishes, and
/// <see cref="WindowRulesOptionsValidator"/> never gets a chance to run in that full pipeline. This
/// test proves the acceptance criterion's explicit <c>[OptionsValidator]</c> +
/// <c>AddOptionsWithValidateOnStart</c> mechanism is still real, generated, working code —
/// deliberately independent of whichever caller happens to reach it first in practice.
/// </remarks>
public sealed class WindowRulesOptionsValidatorTests
{
    private static readonly WindowRulesOptionsValidator s_validator = new();

    [Fact]
    public void ValidateSucceedsForAWellFormedRule()
    {
        var options = new WindowRulesOptions
        {
            Rules = [new WindowRule { Name = "ok", Match = new WindowRuleMatch { ClassName = "X" }, Action = WindowRuleAction.Manage }],
        };

        ValidateOptionsResult result = s_validator.Validate(name: null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ValidateFailsForARuleWithAnEmptyName()
    {
        var options = new WindowRulesOptions
        {
            Rules = [new WindowRule { Name = string.Empty, Match = new WindowRuleMatch { ClassName = "X" }, Action = WindowRuleAction.Manage }],
        };

        ValidateOptionsResult result = s_validator.Validate(name: null, options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void ValidateFailsForARuleWithAWhitespaceOnlyName()
    {
        var options = new WindowRulesOptions
        {
            Rules = [new WindowRule { Name = "   ", Match = new WindowRuleMatch { ClassName = "X" }, Action = WindowRuleAction.Manage }],
        };

        ValidateOptionsResult result = s_validator.Validate(name: null, options);

        Assert.True(result.Failed);
    }
}
