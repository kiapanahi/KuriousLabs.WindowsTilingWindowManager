using Microsoft.Extensions.Logging;

namespace Bastion.Daemon;

/// <summary>
/// Startup-time logging with no natural class-instance home of its own now that
/// <c>BastiondService</c> — which used to log the running version from inside its own
/// <c>ExecuteAsync</c> (GitHub issue #48/PR #49) — has been deleted in favor of the real
/// composition root (GitHub issue #10). <c>Program.cs</c>'s top-level statements cannot declare a
/// <see langword="partial"/> method themselves, so this is the smallest home for the
/// <c>[LoggerMessage]</c> source-generated call it needs.
/// </summary>
internal static partial class StartupLog
{
    /// <summary>
    /// Logs the running <c>bastiond</c> version — the
    /// <see cref="System.Reflection.AssemblyInformationalVersionAttribute"/> MinVer (GitHub issue
    /// #48) sets at build time from git tag + commit height, read the identical way
    /// <c>bastionc</c>'s own <c>PrintAssemblyVersionAction</c> does. Called once, after the host is
    /// built and before it starts running, so it appears in the log stream ahead of every hosted
    /// service's own startup messages.
    /// </summary>
    [LoggerMessage(Level = LogLevel.Information, Message = "bastiond {Version} starting.")]
    public static partial void DaemonStarting(this ILogger logger, string version);
}
