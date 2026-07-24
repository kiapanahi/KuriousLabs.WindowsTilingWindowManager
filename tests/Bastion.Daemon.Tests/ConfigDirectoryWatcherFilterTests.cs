using Xunit;

namespace Bastion.Daemon.Tests;

/// <summary>
/// GitHub issue #9: <see cref="ConfigDirectoryWatcher.IsUserRulesFile"/> — the filename filter that
/// keeps the watcher from reacting to <see cref="WindowRulesSchemaPublisherService"/>'s own
/// <c>rules.schema.json</c> write (or any other unrelated file) landing in the same directory it
/// watches (caught in review). Tested directly as a pure predicate rather than through a real,
/// timing-dependent <see cref="FileSystemWatcher"/>.
/// </summary>
public sealed class ConfigDirectoryWatcherFilterTests
{
    [Fact]
    public void IsUserRulesFileMatchesTheExactName()
    {
        Assert.True(ConfigDirectoryWatcher.IsUserRulesFile("rules.jsonc", "rules.jsonc"));
    }

    [Theory]
    [InlineData("RULES.JSONC")]
    [InlineData("Rules.Jsonc")]
    public void IsUserRulesFileIsCaseInsensitive(string candidateFileName)
    {
        Assert.True(ConfigDirectoryWatcher.IsUserRulesFile(candidateFileName, "rules.jsonc"));
    }

    [Theory]
    [InlineData("rules.schema.json")]
    [InlineData("rules.default.jsonc")]
    [InlineData("notes.txt")]
    [InlineData("rules.jsonc.bak")]
    public void IsUserRulesFileDoesNotMatchAnUnrelatedFile(string candidateFileName)
    {
        Assert.False(ConfigDirectoryWatcher.IsUserRulesFile(candidateFileName, "rules.jsonc"));
    }

    [Fact]
    public void IsUserRulesFileReturnsFalseForANullCandidate()
    {
        // Documented as possible for a Renamed event "if the FileSystemWatcher does not get
        // matching old and new name events from the operating system."
        Assert.False(ConfigDirectoryWatcher.IsUserRulesFile(null, "rules.jsonc"));
    }
}
