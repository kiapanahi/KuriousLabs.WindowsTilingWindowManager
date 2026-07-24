namespace Bastion.Win32.Tests;

/// <summary>Records every <see cref="HotkeyCommand"/> <see cref="OnHotkeyInvoked"/> was called with, in call order.</summary>
internal sealed class FakeHotkeyDispatchTarget : IHotkeyDispatchTarget
{
    public List<HotkeyCommand> InvokedCommands { get; } = [];

    /// <inheritdoc/>
    public void OnHotkeyInvoked(HotkeyCommand command) => InvokedCommands.Add(command);
}
