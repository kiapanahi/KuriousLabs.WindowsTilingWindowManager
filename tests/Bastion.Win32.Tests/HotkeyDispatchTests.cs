using System.Collections.Immutable;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// Exercises <see cref="HotkeyDispatch.TryResolveCommand"/> — a pure lookup, so no real thread,
/// message, or hotkey is needed to prove the <c>WM_HOTKEY</c> <c>wParam</c>-to-<see cref="HotkeyCommand"/>
/// resolution end to end.
/// </summary>
public sealed class HotkeyDispatchTests
{
    private static readonly HotkeyBinding s_binding = new(1, HOT_KEY_MODIFIERS.MOD_ALT, VIRTUAL_KEY.VK_H, HotkeyCommand.FocusLeft);

    [Fact]
    public void TryResolveCommandFindsARegisteredBindingsCommand()
    {
        ImmutableArray<HotkeyRegistrationResult> registrations = [new(s_binding, Registered: true, ErrorCode: null)];

        bool found = HotkeyDispatch.TryResolveCommand(registrations, s_binding.Id, out HotkeyCommand command);

        Assert.True(found);
        Assert.Equal(HotkeyCommand.FocusLeft, command);
    }

    [Fact]
    public void TryResolveCommandIgnoresABindingThatFailedToRegister()
    {
        ImmutableArray<HotkeyRegistrationResult> registrations = [new(s_binding, Registered: false, ErrorCode: default)];

        bool found = HotkeyDispatch.TryResolveCommand(registrations, s_binding.Id, out _);

        Assert.False(found);
    }

    [Fact]
    public void TryResolveCommandReturnsFalseForAnUnrecognizedId()
    {
        ImmutableArray<HotkeyRegistrationResult> registrations = [new(s_binding, Registered: true, ErrorCode: null)];

        bool found = HotkeyDispatch.TryResolveCommand(registrations, id: 999, out _);

        Assert.False(found);
    }

    [Fact]
    public void TryResolveCommandReturnsFalseForAnEmptyRegistrationSet()
    {
        bool found = HotkeyDispatch.TryResolveCommand([], id: 1, out _);

        Assert.False(found);
    }
}
