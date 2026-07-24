using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// Exercises <see cref="LoggingHotkeyDispatchTarget"/> — the default, ordinary-managed-code
/// (never a native-callback) <see cref="IHotkeyDispatchTarget"/> — against a real, DI-shaped
/// constructor-injected <see cref="ILogger{TCategoryName}"/> (GitHub issue #14).
/// </summary>
public sealed class LoggingHotkeyDispatchTargetTests
{
    [Fact]
    public void OnHotkeyInvokedLogsTheCommandThroughTheInjectedLogger()
    {
        var logger = new FakeLogger<LoggingHotkeyDispatchTarget>();
        var target = new LoggingHotkeyDispatchTarget(logger);

        target.OnHotkeyInvoked(HotkeyCommand.FocusLeft);

        FakeLogRecord record = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Information, record.Level);
        Assert.Contains(new KeyValuePair<string, string?>("Command", nameof(HotkeyCommand.FocusLeft)), record.StructuredState!);
    }
}
