using System.Collections.Immutable;
using System.CommandLine;
using System.Text.Json;
using Bastion.Cli;
using Bastion.Core;
using Bastion.Win32;

// DESIGN.md §3.9: bastionc is a thin client over a named-pipe JSON IPC command channel to
// bastiond (GitHub issues #11/#12). `status` is the one command round-tripped end-to-end for this
// PR -- it sends a StatusCommand and prints bastiond's reply; bastionc contains no tiling/business
// logic of its own. `restore-windows` (GitHub issue #8) is, and remains, a *different*, standalone
// path: it must work even when bastiond is not running (DESIGN.md §3.7), so it reads the
// write-ahead HWND journal and force-restores every entry directly via Bastion.Win32, without any
// IPC round trip -- retrofitting it to also try IPC first when the daemon is up would be a real,
// defensible enhancement but is out of scope for the already-closed issue #8.

RootCommand rootCommand = new("bastionc — thin IPC client for bastiond. See DESIGN.md §3.9.");

// GitHub issue #48: read and print the MinVer-derived version explicitly rather than relying on
// System.CommandLine's own (undocumented) default resolution for its built-in --version option —
// that way this doesn't silently change if a future System.CommandLine version alters what its
// default reads.
VersionOption versionOption = rootCommand.Options.OfType<VersionOption>().SingleOrDefault() ?? new VersionOption();
versionOption.Action = new PrintAssemblyVersionAction();
if (!rootCommand.Options.Contains(versionOption)) rootCommand.Options.Add(versionOption);

Command statusCommand = new("status", "Query whether bastiond is running and which version.");
statusCommand.SetAction((_, cancellationToken) => StatusAsync(cancellationToken));
rootCommand.Subcommands.Add(statusCommand);

Command restoreWindowsCommand = new(
    "restore-windows",
    "Force-restores every window recorded in the write-ahead HWND journal (DESIGN.md §3.7). " +
    "Works even when bastiond is not running.");
restoreWindowsCommand.SetAction((_, cancellationToken) => RestoreWindowsAsync(cancellationToken));
rootCommand.Subcommands.Add(restoreWindowsCommand);

return await rootCommand.Parse(args).InvokeAsync().ConfigureAwait(false);

/// <summary>
/// GitHub issue #11's one round-tripped command: check the daemon-presence mutex first (never a
/// throwing connect attempt against a daemon that plainly isn't running), then send a
/// <see cref="StatusCommand"/> and print whatever <see cref="IpcReply"/> comes back.
/// </summary>
static async Task<int> StatusAsync(CancellationToken cancellationToken)
{
    if (!DaemonPresenceProbe.IsDaemonRunning())
    {
        await Console.Error.WriteLineAsync("bastionc status: bastiond is not running.").ConfigureAwait(false);
        return 1;
    }

    IpcReply reply;
    try
    {
        reply = await IpcClient.SendCommandAsync(
            new StatusCommand(IpcCommand.CurrentProtocolVersion),
            connectTimeout: TimeSpan.FromSeconds(5),
            cancellationToken).ConfigureAwait(false);
    }
    catch (TimeoutException)
    {
        await Console.Error.WriteLineAsync("bastionc status: timed out connecting to bastiond.").ConfigureAwait(false);
        return 1;
    }
    catch (IOException ex)
    {
        await Console.Error.WriteLineAsync($"bastionc status: lost connection to bastiond: {ex.Message}").ConfigureAwait(false);
        return 1;
    }

    switch (reply)
    {
        case StatusReply status:
            await Console.Out.WriteLineAsync($"bastiond is running (version {status.DaemonVersion}, protocol {status.ProtocolVersion}).").ConfigureAwait(false);
            return 0;

        case ProtocolVersionMismatchReply mismatch:
            // A clear "daemon is a different version" message, not a raw deserialization
            // exception (GitHub issue #11's own acceptance criteria).
            await Console.Error.WriteLineAsync(
                $"bastionc status: bastiond is a different protocol version (daemon expects {mismatch.ProtocolVersion}, " +
                $"bastionc sent {mismatch.ReceivedProtocolVersion}) -- restart bastiond after upgrading bastionc, or vice versa.")
                .ConfigureAwait(false);
            return 1;

        case ErrorReply error:
            await Console.Error.WriteLineAsync($"bastionc status: {error.Message}").ConfigureAwait(false);
            return 1;

        default:
            // An unrecognized reply kind (e.g. a newer bastiond talking to an older bastionc) must
            // count as a failure, not a silent success -- mirrors RestoreWindowsAsync's own
            // unrecognized-outcome handling below.
            await Console.Error.WriteLineAsync($"bastionc status: unrecognized reply from bastiond: {reply.GetType().Name}.").ConfigureAwait(false);
            return 1;
    }
}

static async Task<int> RestoreWindowsAsync(CancellationToken cancellationToken)
{
    var store = new HwndJournalStore(HwndJournalStore.DefaultJournalFilePath);
    using var journalLock = new HwndJournalLock();
    var restorer = new HwndJournalRestorer(store, new JournalPlacementSystemAdapter(), new WindowProcessIdReader(), journalLock);

    ImmutableArray<JournalRestoreOutcome> outcomes;
    try
    {
        outcomes = await restorer.RestoreAllAsync(cancellationToken).ConfigureAwait(false);
    }
    catch (IOException ex)
    {
        await Console.Error.WriteLineAsync($"bastionc restore-windows: could not read the journal: {ex.Message}").ConfigureAwait(false);
        return 1;
    }
    catch (JsonException ex)
    {
        await Console.Error.WriteLineAsync($"bastionc restore-windows: the journal file is corrupt: {ex.Message}").ConfigureAwait(false);
        return 1;
    }

    if (outcomes.IsEmpty)
    {
        await Console.Out.WriteLineAsync("bastionc restore-windows: no journaled windows to restore.").ConfigureAwait(false);
        return 0;
    }

    int failureCount = 0;
    foreach (JournalRestoreOutcome outcome in outcomes)
    {
        if (await PrintOutcomeAsync(outcome).ConfigureAwait(false))
        {
            failureCount++;
        }
    }

    if (failureCount > 0)
    {
        await Console.Error.WriteLineAsync(
            $"bastionc restore-windows: {failureCount} window(s) could not be restored and remain journaled for a future retry.").ConfigureAwait(false);
        return 1;
    }

    return 0;
}

/// <summary>Prints one restore outcome to stdout/stderr as appropriate. Returns whether it counts as a failure.</summary>
static async Task<bool> PrintOutcomeAsync(JournalRestoreOutcome outcome)
{
    string identity = DescribeIdentity(outcome.Entry.Identity);
    switch (outcome.Kind)
    {
        case JournalRestoreOutcomeKind.Restored:
            await Console.Out.WriteLineAsync($"Restored {identity} (pid {outcome.Entry.ProcessId}).").ConfigureAwait(false);
            return false;
        case JournalRestoreOutcomeKind.SkippedWindowGone:
            await Console.Out.WriteLineAsync($"Skipped {identity} (pid {outcome.Entry.ProcessId}): the window no longer exists.").ConfigureAwait(false);
            return false;
        case JournalRestoreOutcomeKind.SkippedHwndRecycled:
            await Console.Out.WriteLineAsync($"Skipped {identity} (pid {outcome.Entry.ProcessId}): its window handle has been recycled to a different window.").ConfigureAwait(false);
            return false;
        case JournalRestoreOutcomeKind.Failed:
            await Console.Error.WriteLineAsync($"Failed to restore {identity} (pid {outcome.Entry.ProcessId}): Win32 error {outcome.ErrorCode}.").ConfigureAwait(false);
            return true;
        default:
            // An outcome kind this build doesn't recognize (e.g. a newer bastionc talking to a
            // journal shape from a future version) must count as a failure, not a silent success --
            // an unhandled case here must never let this command exit 0 having actually done
            // nothing for that entry (Copilot review finding on this PR).
            await Console.Error.WriteLineAsync($"Unrecognized restore outcome for {identity} (pid {outcome.Entry.ProcessId}): {outcome.Kind}.").ConfigureAwait(false);
            return true;
    }
}

static string DescribeIdentity(WindowIdentity identity) => identity.Kind switch
{
    WindowIdentityKind.Aumid => identity.Value ?? "(aumid)",
    WindowIdentityKind.ExePath => identity.Value ?? "(exe path)",
    _ => "an unidentified window",
};
