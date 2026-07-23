namespace Bastion.Win32;

/// <summary>
/// The window-class-name blocklist consulted by <see cref="WindowManageabilityFilter"/>
/// (DESIGN.md §3.3: "The class name blocklist (<c>Progman</c>/<c>WorkerW</c>/<c>Shell_TrayWnd</c>
/// etc.) is user-editable config, not code — those names are shell implementation details.").
/// </summary>
/// <remarks>
/// Deliberately a plain injectable value, not a compiled-in <see langword="const"/> list: full
/// JSONC config-file loading is GitHub issue #9's job, out of scope here. This type is the seam
/// issue #9's loader will populate from the user's config file; until then, callers construct one
/// from <see cref="Default"/> or a caller-supplied set. Class-name comparison is ordinal — shell
/// window classes are not localized, and this repo's convention (docs/engineering/quality-gates.md
/// §7's <c>InvariantGlobalization</c> discussion) is ordinal matching for window class/title text.
/// </remarks>
internal sealed class WindowClassBlocklist
{
    private readonly IReadOnlySet<string> _classNames;

    public WindowClassBlocklist(IReadOnlySet<string> classNames) => _classNames = classNames;

    /// <summary>
    /// The shell-chrome class names DESIGN.md §3.3 names explicitly, plus the handful of other
    /// well-known desktop/taskbar shell windows every mature WM (komorebi, GlazeWM, Whim) also
    /// excludes. A sensible default for callers that have not yet loaded a user config
    /// (GitHub issue #9) — not a claim that this list is exhaustive or immutable.
    /// </summary>
    public static WindowClassBlocklist Default { get; } = new(new HashSet<string>(StringComparer.Ordinal)
    {
        "Progman", // desktop
        "WorkerW", // per-monitor desktop worker windows
        "Shell_TrayWnd", // taskbar
        "Shell_SecondaryTrayWnd", // taskbar on a secondary monitor
        "Windows.UI.Core.CoreWindow", // shell-owned UWP host chrome (e.g. Start, Search, Action Center)
        "Xaml_WindowedPopupClass", // shell flyouts (volume/network/etc.)
    });

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="className"/> matches an entry in this
    /// blocklist by ordinal (case-sensitive) comparison.
    /// </summary>
    public bool Contains(string className) => _classNames.Contains(className);
}
