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

    // === ATI2/BC5 tests (uses 8-byte tiling despite 16-byte blocks) ===

    [Fact]
    public void PcBlockIndex_ATI2_IsBijective_16x16()
    {
        const int blocksX = 16;
        const int blocksY = 16;
        const int blockSize = 16;
        const uint ati2Format = 0x71;

        var seen = new HashSet<int>();
        for (var y = 0; y < blocksY; y++)
        {
            for (var x = 0; x < blocksX; x++)
            {
                var pcIndex = TextureUtilities.GetPcBlockIndex(x, y, blocksX, blockSize, ati2Format);
                Assert.True(pcIndex >= 0 && pcIndex < blocksX * blocksY,
                    $"PC index {pcIndex} out of range from Xbox ({x},{y})");
                Assert.True(seen.Add(pcIndex),
                    $"Duplicate PC index {pcIndex} from Xbox ({x},{y})");
            }
        }

        Assert.Equal(blocksX * blocksY, seen.Count);
    }

    [Fact]
    public void PcBlockIndex_ATI2_Uses8x2GroupMapping()
    {
        // ATI2/BC5 (16-byte blocks) should use the 8×2 macro-block tiling pattern
        // (same as DXT1) because it's two independent BC4 sub-blocks
        const int blocksX = 16;
        const int blockSize = 16;
        const uint ati2Format = 0x71;

        // Same mapping as DXT1 8×2 pattern
        Assert.Equal(0 * blocksX + 0, TextureUtilities.GetPcBlockIndex(0, 0, blocksX, blockSize, ati2Format));
        Assert.Equal(0 * blocksX + 1, TextureUtilities.GetPcBlockIndex(1, 0, blocksX, blockSize, ati2Format));
        Assert.Equal(1 * blocksX + 0, TextureUtilities.GetPcBlockIndex(2, 0, blocksX, blockSize, ati2Format));
        Assert.Equal(1 * blocksX + 1, TextureUtilities.GetPcBlockIndex(3, 0, blocksX, blockSize, ati2Format));
        Assert.Equal(0 * blocksX + 4, TextureUtilities.GetPcBlockIndex(0, 1, blocksX, blockSize, ati2Format));
    }

    // === Non-square 3XDR tests ===
    // On-disk FNV 3XDR textures are exclusively square (confirmed by scanning ~5k 3XDR files across
    // the 360-final/proto/July builds plus dump-carved 3XDR — 0 non-square), so the golden-decode
    // harness cannot exercise the non-square 3XDR untile path with real data. These unit tests guard
    // that path directly: the macro-block bit-permutation must stay bijective on non-square grids, and
    // the full untile loop (incl. the endian swap that Stage 4 vectorizes) must place every block.

    [Theory]
    [InlineData(16, 8, 8, 0u)]      // DXT1 (8-byte tile unit), 2:1 wide
    [InlineData(8, 16, 8, 0u)]      // DXT1, 1:2 tall
    [InlineData(16, 8, 16, 0u)]     // DXT5 (16-byte tile unit), 2:1 wide
    [InlineData(8, 16, 16, 0u)]     // DXT5, 1:2 tall
    [InlineData(16, 8, 16, 0x71u)]  // ATI2/BC5 (8-byte effective tiling), 2:1 wide
    [InlineData(8, 16, 16, 0x71u)]  // ATI2/BC5, 1:2 tall
    public void PcBlockIndex_IsBijective_NonSquare(int blocksX, int blocksY, int blockSize, uint format)
    {
        var seen = new HashSet<int>();
        for (var y = 0; y < blocksY; y++)
        {
            for (var x = 0; x < blocksX; x++)
            {
                var pc = TextureUtilities.GetPcBlockIndex(x, y, blocksX, blockSize, format);
                Assert.True(pc >= 0 && pc < blocksX * blocksY,
                    $"PC index {pc} out of range from Xbox ({x},{y}) on {blocksX}x{blocksY}");
                Assert.True(seen.Add(pc),
                    $"Duplicate PC index {pc} from Xbox ({x},{y}) on {blocksX}x{blocksY}");
            }
        }

        Assert.Equal(blocksX * blocksY, seen.Count);
    }

    [Fact]
    public void UntileMacroBlocks_NonSquare_DXT5_PlacesEveryBlockWithSwap()
    {
        // 64x32 px DXT5 → 16x8 blocks (a 2:1 non-square grid that uses GetPcBlockIndex, not the
        // ATI2 sub-tile path). Stamp each source block's index into its bytes, untile with endian
        // swap, and assert every block landed (byte-swapped) at its mapped PC slot.
        const int width = 64;
        const int height = 32;
        const int blockSize = 16;
        var blocksX = (width + 3) / 4;
        var blocksY = (height + 3) / 4;
        var src = new byte[blocksX * blocksY * blockSize];
        for (var i = 0; i < blocksX * blocksY; i++)
        {
            for (var b = 0; b < blockSize; b += 2)
            {
                src[i * blockSize + b] = (byte)(i >> 8);
                src[i * blockSize + b + 1] = (byte)i;
            }
        }

        var dst = TextureUtilities.UntileMacroBlocks(src, width, height, blockSize, swapEndian: true);

        var placed = new HashSet<int>();
        for (var i = 0; i < blocksX * blocksY; i++)
        {
            var pc = TextureUtilities.GetPcBlockIndex(i % blocksX, i / blocksX, blocksX, blockSize);
            Assert.True(placed.Add(pc), $"Block {i} collided at PC slot {pc}");
            // swapEndian reverses each 16-bit word, so the stamped (hi,lo) bytes come back (lo,hi).
            Assert.Equal((byte)i, dst[pc * blockSize]);
            Assert.Equal((byte)(i >> 8), dst[pc * blockSize + 1]);
        }

        Assert.Equal(blocksX * blocksY, placed.Count);
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
