namespace Bastion.Core;

/// <summary>
/// Config-tunable <see cref="EffectiveMinSizeCache"/> decay behavior (DESIGN.md §6), mirroring
/// <see cref="ReconcilerOptions"/>/<c>PlacementExecutorOptions</c>'s own "documented default, not a
/// hidden magic number" shape: every timing/threshold value is threaded in as an explicit option
/// rather than a literal baked into <see cref="EffectiveMinSizeCache"/>'s logic. JSONC-driven config
/// binding remains GitHub issue #9's job; this record is the seam it will eventually populate.
/// </summary>
/// <remarks>
/// <b>Decay schedule, recorded here per this issue's own acceptance criteria ("record whatever is
/// chosen directly in code comments/tests").</b> Continuous exponential decay of the <em>excess</em>
/// above <see cref="EffectiveMinSizeCache.SystemFloor"/> (never the floor itself, which is a hard
/// lower bound -- see <see cref="EffectiveMinSizeCache"/>'s own remarks): given elapsed real time
/// <c>Δt</c> since a rule key's most recent <see cref="EffectiveMinSizeCache.RecordClamp"/> call,
/// <c>excess(Δt) = excess(0) * DecayFactor ^ (Δt / DecayInterval)</c>. At <c>Δt = 0</c> the excess is
/// unchanged; at <c>Δt = DecayInterval</c> it has shrunk to a <see cref="DecayFactor"/> fraction of
/// its original size; as <c>Δt → ∞</c> it asymptotically approaches (never crosses below) zero
/// excess, i.e. the floor. The defaults below (24h interval, 0.5 factor -- a "half-life" every day of
/// no reconfirming clamp) are an engineering estimate, not a documented Windows constant: an app
/// update ships at most a few times a month, so decaying within a single user session would be
/// premature, but a stale, no-longer-accurate large minimum should not survive indefinitely either.
/// </remarks>
public sealed record EffectiveMinSizeCacheOptions
{
    /// <summary>The half-life-style decay period -- see this type's remarks for the exact formula.</summary>
    public TimeSpan DecayInterval { get; init; } = TimeSpan.FromHours(24);

    /// <summary>
    /// The multiplicative factor applied to the excess-above-floor once per <see cref="DecayInterval"/>
    /// of continuous elapsed time with no new <see cref="EffectiveMinSizeCache.RecordClamp"/> call.
    /// Must be in (0, 1]: 1.0 disables decay entirely (a legitimate opt-out); values closer to 0
    /// decay faster.
    /// </summary>
    public double DecayFactor { get; init; } = 0.5;

    /// <summary>The shipped default configuration.</summary>
    public static EffectiveMinSizeCacheOptions Default { get; } = new();
}
