using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Bastion.TestWindows;

/// <summary>
/// Purpose-built <c>CreateWindowExW</c> spawner for Tier 3 integration tests (DESIGN.md §11) —
/// never Notepad or any other production app. Creates one real top-level window with a
/// parameterized initial size, minimum-track size, manageability-filter-relevant ex-styles, and
/// owner (<see cref="TestWindowOptions"/>), prints its HWND to stdout so a driving test harness can
/// attach to it, then pumps messages until the window is destroyed.
/// </summary>
/// <remarks>
/// <para>
/// Two ways the window closes. <b>Externally</b>: another process sends
/// <c>SendMessage</c>/<c>PostMessage(WM_CLOSE)</c> to the printed HWND — cross-process-safe per
/// <c>PostMessage</c>'s own documented contract (subject to UIPI integrity-level matching), unlike
/// <c>DestroyWindow</c>, whose contract is "a thread cannot use <c>DestroyWindow</c> to destroy a
/// window created by a different thread": a driving harness is always a different process/thread,
/// so it can request a close but can never call <c>DestroyWindow</c> on this window directly, only
/// this process's own main thread ever legally can. <b>Internally, via stdin</b>
/// (<see cref="WatchStdinForShutdownSignal"/>): covers the no-leak requirement (GitHub issue #13's acceptance
/// criteria) for a harness that dies mid-assertion without ever sending that external close request.
/// This process's own dedicated background thread blocks on a single stdin read and posts
/// <c>WM_CLOSE</c> to itself as soon as that read returns — either an explicit "please exit" line a
/// still-alive harness chooses to write, or EOF, which happens automatically once a redirecting
/// harness's process exits, cleanly or not; content is never inspected, so either outcome is treated
/// identically — routing through the exact same <c>WM_CLOSE</c> -&gt; <c>DefWindowProc</c> -&gt;
/// <c>DestroyWindow</c> -&gt; <c>WM_DESTROY</c> -&gt; <c>PostQuitMessage(0)</c> path as the external
/// case, rather than a second, parallel exit mechanism.
/// </para>
/// <para>
/// TODO(DESIGN.md §11): the harness-side driver that asserts via
/// <c>DWMWA_EXTENDED_FRAME_BOUNDS</c> readbacks is a follow-up once this spawner exists — out of
/// scope for GitHub issue #13, which covers only this spawner's own parameter surface and cleanup.
/// </para>
/// </remarks>
internal static class TestWindowSpawner
{
    private const string ClassName = "BastionTestWindow";

    // This process spawns exactly one window on one thread and pumps its own message loop, so a
    // thread-static pair (rather than the GCHandle-per-callback registry interop.md §3.2 requires
    // for the daemon's long-lived, arbitrarily-reentrant hook callbacks) is sufficient here for
    // WndProc to answer WM_GETMINMAXINFO with the caller-requested minimum size.
#pragma warning disable IDE1006 // Naming rule violation: BCL-style "t_" thread-static prefix.
    [ThreadStatic]
    private static int t_minTrackWidth;

    [ThreadStatic]
    private static int t_minTrackHeight;
#pragma warning restore IDE1006

    public static unsafe int Run(TestWindowOptions options)
    {
        t_minTrackWidth = options.MinWidth;
        t_minTrackHeight = options.MinHeight;

        HINSTANCE instance = PInvoke.GetModuleHandle((string?)null);

        if (!TryRegisterWindowClass(instance))
        {
            Console.Error.WriteLine("RegisterClassEx failed.");
            return 1;
        }

        HWND hwnd = PInvoke.CreateWindowEx(
            BuildExStyle(options),
            ClassName,
            options.Title,
            WINDOW_STYLE.WS_OVERLAPPEDWINDOW,
            PInvoke.CW_USEDEFAULT,
            PInvoke.CW_USEDEFAULT,
            options.Width,
            options.Height,
            ResolveOwnerHwnd(options),
            HMENU.Null,
            instance,
            null);

        if (hwnd.IsNull)
        {
            Console.Error.WriteLine("CreateWindowEx failed.");
            return 1;
        }

        // Tier 3 harness contract: the spawned window's HWND is the sole line on stdout, printed
        // once the window exists and before the message pump starts.
        Console.WriteLine(((nint)hwnd.Value).ToString(CultureInfo.InvariantCulture));

        PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_SHOWNORMAL);
        PInvoke.UpdateWindow(hwnd);

        // No-leak cleanup path (GitHub issue #13's acceptance criteria) — see this type's remarks
        // and StartStdinWatcher's own comment for why the watcher thread is background, not
        // foreground.
        StartStdinWatcher(hwnd);

        while (PInvoke.GetMessage(out MSG message, HWND.Null, 0, 0))
        {
            PInvoke.TranslateMessage(in message);
            PInvoke.DispatchMessage(in message);
        }

        return 0;
    }

    private static unsafe bool TryRegisterWindowClass(HINSTANCE instance)
    {
        fixed (char* className = ClassName)
        {
            WNDCLASSEXW windowClass = new()
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                style = WNDCLASS_STYLES.CS_HREDRAW | WNDCLASS_STYLES.CS_VREDRAW,
                lpfnWndProc = &WndProc,
                hInstance = instance,
                hCursor = PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_ARROW),
                lpszClassName = className,
            };

            return PInvoke.RegisterClassEx(in windowClass) != 0;
        }
    }

    /// <summary>
    /// Manageability-filter-relevant ex-styles (DESIGN.md §3.3), all parameterized so a driving
    /// Tier 3 test can produce every combination the filter branches on. <c>WS_EX_APPWINDOW</c> is
    /// the documented "unless" carve-out for both <c>WS_EX_TOOLWINDOW</c> and an owner (see
    /// <see cref="ResolveOwnerHwnd"/>), so it defaults on to match this spawner's original baseline
    /// and is opted out of via <c>--no-app-window</c> rather than defaulting off like the other two
    /// styles.
    /// </summary>
    private static WINDOW_EX_STYLE BuildExStyle(TestWindowOptions options)
    {
        WINDOW_EX_STYLE exStyle = default;
        if (options.AppWindow)
        {
            exStyle |= WINDOW_EX_STYLE.WS_EX_APPWINDOW;
        }

        if (options.ToolWindow)
        {
            exStyle |= WINDOW_EX_STYLE.WS_EX_TOOLWINDOW;
        }

        if (options.NoActivate)
        {
            exStyle |= WINDOW_EX_STYLE.WS_EX_NOACTIVATE;
        }

        return exStyle;
    }

    /// <summary>
    /// <c>CreateWindowEx</c>'s own documented owner mechanism —
    /// learn.microsoft.com/windows/win32/winmsg/window-features#window-relationships: "An
    /// application creates an owned window by specifying the owner's window handle as the
    /// hwndParent parameter of CreateWindowEx when it creates a window with the WS_OVERLAPPED or
    /// WS_POPUP style." This window is never <c>WS_CHILD</c> (always
    /// <c>WS_OVERLAPPEDWINDOW</c>), so a non-null <c>hWndParent</c> here makes it a genuinely owned
    /// top-level window: <c>GetWindow(GW_OWNER)</c> reads this value back. The owner HWND comes
    /// from a second, separately-spawned instance of this same process, via
    /// <c>--owner &lt;hwnd-value&gt;</c> pointed at the first instance's own printed HWND (see this
    /// type's remarks).
    /// </summary>
    private static HWND ResolveOwnerHwnd(TestWindowOptions options) =>
        options.OwnerHwnd is { } owner ? new HWND(owner) : HWND.Null;

    /// <summary>
    /// Starts the dedicated background thread that watches stdin for a shutdown signal (see this
    /// type's remarks and <see cref="WatchStdinForShutdownSignal"/>). <c>IsBackground = true</c> is deliberate, and is the
    /// opposite of <c>InputPumpService</c>'s/<c>WinEventPumpService</c>'s/<c>ShellComThread</c>'s
    /// own dedicated pump threads (which set <c>IsBackground = false</c> because the daemon owns an
    /// explicit, deterministic stop signal for them): nothing in this small standalone tool ever
    /// joins or signals this thread, so if the window instead closes via the pre-existing external
    /// <c>SendMessage</c>/<c>PostMessage(WM_CLOSE)</c> path while stdin is still open, a foreground
    /// thread stuck in <c>Console.In.ReadLine()</c> would otherwise hang this process forever after
    /// its window is already gone — exactly the leak this issue closes, not a new one.
    /// </summary>
    private static void StartStdinWatcher(HWND hwnd)
    {
        new Thread(() => WatchStdinForShutdownSignal(hwnd))
        {
            Name = "Bastion.TestWindows.StdinWatcher",
            IsBackground = true,
        }.Start();
    }

    /// <summary>
    /// Runs on the dedicated background thread <see cref="Run"/> starts. Blocks on a single read of
    /// this process's own stdin, then posts <c>WM_CLOSE</c> to <paramref name="hwnd"/> regardless of
    /// whether that read returned a line or EOF — see this type's remarks for why this exists and
    /// why the request has to be posted rather than calling <c>DestroyWindow</c> directly from here.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Top-level fault barrier for this dedicated thread: whatever goes wrong " +
            "while watching stdin must still fall through to requesting the window close, and an " +
            "unhandled exception here would otherwise silently crash the whole process without " +
            "ever posting WM_CLOSE.")]
    private static void WatchStdinForShutdownSignal(HWND hwnd)
    {
        try
        {
            // A single call is enough, and deliberately not a loop: ReadLine returns on either a
            // harness's explicit "please exit" line (any content — a non-null return) or EOF (a
            // null return), and both mean the same thing here, so either return value should fall
            // through to the close request below immediately. A `while (... is not null)` loop
            // would instead discard that first line and block again waiting for a *second* one (or
            // EOF) before ever closing — indefinitely, if a harness writes its signal line but
            // deliberately keeps its own end of the pipe open afterward (e.g. to keep watching the
            // child before its own exit). Content is genuinely never inspected either way.
            Console.In.ReadLine();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Stdin watcher fault; closing test window anyway: {ex}");
        }

        // PostMessage — unlike DestroyWindow — is documented to work from any thread, posting into
        // "the message queue associated with the thread that created the specified window"
        // (learn.microsoft.com/windows/win32/api/winuser/nf-winuser-postmessagew), which is exactly
        // why this thread posts WM_CLOSE instead of calling DestroyWindow directly: "A thread cannot
        // use DestroyWindow to destroy a window created by a different thread"
        // (learn.microsoft.com/windows/win32/api/winuser/nf-winuser-destroywindow), and this thread
        // is never the thread that created hwnd (Run's message-pump thread is). WM_CLOSE's default
        // DefWindowProc handling calls DestroyWindow itself
        // (learn.microsoft.com/windows/win32/winmsg/wm-close: "By default, the DefWindowProc
        // function calls the DestroyWindow function to destroy the window"), so this routes through
        // the exact same WM_DESTROY -> PostQuitMessage(0) handling in WndProc below — one exit path,
        // not two.
        if (!PInvoke.PostMessage(hwnd, PInvoke.WM_CLOSE, 0, 0))
        {
            Console.Error.WriteLine(
                $"PostMessage(WM_CLOSE) failed (Win32 error {Marshal.GetLastPInvokeError()}); " +
                "the window may already be gone.");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Mandatory catch-all: an exception must never escape an " +
            "[UnmanagedCallersOnly] callback across the native boundary. See " +
            "docs/engineering/interop.md §3.3.")]
    private static unsafe LRESULT WndProc(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            switch (message)
            {
                case PInvoke.WM_DESTROY:
                    PInvoke.PostQuitMessage(0);
                    return new LRESULT(0);

                case PInvoke.WM_GETMINMAXINFO:
                    var info = (MINMAXINFO*)lParam.Value;
                    info->ptMinTrackSize.X = t_minTrackWidth;
                    info->ptMinTrackSize.Y = t_minTrackHeight;
                    return new LRESULT(0);

                default:
                    return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
            }
        }
        catch (Exception ex)
        {
            // Mandatory catch-all — an exception must never escape an [UnmanagedCallersOnly]
            // method across the native boundary. See docs/engineering/interop.md §3.3.
            Console.Error.WriteLine($"WndProc callback fault: {ex}");
            return new LRESULT(0);
        }
    }
}
