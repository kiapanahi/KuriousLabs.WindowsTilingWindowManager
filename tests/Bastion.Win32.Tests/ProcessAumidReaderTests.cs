using Bastion.Win32;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// Tier 1-adjacent real-process tests (docs/engineering/testing.md §3) for the real
/// <see cref="ProcessAumidReader"/> — the concrete implementation
/// <see cref="WindowIdentityResolverTests"/> deliberately never instantiates, since that suite
/// fakes every rung of the chain. Exercised here against the test host's own process, which is
/// always present regardless of CI runner desktop state.
/// </summary>
public sealed class ProcessAumidReaderTests
{
    [Fact]
    public void OrdinaryTestHostProcessHasNoAumid()
    {
        // The xUnit v3/MTP test host is a plain console executable, not a packaged app — it has
        // no explicit AppUserModelID, so this exercises the real APPMODEL_ERROR_NO_APPLICATION
        // fallthrough path end to end (real OpenProcess + real GetApplicationUserModelId).
        var reader = new ProcessAumidReader();

        string? aumid = reader.TryGetAumid((uint)Environment.ProcessId);

        Assert.Null(aumid);
    }

    [Fact]
    public void NonexistentProcessIdReturnsNull()
    {
        var reader = new ProcessAumidReader();

        // Process ID 0 is the System Idle Process — OpenProcess documents this as a guaranteed
        // failure ("the function fails and the last error code is ERROR_INVALID_PARAMETER").
        string? aumid = reader.TryGetAumid(0);

        Assert.Null(aumid);
    }
}
