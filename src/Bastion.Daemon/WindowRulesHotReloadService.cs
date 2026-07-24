using System.Diagnostics.CodeAnalysis;
using Bastion.Core;
using Microsoft.Extensions.Hosting;

namespace Bastion.Daemon;

/// <summary>
/// Watches GitHub issue #9's user config directory and re-publishes the merged rules on every
/// debounced change (DESIGN.md §3.9): 200 ms debounce, atomic swap on a successful re-parse,
/// keep-serving-the-last-known-good plus a notification on failure.
/// </summary>
/// <remarks>
/// <para>
/// <b>Plain <see cref="IHostedService"/>, not <see cref="BackgroundService"/>.</b> There is no
/// continuous loop to run here — <see cref="StartAsync"/> just subscribes to
/// <see cref="IConfigDirectoryWatcher.Changed"/> and starts the watcher; all the actual work
/// happens in event-driven callbacks. This is the same shape as
/// <c>Bastion.Win32.JournalRestoreOnShutdownService</c> (issue #8): a hosted service that does
/// setup/teardown, not a message pump or an <c>await foreach</c> drain loop, so neither of
/// <c>docs/engineering/daemon-architecture.md</c> §2's two hosted-service rules (raw dedicated
/// thread for pumps; <see cref="BackgroundService"/> for ordinary async loops) quite applies —
/// this is the third, simplest shape.
/// </para>
/// <para>
/// <b>Debounce timer, mirroring <c>Bastion.Win32.Coalescer</c>'s established pattern.</b> A single
/// <see cref="TimeProvider"/>-backed one-shot <see cref="ITimer"/>, (re)armed to
/// <see cref="_debounce"/> on every <see cref="IConfigDirectoryWatcher.Changed"/> firing — a live
/// burst of writes (an editor's temp-file-then-rename can raise multiple events for one logical
/// save) keeps pushing the reload out until the directory actually goes quiet, exactly like the
/// Coalescer's per-(Hwnd, Kind) flush timers. All access to <see cref="_debounceTimer"/> is guarded
/// by <see cref="_gate"/> (a <see cref="Lock"/>, never held across an <see langword="await"/> —
/// there is none here) since <see cref="IConfigDirectoryWatcher.Changed"/> fires on a thread-pool
/// callback thread while <see cref="StopAsync"/> runs on whatever thread the host shutdown sequence
/// uses.
/// </para>
/// <para>
/// <b>Failure handling.</b> <see cref="WindowRulesConfigLoader.LoadMerged"/> throwing — malformed
/// JSON, a rule missing a <see langword="required"/> member, or any
/// <see cref="WindowRulesDocument.ValidateRules"/> violation (an empty name, an empty
/// <see cref="WindowRuleMatch"/>) — is caught broadly (any exception except
/// <see cref="OperationCanceledException"/>, not a narrow allow-list of specific exception types —
/// widened after review: a <see cref="TimerCallback"/> that lets an unanticipated exception type
/// escape can bring down the whole process, and this callback's whole point is "a reload attempt
/// failed for some reason," not "a reload attempt failed for one of these three specific reasons")
/// and reported via <see cref="IWindowRulesReloadNotifier.NotifyReloadFailed"/> rather than
/// propagating. <see cref="PublishedWindowRulesConfig"/> is never touched on this path, so the
/// previously-published config keeps serving unchanged (DESIGN.md §3.9's "parse errors keep the old
/// config"). This is the hot-reload boundary's policy specifically; the *startup* boundary
/// (<c>WindowRulesOptionsValidator</c> + <c>AddOptionsWithValidateOnStart</c>) instead fails
/// <c>bastiond</c> fast, since there is no "old config" yet to fall back to on the very first load
/// (<c>docs/engineering/daemon-architecture.md</c> §4) — both postures coexist by construction,
/// since they are two different code paths reading the same loader.
/// </para>
/// <para>
/// <b>An in-flight callback no-ops after <see cref="StopAsync"/>, rather than still reloading and
/// publishing</b> (caught in review): <see cref="System.Threading.Timer"/>'s own documented race —
/// a callback already queued on a thread-pool thread can still run even after the timer that
/// scheduled it has been disposed — means <see cref="OnDebounceElapsed"/> can start executing after
/// <see cref="StopAsync"/> has already disposed <see cref="_debounceTimer"/>. <see cref="_stopped"/>
/// (guarded by <see cref="_gate"/>, checked once before the (possibly slow) file read and again,
/// atomically with the publish itself, immediately before <see cref="PublishedWindowRulesConfig.Publish"/>)
/// closes this so a "stopped" service can never still swap in a new snapshot or fire a late
/// notification — matching <c>Bastion.Win32.Coalescer.FlushLocked</c>'s identical
/// re-check-after-reacquiring-the-lock pattern for the same class of timer-outlives-dispose race.
/// </para>
/// </remarks>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "Constructed by tests via InternalsVisibleTo today, and intended to be " +
        "registered via AddHostedService<WindowRulesHotReloadService>() once Bastion.Daemon's " +
        "composition root is wired (GitHub issue #10) — not yet wired as of this change. Same " +
        "documented CA1812 false-positive shape as WinEventPumpService/Coalescer/" +
        "JournalRestoreOnShutdownService.")]
internal sealed class WindowRulesHotReloadService : IHostedService, IDisposable
{
    /// <summary>DESIGN.md §3.9's "200 ms debounce," config-tunable via the constructor's <c>debounce</c> parameter. This is only the shipped default.</summary>
    internal static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(200);

    private readonly IConfigDirectoryWatcher _watcher;
    private readonly WindowRulesConfigLoader _loader;
    private readonly PublishedWindowRulesConfig _published;
    private readonly IWindowRulesReloadNotifier _notifier;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _debounce;
    private readonly Lock _gate = new();
    private readonly TimerCallback _onDebounceElapsed;
    private ITimer? _debounceTimer;
    private bool _stopped;

    public WindowRulesHotReloadService(
        IConfigDirectoryWatcher watcher,
        WindowRulesConfigLoader loader,
        PublishedWindowRulesConfig published,
        IWindowRulesReloadNotifier notifier,
        TimeProvider timeProvider,
        TimeSpan debounce)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(published);
        ArgumentNullException.ThrowIfNull(notifier);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(debounce, TimeSpan.Zero);

        _watcher = watcher;
        _loader = loader;
        _published = published;
        _notifier = notifier;
        _timeProvider = timeProvider;
        _debounce = debounce;
        _onDebounceElapsed = OnDebounceElapsed;
        _watcher.Changed += OnDirectoryChanged;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _watcher.Start();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Unsubscribe here too (not only in Dispose): a stopped hosted service must not react to
        // further changes even if the underlying watcher implementation's Stop() doesn't itself
        // guarantee no more events fire (true of the real FileSystemWatcher-backed one, but not an
        // assumption this type should make about every possible IConfigDirectoryWatcher).
        _watcher.Changed -= OnDirectoryChanged;
        _watcher.Stop();
        lock (_gate)
        {
            _stopped = true;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        return Task.CompletedTask;
    }

    private void OnDirectoryChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (_debounceTimer is null)
            {
                _debounceTimer = _timeProvider.CreateTimer(_onDebounceElapsed, null, _debounce, Timeout.InfiniteTimeSpan);
            }
            else
            {
                _debounceTimer.Change(_debounce, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void OnDebounceElapsed(object? state)
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }
        }

        try
        {
            WindowRulesDocument merged = _loader.LoadMerged();

            lock (_gate)
            {
                // Re-checked here, atomically with the publish itself: StopAsync may have run
                // (and disposed the timer) between the check above and this possibly-slow file
                // read completing -- see this type's remarks for the timer-outlives-dispose race
                // this guards against.
                if (_stopped)
                {
                    return;
                }

                _published.Publish(merged);
            }

            _notifier.NotifyReloadSucceeded();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _notifier.NotifyReloadFailed(ex.Message);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _watcher.Changed -= OnDirectoryChanged;
        lock (_gate)
        {
            _debounceTimer?.Dispose();
        }
    }
}
