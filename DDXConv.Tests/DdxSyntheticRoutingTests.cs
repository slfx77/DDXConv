using DDXConv.Tests.Support;
using Xunit;

namespace DDXConv.Tests;

/// <summary>
///     End-to-end proof that the parser ROUTES layout-divergent surfaces to the aligned
///     decode: <c>DdxAlignedUnswizzleTests</c> pins the unswizzler itself against the XG
///     reference, but the historical bug was upstream — one-axis-sub-tile files never reached
///     the correct function (an AND-guard shape check diverted them into a hardcoded
///     192×256+64×256 "horizontal split" whose match condition was vacuously true, then a
///     "simple split" that sliced linear-size bytes disjoint from where level 0 actually
///     lives). These tests decode complete synthetic DDX files through the real parser.
/// </summary>
public class DdxSyntheticRoutingTests
{
    private static int BlockBytes(uint format)
    {
        return format is SyntheticDdx.Dxt1 or SyntheticDdx.Ati1 ? 8 : 16;
    }

    // ── Smaller-DDS emission for genuinely short mip 0 (user ruling) ─────────────────────────

    [Fact]
    public void ShortPayload_ExactLeadingRowBand_EmitsSmallerHeight()
    {
        // 64x256 DXT1: 16x64 logical blocks in a 32x64 tiled surface (2 macro-tile rows). A
        // payload holding exactly the first macro row covers logical rows 0..31 completely and
        // nothing else — the honest output is 64x128, not a half-black 64x256.
        var payload = SyntheticDdx.IndexStampedBlocks(32 * 32, 8); // first macro-tile row only
        var ddx = SyntheticDdx.Build3Xdo(64, 256, SyntheticDdx.Dxt1, payload);

        var dds = new DdxParser().ConvertDdxToDds(ddx, new ConversionOptions { SkipEndianSwap = true });

        Assert.NotNull(dds);
        Assert.Equal(128, BitConverter.ToInt32(dds, 12)); // height shrank to the complete band
        Assert.Equal(64, BitConverter.ToInt32(dds, 16));
        Assert.Equal(128 + 16 * 32 * 8, dds.Length);
    }

    [Fact]
    public void ShortPayload_ExactLeadingColumnBand_EmitsSmallerWidth()
    {
        // 512x64 DXT1: 128x16 logical blocks in a 128x32 tiled surface (4 macro tiles across
        // one macro row). Two macro tiles of payload cover logical columns 0..63 completely.
        var payload = SyntheticDdx.IndexStampedBlocks(2 * 32 * 32, 8);
        var ddx = SyntheticDdx.Build3Xdo(512, 64, SyntheticDdx.Dxt1, payload);

        var dds = new DdxParser().ConvertDdxToDds(ddx, new ConversionOptions { SkipEndianSwap = true });

        Assert.NotNull(dds);
        Assert.Equal(64, BitConverter.ToInt32(dds, 12));
        Assert.Equal(256, BitConverter.ToInt32(dds, 16)); // width shrank to the complete band
        Assert.Equal(128 + 64 * 16 * 8, dds.Length);
    }

    [Fact]
    public void ShortPayload_ScatteredCoverage_KeepsFullDimensionsZeroFilled()
    {
        // A payload cut mid-macro-tile leaves a Morton-interleaved hole pattern — no exact
        // row or column band exists, so shrinking would discard real data. Keep full dims.
        var payload = SyntheticDdx.IndexStampedBlocks(32 * 32 + 100, 8); // 100 blocks into tile 2
        var ddx = SyntheticDdx.Build3Xdo(64, 256, SyntheticDdx.Dxt1, payload);

        var dds = new DdxParser().ConvertDdxToDds(ddx, new ConversionOptions { SkipEndianSwap = true });

        Assert.NotNull(dds);
        Assert.Equal(256, BitConverter.ToInt32(dds, 12));
        Assert.Equal(64, BitConverter.ToInt32(dds, 16));
    }

    // ── Aligned-path routing ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(512, 8, SyntheticDdx.Dxt1)] // lasertripwire01_g shape (192x256 probe used to fire)
    [InlineData(128, 16, SyntheticDdx.Dxt1)] // fxlightrays shape (simple split used to fire)
    [InlineData(32, 128, SyntheticDdx.Dxt5)]
    [InlineData(512, 32, SyntheticDdx.Ati2)]
    [InlineData(16, 128, SyntheticDdx.Ati1)]
    [InlineData(16, 16, SyntheticDdx.Dxt1)] // both-axes sub-tile control (was already routed right)
    public void OneAxisAndSubTileSingleChunkFiles_DecodeViaTheAlignedPath(int width, int height, uint format)
    {
        var bs = BlockBytes(format);
        var blocksW = Math.Max(1, (width + 3) / 4);
        var blocksH = Math.Max(1, (height + 3) / 4);
        var tiledElements = ((blocksW + 31) & ~31) * ((blocksH + 31) & ~31);
        var payload = SyntheticDdx.IndexStampedBlocks(tiledElements, bs);
        var ddx = SyntheticDdx.Build3Xdo(width, height, format, payload);

        // SkipEndianSwap keeps the stamps byte-identical so provenance reads directly.
        var dds = new DdxParser().ConvertDdxToDds(ddx, new ConversionOptions { SkipEndianSwap = true });

        Assert.NotNull(dds);
        Assert.Equal(height, BitConverter.ToInt32(dds, 12));
        Assert.Equal(width, BitConverter.ToInt32(dds, 16));
        Assert.True(dds.Length >= 128 + blocksW * blocksH * bs, "DDS shorter than the logical mip 0");

        for (var by = 0; by < blocksH; by++)
        {
            for (var bx = 0; bx < blocksW; bx++)
            {
                var offset = 128 + (by * blocksW + bx) * bs;
                var stamp = dds[offset] | (dds[offset + 1] << 8);
                var expected = XgReferenceModel.Level0SourceElement(bx, by, width, height, bs);
                Assert.True(expected == stamp,
                    $"{width}x{height} fmt=0x{format:X}: block ({bx},{by}) came from element {stamp}, reference says {expected}");
            }
        }
    }
}
