using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com.StructuredStorage;

namespace Bastion.Win32;

/// <summary>
/// Hand-declared <c>IPropertyStore</c>, per interop.md §4.1: "CsWin32-generated nested
/// <c>.Interface</c> types are not <see langword="partial"/>. Expect to hand-declare the specific
/// shell interfaces Bastion consumes." Only <see cref="GetValue"/> is ever called — the sole
/// consumer is <see cref="PropertyStoreAumidReader"/>'s read of <c>PKEY_AppUserModel_ID</c>.
/// </summary>
/// <remarks>
/// <para>
/// DOCUMENTED CONTRACT (verified against
/// https://learn.microsoft.com/windows/win32/api/propsys/nn-propsys-ipropertystore and the three
/// per-method pages linked from it): <c>IPropertyStore</c>'s real vtable order (after
/// <c>IUnknown</c>'s three slots) is <c>GetCount</c>, <c>GetAt</c>, <c>GetValue</c>, <c>SetValue</c>,
/// <c>Commit</c> — <em>not</em> the alphabetical order the interface's own doc index page lists
/// them in. <see cref="GeneratedComInterfaceAttribute"/>'s vtable is laid out "in the order [the
/// methods were] declared"
/// (https://learn.microsoft.com/dotnet/standard/native-interop/qualify-net-types-for-interoperation#consume-com-types-from-net),
/// so <see cref="GetCount"/> and <see cref="GetAt"/> below are declared — even though neither is
/// ever called — purely as vtable-slot placeholders: omitting them would shift
/// <see cref="GetValue"/> from its real slot 5 to slot 3, silently dispatching every "GetValue"
/// call through the native <c>GetCount</c> implementation instead. Declaring a <em>prefix</em> of
/// an interface's vtable (stopping before <c>SetValue</c>/<c>Commit</c>, which Bastion never
/// calls) is the documented, supported pattern for both classic and source-generated COM interop;
/// skipping a slot in the <em>middle</em> is not.
/// </para>
/// <para>
/// IID_IPropertyStore = <c>{886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99}</c>: this specific value is
/// <em>not</em> printed on the learn.microsoft.com reference page (interface IIDs live in the
/// Windows SDK's <c>propsys.idl</c>, not the HTML API docs) — verified instead against
/// <c>propsys.idl</c> reproductions in the ReactOS source tree, the mingw-w64 Windows DDK port,
/// and an independent python-win32 mailing-list reference, all three agreeing exactly. Classified
/// per <c>verify-windows-api</c> as a corroborated-but-not-learn.microsoft.com-printed constant,
/// not a "documented contract" citation in the usual sense — flagged here rather than silently
/// upgraded to fact. This literal must stay in sync with
/// <see cref="PropertyStoreAumidReader"/>'s own copy (reading it back via <c>typeof(IPropertyStore).GUID</c>
/// would need reflection this repo avoids for AOT-safety reasons — see that type's remarks).
/// </para>
/// </remarks>
[GeneratedComInterface]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
internal partial interface IPropertyStore
{
    /// <summary>Vtable-slot placeholder (slot 3) — never called. See this type's remarks.</summary>
    void GetCount(out uint cProps);

    /// <summary>Vtable-slot placeholder (slot 4) — never called. See this type's remarks.</summary>
    void GetAt(uint iProp, out PROPERTYKEY pkey);

    /// <summary>
    /// Retrieves the data for <paramref name="key"/>. <see cref="PreserveSigAttribute"/> is used
    /// because <c>GetValue</c>'s own docs warn that its two documented "no value present"/
    /// "converted to canonical form" outcomes — <c>S_OK</c> with <c>PROPVARIANT.vt == VT_EMPTY</c>,
    /// and the positive (non-failing) <c>INPLACE_S_TRUNCATED</c> — are both easy to mishandle with
    /// naive HRESULT checking; owning the raw <see cref="HRESULT"/> and checking
    /// <see cref="HRESULT.Succeeded"/> explicitly (both outcomes satisfy it, since neither has the
    /// failure bit set) is more transparent than trusting generated exception-throwing behavior to
    /// get an undocumented-to-this-generator case right.
    /// </summary>
    [PreserveSig]
    HRESULT GetValue(in PROPERTYKEY key, out PROPVARIANT pv);
}
