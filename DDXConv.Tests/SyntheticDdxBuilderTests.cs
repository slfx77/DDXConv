using DDXConv.Compression;
using DDXConv.Tests.Support;
using Xunit;

namespace DDXConv.Tests;

/// <summary>
///     Proves the synthetic DDX builder's LZX framing against the real decompressor before any
///     other test relies on it. A builder bug must fail here, not silently certify a decode test.
/// </summary>
public class SyntheticDdxBuilderTests
{
    private static byte[] DecompressWithRealLzx(byte[] compressed, int outputCapacity, out int consumed)
    {
        var output = new byte[outputCapacity];
        using var lzx = new LzxDecompressor();
        var inCount = compressed.Length;
        var outCount = output.Length;
        var result = lzx.Decompress(compressed, 0, ref inCount, output, 0, ref outCount);
        Assert.Equal(0, result);
        consumed = inCount;
        return output[..outCount];
    }

    [Theory]
    [InlineData(64)] // small
    [InlineData(2048)] // one linear DXT1 mip0 for 512x8
    [InlineData(0x8000)] // exact chunk-granularity fit
    public void LzxChunk_RoundTrips_WithExactOutputBuffer(int payloadLength)
    {
        var payload = SyntheticDdx.IndexStampedBlocks(payloadLength / 16, 16);
        Assert.Equal(payloadLength, payload.Length);

        using var ms = new MemoryStream();
        SyntheticDdx.WriteLzxUncompressedChunk(ms, payload);
        var compressed = ms.ToArray();

        var decompressed = DecompressWithRealLzx(compressed, payload.Length, out var consumed);

        Assert.Equal(payload, decompressed);
        Assert.Equal(compressed.Length, consumed);
    }

    [Fact]
    public void LzxChunk_RoundTrips_WithOversizedOutputBuffer()
    {
        // The parser allocates 2x its hint, so the decoder keeps looking for another block after
        // ours completes; the chunk's terminator pad must stop it deterministically at exactly
        // the payload length.
        var payload = SyntheticDdx.IndexStampedBlocks(512, 8);

        using var ms = new MemoryStream();
        SyntheticDdx.WriteLzxUncompressedChunk(ms, payload);
        var compressed = ms.ToArray();

        var decompressed = DecompressWithRealLzx(compressed, payload.Length * 2, out _);

        Assert.Equal(payload, decompressed);
    }

    [Fact]
    public void LzxChunks_MultiChunk_SingleCallWithLargeBuffer_YieldsBothChunks()
    {
        // The parser hands the whole remainder plus a 2x-hint buffer to ONE Decompress call, so
        // a stream larger than 32 KB chains its chunks inside that call. Both chunks' bytes
        // must land — the intel-E8 bit is per-stream, and a continuation chunk carrying it has
        // its block type parsed one bit off (the original builder bug this test caught).
        var payload = SyntheticDdx.IndexStampedBlocks(65536 / 16, 16); // two-chunk stream
        using var ms = new MemoryStream();
        SyntheticDdx.WriteLzxStream(ms, payload);
        var compressed = ms.ToArray();

        var decompressed = DecompressWithRealLzx(compressed, payload.Length * 2, out var consumed);

        Assert.Equal(compressed.Length, consumed);
        Assert.Equal(payload.Length, decompressed.Length);
        Assert.Equal(payload, decompressed);
    }

    [Fact]
    public void LzxChunks_MultiChunk_EachDecompressesIndependently()
    {
        // The parser decompresses a multi-chunk stream one DecompressXMemCompress call at a
        // time, each with a fresh decompressor, consuming one chunk per call. Simulate that.
        var chunk1Payload = SyntheticDdx.IndexStampedBlocks(1024, 16); // 16 KB
        var chunk2Payload = SyntheticDdx.IndexStampedBlocks(512, 16); // 8 KB

        using var ms = new MemoryStream();
        SyntheticDdx.WriteLzxUncompressedChunk(ms, chunk1Payload);
        SyntheticDdx.WriteLzxUncompressedChunk(ms, chunk2Payload);
        var stream = ms.ToArray();

        var first = DecompressWithRealLzx(stream, 0x10000, out var consumed1);
        Assert.Equal(chunk1Payload, first);

        var remainder = stream[consumed1..];
        var second = DecompressWithRealLzx(remainder, 0x10000, out var consumed2);
        Assert.Equal(chunk2Payload, second);
        Assert.Equal(remainder.Length, consumed2);
    }

    [Fact]
    public void Header_ParsesBackToDeclaredDimensionsAndFormat()
    {
        // End to end through the real parser: a tile-aligned 128x128 DXT1 surface (32x32 blocks,
        // 8192 bytes) decodes to a DDS with the declared dims, format, and exact block bytes.
        // Tile-aligned means the untile is a pure permutation with no crop, so every stamped
        // block must appear exactly once in the output.
        const int width = 128;
        const int height = 128;
        var surface = SyntheticDdx.IndexStampedBlocks(32 * 32, 8);
        var ddx = SyntheticDdx.Build3Xdo(width, height, SyntheticDdx.Dxt1, surface);

        var dds = new DdxParser().ConvertDdxToDds(ddx);

        Assert.NotNull(dds);
        Assert.Equal((byte)'D', dds[0]);
        Assert.Equal(height, BitConverter.ToInt32(dds, 12));
        Assert.Equal(width, BitConverter.ToInt32(dds, 16));
        Assert.Equal("DXT1", System.Text.Encoding.ASCII.GetString(dds, 84, 4));

        // Every source block index appears exactly once in the pixel data (offset 128 onward).
        // The decode endian-swaps 16-bit words, so a stamp (lo,hi) reads back as (hi,lo).
        var seen = new HashSet<int>();
        for (var off = 128; off + 8 <= dds.Length && seen.Count < 32 * 32; off += 8)
        {
            var stamp = (dds[off] << 8) | dds[off + 1];
            Assert.True(seen.Add(stamp), $"block stamp {stamp} appeared twice");
        }

        Assert.Equal(32 * 32, seen.Count);
    }
}
