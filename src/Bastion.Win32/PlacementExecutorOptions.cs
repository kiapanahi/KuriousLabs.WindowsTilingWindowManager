namespace Bastion.Win32;

/// <summary>
/// Config-tunable <see cref="PlacementExecutor"/> behavior (DESIGN.md §3.6), mirroring
/// <c>Bastion.Core</c>'s <c>ReconcilerOptions</c>'s own "documented default, not a hidden magic
/// number" shape: every timing/threshold value is threaded in as an explicit option rather than a
/// literal baked into <see cref="PlacementExecutor"/>'s logic. JSONC-driven config binding remains
/// GitHub issue #9's job; this record is the seam it will eventually populate.
/// </summary>
internal sealed record PlacementExecutorOptions
{
    /// <summary>
    /// The hang probe's timeout (DESIGN.md §3.6a: <c>SendMessageTimeout(WM_NULL,
    /// SMTO_ABORTIFHUNG, 200 ms)</c>).
    /// </summary>
    public TimeSpan HangProbeTimeout { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// How long a window stays quarantined (skipped entirely, no re-probe) after its <em>first</em>
    /// observed hang before being probed again. DESIGN.md §9's "Hung app" row names quarantine with
    /// backoff but not a specific duration — this is the shipped default, config-tunable.
    /// </summary>
    public TimeSpan InitialQuarantineBackoff { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The ceiling <see cref="InitialQuarantineBackoff"/> is allowed to grow to across repeated,
    /// consecutive hangs (see <see cref="QuarantineBackoffMultiplier"/>) — bounds how stale a
    /// chronically-hung window's next retry can become.
    /// </summary>
    public TimeSpan MaxQuarantineBackoff { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Growth factor applied to a window's backoff each time it is re-probed after its previous
    /// backoff elapsed and is <em>still</em> hung — an increasing, not flat, retry interval, capped
    /// at <see cref="MaxQuarantineBackoff"/>. Resets to <see cref="InitialQuarantineBackoff"/> the
    /// next time the window is later observed responsive again (DESIGN.md §3.6d's "any window ever
    /// seen hung" clause is about the batch-vs-per-window <em>placement mode</em>, tracked
    /// separately and never reset — see <see cref="PlacementExecutor"/>'s remarks).
    /// </summary>
    public double QuarantineBackoffMultiplier { get; init; } = 2.0;

    /// <summary>
    /// How many device pixels of width/height difference between a verify-after-move readback and
    /// the originally-requested target are treated as "the app honored the request" rather than a
    /// clamp (DESIGN.md §3.6e). Mirrors <c>ReconcilerOptions.PositionToleranceDevicePixels</c>'s own
    /// rationale: solved placements are exact, fractional coordinates; observed frame bounds are
    /// always whole device pixels.
    /// </summary>
    public double SizeToleranceDevicePixels { get; init; } = 1.0;

    /// <summary>The shipped default configuration.</summary>
    public static PlacementExecutorOptions Default { get; } = new();
}
