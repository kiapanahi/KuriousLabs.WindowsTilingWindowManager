using Bastion.Core;
using FsCheck;
using FsCheck.Fluent;

namespace Bastion.Layout.Tests;

/// <summary>
/// Custom FsCheck generator for <see cref="SplitTree"/>, registered on <see cref="SplitTreeTests"/>
/// via <c>[Properties(Arbitrary = [typeof(SplitTreeGenerators)])]</c>.
/// </summary>
/// <remarks>
/// Builds trees only by folding a random sequence of <see cref="SplitTree.InsertFirst"/>/
/// <see cref="SplitTree.Insert"/> calls from <see cref="SplitTree.Empty"/> — never by trying to
/// directly generate an arbitrary (and possibly invalid) tree shape — so every generated tree is
/// valid by construction. Window count is capped at 6, matching
/// <c>DwindleLayoutEngineTests</c>'s own hand-verified bound for the same reason: it keeps the
/// worst-case chain depth (5) small enough that the generous-but-finite work-area bounds used by
/// <see cref="SplitTreeTests.MinSizeRespected"/> are provably sufficient rather than merely
/// probably sufficient.
/// </remarks>
internal static class SplitTreeGenerators
{
    private const int MaxWindows = 6;

    public static Arbitrary<SplitTree> SplitTrees() => Arb.From(GenSplitTree());

    private static Gen<SplitTree> GenSplitTree() =>
        from count in Gen.Choose(1, MaxWindows)
        from order in Gen.Shuffle(BuildWindowIds(count))
        from anchorSeeds in Gen.ArrayOf(Gen.Choose(0, 1_000_000), Math.Max(count - 1, 0))
        from orientationSeeds in Gen.ArrayOf(Gen.Choose(0, 1_000_000), Math.Max(count - 1, 0))
        from ratioSeeds in Gen.ArrayOf(Gen.Choose(0, 1_000_000), Math.Max(count - 1, 0))
        select BuildTree(order, anchorSeeds, orientationSeeds, ratioSeeds);

    private static WindowId[] BuildWindowIds(int count)
    {
        var ids = new WindowId[count];
        for (int i = 0; i < count; i++)
        {
            ids[i] = WindowId.FromOpaqueValue((ulong)i);
        }

        return ids;
    }

    private static SplitTree BuildTree(WindowId[] order, int[] anchorSeeds, int[] orientationSeeds, int[] ratioSeeds)
    {
        SplitTree tree = SplitTree.Empty.InsertFirst(order[0]);

        for (int i = 1; i < order.Length; i++)
        {
            WindowId anchor = order[anchorSeeds[i - 1] % i];
            SplitOrientation orientation = orientationSeeds[i - 1] % 2 == 0 ? SplitOrientation.Horizontal : SplitOrientation.Vertical;
            double ratio = 0.3 + ((ratioSeeds[i - 1] % 41) * 0.01);
            tree = tree.Insert(anchor, order[i], orientation, ratio);
        }

        return tree;
    }
}
