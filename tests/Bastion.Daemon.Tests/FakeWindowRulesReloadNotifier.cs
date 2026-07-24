namespace Bastion.Daemon.Tests;

/// <summary>Records every call for assertion instead of logging (the real <see cref="LoggingWindowRulesReloadNotifier"/>'s job).</summary>
internal sealed class FakeWindowRulesReloadNotifier : IWindowRulesReloadNotifier
{
    public int SucceededCallCount { get; private set; }

    public List<string> FailureReasons { get; } = [];

    public void NotifyReloadSucceeded() => SucceededCallCount++;

    public void NotifyReloadFailed(string reason) => FailureReasons.Add(reason);
}
