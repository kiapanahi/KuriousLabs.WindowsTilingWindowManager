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
/// <b>Provisional UWP identity is retried, not frozen.</b> DESIGN.md §3.3/§9: UWP attribution
/// "failure degrades to exe-path identity, retried on later SHOW/NAMECHANGE/FOREGROUND." An
/// already-registered <c>ApplicationFrameWindow</c> whose resolved identity is not yet a real
/// AUMID has its identity re-resolved on every subsequent <see cref="TryAdmitAsync"/> call, and
/// the entry is upgraded in place (same <see cref="WindowId"/>/PID/first-seen — only
/// <see cref="WindowRegistryEntry.Identity"/> changes) if a retry succeeds. This is scoped to
/// <c>ApplicationFrameWindow</c> specifically rather than any non-AUMID identity generally: an
/// ordinary desktop app's exe-path identity is not COM/UWP-timing-dependent and will never change
/// on retry, so retrying it on every admission call would be pure waste with no chance of ever
/// improving.
/// </para>
/// <para>
/// <b>HWND recycling correction, both at lookup and at commit time.</b> If a lookup finds an entry
/// for <paramref name="hwnd"/> whose recorded PID no longer matches the window's <em>current</em>
/// live PID, that mismatch is itself authoritative proof the old window is gone even though its
/// <c>EVENT_OBJECT_DESTROY</c> was never observed (DESIGN.md §9's HWND-recycling row cites the
/// documented <c>IsWindow</c> recycling warning as exactly this risk) — the stale entry is evicted
/// and a fresh one considered, in the same "reads are truth" spirit as the Reconciler's own
/// heartbeat (DESIGN.md §1). The same live-PID check runs again immediately before committing a
/// brand-new entry, inside the same lock: identity resolution crosses to <see cref="ShellComThread"/>
/// and back, so the HWND can have been destroyed and recycled to a <em>different</em> window
/// (whose own, faster admission may already have inserted the correct entry) while this call
/// awaited. Committing this call's now-stale <c>(hwnd, pid)</c> pairing without revalidating would
/// silently clobber that newer, correct entry with a ghost for a window that no longer exists —
/// and since its <c>DESTROY</c> was already handled, nothing would ever purge the ghost. This is a
/// read-driven correction, not a second event-driven purge path: <see cref="Purge"/> remains the
/// only <em>proactive</em> removal DESIGN.md §3.3 commits to.
/// </para>
/// <para>
/// <b>Concurrency.</b> Identity resolution (<see cref="IWindowIdentityResolver.ResolveAsync"/>)
/// crosses to <see cref="ShellComThread"/> and back, so two overlapping <see cref="TryAdmitAsync"/>
/// calls for the same window are possible even though DESIGN.md's Reconciler (once GitHub issue #4
/// lands) is itself single-threaded — nothing yet guarantees its calls into this registry are
/// serialized one-at-a-time. The dictionary is guarded by a lock that is never held across an
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
    /// <see cref="WindowId"/> if already registered, retrying a provisional UWP identity along the
    /// way). Safe to call on <c>SHOW</c>, <c>UNCLOAKED</c>, or <c>NAMECHANGE</c> alike — see this
    /// type's remarks for why a failing re-run never evicts an already-registered window.
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

        WindowRegistryEntry? existing = TryFindExisting(hwnd, pid);
        if (existing is not null)
        {
            if (existing.Identity.Kind != WindowIdentityKind.Aumid
                && string.Equals(info.ClassName, ApplicationFrameUwpAttributionProvider.ApplicationFrameWindowClassName, StringComparison.Ordinal))
            {
                await RetryProvisionalIdentityAsync(hwnd, pid, existing, cancellationToken).ConfigureAwait(false);
            }

            return existing.WindowId;
        }

        if (!isManageable)
        {
            return null;
        }

        WindowIdentity identity = await identityResolver.ResolveAsync(hwnd, pid, cancellationToken).ConfigureAwait(false);

        lock (_lock)
        {
            // Revalidate: the window's live PID may have changed again while we awaited identity
            // resolution (e.g. it was destroyed and its HWND recycled to a newer window whose own,
            // faster admission already inserted a correct entry). Committing this call's now-stale
            // (hwnd, pid) pairing would clobber that newer entry with a ghost for a window that no
            // longer exists — see this type's remarks.
            if (pidReader.TryReadProcessId(hwnd) != pid)
            {
                return null;
            }

            WindowRegistryEntry? racedExisting = TryFindExistingUnderLock(hwnd, pid);
            if (racedExisting is not null)
            {
                return racedExisting.WindowId;
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

    private async Task RetryProvisionalIdentityAsync(
        HWND hwnd, uint pid, WindowRegistryEntry previous, CancellationToken cancellationToken)
    {
        WindowIdentity retried = await identityResolver.ResolveAsync(hwnd, pid, cancellationToken).ConfigureAwait(false);
        if (retried.Kind != WindowIdentityKind.Aumid)
        {
            return;
        }

        lock (_lock)
        {
            // Only upgrade if this is still the exact entry we retried for — it may have been
            // purged, or replaced outright (a recycled HWND admitted as a different window), while
            // this retry's resolution was in flight.
            if (_entriesByHwnd.TryGetValue(hwnd, out WindowRegistryEntry? current)
                && current.WindowId == previous.WindowId)
            {
                _entriesByHwnd[hwnd] = current with { Identity = retried };
            }
        }
    }

    private WindowRegistryEntry? TryFindExisting(HWND hwnd, uint pid)
    {
        lock (_lock)
        {
            return TryFindExistingUnderLock(hwnd, pid);
        }
    }

    private WindowRegistryEntry? TryFindExistingUnderLock(HWND hwnd, uint pid)
    {
        if (_entriesByHwnd.TryGetValue(hwnd, out WindowRegistryEntry? entry))
        {
            if (entry.Pid == pid)
            {
                return entry;
            }

            // Stale entry for a recycled HWND — see this type's remarks.
            _entriesByHwnd.Remove(hwnd);
        }

        return null;
    }
}
