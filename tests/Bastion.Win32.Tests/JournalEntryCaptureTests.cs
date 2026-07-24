using Bastion.Core;
using Microsoft.Extensions.Time.Testing;
using Windows.Win32.Foundation;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>GitHub issue #8's <see cref="JournalEntryCapture"/>: builds a <see cref="JournalEntry"/> from a live HWND's current PID and <c>WINDOWPLACEMENT</c>.</summary>
public sealed class JournalEntryCaptureTests
{
    private static readonly HWND s_hwnd = new(0x4000);
    private static readonly JournalWindowPlacement s_placement = new(JournalShowCommand.Normal, 1, 2, 3, 4, new Rect(0, 0, 800, 600));

    [Fact]
    public void TryCaptureBuildsTheEntryFromTheCurrentPidAndPlacement()
    {
        var pidReader = new FakeWindowProcessIdReader();
        pidReader.SetPid(s_hwnd, 4242);
        var placementSystem = new FakeJournalPlacementSystem();
        placementSystem.SetCapturedPlacement(s_hwnd, s_placement);
        var time = new FakeTimeProvider();
        time.SetUtcNow(new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.Zero));
        var capture = new JournalEntryCapture(pidReader, placementSystem, time);
        var identity = new WindowIdentity(WindowIdentityKind.ExePath, @"C:\Program Files\Contoso\app.exe");

        bool succeeded = capture.TryCapture(s_hwnd, WorkspaceKey.Default, identity, out JournalEntry entry);

        Assert.True(succeeded);
        Assert.Equal((long)(IntPtr)s_hwnd, entry.HwndValue);
        Assert.Equal(4242u, entry.ProcessId);
        Assert.Equal(WorkspaceKey.Default, entry.Workspace);
        Assert.Equal(s_placement, entry.PreManagementPlacement);
        Assert.Equal(identity, entry.Identity);
        Assert.Equal(JournalCornerPreference.Unset, entry.CornerPreference);
        Assert.Equal(time.GetUtcNow(), entry.JournaledAtUtc);
    }

    [Fact]
    public void TryCaptureFailsWhenThePidCannotBeRead()
    {
        // No SetPid call -- simulates the window having vanished before this call.
        var pidReader = new FakeWindowProcessIdReader();
        var placementSystem = new FakeJournalPlacementSystem();
        placementSystem.SetCapturedPlacement(s_hwnd, s_placement);
        var capture = new JournalEntryCapture(pidReader, placementSystem, new FakeTimeProvider());

        bool succeeded = capture.TryCapture(s_hwnd, WorkspaceKey.Default, WindowIdentity.Unknown, out _);

        Assert.False(succeeded);
    }

    [Fact]
    public void TryCaptureFailsWhenGetWindowPlacementFails()
    {
        var pidReader = new FakeWindowProcessIdReader();
        pidReader.SetPid(s_hwnd, 4242);
        var placementSystem = new FakeJournalPlacementSystem(); // no SetCapturedPlacement -- simulates GetWindowPlacement failing
        var capture = new JournalEntryCapture(pidReader, placementSystem, new FakeTimeProvider());

        bool succeeded = capture.TryCapture(s_hwnd, WorkspaceKey.Default, WindowIdentity.Unknown, out _);

        Assert.False(succeeded);
    }
}
