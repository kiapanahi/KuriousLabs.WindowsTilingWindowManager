using Bastion.Core;

namespace Bastion.Daemon;

/// <summary>
/// The read-facing seam for GitHub issue #9's live, hot-reloadable merged rules config — what a
/// future consumer (the manageability filter, the Reconciler's rule-matching path — none of which
/// this issue wires up) injects to read "the current rules," never <see cref="WindowRulesOptions"/>
/// or <c>IOptions&lt;WindowRulesOptions&gt;</c> directly. Deliberately not
/// <c>IOptionsMonitor&lt;WindowRulesOptions&gt;</c>: <c>docs/engineering/json-ipc-config.md</c> §2
/// explicitly reserves <c>IConfiguration</c>/<c>IOptionsMonitor</c>-driven reload for options
/// allowed to take effect key-by-key, and this data is deliberately all-or-nothing gated (a single
/// malformed overlay rule must not silently apply a partial reload) — <see cref="PublishedWindowRulesConfig"/>
/// is the purpose-built alternative.
/// </summary>
internal interface IPublishedWindowRulesConfig
{
    /// <summary>
    /// The most recently, successfully loaded-and-merged <see cref="WindowRulesDocument"/> — the
    /// startup-validated document until the first successful hot-reload, and the last
    /// successfully-reloaded one thereafter (a failed reload never changes this value).
    /// </summary>
    WindowRulesDocument Current { get; }
}
