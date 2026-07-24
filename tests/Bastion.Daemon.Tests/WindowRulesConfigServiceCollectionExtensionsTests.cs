using Bastion.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bastion.Daemon.Tests;

/// <summary>
/// GitHub issue #9's end-to-end registration surface (<c>AddWindowRulesConfiguration</c>) and its
/// startup fail-fast gate: <c>[OptionsValidator]</c> + <c>AddOptionsWithValidateOnStart</c>
/// (docs/engineering/daemon-architecture.md §4) failing <c>bastiond</c> fast on a malformed
/// <em>first</em> load, distinct from <see cref="WindowRulesHotReloadServiceTests"/>'s
/// keep-old-config-on-failure hot-reload behavior.
/// </summary>
public sealed class WindowRulesConfigServiceCollectionExtensionsTests : IDisposable
{
    private readonly string _tempDirectory = Directory.CreateTempSubdirectory("bastion-config-di-tests-").FullName;

    private WindowRulesConfigPaths Paths => new()
    {
        ShippedRulesFilePath = Path.Combine(_tempDirectory, "rules.default.jsonc"),
        UserConfigDirectory = _tempDirectory,
        UserRulesFilePath = Path.Combine(_tempDirectory, "rules.jsonc"),
        SchemaFilePath = Path.Combine(_tempDirectory, "rules.schema.json"),
    };

    [Fact]
    public async Task HostStartAsyncStartsSuccessfullyWithAValidShippedRulesFile()
    {
        await File.WriteAllTextAsync(
            Paths.ShippedRulesFilePath,
            """
            { "rules": [ { "name": "ok", "match": { "className": "X" }, "action": "Floating" } ] }
            """,
            TestContext.Current.CancellationToken);

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Services.AddWindowRulesConfiguration(Paths);
        using IHost host = builder.Build();

        // Must not throw -- this is the whole point of the fail-fast gate: a well-formed first
        // load starts cleanly.
        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task HostStartAsyncFailsFastWithAMalformedShippedRulesFile()
    {
        await File.WriteAllTextAsync(Paths.ShippedRulesFilePath, "{ not valid json", TestContext.Current.CancellationToken);

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Services.AddWindowRulesConfiguration(Paths);
        using IHost host = builder.Build();

        // A malformed *first* load throws out of Deserialize before Options validation even runs
        // -- IHost.StartAsync surfaces whatever IStartupValidator.Validate() throws, which for a
        // Configure delegate that itself threw is the original exception, not
        // OptionsValidationException specifically (that type is reserved for a delegate that ran
        // to completion but produced values IValidateOptions<T> rejects -- see the next tests).
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HostStartAsyncFailsFastWithJsonExceptionWhenARuleHasEmptyMatchCriteria()
    {
        // Parses fine -- WindowRuleMatch with every field null/omitted is valid JSON -- but is
        // rejected by WindowRulesDocument.ValidateRules, run inside WindowRulesConfigLoader.LoadMerged
        // itself (see that type's remarks: a rule matching nothing would silently apply its action
        // to every window, and this check must reject it identically whether loaded at startup or
        // via hot-reload, so it lives in the loader rather than only in the Options pipeline).
        // The loader throws JsonException directly out of the Configure delegate, before
        // WindowRulesOptionsValidator's own (still-registered, still-real) check ever gets a chance
        // to run -- see WindowRulesOptionsValidatorTests for direct, isolated proof it still works.
        await File.WriteAllTextAsync(
            Paths.ShippedRulesFilePath,
            """
            { "rules": [ { "name": "matches-nothing", "match": {}, "action": "Ignore" } ] }
            """,
            TestContext.Current.CancellationToken);

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Services.AddWindowRulesConfiguration(Paths);
        using IHost host = builder.Build();

        await Assert.ThrowsAsync<System.Text.Json.JsonException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HostStartAsyncFailsFastWithJsonExceptionWhenARuleHasAnEmptyName()
    {
        // WindowRulesDocument.ValidateRules (run inside WindowRulesConfigLoader.LoadMerged) rejects
        // an empty/whitespace-only name the same way it rejects an empty match -- see the previous
        // test's remarks for why this now throws JsonException from the loader rather than
        // OptionsValidationException from WindowRulesOptionsValidator's [Required] check.
        await File.WriteAllTextAsync(
            Paths.ShippedRulesFilePath,
            """
            { "rules": [ { "name": "", "match": { "className": "X" }, "action": "Ignore" } ] }
            """,
            TestContext.Current.CancellationToken);

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Services.AddWindowRulesConfiguration(Paths);
        using IHost host = builder.Build();

        await Assert.ThrowsAsync<System.Text.Json.JsonException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HostStartAsyncResolvesPublishedConfigSeededFromTheValidatedStartupLoad()
    {
        await File.WriteAllTextAsync(
            Paths.ShippedRulesFilePath,
            """
            { "rules": [ { "name": "seeded", "match": { "className": "X" }, "action": "Manage" } ] }
            """,
            TestContext.Current.CancellationToken);

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Services.AddWindowRulesConfiguration(Paths);
        using IHost host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);

        IPublishedWindowRulesConfig published = host.Services.GetRequiredService<IPublishedWindowRulesConfig>();

        WindowRule only = Assert.Single(published.Current.Rules);
        Assert.Equal("seeded", only.Name);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
