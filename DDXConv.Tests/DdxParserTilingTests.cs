using Xunit;

namespace DDXConv.Tests;

public class DdxParserTilingTests
{
    // === DXT5/16B block tests (bbnumber textures) ===

    [Fact]
    public void PcBlockIndex_DXT5_IsBijective_16x16()
    {
        const int blocksX = 16;
        const int blocksY = 16;
        const int blockSize = 16; // DXT5

        var seen = new HashSet<int>();
        for (var y = 0; y < blocksY; y++)
        {
            for (var x = 0; x < blocksX; x++)
            {
                var pcIndex = TextureUtilities.GetPcBlockIndex(x, y, blocksX, blockSize);
                Assert.True(pcIndex >= 0 && pcIndex < blocksX * blocksY,
                    $"PC index {pcIndex} out of range from Xbox ({x},{y})");
                Assert.True(seen.Add(pcIndex),
                    $"Duplicate PC index {pcIndex} from Xbox ({x},{y})");
            }
        }

        Assert.Equal(blocksX * blocksY, seen.Count);
    }

    [Theory]
    // Empirically verified mappings from Xbox 360 bbnumber0.ddx (DXT5) vs PC reference DDS
    [InlineData(4, 1, 8, 2)]
    [InlineData(5, 1, 8, 3)]
    [InlineData(6, 1, 9, 2)]
    [InlineData(10, 1, 5, 2)]
    [InlineData(11, 1, 5, 3)]
    [InlineData(5, 3, 10, 3)]
    [InlineData(8, 3, 6, 2)]
    [InlineData(10, 3, 7, 2)]
    [InlineData(11, 3, 7, 3)]
    [InlineData(8, 4, 4, 4)]
    [InlineData(9, 4, 4, 5)]
    [InlineData(6, 6, 11, 4)]
    [InlineData(7, 6, 11, 5)]
    [InlineData(8, 6, 6, 4)]
    [InlineData(9, 6, 6, 5)]
    [InlineData(4, 12, 8, 12)]
    [InlineData(5, 12, 8, 13)]
    [InlineData(10, 12, 5, 12)]
    [InlineData(11, 12, 5, 13)]
    public void PcBlockIndex_DXT5_MatchesEmpiricalMapping(int xboxX, int xboxY, int expectedPcX, int expectedPcY)
    {
        const int blocksX = 16;
        const int blockSize = 16; // DXT5
        var pcIdx = TextureUtilities.GetPcBlockIndex(xboxX, xboxY, blocksX, blockSize);
        var actualPcX = pcIdx % blocksX;
        var actualPcY = pcIdx / blocksX;
        Assert.Equal(expectedPcX, actualPcX);
        Assert.Equal(expectedPcY, actualPcY);
    }

    // === DXT1/8B block tests ===

    [Fact]
    public void PcBlockIndex_DXT1_IsBijective_16x16()
    {
        const int blocksX = 16;
        const int blocksY = 16;
        const int blockSize = 8; // DXT1

        var seen = new HashSet<int>();
        for (var y = 0; y < blocksY; y++)
        {
            for (var x = 0; x < blocksX; x++)
            {
                var pcIndex = TextureUtilities.GetPcBlockIndex(x, y, blocksX, blockSize);
                Assert.True(pcIndex >= 0 && pcIndex < blocksX * blocksY,
                    $"PC index {pcIndex} out of range from Xbox ({x},{y})");
                Assert.True(seen.Add(pcIndex),
                    $"Duplicate PC index {pcIndex} from Xbox ({x},{y})");
            }
        }

        Assert.Equal(blocksX * blocksY, seen.Count);
    }

    [Fact]
    public void PcBlockIndex_DXT1_8x2GroupMapping()
    {
        // Verify the 8×2 macro-block pattern for DXT1
        const int blocksX = 16;
        const int blockSize = 8;

        // Xbox row 0: 0,1,2,3,4,5,6,7 → PC: (0,0)(1,0)(0,1)(1,1)(2,0)(3,0)(2,1)(3,1)
        Assert.Equal(0 * blocksX + 0, TextureUtilities.GetPcBlockIndex(0, 0, blocksX, blockSize)); // (0,0)
        Assert.Equal(0 * blocksX + 1, TextureUtilities.GetPcBlockIndex(1, 0, blocksX, blockSize)); // (1,0)
        Assert.Equal(1 * blocksX + 0, TextureUtilities.GetPcBlockIndex(2, 0, blocksX, blockSize)); // (0,1)
        Assert.Equal(1 * blocksX + 1, TextureUtilities.GetPcBlockIndex(3, 0, blocksX, blockSize)); // (1,1)
        Assert.Equal(0 * blocksX + 2, TextureUtilities.GetPcBlockIndex(4, 0, blocksX, blockSize)); // (2,0)
        Assert.Equal(0 * blocksX + 3, TextureUtilities.GetPcBlockIndex(5, 0, blocksX, blockSize)); // (3,0)
        Assert.Equal(1 * blocksX + 2, TextureUtilities.GetPcBlockIndex(6, 0, blocksX, blockSize)); // (2,1)
        Assert.Equal(1 * blocksX + 3, TextureUtilities.GetPcBlockIndex(7, 0, blocksX, blockSize)); // (3,1)

        // Xbox row 1: maps to PC X 4-7
        Assert.Equal(0 * blocksX + 4, TextureUtilities.GetPcBlockIndex(0, 1, blocksX, blockSize)); // (4,0)
        Assert.Equal(0 * blocksX + 5, TextureUtilities.GetPcBlockIndex(1, 1, blocksX, blockSize)); // (5,0)
    }

    // === Common tests ===

    [Fact]
    public void PcBlockIndex_IdentityAtOrigin()
    {
        // Block (0,0) should always map to (0,0) for both block sizes
        Assert.Equal(0, TextureUtilities.GetPcBlockIndex(0, 0, 16, 16));
        Assert.Equal(0, TextureUtilities.GetPcBlockIndex(0, 0, 16, 8));
    }
}
