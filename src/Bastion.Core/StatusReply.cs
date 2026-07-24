namespace Bastion.Core;

/// <summary>
/// Successful reply to a <see cref="StatusCommand"/>: <c>bastiond</c> is alive and running
/// <paramref name="DaemonVersion"/>.
/// </summary>
/// <remarks>
/// Deliberately minimal for v0.1: the original pre-issue-#8 <c>status</c> stub in
/// <c>Bastion.Cli/Program.cs</c> described this as querying "the daemon's current window/monitor
/// topology," but no monitor topology exists yet (the Monitor Topology Service, GitHub issue #16,
/// is a placeholder — <c>Bastion.Win32.MonitorTopologyPlaceholderService</c>'s own remarks) and
/// v0.1 has no multi-workspace state to report either (DESIGN.md §12). Reporting liveness +
/// version is the honest, currently-buildable subset — a real topology/window-count payload is a
/// natural, additive extension once issue #16 lands real state to report (polymorphic
/// <see cref="IpcReply"/> already accommodates a richer reply type later without touching this
/// one).
/// </remarks>
/// <param name="ProtocolVersion">See <see cref="IpcReply.ProtocolVersion"/>.</param>
/// <param name="DaemonVersion">
/// The running <c>bastiond</c>'s MinVer-derived informational version (GitHub issue #48), read the
/// identical way <c>Program.cs</c>'s own startup log and <c>bastionc</c>'s
/// <c>PrintAssemblyVersionAction</c> already do.
/// </param>
public sealed record StatusReply(int ProtocolVersion, string DaemonVersion) : IpcReply(ProtocolVersion);
