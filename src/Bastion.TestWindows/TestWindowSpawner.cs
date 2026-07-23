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
/// parameterized initial size and minimum-track size, prints its HWND to stdout so a driving test
/// harness can attach to it, then pumps messages until the window is destroyed.
/// </summary>
/// <remarks>
/// TODO(DESIGN.md §11): this is the Tier 3 spawner skeleton only — parameterized styles beyond
/// size/min-size (owned/child windows, layered/tool-window ex-styles, etc.) and the harness-side
/// driver that asserts via <c>DWMWA_EXTENDED_FRAME_BOUNDS</c> readbacks are not implemented yet.
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

            if (PInvoke.RegisterClassEx(in windowClass) == 0)
            {
                Console.Error.WriteLine("RegisterClassEx failed.");
                return 1;
            }
        }

        HWND hwnd = PInvoke.CreateWindowEx(
            WINDOW_EX_STYLE.WS_EX_APPWINDOW,
            ClassName,
            options.Title,
            WINDOW_STYLE.WS_OVERLAPPEDWINDOW,
            PInvoke.CW_USEDEFAULT,
            PInvoke.CW_USEDEFAULT,
            options.Width,
            options.Height,
            HWND.Null,
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

        while (PInvoke.GetMessage(out MSG message, HWND.Null, 0, 0))
        {
            PInvoke.TranslateMessage(in message);
            PInvoke.DispatchMessage(in message);
        }

        return 0;
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
