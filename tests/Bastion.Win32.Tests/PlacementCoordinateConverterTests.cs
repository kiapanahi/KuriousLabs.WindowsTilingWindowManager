using Bastion.Core;
using Bastion.Win32;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// Tier 1-adjacent unit tests for <see cref="PlacementCoordinateConverter"/>'s pure math — no
/// HWND/Win32 type anywhere in this file, matching the "extract a small pure predicate/function for
/// unit-testability" pattern established for <c>WinEventFilter</c>/<c>WinEventRootNormalizer</c>
/// (GitHub issue #1).
/// </summary>
public sealed class PlacementCoordinateConverterTests
{
    // --- ApplyBorderCorrection (DESIGN.md §3.6c) ------------------------------------------------

    [Fact]
    public void ApplyBorderCorrectionShiftsTheTargetOutwardByTheInvisibleBorderOnEachEdge()
    {
        // A 7px invisible border on every edge: the raw window rect extends 7px beyond the visible
        // frame on all four sides.
        var frameBounds = new Rect(100, 100, 1100, 700);
        var windowRect = new Rect(93, 93, 1107, 707);
        var desiredVisibleBounds = new Rect(0, 0, 800, 600);

        Rect corrected = PlacementCoordinateConverter.ApplyBorderCorrection(desiredVisibleBounds, windowRect, frameBounds);

        // Setting the WINDOW rect to this corrected value, with the same 7px border still applying,
        // must land the VISIBLE frame exactly on the desired bounds.
        Assert.Equal(new Rect(-7, -7, 807, 607), corrected);
    }

    [Fact]
    public void ApplyBorderCorrectionIsANoOpWhenWindowRectAndFrameBoundsAlreadyMatch()
    {
        var sameRect = new Rect(0, 0, 1000, 1000);
        var desiredVisibleBounds = new Rect(10, 20, 400, 300);

        Rect corrected = PlacementCoordinateConverter.ApplyBorderCorrection(desiredVisibleBounds, sameRect, sameRect);

        Assert.Equal(desiredVisibleBounds, corrected);
    }

    [Fact]
    public void ApplyBorderCorrectionHandlesAsymmetricBorders()
    {
        // Only a left-edge border (e.g. a window class with a thick left resize grip and none
        // elsewhere) -- top/right/bottom deltas are all zero.
        var frameBounds = new Rect(100, 0, 500, 400);
        var windowRect = new Rect(90, 0, 500, 400);
        var desiredVisibleBounds = new Rect(0, 0, 400, 400);

        Rect corrected = PlacementCoordinateConverter.ApplyBorderCorrection(desiredVisibleBounds, windowRect, frameBounds);

        Assert.Equal(new Rect(-10, 0, 400, 400), corrected);
    }

    // --- ToWorkspaceCoordinates (DESIGN.md §3.6b) -----------------------------------------------

    [Fact]
    public void ToWorkspaceCoordinatesIsANoOpWhenThePrimaryMonitorsWorkAreaStartsAtScreenOrigin()
    {
        // Taskbar docked at the bottom/right of the primary monitor: its work area's own top-left
        // is still screen (0,0), so workspace coordinates equal screen coordinates exactly.
        var primaryWorkArea = new Rect(0, 0, 1920, 1040);
        var screenBounds = new Rect(0, 0, 800, 600);

        Rect workspace = PlacementCoordinateConverter.ToWorkspaceCoordinates(screenBounds, primaryWorkArea);

        Assert.Equal(screenBounds, workspace);
    }

    [Fact]
    public void ToWorkspaceCoordinatesPreservesALargeOffsetForAWindowOnASecondaryMonitor()
    {
        // DESIGN.md §3.6b's key caveat: a window on a secondary monitor keeps its large
        // virtual-screen-relative offset -- it is NOT re-zeroed relative to that monitor's own work
        // area. With the primary monitor's own work area already starting at screen (0,0), this
        // secondary-monitor window's coordinates pass through completely unchanged.
        var primaryWorkArea = new Rect(0, 0, 1920, 1040);
        var screenBounds = new Rect(2000, 100, 2400, 500);

        Rect workspace = PlacementCoordinateConverter.ToWorkspaceCoordinates(screenBounds, primaryWorkArea);

        Assert.Equal(screenBounds, workspace);
    }

    [Fact]
    public void ToWorkspaceCoordinatesShiftsByThePrimaryMonitorsWorkAreaOriginWhenATaskbarIsDockedAtItsTopOrLeft()
    {
        // A taskbar docked at the TOP of the primary monitor eats 40px off its own work area's
        // top edge, so workspace-coordinate (0,0) sits 40px below screen (0,0) -- exactly the
        // subtlety DESIGN.md §3.6b flags ("origin at the primary monitor's work-area top-left, not
        // [screen] (0,0) itself").
        var primaryWorkArea = new Rect(0, 40, 1920, 1080);
        var screenBounds = new Rect(0, 40, 800, 640);

        Rect workspace = PlacementCoordinateConverter.ToWorkspaceCoordinates(screenBounds, primaryWorkArea);

        Assert.Equal(new Rect(0, 0, 800, 600), workspace);
    }

    [Fact]
    public void ToWorkspaceCoordinatesAppliesThePrimaryMonitorOffsetUniformlyRegardlessOfWhichMonitorTheWindowIsOn()
    {
        // Combines both subtleties: a top-docked-taskbar offset on the primary monitor, applied to a
        // window that is actually on a secondary monitor far to the right.
        var primaryWorkArea = new Rect(0, 40, 1920, 1080);
        var screenBounds = new Rect(2000, 140, 2400, 540);

        Rect workspace = PlacementCoordinateConverter.ToWorkspaceCoordinates(screenBounds, primaryWorkArea);

        Assert.Equal(new Rect(2000, 100, 2400, 500), workspace);
    }
}
