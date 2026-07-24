using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Bastion.Win32.Tests;

/// <summary>
/// Configurable <see cref="IHotkeyRegistrationSystem"/> fake for <see cref="HotkeyRegistrarTests"/>
/// and <see cref="InputPumpServiceTests"/> — a real OS-level <c>RegisterHotKey</c> conflict cannot
/// be reliably forced from a unit test (it would need a second process racing to claim the same
/// chord first), so every id's registration outcome is configured directly instead, with zero real
/// hotkeys anywhere, matching docs/engineering/testing.md §5's Tier-2 seam shape ("the fake
/// implements the same adapter-facing interface production code compiles against").
/// </summary>
internal sealed class FakeHotkeyRegistrationSystem : IHotkeyRegistrationSystem
{
    private readonly Dictionary<int, HotkeyCallResult> _registerResultById = [];

    /// <summary>Every id <see cref="Register"/> was actually called with, in call order.</summary>
    public List<int> RegisteredIds { get; } = [];

    /// <summary>Every id <see cref="Unregister"/> was actually called with, in call order.</summary>
    public List<int> UnregisteredIds { get; } = [];

    /// <summary>Ids <see cref="Unregister"/> should report as failed.</summary>
    public HashSet<int> UnregisterFailureIds { get; } = [];

    /// <summary>Makes <see cref="Register"/> fail for <paramref name="id"/>, simulating a conflict — the specific <paramref name="errorCode"/> is deliberately irrelevant to callers per DESIGN.md §7's honesty note.</summary>
    public void SetConflict(int id, WIN32_ERROR errorCode = default) => _registerResultById[id] = HotkeyCallResult.Fail(errorCode);

    /// <inheritdoc/>
    public HotkeyCallResult Register(int id, HOT_KEY_MODIFIERS modifiers, uint virtualKeyCode)
    {
        RegisteredIds.Add(id);
        return _registerResultById.GetValueOrDefault(id, HotkeyCallResult.Ok);
    }

    /// <inheritdoc/>
    public bool Unregister(int id)
    {
        UnregisteredIds.Add(id);
        return !UnregisterFailureIds.Contains(id);
    }
}
