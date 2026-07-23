namespace Bastion.Win32;

/// <summary>
/// A window became admittable: it was shown (<c>EVENT_OBJECT_SHOW</c>), uncloaked while genuinely
/// not cloaked (<c>EVENT_OBJECT_UNCLOAKED</c> with a zero <c>DWMWA_CLOAKED</c> read), or its title
/// changed and warrants re-evaluation (<c>EVENT_OBJECT_NAMECHANGE</c> — see
/// <see cref="Coalescer"/>'s handling of that event for why this is the chosen mapping).
/// </summary>
/// <param name="Hwnd">The window this intent concerns.</param>
/// <remarks>
/// DESIGN.md §3.3/§5: "Windows are admitted on SHOW/UNCLOAKED — never CREATE... and re-evaluated
/// on NAMECHANGE (late titles drive rules)." All three raw triggers funnel into this single intent
/// because, from the Reconciler/Registry's perspective (GitHub issue #3), each is the same
/// instruction: "run the manageability filter and rules against this window again."
/// </remarks>
internal sealed record WindowAppeared(nint Hwnd) : CoalescedIntent;
