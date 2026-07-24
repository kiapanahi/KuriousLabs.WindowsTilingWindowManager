namespace Bastion.Core;

/// <summary>
/// Queries whether <c>bastiond</c> is alive and, if so, which version — GitHub issues #11/#12's
/// one concrete, round-tripped command. See <see cref="IpcCommandProcessor"/> for the reply this
/// produces.
/// </summary>
/// <remarks>
/// Deliberately the only concrete <see cref="IpcCommand"/> this PR ships. <c>restore-windows</c>
/// (GitHub issue #8) is already real but is a standalone, non-IPC path by design (DESIGN.md §3.7:
/// it must work "even with the daemon dead," so it talks to the write-ahead HWND journal directly
/// via <c>Bastion.Win32</c>, never through this pipe) and retrofitting it to also try IPC first is
/// scope creep on an already-closed issue. <c>doctor</c> is v0.4 (DESIGN.md §12). <c>status</c> is
/// simple, genuinely useful (it is what proves <c>bastiond</c> is reachable at all), and was the
/// original pre-issue-#8 stub in <c>Bastion.Cli/Program.cs</c> reserved for exactly this.
/// </remarks>
/// <param name="ProtocolVersion">See <see cref="IpcCommand.ProtocolVersion"/>.</param>
public sealed record StatusCommand(int ProtocolVersion) : IpcCommand(ProtocolVersion);
