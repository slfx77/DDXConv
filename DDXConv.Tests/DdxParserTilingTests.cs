using System.Reflection;
using Xunit;

namespace DDXConv.Tests;

public class DdxParserTilingTests
{
    private static readonly MethodInfo _getPcBlockIndex =
        typeof(TextureUtilities).GetMethod("GetPcBlockIndex", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("GetPcBlockIndex not found in TextureUtilities.");

    [Fact]
    public void PcBlockIndex_MatchesAlgebraicFormula()
    {
        const int blocksX = 16;
        const int groupX = 1;
        const int groupY = 1;

        for (var localY = 0; localY < 2; localY++)
        {
            for (var localX = 0; localX < 8; localX++)
            {
                var xboxX = groupX * 8 + localX;
                var xboxY = groupY * 2 + localY;

                var pcLocalX = localY * 4 + (localX / 4) * 2 + (localX % 2);
                var pcLocalY = (localX / 2) % 2;
                var pcX = groupX * 8 + pcLocalX;
                var pcY = groupY * 2 + pcLocalY;
                var expected = pcY * blocksX + pcX;

                var actual = InvokeGetPcBlockIndex(xboxX, xboxY, blocksX);
                Assert.Equal(expected, actual);
            }
        }
    }

    [Fact]
    public void PcBlockIndex_IsBijective()
    {
        const int blocksX = 16;
        const int blocksY = 4;

        // Verify that no two Xbox positions map to the same PC position (bijective mapping)
        var seen = new HashSet<int>();
        for (var xboxY = 0; xboxY < blocksY; xboxY++)
        {
            for (var xboxX = 0; xboxX < blocksX; xboxX++)
            {
                var pcIndex = InvokeGetPcBlockIndex(xboxX, xboxY, blocksX);
                Assert.True(seen.Add(pcIndex),
                    $"Duplicate PC index {pcIndex} from Xbox ({xboxX},{xboxY})");
            }
        }

        Assert.Equal(blocksX * blocksY, seen.Count);
    }

    private static int InvokeGetPcBlockIndex(int xboxX, int xboxY, int blocksX)
    {
        return (int)_getPcBlockIndex.Invoke(null, [xboxX, xboxY, blocksX])!;
    }
}
