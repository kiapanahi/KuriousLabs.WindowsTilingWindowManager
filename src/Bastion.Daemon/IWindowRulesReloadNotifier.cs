namespace Bastion.Daemon;

/// <summary>
/// The "raise a bar notification" hook DESIGN.md §3.9 calls for on hot-reload success/failure — a
/// stub interface, per GitHub issue #9's own acceptance criteria ("a stub/interface is fine — no
/// real bar/toast exists yet"). <see cref="LoggingWindowRulesReloadNotifier"/> is today's only
/// implementation; a real <c>Bastion.Bar</c> toast (DESIGN.md §3.8, deferred to v0.3) implements
/// this same interface later without <see cref="WindowRulesHotReloadService"/> changing at all.
/// </summary>
internal interface IWindowRulesReloadNotifier
{
    /// <summary>A hot-reload parsed and merged successfully and is now the published config.</summary>
    void NotifyReloadSucceeded();

    /// <summary>
    /// A hot-reload attempt failed to parse/merge; the previously-published config is still being
    /// served unchanged. <paramref name="reason"/> is a human-readable summary (an exception
    /// message), not a structured error code — this is a notification surface, not an API contract.
    /// </summary>
    void NotifyReloadFailed(string reason);
}
