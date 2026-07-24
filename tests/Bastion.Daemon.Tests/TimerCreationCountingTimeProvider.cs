using Microsoft.Extensions.Time.Testing;

namespace Bastion.Daemon.Tests;

/// <summary>
/// Decorates a <see cref="FakeTimeProvider"/>, counting <see cref="CreateTimer"/> calls so a test
/// can assert a debounce timer was — or, for the shutdown-race regression test, was not — created.
/// </summary>
/// <remarks>
/// <see cref="FakeTimeProvider"/> itself exposes no such introspection (confirmed by reflecting over
/// the actual <c>Microsoft.Extensions.TimeProvider.Testing</c> 10.8.0 assembly: no
/// <c>GetActiveTimers</c>/timer-count member exists on the type), so this is the standard
/// decorate-a-virtual-member technique instead. Only <see cref="CreateTimer"/> is overridden —
/// <see cref="WindowRulesHotReloadService"/> never calls anything else on its injected
/// <see cref="TimeProvider"/> — and the created <see cref="ITimer"/> is still produced by
/// <paramref name="inner"/> itself, so the test's own <see cref="FakeTimeProvider.Advance"/> calls
/// on <paramref name="inner"/> continue to drive it exactly as if this wrapper were not present.
/// </remarks>
internal sealed class TimerCreationCountingTimeProvider(FakeTimeProvider inner) : TimeProvider
{
    public int TimersCreated { get; private set; }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        TimersCreated++;
        return inner.CreateTimer(callback, state, dueTime, period);
    }
}
