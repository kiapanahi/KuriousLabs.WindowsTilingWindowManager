using Windows.Win32.UI.Input.KeyboardAndMouse;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>Pins DESIGN.md §7's invariants over <see cref="DefaultHotkeyBindings.All"/> itself, independent of any registration/dispatch machinery.</summary>
public sealed class DefaultHotkeyBindingsTests
{
    [Fact]
    public void EveryBindingCarriesModNorepeat()
    {
        // Bitwise AND, not Enum.HasFlag (boxes on every call) — matching PlacementSystemAdapter's
        // own established WINDOW_EX_STYLE-checking idiom.
        Assert.All(
            DefaultHotkeyBindings.All,
            binding => Assert.NotEqual((HOT_KEY_MODIFIERS)0, binding.Modifiers & HOT_KEY_MODIFIERS.MOD_NOREPEAT));
    }

    [Fact]
    public void NoBindingUsesABareWinChord()
    {
        // DESIGN.md §7: MOD_WIN's OS reservation is advisory, not a failure mode — RegisterHotKey
        // may succeed even for a shell-owned combo, so Bastion never relies on registration
        // success/failure to detect one and must not assume a bare Win+X default will register
        // predictably (or fail predictably) in the first place.
        foreach (HotkeyBinding binding in DefaultHotkeyBindings.All)
        {
            if ((binding.Modifiers & HOT_KEY_MODIFIERS.MOD_WIN) == (HOT_KEY_MODIFIERS)0)
            {
                continue;
            }

            bool hasCompanionModifier =
                (binding.Modifiers & HOT_KEY_MODIFIERS.MOD_SHIFT) != (HOT_KEY_MODIFIERS)0 ||
                (binding.Modifiers & HOT_KEY_MODIFIERS.MOD_CONTROL) != (HOT_KEY_MODIFIERS)0;
            Assert.True(hasCompanionModifier, $"Binding id {binding.Id} uses MOD_WIN without a companion MOD_SHIFT/MOD_CONTROL.");
        }
    }

    [Fact]
    public void EveryIdIsUnique()
    {
        var ids = DefaultHotkeyBindings.All.Select(b => b.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void EveryIdIsWithinRegisterHotKeysDocumentedApplicationRange()
    {
        // RegisterHotKey's own documented contract: "An application must specify an id value in
        // the range 0x0000 through 0xBFFF."
        Assert.All(DefaultHotkeyBindings.All, binding => Assert.InRange(binding.Id, 0x0000, 0xBFFF));
    }

    [Fact]
    public void TheTableIsNotEmpty()
    {
        Assert.NotEmpty(DefaultHotkeyBindings.All);
    }
}
