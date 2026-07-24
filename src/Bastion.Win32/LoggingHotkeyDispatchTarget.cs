using System.Diagnostics.CodeAnalysis;

namespace Bastion.Win32;

/// <summary>
/// The default <see cref="IHotkeyDispatchTarget"/> until <c>Bastion.Daemon</c>'s composition root
/// (GitHub issue #10) wires a real Reconciler-driven command dispatcher: logs which
/// <see cref="HotkeyCommand"/> fired and does nothing else.
/// </summary>
/// <remarks>
/// Matches <see cref="HookDiagnostics"/>'s own "not stubbed empty" rationale — a real, if minimal,
/// implementation rather than a silent no-op, so the registration/probing/dispatch pipeline this
/// issue builds is observably exercised end to end even before any layout command exists to invoke.
/// </remarks>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "Intended to be registered as the production IHotkeyDispatchTarget once " +
        "Bastion.Daemon's composition root is wired (GitHub issue #10) — not yet wired as of this " +
        "change. Same documented CA1812 false-positive shape as HotkeyRegistrationSystemAdapter " +
        "(see that class's remarks: this suppression is inert today given .NET 10's default-off " +
        "CA1812 plus this assembly's InternalsVisibleTo, kept for consistency and " +
        "forward-compatibility).")]
internal sealed class LoggingHotkeyDispatchTarget : IHotkeyDispatchTarget
{
    /// <inheritdoc/>
    public void OnHotkeyInvoked(HotkeyCommand command) => HookDiagnostics.LogHotkeyInvoked(command);
}
