using Microsoft.Extensions.Logging;

namespace Bastion.Daemon;

/// <summary>
/// The default <see cref="IWindowRulesReloadNotifier"/>: logs via the standard
/// <c>[LoggerMessage]</c> source-generated pattern (<c>docs/engineering/daemon-architecture.md</c>
/// §3) until a real <c>Bastion.Bar</c> toast (DESIGN.md §3.8, v0.3) implements the same interface.
/// </summary>
internal sealed partial class LoggingWindowRulesReloadNotifier(ILogger<LoggingWindowRulesReloadNotifier> logger)
    : IWindowRulesReloadNotifier
{
    /// <inheritdoc/>
    public void NotifyReloadSucceeded() => LogReloadSucceeded(logger);

    /// <inheritdoc/>
    public void NotifyReloadFailed(string reason) => LogReloadFailed(logger, reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Config directory changed; rules reloaded successfully.")]
    private static partial void LogReloadSucceeded(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Config reload failed, keeping previous rules: {Reason}")]
    private static partial void LogReloadFailed(ILogger logger, string reason);
}
