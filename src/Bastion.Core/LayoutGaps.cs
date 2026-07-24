using System.Runtime.InteropServices;

namespace Bastion.Core;

/// <summary>
/// Gap sizes applied by every <see cref="ILayoutEngine"/> implementation. Originally a
/// <c>Bastion.Layout</c>-only type; relocated here alongside <see cref="ILayoutEngine"/> — see
/// that type's remarks for why.
/// </summary>
/// <param name="Outer">Gap between the work area's edge and the outermost windows.</param>
/// <param name="Inner">Gap between two adjacent windows across a split.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct LayoutGaps(double Outer, double Inner);
