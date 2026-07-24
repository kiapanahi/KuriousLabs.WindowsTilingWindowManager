using System.Collections.Immutable;

namespace Bastion.Core;

/// <summary>
/// The authoritative-read/admission seam between the <see cref="Reconciler"/> and the real
/// Windows desktop (DESIGN.md §3.4). <c>Bastion.Win32</c>'s <c>WindowSystemAdapter</c> is the
/// production implementation (<c>EnumWindows</c> + <c>GetWindowRect</c> +
/// <c>DWMWA_EXTENDED_FRAME_BOUNDS</c> + <c>DWMWA_CLOAKED</c> + <c>IsIconic</c>/<c>IsZoomed</c>,
/// wrapping the Window Registry for admission); tests use a fake with zero interop types
/// (docs/engineering/testing.md §5's <c>IWindowSystem</c>-shaped seam, "above CsWin32/COM, not
/// inside a COM shim").
/// </summary>
/// <remarks>
/// A single method, deliberately. DESIGN.md §1's "reads are truth; events are scheduling hints"
/// means every convergence trigger — the 5 s heartbeat, a coalesced intent, or a distrust
/// escalation — re-derives ground truth from the exact same authoritative read; none of them
/// pass a payload the Reconciler trusts instead. There is therefore no separate
/// "admit this one candidate" method to design around a not-yet-existing Core-safe identity for
/// an un-admitted window (the only such identity, <see cref="WindowId"/>, does not exist until
/// admission happens) — admission is folded into <see cref="ReadAllAsync"/> itself, entirely
/// inside the adapter, where the real <c>HWND</c> the candidate is keyed on never has to leave
/// <c>Bastion.Win32</c>. See <see cref="Reconciler"/>'s remarks for why this also keeps the
/// Reconciler itself trivially single-threaded-actor-safe: one call in, one snapshot out, one
/// state transition applied.
/// </remarks>
public interface IWindowSystem
{
    /// <summary>
    /// Performs one full authoritative re-sync: enumerates every currently visible top-level
    /// window, admits/re-evaluates each one (DESIGN.md §3.3's manageability filter, re-run but
    /// never evicting an already-registered window), and reads fresh geometry/state for every
    /// window that is tracked as a result. Sequentially processes each candidate — never
    /// concurrently — so a single call is trivially safe for the single-threaded-actor Reconciler
    /// to await once per convergence pass, matching the sequencing docs/engineering/testing.md §5
    /// and this issue's own "await each admission before moving to the next" guidance.
    /// </summary>
    /// <returns>
    /// Every currently tracked window's latest reading, including cloaked ones (DESIGN.md §3.3:
    /// "any nonzero cloak value → keep tracked, never tile, never forget" — the Reconciler, not
    /// this seam, is responsible for excluding cloaked windows from tiling). Never
    /// <see langword="default"/> — always rebuilt via <see cref="ImmutableArray{T}.Empty"/>/a
    /// <see cref="ImmutableArray{T}.Builder"/> (docs/engineering/daemon-architecture.md §5).
    /// </returns>
    Task<ImmutableArray<ObservedWindow>> ReadAllAsync(CancellationToken cancellationToken);
}
