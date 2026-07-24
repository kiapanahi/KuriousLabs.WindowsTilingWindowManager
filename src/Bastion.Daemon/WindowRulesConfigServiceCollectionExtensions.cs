using Bastion.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Bastion.Daemon;

/// <summary>
/// The single registration surface for GitHub issue #9's whole config-loading subsystem: options +
/// startup validation, the loader, the hot-reload watcher/service, the published-config seam, the
/// notification stub, and the schema publisher.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope note.</b> Per this issue's own explicit boundary, this method is built and tested but
/// deliberately <em>not</em> called from <c>Program.cs</c> — wiring it into <c>bastiond</c>'s actual
/// composition root, alongside the WinEvent ingest pump, Coalescer, Reconciler, Placement Executor,
/// and IPC servers, is GitHub issue #10's job (blocked on this one). This mirrors
/// <c>Bastion.Win32.JournalRestoreOnShutdownService</c>'s identical "built and tested but not
/// registered" precedent from issue #8 — with one deliberate difference: that precedent registers
/// each hosted service individually from the composition root, while this subsystem bundles
/// <em>every</em> piece (including its two <see cref="IHostedService"/>s) behind this one call, so
/// issue #10's entire job for this subsystem is a single, obvious
/// <c>builder.Services.AddWindowRulesConfiguration();</c> line — several independently-registered
/// pieces here would be easy to partially wire (e.g. forgetting the schema publisher) without it
/// being obvious anything was missed.
/// </para>
/// <para>
/// <b><paramref name="paths"/> is optional</b> specifically so <c>Bastion.Daemon.Tests</c> can call
/// this exact production registration path pointed at a temporary directory, rather than
/// hand-rolling a parallel test-only wiring that could drift from what actually ships.
/// </para>
/// </remarks>
internal static class WindowRulesConfigServiceCollectionExtensions
{
    /// <summary>Registers every service GitHub issue #9's config subsystem needs. See this type's remarks for the scope boundary.</summary>
    public static IServiceCollection AddWindowRulesConfiguration(this IServiceCollection services, WindowRulesConfigPaths? paths = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        WindowRulesConfigPaths resolvedPaths = paths ?? WindowRulesConfigPaths.CreateDefault();
        services.TryAddSingleton(resolvedPaths);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<WindowRulesConfigLoader>();
        services.TryAddSingleton<IWindowRulesReloadNotifier, LoggingWindowRulesReloadNotifier>();
        services.TryAddSingleton<IConfigDirectoryWatcher>(static sp =>
        {
            WindowRulesConfigPaths configPaths = sp.GetRequiredService<WindowRulesConfigPaths>();
            return new ConfigDirectoryWatcher(configPaths.UserConfigDirectory, Path.GetFileName(configPaths.UserRulesFilePath));
        });

        // PublishedWindowRulesConfig is registered under its own concrete type (WindowRulesHotReloadService
        // needs the internal Publish method) AND resolved as IPublishedWindowRulesConfig via the same
        // singleton instance (the read-only seam a future rule-matching consumer will depend on) --
        // never two independently-constructed instances.
        services.TryAddSingleton<PublishedWindowRulesConfig>();
        services.TryAddSingleton<IPublishedWindowRulesConfig>(static sp => sp.GetRequiredService<PublishedWindowRulesConfig>());

        // Startup fail-fast gate (docs/engineering/daemon-architecture.md §4): IHost.StartAsync
        // calls IStartupValidator.Validate() before any hosted service starts, which forces this
        // Configure delegate to run -- see PublishedWindowRulesConfig's remarks for why that is a
        // real, verified mechanism, not an assumption. The Configure delegate itself
        // (loader.LoadMerged()) already enforces every business rule the merged document must
        // satisfy -- required members during deserialization, plus WindowRulesDocument.ValidateRules
        // (empty name, empty match) -- identically for both this startup path and the hot-reload
        // path (WindowRulesHotReloadService), which never goes through Options at all; a malformed
        // load therefore throws JsonException directly out of this Configure delegate before
        // WindowRulesOptionsValidator's own check ever runs. WindowRulesOptionsValidator +
        // [Required] on WindowRule.Name remain registered as the [OptionsValidator]-generated,
        // reflection-free mechanism this issue's acceptance criteria explicitly calls for
        // (docs/engineering/daemon-architecture.md §4: "not ValidateDataAnnotations()") and as a
        // defense-in-depth safety net if the loader's own check is ever weakened -- see
        // WindowRulesOptionsValidatorTests for direct, isolated proof it independently works.
        services.AddOptionsWithValidateOnStart<WindowRulesOptions, WindowRulesOptionsValidator>()
            .Configure<WindowRulesConfigLoader>(static (options, loader) => options.Rules = loader.LoadMerged().Rules);

        services.AddSingleton<IHostedService>(static sp => new WindowRulesHotReloadService(
            sp.GetRequiredService<IConfigDirectoryWatcher>(),
            sp.GetRequiredService<WindowRulesConfigLoader>(),
            sp.GetRequiredService<PublishedWindowRulesConfig>(),
            sp.GetRequiredService<IWindowRulesReloadNotifier>(),
            sp.GetRequiredService<TimeProvider>(),
            WindowRulesHotReloadService.DefaultDebounce));
        services.AddHostedService<WindowRulesSchemaPublisherService>();

        return services;
    }
}
