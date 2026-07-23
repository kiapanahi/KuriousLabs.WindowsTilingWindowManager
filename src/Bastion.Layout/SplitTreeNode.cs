namespace Bastion.Layout;

/// <summary>
/// A node in a <see cref="SplitTree"/>: either a <see cref="SplitNode"/> (internal) or a
/// <see cref="LeafNode"/> (a container). DESIGN.md §6.
/// </summary>
public abstract record SplitTreeNode;
