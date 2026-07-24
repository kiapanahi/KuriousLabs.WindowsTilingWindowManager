using Windows.Win32.Foundation;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// Exercises <see cref="InputPumpService"/>'s pump-thread lifecycle against
/// <see cref="FakeHotkeyRegistrationSystem"/>. Unlike <see cref="WinEventPumpServiceTests"/> — whose
/// own doc comment notes real hook registration outcomes are Tier 3 territory — the full
/// <see cref="InputPumpService.StartAsync"/>/<see cref="InputPumpService.StopAsync"/> roundtrip is
/// safe to run here as an ordinary unit test, because <see cref="IHotkeyRegistrationSystem"/> is a
/// seam: the fake never calls a real <c>RegisterHotKey</c>/<c>UnregisterHotKey</c>, so there is no
/// real OS hotkey, no real conflict, and nothing Tier-3-only about starting and stopping the pump
/// thread itself. Real <c>WM_HOTKEY</c> delivery timing under a live OS is still Tier 3/4 territory
/// (GitHub issue #13) and is not exercised here.
/// </summary>
public sealed class InputPumpServiceTests
{
    [Fact]
    public void ConstructorDoesNotRegisterAnythingBeforeStartAsync()
    {
        var registrationSystem = new FakeHotkeyRegistrationSystem();
        using var pump = new InputPumpService(registrationSystem, new FakeHotkeyDispatchTarget());

        Assert.Empty(pump.RegistrationResults);
        Assert.Empty(registrationSystem.RegisteredIds);
    }

    [Fact]
    public async Task StartAsyncRegistersEveryDefaultBindingAndSurfacesAConflictWithoutStoppingTheRest()
    {
        var registrationSystem = new FakeHotkeyRegistrationSystem();
        HotkeyBinding conflicting = DefaultHotkeyBindings.All[0];
        registrationSystem.SetConflict(conflicting.Id, WIN32_ERROR.ERROR_INVALID_PARAMETER);
        using var pump = new InputPumpService(registrationSystem, new FakeHotkeyDispatchTarget());

        await pump.StartAsync(CancellationToken.None);
        try
        {
            // DESIGN.md §7: every registration is probed at startup, and the conflict data is
            // structured (ImmutableArray<HotkeyRegistrationResult>), not just logged.
            Assert.Equal(DefaultHotkeyBindings.All.Length, pump.RegistrationResults.Length);
            HotkeyRegistrationResult conflictResult = pump.RegistrationResults.Single(r => r.Binding.Id == conflicting.Id);
            Assert.False(conflictResult.Registered);
            Assert.Equal(WIN32_ERROR.ERROR_INVALID_PARAMETER, conflictResult.ErrorCode);

            // The one conflict must not have stopped every other default binding from being
            // attempted and succeeding.
            Assert.True(pump.RegistrationResults.Where(r => r.Binding.Id != conflicting.Id).All(r => r.Registered));
            Assert.Equal(DefaultHotkeyBindings.All.Length, registrationSystem.RegisteredIds.Count);
        }
        finally
        {
            await pump.StopAsync(CancellationToken.None);
        }

        // Only the ids that actually succeeded should ever have been handed to UnregisterHotKey.
        Assert.DoesNotContain(conflicting.Id, registrationSystem.UnregisteredIds);
        Assert.Equal(DefaultHotkeyBindings.All.Length - 1, registrationSystem.UnregisteredIds.Count);
        Assert.False(pump.IsPumpThreadAlive);
    }

    [Fact]
    public async Task StartAsyncWithAnAlreadyCanceledTokenThrowsAndLeavesNoPumpThreadAlive()
    {
        using var pump = new InputPumpService(new FakeHotkeyRegistrationSystem(), new FakeHotkeyDispatchTarget());
        var alreadyCanceled = new CancellationToken(canceled: true);

        // ManualResetEventSlim.Wait(CancellationToken) is documented to throw OperationCanceledException
        // itself (not the Task-cancellation-flavored TaskCanceledException derived type) when the
        // token is already canceled — https://learn.microsoft.com/dotnet/api/system.threading.manualreseteventslim.wait.
        await Assert.ThrowsAsync<OperationCanceledException>(() => pump.StartAsync(alreadyCanceled));

        // StartAsync's catch block awaits StopAsync — which Thread.Joins the already-started pump
        // thread — before rethrowing, so by the time the exception above has propagated, the join
        // has already either completed or itself thrown a TimeoutException. No "shortly after"
        // polling delay is needed: this assertion is deterministic, not a race (identical reasoning
        // to WinEventPumpServiceTests' own regression test for the same bug class).
        Assert.False(pump.IsPumpThreadAlive);
    }
}
