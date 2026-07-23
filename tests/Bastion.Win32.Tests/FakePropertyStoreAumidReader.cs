using Bastion.Win32;
using Windows.Win32.Foundation;

namespace Bastion.Win32.Tests;

/// <summary>
/// Configurable <see cref="IPropertyStoreAumidReader"/> fake for
/// <see cref="WindowIdentityResolverTests"/> — the "fake IPropertyStore-touching step" the issue's
/// acceptance criteria names explicitly, faked at the <see cref="IPropertyStoreAumidReader"/> seam
/// rather than the raw COM call.
/// </summary>
internal sealed class FakePropertyStoreAumidReader : IPropertyStoreAumidReader
{
    public string? Result { get; set; }

    public int CallCount { get; private set; }

    public Task<string?> TryGetAumidAsync(HWND hwnd, CancellationToken cancellationToken = default)
    {
        CallCount++;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Result);
    }
}
