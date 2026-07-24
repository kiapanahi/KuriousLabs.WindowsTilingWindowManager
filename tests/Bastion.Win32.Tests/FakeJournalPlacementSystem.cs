using Windows.Win32.Foundation;

namespace Bastion.Win32.Tests;

/// <summary>Configurable <see cref="IJournalPlacementSystem"/> fake for <see cref="HwndJournalRestorerTests"/> and <see cref="JournalEntryCaptureTests"/> — zero real HWNDs.</summary>
internal sealed class FakeJournalPlacementSystem : IJournalPlacementSystem
{
    private readonly Dictionary<HWND, JournalWindowPlacement> _capturedByHwnd = [];
    private readonly Dictionary<HWND, PlacementCallResult> _applyResultByHwnd = [];

    /// <summary>Every <c>(hwnd, placement)</c> pair <see cref="ApplyWindowPlacement"/> was actually called with, in call order.</summary>
    public List<(HWND Hwnd, JournalWindowPlacement Placement)> AppliedPlacements { get; } = [];

    /// <summary>Sets what <see cref="TryCapturePlacement"/> returns for <paramref name="hwnd"/>. Leaving a window unconfigured simulates <c>GetWindowPlacement</c> failing.</summary>
    public void SetCapturedPlacement(HWND hwnd, JournalWindowPlacement placement) => _capturedByHwnd[hwnd] = placement;

    /// <summary>Sets what <see cref="ApplyWindowPlacement"/> returns for <paramref name="hwnd"/>. Defaults to success.</summary>
    public void SetApplyResult(HWND hwnd, PlacementCallResult result) => _applyResultByHwnd[hwnd] = result;

    /// <inheritdoc/>
    public bool TryCapturePlacement(HWND hwnd, out JournalWindowPlacement placement) => _capturedByHwnd.TryGetValue(hwnd, out placement);

    /// <inheritdoc/>
    public PlacementCallResult ApplyWindowPlacement(HWND hwnd, JournalWindowPlacement placement)
    {
        AppliedPlacements.Add((hwnd, placement));
        return _applyResultByHwnd.GetValueOrDefault(hwnd, PlacementCallResult.Ok);
    }
}
