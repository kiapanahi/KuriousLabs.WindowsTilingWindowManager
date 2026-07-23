using Bastion.Win32;

namespace Bastion.Win32.Tests;

/// <summary>Configurable <see cref="IProcessImagePathReader"/> fake for <see cref="WindowIdentityResolverTests"/>.</summary>
internal sealed class FakeProcessImagePathReader : IProcessImagePathReader
{
    public string? Result { get; set; }

    public int CallCount { get; private set; }

    public string? TryGetImagePath(uint pid)
    {
        CallCount++;
        return Result;
    }
}
