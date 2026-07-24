using System.Runtime.InteropServices;

namespace Bastion.Core;

/// <summary>
/// Minimum-size floor a layout must respect when placing a window.
/// </summary>
/// <remarks>
/// <para>
/// The real, per-window constraint cache landed in GitHub issue #6 (<see cref="EffectiveMinSizeCache"/>,
/// keyed by <see cref="RuleKey"/>, seeded from <c>GetSystemMetricsForDpi(SM_CXMINTRACK/SM_CYMINTRACK)</c>
/// — <c>Bastion.Win32.SystemMinTrackSizeReader</c>) — but <c>Bastion.Layout.SplitTreeLayout.Solve</c>/
/// <c>Bastion.Layout.DwindleLayoutEngine.Solve</c> still take exactly one flat instance of this type,
/// by design: the aggregate-correct, per-window handling that reconciles the cache's per-rule-key
/// minimums against already-solved placements runs entirely outside the solver, as a standalone
/// post-processing stage (<c>Bastion.Layout.MinSizeConflictLadder</c>), specifically to preserve
/// <c>SplitTree</c>'s subtree-locality guarantee — see that type's own remarks for the full
/// rationale. This record therefore remains exactly what every layer needs: the single flat floor
/// every <see cref="ILayoutEngine"/> call site still takes, <see cref="EffectiveMinSizeCache.SystemFloor"/>'s
/// own type, and the ladder's per-window override value type, all at once — not a placeholder
/// awaiting replacement.
/// </para>
/// <para>
/// Originally a <c>Bastion.Layout</c>-only type; relocated here alongside
/// <see cref="Bastion.Core.ILayoutEngine"/> — see that type's remarks for why.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Auto)]
public readonly record struct LayoutConstraints(double MinWidth, double MinHeight);
