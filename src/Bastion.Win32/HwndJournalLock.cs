using System.Diagnostics.CodeAnalysis;
using System.Security.Principal;

namespace Bastion.Win32;

/// <summary>Real, named-<see cref="Semaphore"/>-backed <see cref="IHwndJournalLock"/> (GitHub issue #8, Codex review finding on this PR).</summary>
/// <remarks>
/// <para>
/// <b>A named <see cref="Semaphore"/>, deliberately not a <see cref="Mutex"/>.</b> DOCUMENTED
/// CONTRACT (verified against
/// https://learn.microsoft.com/dotnet/standard/threading/semaphore-and-semaphoreslim#managing-a-limited-resource,
/// "Semaphores and thread identity"): "The two semaphore types don't enforce thread identity on
/// calls to the WaitOne, Wait, Release, and SemaphoreSlim.Release methods." <see cref="Mutex"/>, by
/// contrast, is documented (https://learn.microsoft.com/dotnet/api/system.threading.mutex#remarks)
/// to "enforce[] thread identity, so a mutex can be released only by the thread that acquired it" —
/// releasing from a different thread throws <see cref="ApplicationException"/>. Since
/// <see cref="AcquireAsync"/>'s caller typically releases the returned <see cref="IDisposable"/>
/// only after further <see langword="await"/>s (e.g. <see cref="HwndJournalWriter.RecordThenActAsync"/>'s
/// read/write/hide sequence), and an async continuation can legitimately resume on a different
/// thread-pool thread than the one that acquired the lock, a <see cref="Mutex"/> would risk exactly
/// that release-on-the-wrong-thread exception. A count-1 named <see cref="Semaphore"/> gives the
/// identical cross-process mutual exclusion without the thread-affinity hazard.
/// </para>
/// <para>
/// <b>Acquire is a bounded, synchronous wait on a thread-pool thread</b> — .NET's <see cref="Semaphore"/>
/// has no async wait primitive (unlike <see cref="SemaphoreSlim.WaitAsync(CancellationToken)"/>,
/// which does not support named/cross-process semaphores per its own documented "Named semaphores"
/// limitation). This is justified by the always-brief critical sections this lock guards (a small
/// JSON read+write plus a handful of Win32 calls) and bounded by a fixed timeout rather
/// than waited on indefinitely.
/// </para>
/// </remarks>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "Constructed by tests via InternalsVisibleTo today, and by Bastion.Cli's " +
        "Program.cs (a different assembly); intended to also be registered once Bastion.Daemon's " +
        "composition root is wired (GitHub issue #10). Same documented CA1812 false-positive shape " +
        "as PlacementSystemAdapter/WindowSystemAdapter.")]
internal sealed class HwndJournalLock : IHwndJournalLock, IDisposable
{
    private static readonly TimeSpan s_acquireTimeout = TimeSpan.FromSeconds(10);

    private readonly Semaphore _semaphore;

    public HwndJournalLock()
    {
        // Local\ prefix + user SID -- the same naming discipline
        // docs/engineering/daemon-architecture.md §7 already establishes for the single-instance
        // mutex (never a fixed, predictable, global name: CreateMutex's own documented remarks warn
        // "a malicious user can create this [name] before you do," an equally applicable local-DoS
        // vector for a named semaphore).
        string name = $@"Local\Bastion.HwndJournalLock.{WindowsIdentity.GetCurrent().User!.Value}";
        _semaphore = new Semaphore(initialCount: 1, maximumCount: 1, name: name);
    }

    /// <inheritdoc/>
    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        bool acquired = await Task.Run(() => _semaphore.WaitOne(s_acquireTimeout), cancellationToken).ConfigureAwait(false);
        if (!acquired)
        {
            throw new TimeoutException("Timed out waiting for the HWND journal cross-process lock.");
        }

        return new Releaser(_semaphore);
    }

    /// <inheritdoc/>
    public void Dispose() => _semaphore.Dispose();

    private sealed class Releaser(Semaphore semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }
}
