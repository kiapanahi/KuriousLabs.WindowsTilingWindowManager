using System.Runtime.InteropServices;
using Windows.Win32.Foundation;

namespace Bastion.Win32;

/// <summary>
/// One <see cref="HotkeyBinding"/>'s registration outcome — DESIGN.md §7's "every registration is
/// probed at startup ... conflicts surfaced," structured (not just logged) so a future
/// <c>bastion doctor</c> (GitHub issue #24) can report exactly which chord conflicted with what, per
/// this issue's acceptance criteria. <see cref="InputPumpService.RegistrationResults"/> exposes the
/// full <c>ImmutableArray&lt;HotkeyRegistrationResult&gt;</c> for the whole default table.
/// </summary>
/// <param name="Binding">The binding this outcome describes.</param>
/// <param name="Registered">
/// <see langword="false"/> for a bare zero <c>BOOL</c> return from <c>RegisterHotKey</c> —
/// unconditionally treated as a conflict regardless of <paramref name="ErrorCode"/>'s specific
/// value, per DESIGN.md's honesty note that <c>ERROR_HOTKEY_ALREADY_REGISTERED</c> is observed
/// behavior for this API, not a contractual guarantee.
/// </param>
/// <param name="ErrorCode">
/// The Win32 error code <c>GetLastError</c> reported when <paramref name="Registered"/> is
/// <see langword="false"/> (captured via <c>Marshal.GetLastPInvokeError()</c> — see
/// <see cref="HotkeyRegistrationSystemAdapter"/>'s remarks). Preserved for diagnostics only —
/// never branched on to decide whether a failure "counts" as a conflict. <see langword="null"/>
/// when <paramref name="Registered"/> is <see langword="true"/>.
/// </param>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct HotkeyRegistrationResult(HotkeyBinding Binding, bool Registered, WIN32_ERROR? ErrorCode);
