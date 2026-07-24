using System.Security.Principal;

namespace Bastion.Win32;

/// <summary>
/// The two named-pipe names DESIGN.md §3.9 requires — one request/reply command pipe, one
/// broadcast state-subscription pipe — shared verbatim between the server side
/// (<see cref="IpcCommandServerPump"/>/<see cref="IpcBroadcastServerPump"/>) and the client side
/// (<see cref="IpcClient"/>) so both always agree on the exact string.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scoped to the interactive user's SID</b>, matching the naming discipline
/// <c>Bastion.Daemon.SingleInstanceGuard.MutexName</c> and <c>HwndJournalLock</c> already
/// establish for this repo's other named OS objects: a fixed, predictable, unscoped pipe name
/// would let a second user session on the same machine collide with (or, per <c>CreateMutex</c>'s
/// own documented remarks about predictable names, squat) this one. Unlike those two types' names,
/// a named pipe has no <c>Local\</c>/<c>Global\</c> namespace prefix convention of its own — the
/// bare pipe name below is exactly what both <see cref="System.IO.Pipes.NamedPipeServerStream"/>
/// and <see cref="System.IO.Pipes.NamedPipeClientStream"/>'s <c>pipeName</c> constructor parameter
/// expects (verified against
/// https://learn.microsoft.com/dotnet/api/system.io.pipes.namedpipeserverstream.-ctor and
/// https://learn.microsoft.com/dotnet/api/system.io.pipes.namedpipeclientstream.-ctor: neither
/// documents or expects a <c>Local\</c>/<c>Global\</c>-style prefix; the pipe is exposed at
/// <c>\\.\pipe\&lt;name&gt;</c> internally regardless).
/// </para>
/// <para>
/// <b>Not the single-instance mutex.</b> <c>PipeOptions.CurrentUserOnly</c> on every server/client
/// stream (json-ipc-config.md §4) is what actually enforces same-user, same-elevation-level access
/// at the OS level — the SID in the name below is defense-in-depth against name collision on a
/// shared machine, not the pipe's real security boundary.
/// </para>
/// </remarks>
internal static class IpcPipeNames
{
    /// <summary>The request/reply command pipe (DESIGN.md §3.9) — see <see cref="IpcCommandServerPump"/>.</summary>
    public static string Command { get; } = $"Bastion.Ipc.Command.{WindowsIdentity.GetCurrent().User!.Value}";

    /// <summary>The broadcast state-subscription pipe (DESIGN.md §3.9) — see <see cref="IpcBroadcastServerPump"/>.</summary>
    public static string Broadcast { get; } = $"Bastion.Ipc.Broadcast.{WindowsIdentity.GetCurrent().User!.Value}";
}
