using Bastion.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bastion.Daemon.Tests;

/// <summary>
/// GitHub issue #9: 200 ms debounce, atomic swap on success, keep-old-config-plus-notify on
/// failure (DESIGN.md §3.9) — driven entirely through <see cref="FakeConfigDirectoryWatcher"/> and
/// <see cref="FakeTimeProvider"/>, never a real filesystem watch or a real sleep
/// (docs/engineering/testing.md §4/§5).
/// </summary>
public sealed class WindowRulesHotReloadServiceTests : IDisposable
{
    private static readonly TimeSpan s_debounce = TimeSpan.FromMilliseconds(200);

    private readonly string _tempDirectory = Directory.CreateTempSubdirectory("bastion-hot-reload-tests-").FullName;
    private readonly FakeTimeProvider _time = new();
    private readonly FakeConfigDirectoryWatcher _watcher = new();
    private readonly FakeWindowRulesReloadNotifier _notifier = new();

    private string ShippedPath => Path.Combine(_tempDirectory, "rules.default.jsonc");

    [Fact]
    public async Task StartAsyncStartsTheWatcher()
    {
        using WindowRulesHotReloadService service = CreateService(rules: []);

        await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, _watcher.StartCallCount);
    }

    [Fact]
    public async Task ChangedBeforeDebounceElapsesDoesNotReloadYet()
    {
        await WriteShippedRuleAsync("a", WindowRuleAction.Ignore);
        using WindowRulesHotReloadService service = CreateService(rules: []);
        await service.StartAsync(TestContext.Current.CancellationToken);

        _watcher.RaiseChanged();
        _time.Advance(s_debounce - TimeSpan.FromMilliseconds(1));

        Assert.Equal(0, _notifier.SucceededCallCount);
    }

    [Fact]
    public async Task ChangedAfterDebounceElapsesReloadsAndPublishesSuccessfully()
    {
        await WriteShippedRuleAsync("a", WindowRuleAction.Ignore);
        PublishedWindowRulesConfig published = CreatePublished(initialRules: []);
        using WindowRulesHotReloadService service = CreateService(published);
        await service.StartAsync(TestContext.Current.CancellationToken);

        _watcher.RaiseChanged();
        _time.Advance(s_debounce);

        Assert.Equal(1, _notifier.SucceededCallCount);
        WindowRule only = Assert.Single(published.Current.Rules);
        Assert.Equal("a", only.Name);
    }

    [Fact]
    public async Task MultipleRapidChangesWithinDebounceWindowCoalesceIntoOneReload()
    {
        await WriteShippedRuleAsync("a", WindowRuleAction.Ignore);
        using WindowRulesHotReloadService service = CreateService(rules: []);
        await service.StartAsync(TestContext.Current.CancellationToken);

        // A burst of changes, each arriving before the previous one's debounce would have
        // elapsed -- the classic "editor writes a temp file, then renames it" pattern raising
        // more than one directory event for a single logical save.
        _watcher.RaiseChanged();
        _time.Advance(TimeSpan.FromMilliseconds(50));
        _watcher.RaiseChanged();
        _time.Advance(TimeSpan.FromMilliseconds(50));
        _watcher.RaiseChanged();
        _time.Advance(s_debounce); // now past the debounce window measured from the *last* change

        Assert.Equal(1, _notifier.SucceededCallCount);
    }

    [Fact]
    public async Task FailedReloadKeepsPublishingThePreviousConfigAndNotifiesFailure()
    {
        await WriteShippedRuleAsync("good", WindowRuleAction.Ignore);
        PublishedWindowRulesConfig published = CreatePublished(initialRules: []);
        using WindowRulesHotReloadService service = CreateService(published);
        await service.StartAsync(TestContext.Current.CancellationToken);

        // First reload succeeds.
        _watcher.RaiseChanged();
        _time.Advance(s_debounce);
        Assert.Equal(1, _notifier.SucceededCallCount);
        WindowRulesDocument beforeFailedReload = published.Current;

        // Second reload sees a corrupted file.
        await File.WriteAllTextAsync(ShippedPath, "{ not valid json", TestContext.Current.CancellationToken);
        _watcher.RaiseChanged();
        _time.Advance(s_debounce);

        Assert.Single(_notifier.FailureReasons);
        Assert.Equal(beforeFailedReload, published.Current);
        Assert.Equal(1, _notifier.SucceededCallCount); // unchanged -- the failure did not also count as a success
    }

    [Fact]
    public async Task ReloadWithARuleHavingNoMatchCriteriaIsRejectedLikeAnyOtherInvalidLoad()
    {
        // Regression test for the exact gap caught in review: a hot-reloaded rule with no match
        // criteria (which would match every window) must be rejected the same way startup rejects
        // it, not silently accepted just because this path never goes through the Options pipeline.
        // WindowRulesConfigLoader.LoadMerged now enforces WindowRulesDocument.ValidateRules itself,
        // so this is exercised identically to any other LoadMerged failure -- no separate code path.
        await WriteShippedRuleAsync("good", WindowRuleAction.Ignore);
        PublishedWindowRulesConfig published = CreatePublished(initialRules: []);
        using WindowRulesHotReloadService service = CreateService(published);
        await service.StartAsync(TestContext.Current.CancellationToken);

        _watcher.RaiseChanged();
        _time.Advance(s_debounce);
        Assert.Equal(1, _notifier.SucceededCallCount);
        WindowRulesDocument beforeInvalidReload = published.Current;

        await File.WriteAllTextAsync(
            ShippedPath,
            """
            { "rules": [ { "name": "matches-nothing", "match": {}, "action": "Ignore" } ] }
            """,
            TestContext.Current.CancellationToken);
        _watcher.RaiseChanged();
        _time.Advance(s_debounce);

        Assert.Single(_notifier.FailureReasons);
        Assert.Equal(beforeInvalidReload, published.Current);
        Assert.Equal(1, _notifier.SucceededCallCount);
    }

    [Fact]
    public async Task StopAsyncStopsTheWatcherAndFurtherChangesAreIgnored()
    {
        await WriteShippedRuleAsync("a", WindowRuleAction.Ignore);
        using WindowRulesHotReloadService service = CreateService(rules: []);
        await service.StartAsync(TestContext.Current.CancellationToken);

        await service.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, _watcher.StopCallCount);

        _watcher.RaiseChanged();
        _time.Advance(s_debounce);

        Assert.Equal(0, _notifier.SucceededCallCount);
    }

    private WindowRulesHotReloadService CreateService(System.Collections.Immutable.ImmutableArray<WindowRule> rules) =>
        CreateService(CreatePublished(rules));

    private WindowRulesHotReloadService CreateService(PublishedWindowRulesConfig published)
    {
        var paths = new WindowRulesConfigPaths
        {
            ShippedRulesFilePath = ShippedPath,
            UserConfigDirectory = _tempDirectory,
            UserRulesFilePath = Path.Combine(_tempDirectory, "rules.jsonc"),
            SchemaFilePath = Path.Combine(_tempDirectory, "rules.schema.json"),
        };
        var loader = new WindowRulesConfigLoader(paths);
        return new WindowRulesHotReloadService(_watcher, loader, published, _notifier, _time, s_debounce);
    }

    private static PublishedWindowRulesConfig CreatePublished(System.Collections.Immutable.ImmutableArray<WindowRule> initialRules)
    {
        using ServiceProvider provider = new ServiceCollection()
            .AddOptions<WindowRulesOptions>()
            .Configure(options => options.Rules = initialRules)
            .Services
            .BuildServiceProvider();
        return new PublishedWindowRulesConfig(provider.GetRequiredService<IOptionsMonitor<WindowRulesOptions>>());
    }

    private Task WriteShippedRuleAsync(string name, WindowRuleAction action) =>
        File.WriteAllTextAsync(
            ShippedPath,
            $$"""
            { "rules": [ { "name": "{{name}}", "match": { "className": "X" }, "action": "{{action}}" } ] }
            """,
            TestContext.Current.CancellationToken);

    public void Dispose()
    {
        _watcher.Dispose();
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
