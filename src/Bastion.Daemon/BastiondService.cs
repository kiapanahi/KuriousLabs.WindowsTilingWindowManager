using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bastion.Daemon;

/// <summary>
/// Placeholder composition-root hosted service.
/// </summary>
/// <remarks>
/// TODO(DESIGN.md §3.1-§3.10): this needs to become (or hand off to) the WinEvent ingest pump,
/// Coalescer, Reconciler, Placement Executor, IPC command/broadcast servers, the startup
/// <c>EnumWindows</c> adoption pass with journaling, and the watchdog-observable lifecycle those
/// sections describe. This stub only proves the <see cref="Host.CreateApplicationBuilder(string[])"/>
/// + <see cref="BackgroundService"/> composition builds, publishes, and runs under NativeAOT.
/// </remarks>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "Instantiated by the DI container via AddHostedService<BastiondService>() in " +
        "Program.cs, not via a visible constructor call — a documented CA1812 false-positive " +
        "class for IoC-registered types.")]
internal sealed partial class BastiondService(ILogger<BastiondService> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted();
        try
        {
            // Placeholder: real work is event-driven (WinEvent hooks + IPC listeners), not a
            // polling loop. This just keeps the host alive until shutdown is requested.
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected on graceful shutdown — DESIGN.md §3.10.
        }

        LogStopped();
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "bastiond started (placeholder composition root; see DESIGN.md §3.1-§3.10).")]
    private partial void LogStarted();

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "bastiond stopping.")]
    private partial void LogStopped();
}
