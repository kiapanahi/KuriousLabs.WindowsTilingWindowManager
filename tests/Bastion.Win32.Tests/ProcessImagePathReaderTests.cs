using Bastion.Win32;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// Tier 1-adjacent real-process tests (docs/engineering/testing.md §3) for the real
/// <see cref="ProcessImagePathReader"/> — exercised against the test host's own process (its exe
/// path is always known and stable, unlike a live desktop's window set).
/// </summary>
public sealed class ProcessImagePathReaderTests
{
    [Fact]
    public void OrdinaryTestHostProcessResolvesItsOwnExePath()
    {
        var reader = new ProcessImagePathReader();

        string? path = reader.TryGetImagePath((uint)Environment.ProcessId);

        Assert.False(string.IsNullOrEmpty(path));
        Assert.True(Path.IsPathRooted(path));
    }

    [Fact]
    public void NonexistentProcessIdReturnsNull()
    {
        var reader = new ProcessImagePathReader();

        // Process ID 0 is the System Idle Process — OpenProcess documents this as a guaranteed
        // failure ("the function fails and the last error code is ERROR_INVALID_PARAMETER").
        string? path = reader.TryGetImagePath(0);

        Assert.Null(path);
    }
}
