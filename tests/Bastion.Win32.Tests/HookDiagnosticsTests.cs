using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// Exercises <see cref="HookDiagnostics"/>'s one native-callback-constrained logging mechanism:
/// <see cref="HookDiagnostics.LogCallbackFault(Exception)"/> reading a static, once-set
/// <see cref="ILogger"/> reference rather than resolving one from DI (GitHub issue #14). Every
/// <see cref="Fact"/> below both starts and ends its own mutation of that shared static field with
/// <see cref="HookDiagnostics.ResetForTesting"/>, so the tests are order-independent regardless of
/// xUnit's execution order within this class — <see cref="HookDiagnostics"/>' static state is never
/// touched by any other test class in this project (verified: it is the sole consumer).
/// </summary>
public sealed class HookDiagnosticsTests
{
    [Fact]
    public void LogCallbackFaultBeforeInitializeDoesNotThrow()
    {
        HookDiagnostics.ResetForTesting();

        // No ILogger has been handed over yet -- e.g. a test constructing WinEventPumpService/
        // WindowProbe directly via InternalsVisibleTo, with no composition root ever having run.
        // The pre-existing minimal Console.Error fallback must still make this a safe, non-throwing
        // call: reaching the end of this test method (without HookDiagnostics.LogCallbackFault
        // itself throwing) is the assertion, matching this repo's established "no assertion beyond
        // 'this line was reached'" pattern for a mandatory-catch-all-adjacent boundary (see
        // HotkeyDispatchTests' identical shape for HotkeyDispatch.InvokeSafely).
        HookDiagnostics.LogCallbackFault(new InvalidOperationException("boom"));
    }

    [Fact]
    public void InitializeMakesLogCallbackFaultLogThroughTheGivenLogger()
    {
        HookDiagnostics.ResetForTesting();
        // HookDiagnostics is a static class, so FakeLogger<HookDiagnostics> (a generic type
        // argument) is not legal C# (CS0718) -- the non-generic FakeLogger works identically for
        // this assertion's purposes, which never inspects the category name.
        var logger = new FakeLogger();
        var exception = new InvalidOperationException("boom");

        try
        {
            HookDiagnostics.Initialize(logger);
            HookDiagnostics.LogCallbackFault(exception);

            FakeLogRecord record = Assert.Single(logger.Collector.GetSnapshot());
            Assert.Equal(LogLevel.Error, record.Level);
            Assert.Same(exception, record.Exception);
        }
        finally
        {
            HookDiagnostics.ResetForTesting();
        }
    }

    [Fact]
    public void LogCallbackFaultNeverThrowsEvenWhenTheInitializedLoggerItselfFaults()
    {
        HookDiagnostics.ResetForTesting();

        try
        {
            // Defense in depth (see LogCallbackFault's own remarks): a faulting ILogger provider
            // must not itself become the exception that escapes the native boundary. Reaching the
            // end of this test method is the assertion.
            HookDiagnostics.Initialize(new ThrowingLogger());
            HookDiagnostics.LogCallbackFault(new InvalidOperationException("boom"));
        }
        finally
        {
            HookDiagnostics.ResetForTesting();
        }
    }

    [Fact]
    public void InitializeThrowsForANullLogger()
    {
        Assert.Throws<ArgumentNullException>(() => HookDiagnostics.Initialize(null!));
    }

    [Fact]
    public void InitializeThrowsIfAlreadyInitialized()
    {
        // Codex/Copilot PR review finding: a second Initialize call must not silently overwrite the
        // logger -- that would make LogCallbackFault's choice of logger non-deterministic depending
        // on call order, for no legitimate reason (production has exactly one composition root and
        // therefore exactly one legitimate caller).
        HookDiagnostics.ResetForTesting();

        try
        {
            HookDiagnostics.Initialize(new FakeLogger());

            Assert.Throws<InvalidOperationException>(() => HookDiagnostics.Initialize(new FakeLogger()));
        }
        finally
        {
            HookDiagnostics.ResetForTesting();
        }
    }

    [Fact]
    public void LogCallbackFaultNeverThrowsWhenTheExceptionsOwnToStringThrows()
    {
        // Codex/Copilot PR review finding: LogCallbackFault's Console.Error fallback interpolates
        // the exception, which calls the exception's own ToString() implicitly -- a pathological
        // override must not escape LogCallbackFault either. No logger initialized, so this exercises
        // WriteFallback via the "logger is null" branch specifically.
        HookDiagnostics.ResetForTesting();

        HookDiagnostics.LogCallbackFault(new ToStringThrowsException());
    }

    [Fact]
    public void LogCallbackFaultNeverThrowsWhenBothTheLoggerAndTheExceptionsToStringFault()
    {
        // The doubly-defensive case: LogCallbackFaultCore throws (ThrowingLogger), which routes to
        // WriteFallback from LogCallbackFault's own catch block -- and WriteFallback's own attempt to
        // render the exception then faults too (ToStringThrowsException). Nothing should escape even
        // when every layer of the fallback chain fails simultaneously.
        HookDiagnostics.ResetForTesting();

        try
        {
            HookDiagnostics.Initialize(new ThrowingLogger());
            HookDiagnostics.LogCallbackFault(new ToStringThrowsException());
        }
        finally
        {
            HookDiagnostics.ResetForTesting();
        }
    }

    /// <summary>A minimal <see cref="ILogger"/> that always faults, simulating a broken logging provider.</summary>
    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("simulated faulting logging provider");
    }

    /// <summary>An exception whose own <see cref="ToString"/> faults, simulating a pathological override.</summary>
    private sealed class ToStringThrowsException : Exception
    {
        public ToStringThrowsException()
        {
        }

        public ToStringThrowsException(string message)
            : base(message)
        {
        }

        public ToStringThrowsException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        public override string ToString() => throw new InvalidOperationException("simulated faulting Exception.ToString()");
    }
}
