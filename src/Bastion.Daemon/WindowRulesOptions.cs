using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using Bastion.Core;
using Microsoft.Extensions.Options;

namespace Bastion.Daemon;

/// <summary>
/// The <c>Microsoft.Extensions.Options</c>-shaped vehicle for GitHub issue #9's startup fail-fast
/// gate (<c>docs/engineering/daemon-architecture.md</c> §4: <c>[OptionsValidator]</c> +
/// <c>AddOptionsWithValidateOnStart</c>) — deliberately a separate, mutable type from the pure,
/// immutable <see cref="WindowRulesDocument"/> it wraps.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not register <see cref="WindowRulesDocument"/> itself as the options type?</b> The
/// options pattern's default <c>IOptionsFactory&lt;TOptions&gt;</c> constructs
/// <c>new TOptions()</c> and then calls every registered <c>IConfigureOptions&lt;TOptions&gt;.Configure(options)</c>
/// to <em>mutate</em> it — which requires a settable property.
/// <see cref="WindowRulesDocument.Rules"/> is <see langword="init"/>-only (matching this repo's
/// pure-DTO convention throughout <c>Bastion.Core</c>), so a <c>Configure</c> delegate body cannot
/// assign it (CS8852). This type exists solely to be that one-time, DI-idiomatic, mutable
/// configuration surface; <see cref="PublishedWindowRulesConfig"/> converts it into an immutable
/// <see cref="WindowRulesDocument"/> exactly once, at startup, and every other consumer in the
/// process reads only that immutable, hot-reload-swappable copy — never this type again.
/// </para>
/// <para>
/// <b><see cref="ValidateEnumeratedItemsAttribute"/> recurses <c>[OptionsValidator]</c>'s
/// compile-time-generated validation into each <see cref="WindowRule"/></b> — by default,
/// DataAnnotations validation (reflection-based or source-generated) only inspects the properties
/// of the options type itself, never the items of a collection property
/// (learn.microsoft.com/dotnet/core/extensions/options#options-validation). This attribute is what
/// makes <see cref="WindowRule.Name"/>'s own <c>[Required]</c> attribute actually run
/// against every loaded rule, not just get silently ignored.
/// </para>
/// </remarks>
internal sealed class WindowRulesOptions
{
    /// <summary>The merged shipped-plus-user rule set, populated once by <see cref="WindowRulesConfigServiceCollectionExtensions"/>'s <c>Configure</c> delegate.</summary>
    [ValidateEnumeratedItems]
    public ImmutableArray<WindowRule> Rules { get; set; } = ImmutableArray<WindowRule>.Empty;
}
