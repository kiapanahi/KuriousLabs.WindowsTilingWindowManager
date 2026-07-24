using Bastion.Core;
using Microsoft.Extensions.Options;

namespace Bastion.Daemon;

/// <summary>
/// The atomically-swappable holder behind <see cref="IPublishedWindowRulesConfig"/> (GitHub issue
/// #9). Seeded once from the startup-validated <see cref="WindowRulesOptions"/>; every subsequent
/// update flows only through <see cref="Publish"/>, called by
/// <see cref="WindowRulesHotReloadService"/> after a successful debounced reload.
/// </summary>
/// <remarks>
/// <para>
/// <b>Seeded via <see cref="IOptionsMonitor{TOptions}"/>, not <see cref="IOptions{TOptions}"/>.</b>
/// <c>AddOptionsWithValidateOnStart</c>'s generated startup validator
/// (<c>Microsoft.Extensions.Options</c>'s own <c>ValidateOnStart</c> implementation) calls
/// <c>IOptionsMonitor&lt;WindowRulesOptions&gt;.Get(name)</c> internally during
/// <c>IHost.StartAsync</c>, which computes and caches the value in <em>that specific singleton
/// monitor instance's</em> cache. <see cref="IOptions{TOptions}"/> is a separate singleton
/// (<c>OptionsManager&lt;TOptions&gt;</c>) with its own independent cache — consuming it here would
/// silently re-run <see cref="WindowRulesConfigLoader"/>'s file I/O a second time at startup for no
/// benefit. Depending on the same <see cref="IOptionsMonitor{TOptions}"/> abstraction the validator
/// itself uses reuses the exact value already computed and validated, so the loader runs exactly
/// once per process start.
/// </para>
/// <para>
/// <b><see cref="Interlocked.Exchange{T}(ref T, T)"/> for the write, <see cref="Volatile.Read{T}(ref T)"/>
/// for the read.</b> Reference assignment is never torn on .NET, but without an explicit memory
/// barrier a reader on another thread could observe a stale cached value indefinitely; this pairing
/// is the standard, minimal-ceremony way to publish an immutable snapshot across threads without a
/// lock (matching this repo's existing "think Interlocked.Exchange or similar over an immutable
/// snapshot" framing for hot-reload).
/// </para>
/// </remarks>
internal sealed class PublishedWindowRulesConfig : IPublishedWindowRulesConfig
{
    private WindowRulesDocument _current;

    public PublishedWindowRulesConfig(IOptionsMonitor<WindowRulesOptions> startupOptionsMonitor)
    {
        ArgumentNullException.ThrowIfNull(startupOptionsMonitor);

        // .CurrentValue reuses whatever ValidateOnStart already computed/validated during
        // IHost.StartAsync (or, in a context with no host validation -- e.g. a unit test
        // constructing this type directly -- computes it now); either way this line never re-runs
        // the loader on its own.
        WindowRulesOptions validated = startupOptionsMonitor.CurrentValue;
        _current = new WindowRulesDocument { Rules = validated.Rules };
    }

    /// <inheritdoc/>
    public WindowRulesDocument Current => Volatile.Read(ref _current);

    /// <summary>
    /// Atomically replaces the published document, returning whatever was previously published.
    /// Called only by <see cref="WindowRulesHotReloadService"/> after a successful reload — never
    /// on a failed one, so a bad overlay edit never disturbs <see cref="Current"/>.
    /// </summary>
    internal WindowRulesDocument Publish(WindowRulesDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Interlocked.Exchange(ref _current, document);
    }
}
