using Windows.Win32.Foundation;

namespace Bastion.Win32;

/// <summary>
/// Real <see cref="IWindowIdentityResolver"/>: DESIGN.md §3.3's identity chain — "Rule identity:
/// window-level <c>PKEY_AppUserModel_ID</c> via <c>SHGetPropertyStoreForWindow</c>, then process
/// AUMID via <c>GetApplicationUserModelId</c>, then exe path via
/// <c>OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)</c> + <c>QueryFullProcessImageNameW</c>," with
/// <see cref="IUwpAttributionProvider"/> tried first per DESIGN.md §3.3's UWP-attribution note.
/// Each rung's own failure — not found, access denied, no application identity, etc. — falls
/// through to the next rather than throwing; only a genuinely unexpected exception from a rung
/// propagates.
/// </summary>
internal sealed class WindowIdentityResolver(
    IUwpAttributionProvider uwpAttributionProvider,
    IPropertyStoreAumidReader propertyStoreAumidReader,
    IProcessAumidReader processAumidReader,
    IProcessImagePathReader processImagePathReader) : IWindowIdentityResolver
{
    /// <inheritdoc/>
    public async Task<WindowIdentity> ResolveAsync(HWND hwnd, uint pid, CancellationToken cancellationToken = default)
    {
        string? uwpAumid = uwpAttributionProvider.TryGetAumid(hwnd);
        if (uwpAumid is not null)
        {
            return new WindowIdentity(WindowIdentityKind.Aumid, uwpAumid);
        }

        cancellationToken.ThrowIfCancellationRequested();
        string? windowAumid = await propertyStoreAumidReader.TryGetAumidAsync(hwnd, cancellationToken).ConfigureAwait(false);
        if (windowAumid is not null)
        {
            return new WindowIdentity(WindowIdentityKind.Aumid, windowAumid);
        }

        cancellationToken.ThrowIfCancellationRequested();
        string? processAumid = processAumidReader.TryGetAumid(pid);
        if (processAumid is not null)
        {
            return new WindowIdentity(WindowIdentityKind.Aumid, processAumid);
        }

        string? exePath = processImagePathReader.TryGetImagePath(pid);
        return exePath is not null
            ? new WindowIdentity(WindowIdentityKind.ExePath, exePath)
            : WindowIdentity.Unknown;
    }
}
