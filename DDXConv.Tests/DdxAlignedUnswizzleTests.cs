using DDXConv.Tests.Support;
using Xunit;

namespace DDXConv.Tests;

/// <summary>
///     Pins the level-0 untile (ceil pitch, tile-aligned extent, XG mip-tail origin crop)
///     against <see cref="XgReferenceModel" /> — the independent transcription of the decompiled
///     Xenon XGraphics — with index-stamped source blocks, so every output block's provenance is
///     asserted exactly. Before these tests existed, reverting the round-1 decode fixes failed
///     zero tests.
/// </summary>
public class DdxAlignedUnswizzleTests
{
    private static int BlockBytes(uint format)
    {
        return format is SyntheticDdx.Dxt1 or SyntheticDdx.Ati1 ? 8 : 16;
    }

    /// <summary>
    ///     Builds a tiled surface buffer large enough for both the production layout and the
    ///     reference layout, with every element slot stamped with its own index.
    /// </summary>
    private static byte[] StampedTiledSurface(int width, int height, uint format)
    {
        var bs = BlockBytes(format);
        var blocksW = Math.Max(1, (width + 3) / 4);
        var blocksH = Math.Max(1, (height + 3) / 4);
        var portElements = ((blocksW + 31) & ~31) * ((blocksH + 31) & ~31);
        var (_, _, refPitch, refRows) = XgReferenceModel.Level0Surface(width, height, bs);
        var elements = Math.Max(portElements, refPitch * refRows);
        return SyntheticDdx.IndexStampedBlocks(elements, bs);
    }

    private static int StampAt(byte[] buffer, int blockIndex, int blockBytes)
    {
        return buffer[blockIndex * blockBytes] | (buffer[blockIndex * blockBytes + 1] << 8);
    }

    public static TheoryData<int, int, uint> Level0Shapes()
    {
        int[][] shapes =
        [
            [8, 8], [16, 16], [8, 16], [2, 32], [4, 4], // both axes sub-tile (tail-base 0)
            [512, 8], [128, 16], [16, 128], [8, 512], [512, 32], [32, 128], // one axis sub-tile
            [192, 192], [320, 64], // over-tile, not 32-block-aligned
            [24, 512], [512, 24], [20, 20] // npot: ceil-vs-floor tail-base window
        ];
        uint[] spanningFormats = [SyntheticDdx.Dxt3, SyntheticDdx.Ati1, SyntheticDdx.Ati2];
        int[][] spanningShapes = [[16, 16], [512, 8], [8, 512], [320, 64], [24, 512], [20, 20]];

        var data = new TheoryData<int, int, uint>();
        foreach (var s in shapes)
        {
            data.Add(s[0], s[1], SyntheticDdx.Dxt1);
            data.Add(s[0], s[1], SyntheticDdx.Dxt5);
        }

        foreach (var s in spanningShapes)
        {
            foreach (var f in spanningFormats)
            {
                data.Add(s[0], s[1], f);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Level0Shapes))]
    public void Level0Untile_ReadsExactlyTheReferenceSourceBlocks(int width, int height, uint format)
    {
        var bs = BlockBytes(format);
        var blocksW = Math.Max(1, (width + 3) / 4);
        var blocksH = Math.Max(1, (height + 3) / 4);
        var src = StampedTiledSurface(width, height, format);

        var result = TextureUtilities.UnswizzleMortonDxtAligned(src, width, height, format, swapEndian: false);

        Assert.True(result.Length >= blocksW * blocksH * bs,
            $"{width}x{height} fmt=0x{format:X}: output {result.Length} bytes < logical surface");
        for (var by = 0; by < blocksH; by++)
        {
            for (var bx = 0; bx < blocksW; bx++)
            {
                var expected = XgReferenceModel.Level0SourceElement(bx, by, width, height, bs);
                var actual = StampAt(result, by * blocksW + bx, bs);
                Assert.True(expected == actual,
                    $"{width}x{height} fmt=0x{format:X}: logical block ({bx},{by}) read element {actual}, reference says {expected}");
            }
        }
    }

    [Theory]
    [InlineData(192, 192, SyntheticDdx.Dxt1)]
    [InlineData(192, 192, SyntheticDdx.Dxt5)]
    [InlineData(320, 128, SyntheticDdx.Dxt1)]
    [InlineData(320, 128, SyntheticDdx.Dxt5)]
    [InlineData(136, 256, SyntheticDdx.Ati2)]
    [InlineData(264, 136, SyntheticDdx.Ati1)]
    public void PlainUntile_UsesCeilPitch_MatchingXgAddress2DTiledOffset(int width, int height, uint format)
    {
        // The plain (uncropped) path unswizzles at the logical block pitch; the reference rounds
        // the macro-tile pitch UP. The old floor-pitch code halved the row stride for any
        // non-32-multiple pitch, sending every block row past y=31 to the wrong tile.
        var bs = BlockBytes(format);
        var blocksW = width / 4;
        var blocksH = height / 4;

        var maxElement = 0;
        for (var y = 0; y < blocksH; y++)
        {
            for (var x = 0; x < blocksW; x++)
            {
                maxElement = Math.Max(maxElement, XgReferenceModel.TiledOffset(x, y, blocksW, bs));
            }
        }

        var src = SyntheticDdx.IndexStampedBlocks(maxElement + 1, bs);
        var result = TextureUtilities.UnswizzleMortonDxt(src, width, height, format, swapEndian: false);

        for (var y = 0; y < blocksH; y++)
        {
            for (var x = 0; x < blocksW; x++)
            {
                var expected = XgReferenceModel.TiledOffset(x, y, blocksW, bs);
                var actual = StampAt(result, y * blocksW + x, bs);
                Assert.True(expected == actual,
                    $"{width}x{height} fmt=0x{format:X}: block ({x},{y}) read element {actual}, reference says {expected}");
            }
        }
    }

    [Theory]
    [InlineData(16, 16)]
    [InlineData(32, 16)]
    [InlineData(16, 32)]
    [InlineData(32, 8)]
    [InlineData(8, 32)]
    [InlineData(4, 4)]
    [InlineData(256, 16)]
    [InlineData(16, 256)]
    public void TailLevelOffsets_MatchDecompiledCoords(int tailWidth, int tailHeight)
    {
        for (var level = 0; level <= 4; level++)
        {
            var expected = XgReferenceModel.TailLevelOffsetBlocks(level, tailWidth, tailHeight);
            var actual = TextureUtilities.ComputeTailLevelOffset(level, tailWidth, tailHeight);
            Assert.True(expected == actual,
                $"tail {tailWidth}x{tailHeight} level {level}: got {actual}, reference says {expected}");
        }
    }

    [Theory]
    [InlineData(16, 512)]
    [InlineData(512, 16)]
    [InlineData(17, 512)] // npot window: ceil says base 1, floor said base 0
    [InlineData(20, 20)]
    [InlineData(24, 512)]
    [InlineData(512, 24)]
    [InlineData(31, 64)]
    [InlineData(32, 512)]
    [InlineData(33, 64)]
    [InlineData(64, 64)]
    [InlineData(2, 32)]
    [InlineData(1, 1)]
    public void TailBaseLevel_UsesCeilLog2_PerXgGetMipTailBaseLevel(int width, int height)
    {
        Assert.Equal(XgReferenceModel.MipTailBaseLevel(width, height),
            TextureUtilities.ComputeXgTailBaseLevel(width, height));
    }

    [Fact]
    public void ReferenceSpellings_AgreeWithEachOther()
    {
        // Two independent transcriptions of the same decompiled function; a transcription slip
        // in one is caught by the other.
        foreach (var bs in new[] { 8, 16 })
        {
            foreach (var pitch in new[] { 1, 2, 7, 12, 31, 32, 33, 48, 96, 128 })
            {
                for (var y = 0; y < 96; y++)
                {
                    for (var x = 0; x < Math.Min(pitch, 96); x++)
                    {
                        Assert.Equal(
                            XgReferenceModel.TiledOffset(x, y, pitch, bs),
                            XgReferenceModel.TiledOffsetExtentsForm(x, y, pitch, bs));
                    }
                }
            }
        }
    }
}
