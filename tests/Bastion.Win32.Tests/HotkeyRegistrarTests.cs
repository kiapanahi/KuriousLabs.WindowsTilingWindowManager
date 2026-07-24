using System.Collections.Immutable;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// Exercises <see cref="HotkeyRegistrar"/>'s registration-conflict logic against
/// <see cref="FakeHotkeyRegistrationSystem"/> — a real OS-level <c>RegisterHotKey</c> conflict
/// cannot be reliably forced in a unit test, so DESIGN.md §7's "a failed registration is treated as
/// a conflict regardless of GetLastError's specific value" is verified here instead, with zero real
/// hotkeys and no pump thread involved at all.
/// </summary>
public sealed class HotkeyRegistrarTests
{
    private static readonly ImmutableArray<HotkeyBinding> s_threeBindings =
    [
        new(1, HOT_KEY_MODIFIERS.MOD_ALT | HOT_KEY_MODIFIERS.MOD_NOREPEAT, VIRTUAL_KEY.VK_H, HotkeyCommand.FocusLeft),
        new(2, HOT_KEY_MODIFIERS.MOD_ALT | HOT_KEY_MODIFIERS.MOD_NOREPEAT, VIRTUAL_KEY.VK_L, HotkeyCommand.FocusRight),
        new(3, HOT_KEY_MODIFIERS.MOD_ALT | HOT_KEY_MODIFIERS.MOD_NOREPEAT, VIRTUAL_KEY.VK_J, HotkeyCommand.FocusDown),
    ];

    [Fact]
    public void RegisterAllReturnsOneSuccessfulResultPerBindingWhenNothingConflicts()
    {
        var system = new FakeHotkeyRegistrationSystem();

        ImmutableArray<HotkeyRegistrationResult> results = HotkeyRegistrar.RegisterAll(system, s_threeBindings);

        Assert.Equal(3, results.Length);
        Assert.All(results, r => Assert.True(r.Registered));
        Assert.All(results, r => Assert.Null(r.ErrorCode));
        Assert.Equal([1, 2, 3], system.RegisteredIds);
    }

    [Fact]
    public void RegisterAllTreatsAFailedRegistrationAsAConflictRegardlessOfTheSpecificErrorCode()
    {
        var system = new FakeHotkeyRegistrationSystem();

        // Deliberately NOT ERROR_HOTKEY_ALREADY_REGISTERED — DESIGN.md §7's honesty note: that
        // specific code is observed behavior for RegisterHotKey, not a contractual guarantee, so
        // conflict detection must fire for ANY failed registration, not just that one code.
        system.SetConflict(2, WIN32_ERROR.ERROR_INVALID_PARAMETER);

        ImmutableArray<HotkeyRegistrationResult> results = HotkeyRegistrar.RegisterAll(system, s_threeBindings);

        Assert.True(results[0].Registered);
        Assert.False(results[1].Registered);
        Assert.Equal(WIN32_ERROR.ERROR_INVALID_PARAMETER, results[1].ErrorCode);
    }

    [Fact]
    public void RegisterAllStillAttemptsLaterBindingsAfterAnEarlierOneConflicts()
    {
        var system = new FakeHotkeyRegistrationSystem();
        system.SetConflict(2);

        ImmutableArray<HotkeyRegistrationResult> results = HotkeyRegistrar.RegisterAll(system, s_threeBindings);

        // A conflict on id 2 must not stop id 3 from being attempted — partial availability beats
        // an all-or-nothing startup failure (DESIGN.md §1/§3.1's "WinEvents/hotkeys are hints" posture).
        Assert.Equal([1, 2, 3], system.RegisteredIds);
        Assert.True(results[2].Registered);
    }

    [Fact]
    public void UnregisterAllOnlyUnregistersBindingsThatActuallySucceeded()
    {
        var system = new FakeHotkeyRegistrationSystem();
        ImmutableArray<HotkeyRegistrationResult> results =
        [
            new(s_threeBindings[0], Registered: true, ErrorCode: null),
            new(s_threeBindings[1], Registered: false, ErrorCode: WIN32_ERROR.ERROR_INVALID_PARAMETER),
            new(s_threeBindings[2], Registered: true, ErrorCode: null),
        ];

        HotkeyRegistrar.UnregisterAll(system, results);

        Assert.Equal([1, 3], system.UnregisteredIds);
    }
}
