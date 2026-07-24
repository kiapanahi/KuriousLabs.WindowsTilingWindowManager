namespace Bastion.Win32.Tests;

/// <summary>Records every <see cref="HotkeyCommand"/> <see cref="OnHotkeyInvoked"/> was called with, in call order.</summary>
internal sealed class FakeHotkeyDispatchTarget : IHotkeyDispatchTarget
{
    public List<HotkeyCommand> InvokedCommands { get; } = [];

    /// <summary>
    /// When set, <see cref="OnHotkeyInvoked"/> throws this instead of recording the command —
    /// simulates a future Reconciler-driven command implementation misbehaving, for
    /// <see cref="HotkeyDispatch.InvokeSafely"/>'s crash-containment test.
    /// </summary>
    public Exception? ExceptionToThrow { get; set; }

    /// <inheritdoc/>
    public void OnHotkeyInvoked(HotkeyCommand command)
    {
        if (ExceptionToThrow is { } exception)
        {
            throw exception;
        }

        InvokedCommands.Add(command);
    }
}
