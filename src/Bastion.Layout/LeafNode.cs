using System.Collections.Immutable;
using Bastion.Core;

namespace Bastion.Layout;

/// <summary>
/// A leaf: an ordered stack of one or more windows sharing a single tile (DESIGN.md §6 — stacked
/// windows are a first-class container, surfaced via the bar, since Bastion cannot draw tabs
/// inside foreign windows). Invariant: never empty — an emptied leaf collapses its parent instead
/// (see <see cref="SplitTree.Remove"/>).
/// </summary>
/// <remarks>
/// <see cref="ImmutableArray{T}"/>'s default equality is reference-based, not element-wise — two
/// structurally identical <see cref="LeafNode"/>s built independently will not compare equal via
/// record equality. Nothing in this codebase relies on <see cref="SplitTreeNode"/> equality for
/// anything semantically meaningful (tests compare solved <see cref="Rect"/>s, never tree nodes)
/// — keep it that way rather than leaning on this type's default equality.
/// </remarks>
public sealed record LeafNode(ImmutableArray<WindowId> Windows) : SplitTreeNode;
