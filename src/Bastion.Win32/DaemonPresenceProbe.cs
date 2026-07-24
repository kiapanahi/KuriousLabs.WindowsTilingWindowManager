using System.Security.Principal;

namespace Bastion.Win32;

/// <summary>
/// <c>bastionc</c>'s daemon-presence check (DESIGN.md §3.9, GitHub issue #11): a cheap,
/// non-throwing pre-flight before ever attempting to connect to the command pipe.
/// </summary>
/// <remarks>
/// <para>
/// <b>Must match <c>Bastion.Daemon.SingleInstanceGuard.MutexName</c> character-for-character.</b>
/// <c>Bastion.Cli</c> cannot reference that <see langword="internal"/> type directly — it is a
/// different assembly, and <c>Bastion.Cli</c> has no project reference to <c>Bastion.Daemon</c>
/// today (only to <c>Bastion.Win32</c>) — so docs/engineering/daemon-architecture.md §7's own
/// sample independently reconstructs the identical formula here rather than adding a cross-project
/// reference purely for this one string. <b>If <c>SingleInstanceGuard.MutexName</c>'s formula ever
/// changes, <see cref="MutexName"/> below must change with it</b> — there is no compiler or test
/// that spans both assemblies to catch a drift automatically.
/// </para>
/// <para>
/// <see cref="IsDaemonRunning"/> uses <see cref="Mutex.TryOpenExisting(string,out Mutex?)"/> — a
/// non-throwing existence check — never <see cref="Mutex.OpenExisting(string)"/> wrapped in a
/// <see langword="try"/>/<see langword="catch"/> (GitHub issue #11's own acceptance criteria;
/// docs/engineering/daemon-architecture.md §7's identical rule for this exact check).
/// </para>
/// </remarks>
internal static class DaemonPresenceProbe
{
    /// <summary>
    /// Exposed so tests can create/dispose a mutex under this exact name to simulate "a daemon is
    /// running" without waiting on a real <c>bastiond</c> process.
    /// </summary>
    public static string MutexName { get; } = $@"Local\Bastion.Daemon.{WindowsIdentity.GetCurrent().User!.Value}";

    /// <summary>Whether a <c>bastiond</c> instance currently owns the single-instance mutex for this user/session.</summary>
    public static bool IsDaemonRunning()
    {
        bool daemonRunning = Mutex.TryOpenExisting(MutexName, out Mutex? existing);
        existing?.Dispose();
        return daemonRunning;
    }
}
