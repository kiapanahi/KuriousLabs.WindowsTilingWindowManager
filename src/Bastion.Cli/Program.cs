using System.Collections.Immutable;
using System.CommandLine;
using System.Text.Json;
using Bastion.Win32;

// TODO(DESIGN.md §3.9): bastionc is meant to be a thin client over a named-pipe JSON IPC command
// channel to bastiond (request/reply for commands like `status`/`focus`/`swap`, plus a separate
// broadcast pipe for state subscriptions). That channel does not exist yet, so the `status`
// subcommand below is an honest, explicit "not implemented" stub — it does not simulate or fake a
// response. `restore-windows` (GitHub issue #8) is the one command that must work standalone even
// when bastiond is not running (DESIGN.md §3.7), so it is real: it reads the write-ahead HWND
// journal and force-restores every entry directly via Bastion.Win32, without any IPC round trip.

RootCommand rootCommand = new("bastionc — thin IPC client for bastiond. See DESIGN.md §3.9.");

Command statusCommand = new("status", "Query the daemon's current window/monitor topology.");
statusCommand.SetAction(_ => NotYetImplemented("status"));
rootCommand.Subcommands.Add(statusCommand);

Command restoreWindowsCommand = new(
    "restore-windows",
    "Force-restores every window recorded in the write-ahead HWND journal (DESIGN.md §3.7). " +
    "Works even when bastiond is not running.");
restoreWindowsCommand.SetAction((_, cancellationToken) => RestoreWindowsAsync(cancellationToken));
rootCommand.Subcommands.Add(restoreWindowsCommand);

return await rootCommand.Parse(args).InvokeAsync().ConfigureAwait(false);

static int NotYetImplemented(string subcommandName)
{
    Console.Error.WriteLine(
        $"bastionc {subcommandName}: not yet implemented — the named-pipe JSON IPC command " +
        "channel (DESIGN.md §3.9) does not exist yet.");
    return 1;
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
