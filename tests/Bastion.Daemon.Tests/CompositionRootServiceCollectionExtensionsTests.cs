using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Bastion.Daemon.Tests;

/// <summary>
/// A composition-root smoke test for GitHub issue #10: builds the exact same
/// <see cref="HostApplicationBuilder"/> registration sequence <c>Program.cs</c> does (minus the
/// single-instance mutex, which is <see cref="SingleInstanceGuardTests"/>'s own concern) and asserts
/// the container resolves every registered <see cref="IHostedService"/> through its full
/// constructor-dependency graph without a <c>DI</c> exception.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately never calls <see cref="IHost.StartAsync"/>.</b> This test's whole purpose is
/// proving the registration graph resolves — a build/test-time guarantee that would otherwise only
/// surface as a runtime "Unable to resolve service for type X" failure the first time
/// <c>bastiond</c> actually started. Resolving <see cref="IHostedService"/> instances via
/// <see cref="ServiceProviderServiceExtensions.GetServices{T}"/> already forces every constructor in
/// the graph to run (DI has no lazy/partial construction) without invoking any lifecycle method, so
/// no real WinEvent hook is installed, no real hotkey is registered via <c>RegisterHotKey</c>, and no
/// real window is ever touched — matching this issue's own "minus anything that would actually touch
/// live WinEvents/hotkeys/real windows" scope. The one real OS resource this test does touch is
/// <c>ShellComThread</c>'s own dedicated STA thread, started unconditionally from its constructor
/// (not from a lifecycle method) — already an established, safe-for-CI pattern
/// (<c>ShellComThreadTests</c> exercises the identical type directly); this test's own
/// <see langword="using IHost host"/> disposes the container (and therefore that thread) cleanly at
/// the end of every test.
/// </para>
/// <para>
/// <b>Type names, not <see langword="typeof"/>.</b> This test project references only
/// <c>Bastion.Daemon.csproj</c> (matching every other file in this project) — it has no direct
/// project reference to <c>Bastion.Win32</c>, so it cannot name any of that assembly's internal
/// types at compile time even with <c>InternalsVisibleTo</c> (which lifts the accessibility check,
/// not the need for an assembly reference to resolve the identifier in source). The concrete
/// <see cref="IHostedService"/> instances resolved here are still real, loaded-at-runtime instances
/// of those types regardless — <see cref="object.GetType"/>'s <c>Name</c> is how this test identifies
/// which ones actually landed in the collection.
/// </para>
/// </remarks>
public sealed class CompositionRootServiceCollectionExtensionsTests : IDisposable
{
    private readonly string _tempDirectory = Directory.CreateTempSubdirectory("bastion-composition-di-tests-").FullName;

    private WindowRulesConfigPaths RulesPaths => new()
    {
        ShippedRulesFilePath = Path.Combine(_tempDirectory, "rules.default.jsonc"),
        UserConfigDirectory = _tempDirectory,
        UserRulesFilePath = Path.Combine(_tempDirectory, "rules.jsonc"),
        SchemaFilePath = Path.Combine(_tempDirectory, "rules.schema.json"),
    };

    private string JournalFilePath => Path.Combine(_tempDirectory, "hwnd-journal.json");

    [Fact]
    public async Task TheFullCompositionRootResolvesEveryRegisteredHostedServiceWithoutADependencyInjectionException()
    {
        await File.WriteAllTextAsync(
            RulesPaths.ShippedRulesFilePath,
            """{ "rules": [] }""",
            TestContext.Current.CancellationToken);

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();

        // The exact registration sequence Program.cs uses (single-instance mutex aside -- that is
        // SingleInstanceGuardTests' own concern, and runs before any of this in production).
        builder.Services.AddBastionHwndJournal(JournalFilePath);
        builder.Services.AddWindowRulesConfiguration(RulesPaths);
        builder.Services.AddBastionEventAndReconciliationPipeline();
        builder.Services.AddBastionInputPipeline();
        builder.Services.AddBastionMonitorTopologyStub();
        builder.Services.AddBastionIpcServer(daemonVersion: "test-version");

        using IHost host = builder.Build();

        List<IHostedService> hostedServices = [.. host.Services.GetServices<IHostedService>()];

        // One hosted service per registration call across every AddBastionXxx/AddWindowRulesConfiguration
        // extension: journal restore; hot-reload + schema publisher; the default-workspace seed,
        // WinEvent pump, Coalescer, ReconcilerIntentPump, ReconcilerLoopService,
        // PlacementExecutionPump; input pump; the monitor-topology stub; the IPC command server
        // pump and the IPC broadcast server pump (GitHub issues #11/#12). A mismatch here means
        // either a missing registration or an accidental double-registration -- both real
        // composition-root bugs.
        Assert.Equal(13, hostedServices.Count);

        HashSet<string> hostedServiceTypeNames = [.. hostedServices.Select(s => s.GetType().Name)];
        Assert.Contains("JournalRestoreOnShutdownService", hostedServiceTypeNames);
        Assert.Contains("WindowRulesHotReloadService", hostedServiceTypeNames);
        Assert.Contains("WindowRulesSchemaPublisherService", hostedServiceTypeNames);
        Assert.Contains("DefaultWorkspaceSeedingService", hostedServiceTypeNames);
        Assert.Contains("WinEventPumpService", hostedServiceTypeNames);
        Assert.Contains("Coalescer", hostedServiceTypeNames);
        Assert.Contains("ReconcilerIntentPump", hostedServiceTypeNames);
        Assert.Contains("ReconcilerLoopService", hostedServiceTypeNames);
        Assert.Contains("PlacementExecutionPump", hostedServiceTypeNames);
        Assert.Contains("InputPumpService", hostedServiceTypeNames);
        Assert.Contains("MonitorTopologyPlaceholderService", hostedServiceTypeNames);
        Assert.Contains("IpcCommandServerPump", hostedServiceTypeNames);
        Assert.Contains("IpcBroadcastServerPump", hostedServiceTypeNames);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
