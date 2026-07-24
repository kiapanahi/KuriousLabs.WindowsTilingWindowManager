namespace Bastion.Core;

/// <summary>
/// The classification a <see cref="WindowRule"/> assigns to every window it matches (GitHub issue
/// #9; DESIGN.md §9's "Curated rules seed" paragraph: "games (ignore), PiP players (float, ...),
/// Teams/OneDrive/notification popups (ignore), installers and setup wizards (float)").
/// </summary>
/// <remarks>
/// Only three values exist deliberately: <see cref="Floating"/>/<see cref="Ignore"/> are the two
/// DESIGN.md §9 names explicitly ("float" and "ignore" — <see cref="Floating"/>, not <c>Float</c>,
/// specifically to avoid CA1720 "identifier contains type name," since <c>float</c> is a C# built-in
/// type keyword), and <see cref="Manage"/> exists solely so a user's own rule can override a shipped
/// classification back to ordinary tiling (e.g. the shipped seed floats a class of app the user
/// actually wants tiled) — the object-graph merge in <see cref="WindowRulesDocument.Merge"/>
/// replaces a shipped rule wholesale when the user overlay defines another rule with the same
/// <see cref="WindowRule.Name"/>, so <see cref="Manage"/> is how that replacement rule spells
/// "actually, tile this one." Consuming this classification against a resolved window identity
/// (i.e. wiring it into the Reconciler/manageability filter) is explicitly out of scope for this
/// issue — see <see cref="WindowRule"/>'s remarks.
/// </remarks>
public enum WindowRuleAction
{
    /// <summary>Tile the window normally — the default a rule chooses only to override a shipped classification.</summary>
    Manage,

    /// <summary>Treat the window as floating: excluded from the tiled layout, positioned/sized by the app itself.</summary>
    Floating,

    /// <summary>Never manage or tile the window at all — DESIGN.md §9's "ignore" classification for games and transient popups.</summary>
    Ignore,
}
