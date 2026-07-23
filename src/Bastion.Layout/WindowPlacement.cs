using System.Runtime.InteropServices;
using Bastion.Core;

namespace Bastion.Layout;

/// <summary>One window's solved, gap-adjusted bounds within the work area.</summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct WindowPlacement(WindowId WindowId, Rect Bounds);
