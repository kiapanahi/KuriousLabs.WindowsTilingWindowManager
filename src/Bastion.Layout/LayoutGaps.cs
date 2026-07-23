using System.Runtime.InteropServices;

namespace Bastion.Layout;

/// <summary>Gap sizes applied by every <see cref="ILayoutEngine"/> implementation.</summary>
/// <param name="Outer">Gap between the work area's edge and the outermost windows.</param>
/// <param name="Inner">Gap between two adjacent windows across a split.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct LayoutGaps(double Outer, double Inner);
