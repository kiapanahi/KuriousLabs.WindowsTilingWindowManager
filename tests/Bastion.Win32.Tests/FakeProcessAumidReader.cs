using Bastion.Win32;

namespace Bastion.Win32.Tests;

/// <summary>Configurable <see cref="IProcessAumidReader"/> fake for <see cref="WindowIdentityResolverTests"/>.</summary>
internal sealed class FakeProcessAumidReader : IProcessAumidReader
{
    public string? Result { get; set; }

    public int CallCount { get; private set; }

    public string? TryGetAumid(uint pid)
    {
        CallCount++;
        return Result;
    }
}
