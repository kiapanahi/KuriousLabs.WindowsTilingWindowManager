using System.CommandLine;

// TODO(DESIGN.md §3.9): bastionc is meant to be a thin client over a named-pipe JSON IPC command
// channel to bastiond (request/reply for commands like `status`/`focus`/`swap`, plus a separate
// broadcast pipe for state subscriptions). That channel does not exist yet, so every subcommand
// below is an honest, explicit "not implemented" stub — it does not simulate or fake a response.
// This file only proves the System.CommandLine wiring builds and runs under NativeAOT.

RootCommand rootCommand = new("bastionc — thin IPC client for bastiond. See DESIGN.md §3.9.");

Command statusCommand = new("status", "Query the daemon's current window/monitor topology.");
statusCommand.SetAction(_ => NotYetImplemented("status"));
rootCommand.Subcommands.Add(statusCommand);

return rootCommand.Parse(args).Invoke();

static int NotYetImplemented(string subcommandName)
{
    Console.Error.WriteLine(
        $"bastionc {subcommandName}: not yet implemented — the named-pipe JSON IPC command " +
        "channel (DESIGN.md §3.9) does not exist yet.");
    return 1;
}
