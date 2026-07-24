using Windows.Win32.Foundation;

namespace Bastion.Win32;

/// <summary>
/// The minimal Win32 seam the write-ahead journal needs (GitHub issue #8, DESIGN.md §3.7):
/// capture a window's current placement before it is hidden/moved away, and force-apply a
/// previously-captured placement back on restore. Deliberately smaller than
/// <see cref="IPlacementSystem"/> — a force-restore is a direct, one-window-at-a-time
/// <c>SetWindowPlacement</c> call, not a hang-probed, batched, verify-after-move tile placement
/// (this issue's design guidance: "a bare-metal restore likely doesn't need the full batched
/// Defer-window-pos machinery"). Matches <c>docs/engineering/testing.md</c> §5's Tier-2 seam
/// shape: <see cref="JournalPlacementSystemAdapter"/> is the real implementation;
/// <c>Bastion.Win32.Tests</c>'s <c>FakeJournalPlacementSystem</c> is the fake.
/// </summary>
internal interface IJournalPlacementSystem
{
    /// <summary>
    /// Captures <paramref name="hwnd"/>'s current <c>WINDOWPLACEMENT</c> (<c>GetWindowPlacement</c>).
    /// Returns <see langword="false"/> if the call fails (e.g. the window vanished between whatever
    /// produced <paramref name="hwnd"/> and this call — a routine race, not exceptional, matching
    /// this assembly's other <c>Try*</c> members).
    /// </summary>
    bool TryCapturePlacement(HWND hwnd, out JournalWindowPlacement placement);

    /// <summary>
    /// Force-applies <paramref name="placement"/> back onto <paramref name="hwnd"/>
    /// (<c>SetWindowPlacement</c>), for crash-recovery restore — never for ordinary tile placement
    /// (see <see cref="IPlacementSystem.ApplyWindowPlacement"/> for that).
    /// </summary>
    PlacementCallResult ApplyWindowPlacement(HWND hwnd, JournalWindowPlacement placement);
}
