using Bastion.Core;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Bastion.Win32;

/// <summary>
/// Reads the OS-defined minimum window tracking size (DESIGN.md §6's effective-min-size cache
/// seed: "seeded with <c>GetSystemMetrics(SM_CXMINTRACK/SM_CYMINTRACK)</c> floors") via the
/// DPI-aware <c>GetSystemMetricsForDpi</c>, never the plain, non-DPI-aware <c>GetSystemMetrics</c>
/// the acceptance criteria names literally -- see this type's remarks for why that substitution is
/// correct rather than a deviation.
/// </summary>
/// <remarks>
/// <para>
/// <b>DOCUMENTED CONTRACT (verified against
/// https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getsystemmetrics#remarks):</b>
/// "This API is not DPI aware, and should not be used if the calling thread is per-monitor DPI
/// aware. For the DPI-aware version of this API, see GetSystemMetricsForDPI." DESIGN.md §8 commits
/// Bastion to a PerMonitorV2 manifest, which makes every Bastion thread per-monitor DPI aware --
/// so this reader always calls
/// <see href="https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getsystemmetricsfordpi"><c>GetSystemMetricsForDpi(nIndex, dpi)</c></see>
/// instead of plain <c>GetSystemMetrics(nIndex)</c>. This is a straightforward substitution per
/// that same documented guidance (both share the identical <c>SM_*</c> index vocabulary; the
/// DPI-aware overload "returns the same result as GetSystemMetrics but scales it according to an
/// arbitrary DPI you provide"), not merely an available alternative -- so it is used unconditionally
/// here rather than falling back to the acceptance criteria's literal wording.
/// </para>
/// <para>
/// The <c>dpi</c> this reader supplies comes from
/// <see href="https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getdpiforsystem"><c>GetDpiForSystem()</c></see>
/// (documented: "Returns the system DPI... For any [DPI_AWARENESS] value [other than UNAWARE], the
/// return value will be the actual system DPI. You should not cache the system DPI, but should use
/// GetDpiForSystem whenever you need the system DPI value" -- honored here by calling it fresh on
/// every <see cref="ReadSeedFloor"/> invocation, never caching its result across calls), not a
/// per-monitor DPI. This seed is a single, repo-wide floor (DESIGN.md §6: "seeded with
/// GetSystemMetrics(...) floors, persisted per rule-key" -- one shared floor every rule key starts
/// from, not a per-monitor value), and no monitor topology service exists yet to supply a real
/// per-monitor DPI (GitHub issue #16) -- the system DPI is the least-arbitrary single value
/// available today for a seed that is immediately supersedable by real, per-window learned data
/// (this issue's own scope note). Revisit if a genuinely monitor-aware seed ever proves necessary
/// once issue #16 lands.
/// </para>
/// </remarks>
internal static class SystemMinTrackSizeReader
{
    /// <summary>
    /// Reads the current <c>SM_CXMINTRACK</c>/<c>SM_CYMINTRACK</c> floor, DPI-scaled for the
    /// current system DPI.
    /// </summary>
    public static LayoutConstraints ReadSeedFloor()
    {
        uint dpi = PInvoke.GetDpiForSystem();
        int width = PInvoke.GetSystemMetricsForDpi(SYSTEM_METRICS_INDEX.SM_CXMINTRACK, dpi);
        int height = PInvoke.GetSystemMetricsForDpi(SYSTEM_METRICS_INDEX.SM_CYMINTRACK, dpi);

        // DOCUMENTED CONTRACT: "If the function fails, the return value is zero." SM_CXMINTRACK/
        // SM_CYMINTRACK are never legitimately zero on a real Windows desktop -- a window's minimum
        // interactively-resizable size can't be zero pixels -- so a non-positive reading is treated
        // the same way PlacementSystemAdapter.ReadPrimaryWorkArea already treats its own
        // undocumented-failure case: fall back to an all-zero LayoutConstraints (i.e. no floor
        // enforced beyond zero) rather than fabricate a guessed nonzero value or throw.
        return width > 0 && height > 0 ? new LayoutConstraints(width, height) : default;
    }
}
