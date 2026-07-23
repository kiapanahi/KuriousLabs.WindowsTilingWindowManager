using Bastion.Win32;
using Windows.Win32.Foundation;

namespace Bastion.Win32.Tests;

/// <summary>Configurable <see cref="IUwpAttributionProvider"/> fake for <see cref="WindowIdentityResolverTests"/>.</summary>
internal sealed class FakeUwpAttributionProvider : IUwpAttributionProvider
{
    public string? Result { get; set; }

    public int CallCount { get; private set; }

    public string? TryGetAumid(HWND hwnd)
    {
        CallCount++;
        return Result;
    }
}
