using System.Security.Principal;

namespace Bastion.Daemon;

/// <summary>
/// Single-instance enforcement for <c>bastiond</c> (GitHub issue #10,
/// docs/engineering/daemon-architecture.md §7): a session-scoped named <see cref="Mutex"/>, checked
/// before any hosted service is registered.
/// </summary>
/// <remarks>
/// Factored out of <c>Program.cs</c>'s top-level statements into its own testable unit — a bare
/// top-level statement cannot be exercised by a test at all, and this behavior (a second acquire
/// attempt while the first is still held observably failing) is exactly the kind of thing that
/// should be proven, not merely asserted by inspection.
/// </remarks>
internal static class SingleInstanceGuard
{
    /// <summary>
    /// The session-scoped mutex name docs/engineering/daemon-architecture.md §7 specifies verbatim:
    /// a <c>Local\</c> prefix plus the interactive user's SID — never a fixed, predictable, global
    /// name. <c>CreateMutex</c>'s own documented remarks
    /// (https://learn.microsoft.com/windows/win32/api/synchapi/nf-synchapi-createmutexw#remarks)
    /// warn that "a malicious user can create this mutex before you do and prevent your application
    /// from starting" when the name is fixed and predictable — a documented local-DoS vector this
    /// naming avoids, matching <c>Bastion.Win32.HwndJournalLock</c>'s own named
    /// <see cref="Semaphore"/> for a different named-synchronization-object in this repo.
    /// </summary>
    public static string MutexName { get; } = $@"Local\Bastion.Daemon.{WindowsIdentity.GetCurrent().User!.Value}";

    /// <summary>
    /// Attempts to acquire <c>bastiond</c>'s single-instance mutex. On success, the returned
    /// <see cref="Mutex"/> is owned by the caller and must be kept alive (never disposed) for the
    /// entire process lifetime — that liveness, not any explicit lock/wait, is what enforces
    /// single-instance; disposing it early would let a second <c>bastiond</c> invocation acquire the
    /// name out from under this one.
    /// </summary>
    /// <returns>
    /// The acquired <see cref="Mutex"/>, or <see langword="null"/> if another instance already owns
    /// it for this user/session — per <see cref="Mutex(bool, string?, out bool)"/>'s own documented
    /// contract, this instance is disposed immediately rather than left dangling.
    /// </returns>
    public static Mutex? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, name: MutexName, createdNew: out bool createdNew);
        if (createdNew)
        {
            return mutex;
        }

        mutex.Dispose();
        return null;
    }
}
