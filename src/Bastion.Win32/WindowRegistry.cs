using Bastion.Core;
using Windows.Win32.Foundation;

namespace Bastion.Win32;

/// <summary>
/// DESIGN.md §3.3's Window Registry: "Decides what is tile-able and owns identity." Callers —
/// eventually the Reconciler (GitHub issue #4), driven per coalesced intent — call
/// <see cref="TryAdmitAsync"/> on <c>SHOW</c>, <c>UNCLOAKED</c>, and <c>NAMECHANGE</c> alike
/// (DESIGN.md §5: "admitted on SHOW/UNCLOAKED — never CREATE — and re-evaluated on NAMECHANGE")
/// and <see cref="Purge"/> on <c>EVENT_OBJECT_DESTROY</c>. This type owns no WinEvent subscription
/// of its own — DESIGN.md §5's full open/vanish sequence is driven from outside it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Admission is idempotent, not just filtered.</b> A call for an <see cref="HWND"/> that is
/// already registered (same live PID) returns the existing <see cref="WindowId"/> without
/// re-running the manageability filter's <em>outcome</em> against it — DESIGN.md §3.3's "entries
/// are purged only on <c>EVENT_OBJECT_DESTROY</c> — never by <c>IsWindow</c> polling" and "any
/// nonzero cloak value → keep tracked, never tile, never forget" both mean an already-admitted
/// window is never evicted just because a later filter re-run would fail it. The filter genuinely
/// does re-run on every call (satisfying "NAMECHANGE re-runs the filter... against an
/// already-registered window") — it is the <em>registry's own reaction</em> to that outcome that
/// is asymmetric: failing admits nothing new; already-registered stays registered regardless.
/// </para>
/// <para>
/// <b>HWND recycling correction.</b> If a lookup finds an entry for <paramref name="hwnd"/> whose
/// recorded PID no longer matches the window's <em>current</em> live PID, that mismatch is itself
/// authoritative proof the old window is gone even though its <c>EVENT_OBJECT_DESTROY</c> was
/// never observed (DESIGN.md §9's HWND-recycling row cites the documented <c>IsWindow</c>
/// recycling warning as exactly this risk) — the stale entry is evicted and a fresh one considered,
/// in the same "reads are truth" spirit as the Reconciler's own heartbeat (DESIGN.md §1). This is a
/// read-driven correction, not a second event-driven purge path: <see cref="Purge"/> remains the
/// only <em>proactive</em> removal DESIGN.md §3.3 commits to.
/// </para>
/// <para>
/// <b>Concurrency.</b> Identity resolution (<see cref="IWindowIdentityResolver.ResolveAsync"/>)
/// crosses to <see cref="ShellComThread"/> and back, so two overlapping <see cref="TryAdmitAsync"/>
/// calls for the same window are possible even though DESIGN.md's Reconciler (once GitHub issue #4
/// lands) is itself single-threaded — nothing yet guarantees its calls into this registry are
/// serialized one-at-a-time. The dictionary is guarded by a lock that is never held across the
/// identity-resolution <see langword="await"/>, with a re-check immediately after it, so a race
/// mints at most one <see cref="WindowId"/> per window rather than two.
/// </para>
/// </remarks>
internal sealed class WindowRegistry(
    IWindowProcessIdReader pidReader,
    IWindowManageabilityInfoReader infoReader,
    IWindowIdentityResolver identityResolver,
    WindowClassBlocklist blocklist,
    WindowIdMinter idMinter,
    TimeProvider timeProvider)
{
    private readonly Dictionary<HWND, WindowRegistryEntry> _entriesByHwnd = [];
    private readonly Lock _lock = new();

    /// <summary>
    /// Evaluates <paramref name="hwnd"/> for admission (or returns its existing
    /// <see cref="WindowId"/> if already registered). Safe to call on <c>SHOW</c>,
    /// <c>UNCLOAKED</c>, or <c>NAMECHANGE</c> alike — see this type's remarks for why a failing
    /// re-run never evicts an already-registered window.
    /// </summary>
    /// <returns>
    /// The window's <see cref="WindowId"/> if it is now (or was already) registered, or
    /// <see langword="null"/> if it is not manageable or no longer exists.
    /// </returns>
    public async Task<WindowId?> TryAdmitAsync(HWND hwnd, CancellationToken cancellationToken = default)
    {
        uint? pidOrNull = pidReader.TryReadProcessId(hwnd);
        if (pidOrNull is not { } pid)
        {
            // Window already gone by the time we got here — a routine race with the caller's own
            // event delivery, not exceptional.
            return null;
        }

        // The filter always runs against a fresh read — DESIGN.md §3.3's "NAMECHANGE re-runs the
        // filter ... against an already-registered window" — but its outcome only ever gates a
        // *new* admission below; an already-registered window's cached WindowId (checked next) is
        // returned regardless of what a later re-run says here. See this type's remarks for why a
        // failing re-run never evicts an existing entry.
        WindowManageabilityInfo info = infoReader.Read(hwnd);
        bool isManageable = WindowManageabilityFilter.IsManageable(info, blocklist);

        WindowId? existing = TryFindExisting(hwnd, pid);
        if (existing is not null)
        {
            return existing;
        }

        if (!isManageable)
        {
            return null;
        }

        WindowIdentity identity = await identityResolver.ResolveAsync(hwnd, pid, cancellationToken).ConfigureAwait(false);

        lock (_lock)
        {
            WindowId? racedExisting = TryFindExistingUnderLock(hwnd, pid);
            if (racedExisting is not null)
            {
                return racedExisting;
            }

            WindowId windowId = idMinter.Mint();
            _entriesByHwnd[hwnd] = new WindowRegistryEntry(windowId, pid, timeProvider.GetUtcNow(), identity);
            return windowId;
        }
    }

    /// <summary>
    /// Removes <paramref name="hwnd"/>'s entry. The only proactive purge path — call this, and
    /// only this, on <c>EVENT_OBJECT_DESTROY</c> (DESIGN.md §3.3).
    /// </summary>
    public void Purge(HWND hwnd)
    {
        lock (_lock)
        {
            _entriesByHwnd.Remove(hwnd);
        }
    }

    /// <summary>Returns <paramref name="hwnd"/>'s registry entry, if any.</summary>
    public WindowRegistryEntry? TryGetEntry(HWND hwnd)
    {
        lock (_lock)
        {
            return _entriesByHwnd.GetValueOrDefault(hwnd);
        }
    }

    private WindowId? TryFindExisting(HWND hwnd, uint pid)
    {
        lock (_lock)
        {
            return TryFindExistingUnderLock(hwnd, pid);
        }
    }

    private WindowId? TryFindExistingUnderLock(HWND hwnd, uint pid)
    {
        if (_entriesByHwnd.TryGetValue(hwnd, out WindowRegistryEntry? entry))
        {
            if (entry.Pid == pid)
            {
                return entry.WindowId;
            }

            // Stale entry for a recycled HWND — see this type's remarks.
            _entriesByHwnd.Remove(hwnd);
        }

        return null;
    }
}
