using System.Globalization;

namespace Bastion.TestWindows;

/// <summary>
/// Parameters for one spawned test window (DESIGN.md §11) — size/min-size, plus every
/// manageability-filter-relevant ex-style/owner combination (DESIGN.md §3.3;
/// <c>Bastion.Win32</c>'s <c>WindowManageabilityFilter</c>) a Tier 3 test needs to exercise the
/// filter's admit/reject branches.
/// </summary>
/// <param name="Width">Initial window width, in device units, passed to <c>CreateWindowExW</c>.</param>
/// <param name="Height">Initial window height, in device units, passed to <c>CreateWindowExW</c>.</param>
/// <param name="MinWidth">
/// Minimum tracking width this window reports in response to <c>WM_GETMINMAXINFO</c> — what a
/// constraint-cache test clamps a resize attempt against.
/// </param>
/// <param name="MinHeight">Minimum tracking height; see <paramref name="MinWidth"/>.</param>
/// <param name="Title">The window's title-bar text.</param>
/// <param name="ToolWindow">
/// Extended style includes <c>WS_EX_TOOLWINDOW</c> — DESIGN.md §3.3's filter rejects this unless
/// <see cref="AppWindow"/> is also set.
/// </param>
/// <param name="AppWindow">
/// Extended style includes <c>WS_EX_APPWINDOW</c> — the documented "unless" carve-out the filter
/// checks for both <see cref="ToolWindow"/> and a non-null <see cref="OwnerHwnd"/>. Defaults to
/// <see langword="true"/> (this spawner's original, pre-issue-#13 baseline always set it); pass
/// <c>--no-app-window</c> to unset it and reach the filter's rejection branches.
/// </param>
/// <param name="NoActivate">
/// Extended style includes <c>WS_EX_NOACTIVATE</c> — DESIGN.md §3.3's filter unconditionally
/// rejects this one; there is no <see cref="AppWindow"/> carve-out for it.
/// </param>
/// <param name="OwnerHwnd">
/// A real owner HWND value — as printed to stdout by a separately-spawned instance of this same
/// process — to pass as <c>CreateWindowExW</c>'s <c>hWndParent</c>, the documented mechanism that
/// makes <c>GetWindow(GW_OWNER)</c> read back a non-null value for a top-level (non-<c>WS_CHILD</c>)
/// window (see <see cref="TestWindowSpawner.Run"/>'s remarks). <see langword="null"/> (the default)
/// creates an unowned window.
/// </param>
internal sealed record TestWindowOptions(
    int Width,
    int Height,
    int MinWidth,
    int MinHeight,
    string Title,
    bool ToolWindow,
    bool AppWindow,
    bool NoActivate,
    nint? OwnerHwnd)
{
    public static TestWindowOptions Default { get; } = new(
        Width: 800,
        Height: 600,
        MinWidth: 200,
        MinHeight: 150,
        Title: "Bastion Test Window",
        ToolWindow: false,
        AppWindow: true,
        NoActivate: false,
        OwnerHwnd: null);

    /// <summary>
    /// Parses <c>--width</c>/<c>--height</c>/<c>--min-width</c>/<c>--min-height</c>/<c>--title</c>,
    /// plus <c>--tool-window</c>/<c>--no-app-window</c>/<c>--no-activate</c>/
    /// <c>--owner &lt;hwnd-value&gt;</c>, from raw args, falling back to <see cref="Default"/> for
    /// anything unspecified. Deliberately hand-rolled rather than a <c>System.CommandLine</c>
    /// dependency: this is a subprocess-only test tool with a handful of flat options, not a
    /// user-facing CLI.
    /// </summary>
    public static TestWindowOptions Parse(string[] args)
    {
        int width = Default.Width;
        int height = Default.Height;
        int minWidth = Default.MinWidth;
        int minHeight = Default.MinHeight;
        string title = Default.Title;
        bool toolWindow = Default.ToolWindow;
        bool appWindow = Default.AppWindow;
        bool noActivate = Default.NoActivate;
        nint? ownerHwnd = Default.OwnerHwnd;

        // i < args.Length (not args.Length - 1, the previous bound): that bound silently ignored a
        // presence-only flag (--tool-window et al.) if it were the very last argument, since it was
        // sized only for the value-taking flags this parser originally had exclusively. The
        // `when i + 1 < args.Length` guards below take over that bound's job for the value-taking
        // cases, so a trailing flag missing its value is silently skipped rather than throwing.
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--width" when i + 1 < args.Length:
                    width = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--height" when i + 1 < args.Length:
                    height = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--min-width" when i + 1 < args.Length:
                    minWidth = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--min-height" when i + 1 < args.Length:
                    minHeight = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--title" when i + 1 < args.Length:
                    title = args[++i];
                    break;
                case "--tool-window":
                    toolWindow = true;
                    break;
                case "--no-app-window":
                    appWindow = false;
                    break;
                case "--no-activate":
                    noActivate = true;
                    break;
                case "--owner" when i + 1 < args.Length:
                    ownerHwnd = nint.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
            }
        }

        return new TestWindowOptions(
            width, height, minWidth, minHeight, title, toolWindow, appWindow, noActivate, ownerHwnd);
    }
}
