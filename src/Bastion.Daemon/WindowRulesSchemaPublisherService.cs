using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bastion.Daemon;

/// <summary>
/// Writes GitHub issue #9's published JSON Schema once, at startup — see
/// <see cref="WindowRulesSchemaWriter"/>'s remarks for why startup (not build-time) is this issue's
/// chosen generation point.
/// </summary>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "Constructed by tests via InternalsVisibleTo today, and intended to be " +
        "registered via AddHostedService<WindowRulesSchemaPublisherService>() once Bastion.Daemon's " +
        "composition root is wired (GitHub issue #10) — not yet wired as of this change.")]
internal sealed partial class WindowRulesSchemaPublisherService(
    WindowRulesConfigPaths paths, ILogger<WindowRulesSchemaPublisherService> logger) : IHostedService
{
    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await WindowRulesSchemaWriter.WriteAsync(paths.SchemaFilePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort per WindowRulesSchemaWriter's remarks: a failure to write the schema
            // (e.g. a locked/read-only user config directory) must never prevent bastiond from
            // starting -- it is an editor-tooling convenience, not part of the config-gating
            // contract itself.
            LogSchemaWriteFailed(logger, ex);
        }
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to write the published rules JSON Schema; continuing without it.")]
    private static partial void LogSchemaWriteFailed(ILogger logger, Exception exception);
}
