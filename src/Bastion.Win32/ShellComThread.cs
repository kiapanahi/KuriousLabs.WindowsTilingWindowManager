using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;

namespace Bastion.Win32;

/// <summary>
/// The single dedicated STA thread interop.md §5 requires for every shell-COM call
/// (<c>IPropertyStore</c> today; <c>IVirtualDesktopManager</c>/<c>ITaskbarList</c> in later
/// issues) — "exactly one dedicated STA thread ... performs every shell-COM call ... Never from
/// <c>Task.Run</c>, the thread pool, or any other thread." This is the first shell-COM consumer in
/// the codebase, so this type stands the thread up; every future consumer reuses it via
/// <see cref="InvokeAsync{T}"/> rather than constructing its own.
/// </summary>
/// <remarks>
/// <para>
/// <b>Construction mechanics</b> (concurrency-performance.md §2): a raw dedicated
/// <see cref="Thread"/>, never <see cref="Task.Run(Action)"/> or
/// <see cref="TaskCreationOptions.LongRunning"/> (both produce MTA thread-pool threads that cannot
/// have <see cref="Thread.SetApartmentState"/> applied — it must be called before
/// <see cref="Thread.Start()"/>, which is exactly the ordering used below).
/// </para>
/// <para>
/// <b>Work dispatch, scoped to what's needed today</b>: interop.md §5 describes the thread as
/// eventually "pumping its own message loop" — but that describes the <em>eventual</em> shape once
/// this thread also owns Bastion's own HWNDs (the bar window, a hidden owner window — neither
/// exists yet; both are later work, Bastion.Bar is GitHub issue #19/v0.3). Today this thread has no
/// incoming callback surface and executes only short-lived, synchronous shell-COM calls, so a
/// blocking work-queue drain loop is sufficient and correctly scoped: no
/// <c>GetMessage</c>/<c>DispatchMessage</c> pump is needed until a real window lives here. A
/// message loop can be layered on top of this same "one thread, funnel every shell-COM call
/// through it" contract later without changing <see cref="InvokeAsync{T}"/>'s shape.
/// </para>
/// <para>
/// <b>Caveat, honestly flagged rather than silently assumed</b>: the documented
/// <c>CoInitializeEx</c> STA guidance ("Initializing the COM Library",
/// https://learn.microsoft.com/windows/win32/learnwin32/initializing-the-com-library) states an
/// apartment-threaded thread should "have a message loop" so that calls proxied in <em>from other
/// apartments</em> can be dispatched at message-queue boundaries. Nothing marshals a call into this
/// thread from another apartment today — every use is this thread issuing an outbound call on an
/// interface pointer it itself obtained on this same thread — so the documented guidance's
/// rationale does not yet apply. This is deliberately re-evaluated the day something needs to call
/// <em>into</em> this thread from COM (an eventual sink interface, or <c>ITaskbarButtonCreated</c>
/// registration) rather than assumed safe indefinitely.
/// </para>
/// <para>
/// <b>Result/exception marshaling</b>: each queued <see cref="Action"/> completes a
/// <see cref="TaskCompletionSource{TResult}"/> with <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>
/// — without that flag, a continuation awaiting the returned <see cref="Task{TResult}"/> could run
/// synchronously inline on <em>this</em> thread inside <c>SetResult</c>/<c>SetException</c>,
/// stealing time from the next queued shell-COM call and, worse, letting arbitrary caller
/// continuation code execute on the one thread this type exists to keep free for COM.
/// </para>
/// </remarks>
internal sealed class ShellComThread : IDisposable
{
    private readonly BlockingCollection<Action> _workItems = new();
    private readonly Thread _thread;
    private bool _disposed;

    public ShellComThread()
    {
        _thread = new Thread(RunLoop)
        {
            IsBackground = false,
        };
        _thread.SetApartmentState(ApartmentState.STA); // must precede Start() — throws afterward
        _thread.Name = "Bastion.ShellComThread";
        _thread.Start();
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the dedicated STA thread and returns its result. The
    /// general-purpose entry point every shell-COM consumer (this issue's <c>IPropertyStore</c>
    /// read; future <c>IVirtualDesktopManager</c>/<c>ITaskbarList</c> work) funnels through.
    /// </summary>
    public Task<T> InvokeAsync<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_workItems.TryAdd(RunAndComplete))
        {
            // CompleteAdding() has already been called (Dispose in progress/complete) — the
            // caller gets a faulted task rather than an item silently dropped into a closed queue.
            completion.SetException(new ObjectDisposedException(nameof(ShellComThread)));
        }

        return completion.Task;

        [SuppressMessage(
            "Design",
            "CA1031:Do not catch general exception types",
            Justification = "Deliberate catch-all: this runs on the dedicated ShellComThread, " +
                "off the caller's call stack, so any exception from the shell-COM action — " +
                "whatever its type — must be captured and marshaled back through the Task " +
                "returned by InvokeAsync rather than crossing threads unhandled or crashing the " +
                "ShellComThread's drain loop.")]
        void RunAndComplete()
        {
            try
            {
                completion.SetResult(action());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }
    }

    private void RunLoop()
    {
        // DOCUMENTED CONTRACT (verified against
        // https://learn.microsoft.com/windows/win32/api/combaseapi/nf-combaseapi-coinitializeex):
        // "You must set exactly one of these flags" (COINIT_APARTMENTTHREADED here); every
        // successful CoInitializeEx must be balanced by exactly one CoUninitialize.
        HRESULT hr = PInvoke.CoInitializeEx(COINIT.COINIT_APARTMENTTHREADED);
        if (hr.Failed)
        {
            // This thread's only purpose is shell-COM work; if COM itself never came up, every
            // queued item would fail identically and silently. A fresh, never-before-initialized
            // thread failing CoInitializeEx is not an anticipated, recoverable condition (no prior
            // call on this thread could have set a conflicting apartment mode) — fail loudly
            // rather than leave a permanently-broken thread draining a queue nothing can service.
            throw new InvalidOperationException($"CoInitializeEx failed on {nameof(ShellComThread)}: 0x{(int)hr:X8}");
        }

        try
        {
            foreach (Action workItem in _workItems.GetConsumingEnumerable())
            {
                workItem();
            }
        }
        finally
        {
            PInvoke.CoUninitialize();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _workItems.CompleteAdding();
        _thread.Join();
        _workItems.Dispose();
    }
}
