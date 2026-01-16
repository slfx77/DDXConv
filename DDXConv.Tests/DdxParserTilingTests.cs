using System.Reflection;
using Xunit;

namespace DDXConv.Tests;

public class DdxParserTilingTests
{
    private static readonly MethodInfo _getPcBlockIndex =
        typeof(DdxParser).GetMethod("GetPcBlockIndex", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("GetPcBlockIndex not found.");

    private static readonly MethodInfo _getXboxTiledBlockIndex =
        typeof(DdxParser).GetMethod("GetXboxTiledBlockIndex", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("GetXboxTiledBlockIndex not found.");

    [Fact]
    public void PcBlockIndex_MatchesAlgebraicFormula()
    {
        const int blocksX = 16;
        const int blocksY = 4;
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

                var actual = InvokeGetPcBlockIndex(xboxX, xboxY, blocksX, blocksY);
                Assert.Equal(expected, actual);
            }
        }
    }

    [Fact]
    public void XboxTiledBlockIndex_IsInverseOfPcBlockIndex()
    {
        const int blocksX = 16;
        const int blocksY = 4;

        for (var xboxY = 0; xboxY < blocksY; xboxY++)
        {
            for (var xboxX = 0; xboxX < blocksX; xboxX++)
            {
                var pcIndex = InvokeGetPcBlockIndex(xboxX, xboxY, blocksX, blocksY);
                var pcX = pcIndex % blocksX;
                var pcY = pcIndex / blocksX;

                var xboxIndex = InvokeGetXboxTiledBlockIndex(pcX, pcY, blocksX, blocksY);
                var expectedIndex = xboxY * blocksX + xboxX;

                Assert.Equal(expectedIndex, xboxIndex);
            }
        }
    }

    private static int InvokeGetPcBlockIndex(int xboxX, int xboxY, int blocksX, int blocksY)
    {
        return (int)_getPcBlockIndex.Invoke(null, [xboxX, xboxY, blocksX, blocksY])!;
    }

    private static int InvokeGetXboxTiledBlockIndex(int pcX, int pcY, int blocksX, int blocksY)
    {
        return (int)_getXboxTiledBlockIndex.Invoke(null, [pcX, pcY, blocksX, blocksY])!;
    }
}
