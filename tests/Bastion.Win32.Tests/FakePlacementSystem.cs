using Bastion.Core;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Bastion.Win32.Tests;

/// <summary>
/// Configurable <see cref="IPlacementSystem"/> fake for <see cref="PlacementExecutorTests"/> —
/// every Win32 call this seam abstracts is configurable per HWND, with zero real HWNDs anywhere,
/// matching docs/engineering/testing.md §5's Tier-2 seam shape ("the fake implements the same
/// adapter-facing interface production code compiles against").
/// </summary>
internal sealed class FakePlacementSystem : IPlacementSystem
{
    private readonly Dictionary<WindowId, HWND> _hwndsByWindowId = [];
    private readonly HashSet<HWND> _hungHwnds = [];
    private readonly Dictionary<HWND, WindowPlacementState> _stateByHwnd = [];
    private readonly Dictionary<HWND, (Rect WindowRect, Rect FrameBounds)> _geometryByHwnd = [];
    private readonly Dictionary<HWND, PlacementCallResult> _placementResultByHwnd = [];
    private readonly Dictionary<HWND, PlacementCallResult> _fallbackResultByHwnd = [];
    private readonly HashSet<HWND> _deferFailureHwnds = [];
    private nint _nextDeferHandle = 1;

    /// <summary>What <see cref="ReadPrimaryWorkArea"/> returns. Defaults to <see langword="default"/> (no correction).</summary>
    public Rect PrimaryWorkArea { get; set; }

    /// <summary>When <see langword="true"/>, <see cref="BeginDefer"/> returns a null <c>HDWP</c> (allocation failure).</summary>
    public bool BeginDeferFails { get; set; }

    /// <summary>When <see langword="true"/>, <see cref="EndDefer"/> returns <see langword="false"/>.</summary>
    public bool EndDeferFails { get; set; }

    /// <summary>Every HWND <see cref="TryDefer"/> actually succeeded for, in call order, across every batch attempt.</summary>
    public List<HWND> DeferredHwnds { get; } = [];

    /// <summary>Every HWND <see cref="ApplyWindowPosFallback"/> was actually called with, in call order.</summary>
    public List<HWND> FallbackAppliedHwnds { get; } = [];

    /// <summary>Every <c>(hwnd, rcNormalPosition)</c> pair <see cref="ApplyWindowPlacement"/> was actually called with, in call order — lets tests assert the coordinate-space conversion the executor applied before calling in.</summary>
    public List<(HWND Hwnd, Rect Target)> AppliedPlacements { get; } = [];

    /// <summary>Test-observable: how many times <see cref="EndDefer"/> was called.</summary>
    public int EndDeferCallCount { get; private set; }

    /// <summary>Sets the HWND <see cref="TryResolveHwnd"/> returns for <paramref name="windowId"/>. Leaving a window unconfigured simulates it having vanished.</summary>
    public void SetHwnd(WindowId windowId, HWND hwnd) => _hwndsByWindowId[windowId] = hwnd;

    /// <summary>Makes <see cref="ProbeIsHung"/> return <see langword="true"/> for <paramref name="hwnd"/>.</summary>
    public void SetHung(HWND hwnd) => _hungHwnds.Add(hwnd);

    /// <summary>Makes <see cref="ProbeIsHung"/> return <see langword="false"/> for <paramref name="hwnd"/> again.</summary>
    public void SetResponsive(HWND hwnd) => _hungHwnds.Remove(hwnd);

    /// <summary>Sets the <see cref="WindowPlacementState"/> <see cref="ReadPlacementState"/> returns for <paramref name="hwnd"/>.</summary>
    public void SetState(HWND hwnd, WindowPlacementState state) => _stateByHwnd[hwnd] = state;

    /// <summary>Sets what <see cref="TryReadGeometry"/> returns for <paramref name="hwnd"/> — used for both the pre-move border-correction read and the post-move verify-after-move read.</summary>
    public void SetGeometry(HWND hwnd, Rect windowRect, Rect frameBounds) => _geometryByHwnd[hwnd] = (windowRect, frameBounds);

    /// <summary>Sets what <see cref="ApplyWindowPlacement"/> returns for <paramref name="hwnd"/>. Defaults to success.</summary>
    public void SetPlacementResult(HWND hwnd, PlacementCallResult result) => _placementResultByHwnd[hwnd] = result;

    /// <summary>Sets what <see cref="ApplyWindowPosFallback"/> returns for <paramref name="hwnd"/>. Defaults to success.</summary>
    public void SetFallbackResult(HWND hwnd, PlacementCallResult result) => _fallbackResultByHwnd[hwnd] = result;

    /// <summary>Makes <see cref="TryDefer"/> return <see langword="null"/> (failure) for <paramref name="hwnd"/> — the acceptance-criteria-mandated Defer-batch-failure scenario.</summary>
    public void SetDeferFails(HWND hwnd) => _deferFailureHwnds.Add(hwnd);

    /// <inheritdoc/>
    public bool TryResolveHwnd(WindowId windowId, out HWND hwnd) => _hwndsByWindowId.TryGetValue(windowId, out hwnd);

    /// <inheritdoc/>
    public bool ProbeIsHung(HWND hwnd, TimeSpan timeout) => _hungHwnds.Contains(hwnd);

    /// <inheritdoc/>
    public WindowPlacementState ReadPlacementState(HWND hwnd) => _stateByHwnd.GetValueOrDefault(hwnd);

    /// <inheritdoc/>
    public bool TryReadGeometry(HWND hwnd, out Rect windowRect, out Rect frameBounds)
    {
        if (!_geometryByHwnd.TryGetValue(hwnd, out (Rect WindowRect, Rect FrameBounds) geometry))
        {
            windowRect = default;
            frameBounds = default;
            return false;
        }

        (windowRect, frameBounds) = geometry;
        return true;
    }

    /// <inheritdoc/>
    public Rect ReadPrimaryWorkArea() => PrimaryWorkArea;

    /// <inheritdoc/>
    public PlacementCallResult ApplyWindowPlacement(HWND hwnd, Rect rcNormalPosition)
    {
        AppliedPlacements.Add((hwnd, rcNormalPosition));
        return _placementResultByHwnd.GetValueOrDefault(hwnd, PlacementCallResult.Ok);
    }

    /// <inheritdoc/>
    public HDWP BeginDefer(int windowCount)
    {
        if (BeginDeferFails)
        {
            return default;
        }

        _nextDeferHandle++;
        return new HDWP((IntPtr)_nextDeferHandle);
    }

    /// <inheritdoc/>
    public HDWP? TryDefer(HDWP batch, HWND hwnd, Rect screenBounds)
    {
        if (_deferFailureHwnds.Contains(hwnd))
        {
            return null;
        }

        DeferredHwnds.Add(hwnd);
        return batch;
    }

    /// <inheritdoc/>
    public bool EndDefer(HDWP batch)
    {
        EndDeferCallCount++;
        return !EndDeferFails;
    }

    /// <inheritdoc/>
    public PlacementCallResult ApplyWindowPosFallback(HWND hwnd, Rect screenBounds)
    {
        FallbackAppliedHwnds.Add(hwnd);
        return _fallbackResultByHwnd.GetValueOrDefault(hwnd, PlacementCallResult.Ok);
    }
}
