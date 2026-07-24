using Bastion.Core;
using Xunit;

namespace Bastion.Core.Tests;

/// <summary>
/// Exercises <see cref="IpcCommandProcessor"/> directly — no pipes, no hosting, no JSON, per that
/// type's own remarks on why dispatch logic is pure and lives in <c>Bastion.Core</c>.
/// </summary>
/// <remarks>
/// Example-based facts rather than an FsCheck property suite (docs/engineering/testing.md §3, the
/// <c>pure-core</c> skill's usual preference): <see cref="IpcCommandProcessor.Process"/>'s entire
/// behavior space is a small, fully-enumerable set of cases (the one known command, a version
/// mismatch, an unrecognized command shape) — mirrors <c>WindowRulesDocumentTests</c>'s own
/// identical reasoning for <see cref="WindowRulesDocument.Merge"/>.
/// </remarks>
public sealed class IpcCommandProcessorTests
{
    [Fact]
    public void ProcessReturnsAStatusReplyCarryingTheConfiguredDaemonVersionForAStatusCommand()
    {
        var processor = new IpcCommandProcessor(daemonVersion: "1.2.3-test");

        IpcReply reply = processor.Process(new StatusCommand(IpcCommand.CurrentProtocolVersion));

        StatusReply status = Assert.IsType<StatusReply>(reply);
        Assert.Equal("1.2.3-test", status.DaemonVersion);
        Assert.Equal(IpcCommand.CurrentProtocolVersion, status.ProtocolVersion);
    }

    [Fact]
    public void ProcessReturnsAProtocolVersionMismatchReplyWhenTheCommandsVersionDiffers()
    {
        var processor = new IpcCommandProcessor(daemonVersion: "1.2.3-test");

        IpcReply reply = processor.Process(new StatusCommand(ProtocolVersion: IpcCommand.CurrentProtocolVersion + 1));

        ProtocolVersionMismatchReply mismatch = Assert.IsType<ProtocolVersionMismatchReply>(reply);
        Assert.Equal(IpcCommand.CurrentProtocolVersion, mismatch.ProtocolVersion);
        Assert.Equal(IpcCommand.CurrentProtocolVersion + 1, mismatch.ReceivedProtocolVersion);
    }

    [Fact]
    public void ProcessReturnsAnErrorReplyForACommandShapeItDoesNotRecognizeEvenAtTheCurrentProtocolVersion()
    {
        // IpcCommand is a public, non-sealed abstract record -- a same-version, unrecognized-to-
        // the-switch subtype is a real possibility this defensive default arm exists for (see
        // IpcCommandProcessor's own remarks), even though IpcJsonContext's closed [JsonDerivedType]
        // set never actually produces one via real deserialization today.
        var processor = new IpcCommandProcessor(daemonVersion: "1.2.3-test");

        IpcReply reply = processor.Process(new FutureCommand(IpcCommand.CurrentProtocolVersion));

        ErrorReply error = Assert.IsType<ErrorReply>(reply);
        Assert.Contains("FutureCommand", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProcessThrowsArgumentNullExceptionForANullCommand() =>
        Assert.Throws<ArgumentNullException>(() => new IpcCommandProcessor("1.2.3-test").Process(null!));

    private sealed record FutureCommand(int ProtocolVersion) : IpcCommand(ProtocolVersion);
}
