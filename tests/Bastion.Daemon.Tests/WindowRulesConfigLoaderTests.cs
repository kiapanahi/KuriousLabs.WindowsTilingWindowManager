using Bastion.Core;
using Xunit;

namespace Bastion.Daemon.Tests;

/// <summary>
/// GitHub issue #9: JSONC parsing (comments/trailing commas), the shipped-file/user-overlay
/// layering contract, and malformed-JSON failure — all against real temporary files on disk (plain
/// synchronous file I/O, not the live filesystem-*notification* mechanism <see cref="IConfigDirectoryWatcher"/>
/// abstracts away for <see cref="WindowRulesHotReloadServiceTests"/>).
/// </summary>
public sealed class WindowRulesConfigLoaderTests : IDisposable
{
    private readonly string _tempDirectory = Directory.CreateTempSubdirectory("bastion-config-loader-tests-").FullName;

    private string ShippedPath => Path.Combine(_tempDirectory, "rules.default.jsonc");

    private string UserPath => Path.Combine(_tempDirectory, "rules.jsonc");

    [Fact]
    public void LoadMergedReturnsEmptyDocumentWhenNeitherFileExists()
    {
        WindowRulesConfigLoader loader = CreateLoader();

        WindowRulesDocument merged = loader.LoadMerged();

        Assert.Empty(merged.Rules);
    }

    [Fact]
    public async Task LoadMergedReturnsShippedRulesWhenOnlyTheShippedFileExists()
    {
        await File.WriteAllTextAsync(
            ShippedPath,
            """
            {
                "rules": [
                    { "name": "game", "match": { "executablePath": "C:\\game.exe" }, "action": "Ignore" }
                ]
            }
            """,
            TestContext.Current.CancellationToken);
        WindowRulesConfigLoader loader = CreateLoader();

        WindowRulesDocument merged = loader.LoadMerged();

        WindowRule only = Assert.Single(merged.Rules);
        Assert.Equal("game", only.Name);
        Assert.Equal(WindowRuleAction.Ignore, only.Action);
    }

    [Fact]
    public async Task LoadMergedPrefersTheUserOverlayWhenBothFilesNameTheSameRule()
    {
        await File.WriteAllTextAsync(
            ShippedPath,
            """
            { "rules": [ { "name": "spotify", "match": { "className": "SpotifyMainWindow" }, "action": "Floating" } ] }
            """,
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            UserPath,
            """
            { "rules": [ { "name": "spotify", "match": { "className": "SpotifyMainWindow" }, "action": "Manage" } ] }
            """,
            TestContext.Current.CancellationToken);
        WindowRulesConfigLoader loader = CreateLoader();

        WindowRulesDocument merged = loader.LoadMerged();

        WindowRule only = Assert.Single(merged.Rules);
        Assert.Equal(WindowRuleAction.Manage, only.Action);
    }

    [Fact]
    public async Task LoadMergedParsesJsoncCommentsAndTrailingCommas()
    {
        // The exact JSONC round-trip acceptance criterion: line comments, block comments, and a
        // trailing comma before the closing bracket/brace all inside one file.
        await File.WriteAllTextAsync(
            ShippedPath,
            """
            {
                // a leading line comment
                "rules": [
                    /* a block comment */
                    {
                        "name": "commented-rule",
                        "match": { "appUserModelId": "Example.App" },
                        "action": "Floating", // trailing comment on a property line
                    },
                ]
            }
            """,
            TestContext.Current.CancellationToken);
        WindowRulesConfigLoader loader = CreateLoader();

        WindowRulesDocument merged = loader.LoadMerged();

        WindowRule only = Assert.Single(merged.Rules);
        Assert.Equal("commented-rule", only.Name);
    }

    [Fact]
    public async Task LoadMergedThrowsJsonExceptionForMalformedJson()
    {
        await File.WriteAllTextAsync(ShippedPath, "{ this is not valid json", TestContext.Current.CancellationToken);
        WindowRulesConfigLoader loader = CreateLoader();

        Assert.Throws<System.Text.Json.JsonException>(() => loader.LoadMerged());
    }

    [Fact]
    public async Task LoadMergedThrowsJsonExceptionWhenARuleIsMissingTheRequiredName()
    {
        await File.WriteAllTextAsync(
            ShippedPath,
            """
            { "rules": [ { "match": { "className": "X" }, "action": "Floating" } ] }
            """,
            TestContext.Current.CancellationToken);
        WindowRulesConfigLoader loader = CreateLoader();

        // "name" is a required member (Bastion.Core.WindowRule.Name) -- System.Text.Json's
        // source-generated deserializer throws rather than silently defaulting it.
        Assert.Throws<System.Text.Json.JsonException>(() => loader.LoadMerged());
    }

    [Fact]
    public async Task LoadMergedThrowsJsonExceptionWhenARuleHasNoMatchCriteria()
    {
        // Deserializes fine (an empty "match": {} is syntactically valid) -- rejected by
        // WindowRulesDocument.ValidateRules, run inside LoadMerged itself specifically so this
        // business rule applies identically whether loaded here (startup) or from
        // WindowRulesHotReloadService's debounce callback (hot-reload) -- see this type's own
        // remarks and WindowRulesHotReloadServiceTests.FailedReload... for the hot-reload half.
        await File.WriteAllTextAsync(
            ShippedPath,
            """
            { "rules": [ { "name": "matches-nothing", "match": {}, "action": "Ignore" } ] }
            """,
            TestContext.Current.CancellationToken);
        WindowRulesConfigLoader loader = CreateLoader();

        Assert.Throws<System.Text.Json.JsonException>(() => loader.LoadMerged());
    }

    [Fact]
    public async Task LoadMergedThrowsJsonExceptionWhenARulePresentsAnEmptyName()
    {
        // Distinct from the "missing name" case above: "name" is present but empty, which
        // System.Text.Json's required-member enforcement alone does not reject (the property *was*
        // supplied) -- WindowRulesDocument.ValidateRules catches this one instead.
        await File.WriteAllTextAsync(
            ShippedPath,
            """
            { "rules": [ { "name": "", "match": { "className": "X" }, "action": "Ignore" } ] }
            """,
            TestContext.Current.CancellationToken);
        WindowRulesConfigLoader loader = CreateLoader();

        Assert.Throws<System.Text.Json.JsonException>(() => loader.LoadMerged());
    }

    [Fact]
    public async Task LoadMergedCollapsesADuplicateNameWithinTheShippedFileAndStillAppliesTheUserOverride()
    {
        // Regression test for a merge bug caught in review: two shipped rules sharing one name used
        // to both survive in the output (only the name-to-index lookup was repointed, not the
        // earlier entry itself), so a user override by that name only ever reached whichever
        // duplicate the lookup pointed at, leaving the other a stale, un-overridden leftover.
        await File.WriteAllTextAsync(
            ShippedPath,
            """
            {
                "rules": [
                    { "name": "dup", "match": { "className": "First" }, "action": "Ignore" },
                    { "name": "dup", "match": { "className": "Second" }, "action": "Floating" }
                ]
            }
            """,
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            UserPath,
            """
            { "rules": [ { "name": "dup", "match": { "className": "Second" }, "action": "Manage" } ] }
            """,
            TestContext.Current.CancellationToken);
        WindowRulesConfigLoader loader = CreateLoader();

        WindowRulesDocument merged = loader.LoadMerged();

        WindowRule only = Assert.Single(merged.Rules);
        Assert.Equal(WindowRuleAction.Manage, only.Action);
    }

    private WindowRulesConfigLoader CreateLoader() => new(new WindowRulesConfigPaths
    {
        ShippedRulesFilePath = ShippedPath,
        UserConfigDirectory = _tempDirectory,
        UserRulesFilePath = UserPath,
        SchemaFilePath = Path.Combine(_tempDirectory, "rules.schema.json"),
    });

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
