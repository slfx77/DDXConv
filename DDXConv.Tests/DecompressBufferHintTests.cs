using Xunit;

namespace DDXConv.Tests;

/// <summary>
///     Pins the decompression-buffer sizing math. The buffer is a hard ceiling — the LZX
///     decompressor silently stops writing when it fills — so an undersized hint IS data loss:
///     a 64×64 ATI1 single-chunk file needs 8192 (tiled mip 0) + 16384 (sequential mips) and the
///     old mip0-only hint collapsed its 7-mip chain to 1.
/// </summary>
public class DecompressBufferHintTests
{
    [Theory]
    [InlineData(64, 64, 0x7Bu, 8192, 16384)] // ATI1 — the acunit_s shape from the review
    [InlineData(128, 128, 0x52u, 8192, 24576)] // DXT1 — the two-chunk eye-texture shape
    [InlineData(512, 8, 0x52u, 32768, 16384)] // one-axis sub-tile; tail = full 64-block-wide extent, not one 32x32 tile
    [InlineData(256, 32, 0x54u, 32768, 16384)] // DXT5 one-axis
    [InlineData(16, 16, 0x52u, 8192, 8192)] // tail-base-0 (chain shares mip0's tile; headroom ok)
    [InlineData(512, 512, 0x54u, 262144, 131072)] // large aligned DXT5
    public void TiledMip0AndSequentialChain_PinnedSizes(
        int width, int height, uint format, int expectedTiledMip0, int expectedSequentialMips)
    {
        Assert.Equal(expectedTiledMip0, TextureUtilities.CalculateTiledMipSize(width, height, format));
        Assert.Equal(expectedSequentialMips,
            TextureUtilities.ComputeSequentialTiledMipTotal(width, height, format));
    }

    [Fact]
    public void NonBlockFormats_UseLinearSizes_NotBogusTiledOnes()
    {
        // 0x06 A8R8G8B8: GetBlockSize's default-16 branch used to turn the "tiled" size into
        // ceil128(w)*ceil128(h) garbage. The honest answer for a non-block format is linear.
        Assert.Equal(100 * 100 * 4, TextureUtilities.CalculateTiledMipSize(100, 100, 0x06));
        Assert.Equal(16 * 16 * 2, TextureUtilities.CalculateTiledMipSize(16, 16, 0x04));

        // Sequential chain for 100x100 A8R8G8B8: 50²+25²+12²+6²+3²+1² pixels × 4 bytes.
        Assert.Equal((2500 + 625 + 144 + 36 + 9 + 1) * 4,
            TextureUtilities.ComputeSequentialTiledMipTotal(100, 100, 0x06));
    }
}
