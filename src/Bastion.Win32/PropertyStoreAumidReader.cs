using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;

namespace Bastion.Win32;

/// <summary>
/// Real <see cref="IPropertyStoreAumidReader"/>: <c>SHGetPropertyStoreForWindow</c> +
/// <c>IPropertyStore::GetValue(PKEY_AppUserModel_ID)</c>, run on <see cref="ShellComThread"/> per
/// interop.md §5's apartment-discipline rule.
/// </summary>
/// <remarks>
/// DOCUMENTED CONTRACT (verified against
/// https://learn.microsoft.com/windows/win32/api/shellapi/nf-shellapi-shgetpropertystoreforwindow):
/// <c>HRESULT SHGetPropertyStoreForWindow(HWND hwnd, REFIID riid, void** ppv)</c>. The returned
/// <c>ppv</c> is a fresh COM reference the caller owns — <see cref="StrategyBasedComWrappers"/>'s
/// <c>+1</c>-reference-count convention (interop.md §4.2) assumes exactly this shape, so no extra
/// <c>AddRef</c> is needed before wrapping it. <see cref="CreateObjectFlags.UniqueInstance"/> is
/// used (rather than the identity-cached default) because every call targets a <em>different</em>
/// window's property store — there is nothing to usefully cache across calls, unlike the
/// long-lived <c>ITaskbarList</c>/<c>IVirtualDesktopManager</c> singletons interop.md §4.2
/// describes — so the wrapper is deterministically <see cref="IDisposable.Dispose"/>d immediately
/// after use instead of left for the GC.
/// </remarks>
/// <remarks>
/// <b>Correction to interop.md §4.2</b>: that doc's code sample calls
/// <c>StrategyBasedComWrappers.Instance.GetOrCreateObjectForComInstance(...)</c>, but
/// <see cref="StrategyBasedComWrappers"/> has no static <c>Instance</c> member — verified against
/// https://learn.microsoft.com/dotnet/api/system.runtime.interopservices.marshalling.strategybasedcomwrappers,
/// whose full member list is one public constructor plus instance methods only. This type
/// constructs and reuses its own instance (<see cref="s_comWrappers"/>) instead; interop.md §4.2
/// should be corrected to match rather than copied as-is by a future consumer.
/// </remarks>
internal sealed class PropertyStoreAumidReader(ShellComThread shellComThread) : IPropertyStoreAumidReader
{
    // Must stay in sync with IPropertyStore's own [Guid] attribute — see that type's remarks for
    // why this is not read back via reflection (typeof(IPropertyStore).GUID).
    private static readonly Guid s_propertyStoreIid = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

    // StrategyBasedComWrappers has no static Instance accessor (see this type's remarks) — a
    // single shared instance is constructed here instead, since ComWrappers.GetOrCreateObjectForComInstance's
    // own identity cache is keyed per ComWrappers instance and per interop.md §4.2 should not be
    // re-created per call.
    private static readonly StrategyBasedComWrappers s_comWrappers = new();

    /// <inheritdoc/>
    public Task<string?> TryGetAumidAsync(HWND hwnd, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return shellComThread.InvokeAsync(() => TryGetAumid(hwnd));
    }

    private static unsafe string? TryGetAumid(HWND hwnd)
    {
        Guid riid = s_propertyStoreIid;
        void* ppv = null;
        HRESULT hr = PInvoke.SHGetPropertyStoreForWindow(hwnd, &riid, &ppv);
        if (hr.Failed)
        {
            // "When this function returns [successfully], contains the interface pointer
            // requested in riid" — the documented contract guarantees ppv is populated whenever
            // hr indicates success, so no separate null check is needed (or, per CA1508,
            // possible: the analyzer's flow model already treats it as always-populated here).
            return null;
        }

        object wrapper = s_comWrappers.GetOrCreateObjectForComInstance(
            (nint)ppv, CreateObjectFlags.Unwrap | CreateObjectFlags.UniqueInstance);
        try
        {
            return ReadAumid((IPropertyStore)wrapper);
        }
        finally
        {
            (wrapper as IDisposable)?.Dispose();
        }
    }

    private static string? ReadAumid(IPropertyStore propertyStore)
    {
        HRESULT hr = propertyStore.GetValue(in PInvoke.PKEY_AppUserModel_ID, out PROPVARIANT value);
        if (hr.Failed)
        {
            // GetValue's own documented contract makes no promise about *pv's contents on a
            // failure HRESULT (only the success path's "vt is set to VT_EMPTY when absent" is
            // documented) — the generated marshaller pre-zeros the out parameter before the call,
            // so a well-behaved COM implementation that simply doesn't touch it on failure leaves
            // a harmless all-zero (VT_EMPTY) PROPVARIANT, but nothing guarantees a *misbehaving*
            // one didn't partially write before failing. PropVariantClear must never run against
            // that undefined state, so skip it entirely on failure rather than trust either
            // possibility.
            return null;
        }

        try
        {
            // Both documented success outcomes (S_OK and the positive INPLACE_S_TRUNCATED) satisfy
            // Succeeded; a present-but-empty property reads back as VT_EMPTY per GetValue's own
            // docs, which IPropertyStore.cs's remarks cite.
            if (value.vt != VARENUM.VT_LPWSTR)
            {
                return null;
            }

            string? aumid = value.pwszVal.ToString();
            return string.IsNullOrEmpty(aumid) ? null : aumid;
        }
        finally
        {
            // Reached only after a successful GetValue, so value is well-defined here regardless
            // of which VARENUM it holds — safe to clear unconditionally within this branch.
            PInvoke.PropVariantClear(ref value);
        }
    }
}
