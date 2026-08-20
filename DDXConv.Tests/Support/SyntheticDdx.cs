namespace DDXConv.Tests.Support;

/// <summary>
///     Builds well-formed synthetic <c>.ddx</c> byte streams so decode behaviour is testable
///     end-to-end without real game assets. The header layout matches what
///     <see cref="DdxHeaderWriter.ParseD3DTextureHeaderWithDimensions" /> actually reads
///     (magic @0x00, version @0x07, DataFormat byte @0x24, ActualFormat byte @0x2B, size_2d
///     big-endian dword @0x2C, payload @0x44), and the payload is framed as XMemCompress/LZX
///     chunks containing a single <em>uncompressed</em> LZX block each, which the shipped
///     <c>LzxDecompressor</c> accepts. The framing is proven by
///     <c>SyntheticDdxBuilderTests</c>' round-trip against the real decompressor before anything
///     else relies on it.
/// </summary>
internal static class SyntheticDdx
{
    internal const uint Dxt1 = 0x52;
    internal const uint Dxt3 = 0x53;
    internal const uint Dxt5 = 0x54;
    internal const uint Ati1 = 0x7B;
    internal const uint Ati2 = 0x71;

    private const int HeaderSize = 0x44;
    private const int LzxChunkPayloadMax = 0x8000; // DefaultUncompressedChunkSize

    /// <summary>
    ///     Builds a complete 3XDO DDX file: header for <paramref name="width" />×
    ///     <paramref name="height" /> in <paramref name="gpuFormat" />, followed by
    ///     <paramref name="decompressedPayload" /> as one XMemCompress stream (32 KB chunks
    ///     chained inside a single decompress call, 0xFF-terminated).
    /// </summary>
    internal static byte[] Build3Xdo(int width, int height, uint gpuFormat, byte[] decompressedPayload)
    {
        return Build3XdoStreams(width, height, gpuFormat, decompressedPayload);
    }

    /// <summary>
    ///     Builds a 3XDO DDX whose payload is several consecutive XMemCompress STREAMS — the
    ///     real two-chunk DDX layout ([mips stream][main stream]): the parser decompresses one
    ///     stream per <c>DecompressXMemCompress</c> call and combines the results.
    /// </summary>
    internal static byte[] Build3XdoStreams(int width, int height, uint gpuFormat, params byte[][] streams)
    {
        using var ms = new MemoryStream();
        var header = BuildHeader(width, height, gpuFormat);
        ms.Write(header, 0, header.Length);
        foreach (var payload in streams)
        {
            WriteLzxStream(ms, payload);
        }

        return ms.ToArray();
    }

    /// <summary>
    ///     One XMemCompress stream: consecutive uncompressed-block chunks of at most 32 KB.
    ///     Two stream-level rules matter (both bitten in practice): the intel-E8 bit exists
    ///     only in the FIRST chunk of a stream (the decoder reads it once per decompressor,
    ///     so a continuation chunk carrying it has its block type parsed 1 bit off), and the
    ///     LAST chunk uses the 0xFF explicit-size framing that terminates the decompress call
    ///     (which is how consecutive streams in one file stay separable).
    /// </summary>
    internal static void WriteLzxStream(Stream stream, ReadOnlySpan<byte> payload)
    {
        var offset = 0;
        var first = true;
        while (true)
        {
            var remaining = payload.Length - offset;
            var n = Math.Min(LzxChunkPayloadMax, remaining);
            var last = remaining <= LzxChunkPayloadMax;
            WriteLzxUncompressedChunk(stream, payload.Slice(offset, n), first, last);
            offset += n;
            first = false;
            if (last)
            {
                break;
            }
        }
    }

    /// <summary>
    ///     The 0x44-byte 3XDO header. Only the fields the parser reads are populated; everything
    ///     else stays zero (which the parser ignores).
    /// </summary>
    internal static byte[] BuildHeader(int width, int height, uint gpuFormat)
    {
        var header = new byte[HeaderSize];
        header[0] = (byte)'3';
        header[1] = (byte)'X';
        header[2] = (byte)'D';
        header[3] = (byte)'O';

        // priority bytes @0x04-0x06 stay 0; version (LE ushort) @0x07 must be >= 3.
        header[0x07] = 3;

        // Format dwords are read little-endian from file offset 0x18. DataFormat is the low byte
        // of dword3 (@0x24); ActualFormat is the high byte of dword4 (@0x2B).
        header[0x24] = (byte)gpuFormat;
        header[0x2B] = (byte)gpuFormat;

        // size_2d @0x2C, BIG-endian: bits 0-12 = width-1, bits 13-25 = height-1.
        var size2D = (uint)((width - 1) & 0x1FFF) | ((uint)((height - 1) & 0x1FFF) << 13);
        header[0x2C] = (byte)(size2D >> 24);
        header[0x2D] = (byte)(size2D >> 16);
        header[0x2E] = (byte)(size2D >> 8);
        header[0x2F] = (byte)size2D;
        return header;
    }

    /// <summary>
    ///     One XMemCompress chunk holding a single uncompressed LZX block.
    ///     <para>
    ///         Content layout after the framing header: a 4-byte bitstream seed (two 16-bit
    ///         little-endian words, consumed MSB-first) packing [intelE8=0 (1 bit, FIRST chunk
    ///         of a stream only)] blockType=3 (3 bits), blockSize=payload length (24 bits),
    ///         pad bits — then R0/R1/R2 repeat offsets (12 bytes, LE, value 1), the raw
    ///         payload, and 4 zero bytes. The decoder's <c>ReadUncompressedHeader</c> backs the
    ///         cursor up two bytes before reading R0, which lands it exactly at the R0 field
    ///         because the header bits trigger exactly one 2-byte refill (for both seed
    ///         variants). The trailing zeros make any spurious next-block-header read parse
    ///         deterministically as an invalid block type.
    ///     </para>
    ///     <para>
    ///         Framing: continuation chunks use the implicit 2-byte big-endian compressed-size
    ///         header (the decoder assumes 32 KB uncompressed and chains to the next chunk);
    ///         the stream's LAST chunk uses the 0xFF explicit-size header, which terminates the
    ///         decompress call. The 0xFF path consumes compressedSize + 10 bytes total, so 5
    ///         trailing pad bytes keep the next stream aligned with what the decoder consumed.
    ///     </para>
    /// </summary>
    internal static void WriteLzxUncompressedChunk(Stream stream, ReadOnlySpan<byte> payload,
        bool firstChunkOfStream = true, bool lastChunkOfStream = true)
    {
        if (payload.Length > LzxChunkPayloadMax)
        {
            throw new ArgumentOutOfRangeException(nameof(payload),
                $"LZX chunk payload {payload.Length} exceeds the 32 KB chunk granularity");
        }

        if (!lastChunkOfStream && payload.Length != LzxChunkPayloadMax)
        {
            throw new ArgumentOutOfRangeException(nameof(payload),
                "only the last chunk of a stream may be shorter than 32 KB — earlier chunks are " +
                "implicitly 32 KB and a short one desynchronizes the chain");
        }

        var c = payload.Length + 20; // seed(4) + R0R1R2(12) + payload + terminator pad(4)
        if (lastChunkOfStream)
        {
            stream.WriteByte(0xFF);
            stream.WriteByte((byte)(payload.Length >> 8));
            stream.WriteByte((byte)payload.Length);
            stream.WriteByte((byte)(c >> 8));
            stream.WriteByte((byte)c);
        }
        else
        {
            stream.WriteByte((byte)(c >> 8));
            stream.WriteByte((byte)c);
        }

        // The intel-E8 flag is a stream-level header the decoder reads exactly once, so only
        // the first chunk carries it; later chunks start directly at the block type.
        var seed = firstChunkOfStream
            ? (3u << 28) | ((uint)payload.Length << 4) // 0b0_011_<24-bit size>_0000
            : (3u << 29) | ((uint)payload.Length << 5); // 0b011_<24-bit size>_00000
        var word0 = (ushort)(seed >> 16);
        var word1 = (ushort)seed;
        stream.WriteByte((byte)word0);
        stream.WriteByte((byte)(word0 >> 8));
        stream.WriteByte((byte)word1);
        stream.WriteByte((byte)(word1 >> 8));

        Span<byte> repeatOffsets = [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0];
        stream.Write(repeatOffsets);

        stream.Write(payload);

        Span<byte> terminatorPad = [0, 0, 0, 0];
        stream.Write(terminatorPad);

        if (lastChunkOfStream)
        {
            // The 0xFF framing's consumed size is compressedSize + 10 (5 header + content + 5).
            Span<byte> streamTail = [0, 0, 0, 0, 0];
            stream.Write(streamTail);
        }
    }

    /// <summary>
    ///     A deterministic non-zero payload where every compression block is stamped with its own
    ///     index (little-endian in the first two bytes, repeated through the block) so tests can
    ///     assert exactly which source block landed where after the decode.
    /// </summary>
    internal static byte[] IndexStampedBlocks(int blockCount, int blockSize)
    {
        var data = new byte[blockCount * blockSize];
        for (var i = 0; i < blockCount; i++)
        {
            for (var b = 0; b < blockSize; b += 2)
            {
                data[i * blockSize + b] = (byte)i;
                data[i * blockSize + b + 1] = (byte)(i >> 8);
            }
        }

        return data;
    }
}
