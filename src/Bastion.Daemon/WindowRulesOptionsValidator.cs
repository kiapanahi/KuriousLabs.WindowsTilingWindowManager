using Microsoft.Extensions.Options;

namespace Bastion.Daemon;

/// <summary>
/// The <c>[OptionsValidator]</c> source-generated, reflection-free <see cref="IValidateOptions{TOptions}"/>
/// for <see cref="WindowRulesOptions"/> (GitHub issue #9), per
/// <c>docs/engineering/daemon-architecture.md</c> §4's explicit "not <c>ValidateDataAnnotations()</c>"
/// requirement. The compiler generates this partial class's <c>Validate</c> method from
/// <see cref="WindowRulesOptions.Rules"/>'s <c>[ValidateEnumeratedItems]</c> attribute plus
/// <see cref="Bastion.Core.WindowRule.Name"/>'s <c>[Required]</c> attribute — no
/// hand-written body, no reflection, fully AOT/trim-safe
/// (learn.microsoft.com/dotnet/core/extensions/options-validation-generator).
/// </summary>
[OptionsValidator]
internal sealed partial class WindowRulesOptionsValidator : IValidateOptions<WindowRulesOptions>;
