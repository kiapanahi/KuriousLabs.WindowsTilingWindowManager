namespace Bastion.Core;

/// <summary>
/// Config-tunable <see cref="Reconciler"/> behavior (DESIGN.md §3.4). Every timing/threshold value
/// here is threaded into <see cref="Reconciler"/> as an explicit constructor parameter rather than
/// a literal baked into its logic (pure-core skill item 2) — JSONC-driven config binding is
/// GitHub issue #9's job; this record is the seam it will eventually populate. The values below
/// are only the shipped defaults, matching <c>Coalescer.DefaultCoalesceWindow</c>/
/// <c>DefaultAdmissionGrace</c>'s own "documented default, not a hidden magic number" shape.
/// </summary>
public sealed record ReconcilerOptions
{
    /// <summary>
    /// The 5 s full-resync cadence (DESIGN.md §3.4) driving <see cref="Reconciler.RunHeartbeatLoopAsync"/>'s
    /// <see cref="PeriodicTimer"/>.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The reassert budget's named counter ceiling (DESIGN.md §3.4: "default 2 per 2-second
    /// window") — the number of <see cref="PlacementAction.Move"/> instructions a single window
    /// may receive within <see cref="ReassertBudgetWindow"/> before the Reconciler floats it
    /// instead of continuing to re-assert its position.
    /// </summary>
    public int ReassertBudgetPerWindow { get; init; } = 2;

    /// <summary>The rolling window <see cref="ReassertBudgetPerWindow"/> is counted over (DESIGN.md §3.4).</summary>
    public TimeSpan ReassertBudgetWindow { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How many device pixels of difference between a desired and observed rect edge are treated
    /// as "already converged" rather than needing a <see cref="PlacementAction.Move"/> instruction.
    /// Needed because <see cref="Bastion.Layout.ILayoutEngine"/> solves in exact, fractional
    /// <see cref="double"/> coordinates (DESIGN.md §6) while observed frame bounds are always
    /// whole device pixels — without tolerance, a perfectly-settled window would be re-asserted
    /// (and consume its reassert budget) on every single convergence pass.
    /// </summary>
    public double PositionToleranceDevicePixels { get; init; } = 1.0;

    /// <summary>The shipped default configuration.</summary>
    public static ReconcilerOptions Default { get; } = new();
}
