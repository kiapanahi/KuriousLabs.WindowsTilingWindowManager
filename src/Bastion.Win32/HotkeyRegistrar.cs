using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace Bastion.Win32;

/// <summary>
/// Registers/unregisters the full default hotkey table against a real or fake
/// <see cref="IHotkeyRegistrationSystem"/>, folding each call's outcome into an
/// <see cref="ImmutableArray{T}"/> of <see cref="HotkeyRegistrationResult"/>.
/// </summary>
/// <remarks>
/// Extracted out of <see cref="InputPumpService"/>'s pump thread as small, directly-callable, pure
/// functions — no thread, no message loop, no real HWND/hotkey required — so DESIGN.md §7's "every
/// registration is probed at startup ... a failed registration is treated as a conflict regardless
/// of GetLastError's specific value" is independently unit-testable via a fake
/// <see cref="IHotkeyRegistrationSystem"/> (<c>HotkeyRegistrarTests</c>), matching
/// <c>HookUnregistration</c>'s own established shape for the identical reason.
/// </remarks>
internal static class HotkeyRegistrar
{
    /// <summary>
    /// Registers every <paramref name="bindings"/> entry against <paramref name="system"/>, in
    /// order. A failed registration is logged as a conflict and does not stop the remaining
    /// bindings from being attempted — WinEvents/hotkeys are hints, and partial availability beats
    /// an all-or-nothing startup failure, the same posture <c>WinEventPumpService.RegisterHooks</c>
    /// takes for its own six hook ranges.
    /// </summary>
    public static ImmutableArray<HotkeyRegistrationResult> RegisterAll(
        ILogger logger,
        IHotkeyRegistrationSystem system,
        ImmutableArray<HotkeyBinding> bindings)
    {
        ImmutableArray<HotkeyRegistrationResult>.Builder results = ImmutableArray.CreateBuilder<HotkeyRegistrationResult>(bindings.Length);
        foreach (HotkeyBinding binding in bindings)
        {
            HotkeyCallResult callResult = system.Register(binding.Id, binding.Modifiers, (uint)binding.VirtualKey);
            if (!callResult.Success)
            {
                logger.LogHotkeyRegistrationConflict(binding.Id, binding.Modifiers, (uint)binding.VirtualKey, callResult.ErrorCode);
            }

            results.Add(new HotkeyRegistrationResult(binding, callResult.Success, callResult.ErrorCode));
        }

        return results.MoveToImmutable();
    }

    /// <summary>
    /// Unregisters every <paramref name="results"/> entry that actually succeeded at registration.
    /// A binding whose registration failed was never claimed, so there is nothing for
    /// <c>UnregisterHotKey</c> to free — attempting it anyway would just be a second, redundant
    /// documented failure.
    /// </summary>
    public static void UnregisterAll(ILogger logger, IHotkeyRegistrationSystem system, ImmutableArray<HotkeyRegistrationResult> results)
    {
        foreach (HotkeyRegistrationResult result in results)
        {
            if (result.Registered && !system.Unregister(result.Binding.Id))
            {
                logger.LogUnregisterHotkeyFailed(result.Binding.Id);
            }
        }
    }
}
