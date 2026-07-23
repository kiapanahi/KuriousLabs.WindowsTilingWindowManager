using Bastion.Core;

namespace Bastion.Win32;

/// <summary>
/// Resolves <see cref="WindowId"/>'s minting-strategy TODO (DESIGN.md §3.4, GitHub issue #3): a
/// process-lifetime monotonic counter, minted exactly once per new <see cref="WindowRegistry"/>
/// entry.
/// </summary>
/// <remarks>
/// A monotonic counter is the simplest strategy that satisfies <see cref="WindowId"/>'s own
/// documented contract — "opaque, stable, equatable" — with no reliance on window content (HWND
/// value, PID, title) that could collide across HWND recycling or process-identity reuse. Starting
/// at 1 rather than 0 keeps every minted id distinguishable from <see langword="default"/>
/// <see cref="WindowId"/> (whose backing field is implicitly zero for any struct default, with or
/// without a public parameterless constructor), so a stray <see langword="default"/> value can
/// never be mistaken for a real minted id.
/// </remarks>
internal sealed class WindowIdMinter
{
    private long _nextValue;

    /// <summary>Mints a new, distinct <see cref="WindowId"/>. Thread-safe.</summary>
    public WindowId Mint()
    {
        long value = Interlocked.Increment(ref _nextValue);
        return WindowId.FromOpaqueValue((ulong)value);
    }
}
