namespace Bastion.Layout;

/// <summary>An internal split: two children sharing the parent rect at <see cref="Ratio"/>.</summary>
/// <param name="Orientation">Which axis the split divides.</param>
/// <param name="Ratio">The fraction of the parent's split axis <see cref="First"/> receives, in (0, 1).</param>
/// <param name="First">The child receiving <paramref name="Ratio"/> of the split axis.</param>
/// <param name="Second">The child receiving the remainder.</param>
public sealed record SplitNode(SplitOrientation Orientation, double Ratio, SplitTreeNode First, SplitTreeNode Second) : SplitTreeNode;
