using DDXConv.Compression;

namespace DDXConv;

/// <summary>
///     Handles block-level texture data processing for memory-dumped textures:
///     decompression, untiling, mip atlas extraction, D3D header parsing, and DDS building.
///     Extracted from MemoryTextureParser to separate block/data operations from high-level conversion.
/// </summary>
internal static class MemoryTextureBlockReader
{
    /// <summary>
    ///     Decompress texture data using XMemCompress.
    ///     Handles multiple compressed chunks common in memory dumps.
    /// </summary>
    internal static byte[] DecompressTextureData(byte[] compressed, int width, int height, uint format, bool verbose)
    {
        var expectedSize = (uint)TextureUtilities.CalculateMipSize(width, height, format);
        var chunks = new List<byte[]>();
        var totalConsumed = 0;

        var firstChunk = DecompressXMemCompress(compressed, expectedSize, out var consumed);
        if (verbose) Console.WriteLine($"Chunk 1: {consumed} compressed -> {firstChunk.Length} decompressed");

        chunks.Add(firstChunk);
        totalConsumed += consumed;

        while (totalConsumed < compressed.Length - 10)
            try
            {
                var remaining = new byte[compressed.Length - totalConsumed];
                Array.Copy(compressed, totalConsumed, remaining, 0, remaining.Length);

                var chunk = DecompressXMemCompress(remaining, expectedSize, out consumed);
                if (consumed == 0 || chunk.Length == 0) break;

                if (verbose)
                    Console.WriteLine(
                        $"Chunk {chunks.Count + 1}: {consumed} compressed -> {chunk.Length} decompressed");

                chunks.Add(chunk);
                totalConsumed += consumed;
            }
            catch
            {
                break;
            }

        var total = chunks.Sum(c => c.Length);
        var result = new byte[total];
        var offset = 0;
        foreach (var chunk in chunks)
        {
            Array.Copy(chunk, 0, result, offset, chunk.Length);
            offset += chunk.Length;
        }

        return result;
    }

    /// <summary>
    ///     Process decompressed texture data and convert to DDS.
    ///     Handles the various memory layouts:
    ///     - Standard main surface + mips
    ///     - Packed mip atlas (half-size base in full-size tiled space)
    ///     - Single surface only
    /// </summary>
    internal static MemoryTextureParser.ConversionResult ProcessMemoryTextureData(byte[] data, int width, int height,
        MemoryTextureParser.TextureInfo texture, bool saveAtlas, bool verbose)
    {
        var mainSurfaceSize = TextureUtilities.CalculateMipSize(width, height, texture.ActualFormat);

        if (data.Length == mainSurfaceSize)
        {
            if (width >= 256 && height >= 256 && width == height)
                return TryExtractPackedMipAtlas(data, width, height, texture, saveAtlas, verbose);

            var untiled = UntileTexture(data, width, height, texture.ActualFormat);
            var dds = BuildDds(untiled, width, height, 1, texture);
            return new MemoryTextureParser.ConversionResult
            {
                Success = true,
                DdsData = dds,
                Width = width,
                Height = height,
                MipLevels = 1
            };
        }

        if (data.Length == mainSurfaceSize * 2 && width >= 128 && height >= 128)
            return TryExtractPackedMipAtlas(data, width, height, texture, saveAtlas, verbose);

        if (data.Length > mainSurfaceSize)
            return ProcessMainPlusMips(data, width, height, texture, mainSurfaceSize);

        {
            if (verbose) Console.WriteLine($"Partial data: {data.Length} bytes, expected {mainSurfaceSize}");

            var untiled = UntileTexture(data, width, height, texture.ActualFormat);
            var dds = BuildDds(untiled, width, height, 1, texture);
            return new MemoryTextureParser.ConversionResult
            {
                Success = true,
                DdsData = dds,
                Width = width,
                Height = height,
                MipLevels = 1,
                Notes = $"Partial texture data ({data.Length}/{mainSurfaceSize} bytes)"
            };
        }
    }

    /// <summary>
    ///     Try to extract a packed mip atlas.
    ///     Xbox 360 often stores textures in memory with half-size base + mips packed in the full-size tiled space.
    ///     Layout: [Base WxH at (0,0)] [Mips stacked vertically at (W,0)]
    /// </summary>
    private static MemoryTextureParser.ConversionResult TryExtractPackedMipAtlas(byte[] data, int atlasWidth,
        int atlasHeight, MemoryTextureParser.TextureInfo texture, bool saveAtlas, bool verbose)
    {
        var fullUntiled = UntileTexture(data, atlasWidth, atlasHeight, texture.ActualFormat);
        byte[]? atlasData = null;

        if (saveAtlas) atlasData = BuildDds(fullUntiled, atlasWidth, atlasHeight, 1, texture);

        var baseWidth = atlasWidth / 2;
        var baseHeight = atlasHeight / 2;

        var mips = ExtractPackedMips(fullUntiled, atlasWidth, atlasHeight, baseWidth, baseHeight,
            texture.ActualFormat, verbose);

        if (mips is { Count: >= 2 })
        {
            var totalSize = mips.Sum(m => m.Length);
            var combined = new byte[totalSize];
            var offset = 0;
            foreach (var mip in mips)
            {
                Array.Copy(mip, 0, combined, offset, mip.Length);
                offset += mip.Length;
            }

            var dds = BuildDds(combined, baseWidth, baseHeight, mips.Count, texture);

            if (verbose)
                Console.WriteLine($"Extracted packed mip atlas: {baseWidth}x{baseHeight} with {mips.Count} mip levels");

            return new MemoryTextureParser.ConversionResult
            {
                Success = true,
                DdsData = dds,
                AtlasData = atlasData,
                Width = baseWidth,
                Height = baseHeight,
                MipLevels = mips.Count,
                Notes = $"Packed mip atlas: {baseWidth}x{baseHeight} from {atlasWidth}x{atlasHeight} tile space"
            };
        }

        var singleDds = BuildDds(fullUntiled, atlasWidth, atlasHeight, 1, texture);
        return new MemoryTextureParser.ConversionResult
        {
            Success = true,
            DdsData = singleDds,
            AtlasData = atlasData,
            Width = atlasWidth,
            Height = atlasHeight,
            MipLevels = 1
        };
    }

    /// <summary>
    ///     Extract mip levels from a packed atlas.
    ///     Layout: Base at (0,0), mips stacked vertically in right column starting at (baseWidth, 0)
    /// </summary>
    private static List<byte[]>? ExtractPackedMips(byte[] atlasData, int atlasWidth, int atlasHeight,
        int baseWidth, int baseHeight, uint format, bool verbose)
    {
        _ = TextureUtilities.GetBlockSize(format);
        var mips = new List<byte[]>();

        var baseMip = ExtractRegion(atlasData, atlasWidth, 0, 0, baseWidth, baseHeight, format);
        if (baseMip == null) return null;

        var expectedBaseSize = TextureUtilities.CalculateMipSize(baseWidth, baseHeight, format);
        if (baseMip.Length != expectedBaseSize) return null;

        mips.Add(baseMip);

        if (verbose) Console.WriteLine($"  Mip 0: {baseWidth}x{baseHeight} at (0,0), {baseMip.Length} bytes");

        var mipX = baseWidth;
        var mipY = 0;
        var mipW = baseWidth / 2;
        var mipH = baseHeight / 2;
        var mipLevel = 1;

        while (mipW >= 4 && mipH >= 4 && mipX + mipW <= atlasWidth && mipY + mipH <= atlasHeight)
        {
            var mip = ExtractRegion(atlasData, atlasWidth, mipX, mipY, mipW, mipH, format);
            var expectedSize = TextureUtilities.CalculateMipSize(mipW, mipH, format);

            if (mip == null || mip.Length != expectedSize) break;

            mips.Add(mip);

            if (verbose) Console.WriteLine($"  Mip {mipLevel}: {mipW}x{mipH} at ({mipX},{mipY}), {mip.Length} bytes");

            mipY += mipH;
            mipW /= 2;
            mipH /= 2;
            mipLevel++;
        }

        return mips.Count >= 2 ? mips : null;
    }

    /// <summary>
    ///     Process texture with main surface + sequential mips.
    /// </summary>
    private static MemoryTextureParser.ConversionResult ProcessMainPlusMips(byte[] data, int width, int height,
        MemoryTextureParser.TextureInfo texture, int mainSurfaceSize)
    {
        var mainTiled = new byte[mainSurfaceSize];
        Array.Copy(data, 0, mainTiled, 0, mainSurfaceSize);
        var mainUntiled = UntileTexture(mainTiled, width, height, texture.ActualFormat);

        var mipDataList = new List<byte[]> { mainUntiled };
        var mipOffset = mainSurfaceSize;
        var mipW = width / 2;
        var mipH = height / 2;

        while (mipW >= 4 && mipH >= 4 && mipOffset < data.Length)
        {
            var mipSize = TextureUtilities.CalculateMipSize(mipW, mipH, texture.ActualFormat);
            if (mipOffset + mipSize > data.Length) break;

            var mipTiled = new byte[mipSize];
            Array.Copy(data, mipOffset, mipTiled, 0, mipSize);
            var mipUntiled = UntileTexture(mipTiled, mipW, mipH, texture.ActualFormat);
            mipDataList.Add(mipUntiled);

            mipOffset += mipSize;
            mipW /= 2;
            mipH /= 2;
        }

        var totalSize = mipDataList.Sum(m => m.Length);
        var combined = new byte[totalSize];
        var offset = 0;
        foreach (var mip in mipDataList)
        {
            Array.Copy(mip, 0, combined, offset, mip.Length);
            offset += mip.Length;
        }

        var dds = BuildDds(combined, width, height, mipDataList.Count, texture);
        return new MemoryTextureParser.ConversionResult
        {
            Success = true,
            DdsData = dds,
            Width = width,
            Height = height,
            MipLevels = mipDataList.Count
        };
    }

    /// <summary>
    ///     Parse D3DTexture header to extract dimensions and format.
    /// </summary>
    internal static MemoryTextureParser.TextureInfo ParseD3DTextureHeader(byte[] header, out int width,
        out int height, bool verbose)
    {
        var dword5Bytes = new byte[4];
        Array.Copy(header, 36, dword5Bytes, 0, 4);
        Array.Reverse(dword5Bytes);
        var dword5 = BitConverter.ToUInt32(dword5Bytes, 0);

        width = (int)((dword5 & 0x1FFF) + 1);
        height = (int)(((dword5 >> 13) & 0x1FFF) + 1);

        var formatDwords = new uint[6];
        for (var i = 0; i < 6; i++) formatDwords[i] = BitConverter.ToUInt32(header, 16 + i * 4);

        var dword3 = formatDwords[3];
        var dword4 = formatDwords[4];

        var dataFormat = (byte)(dword3 & 0xFF);

        var actualFormat = (byte)((dword4 >> 24) & 0xFF);
        if (actualFormat == 0) actualFormat = dataFormat;

        if (verbose)
            Console.WriteLine(
                $"Parsed: {width}x{height}, DataFormat=0x{dataFormat:X2}, ActualFormat=0x{actualFormat:X2}");

        return new MemoryTextureParser.TextureInfo
        {
            Width = width, Height = height, DataFormat = dataFormat, ActualFormat = actualFormat
        };
    }

    /// <summary>
    ///     Untile/unswizzle Xbox 360 texture data using the shared Morton tiling algorithm.
    /// </summary>
    private static byte[] UntileTexture(byte[] src, int width, int height, uint format)
    {
        return TextureUtilities.UnswizzleMortonDXT(src, width, height, format);
    }

    /// <summary>
    ///     Extract a rectangular region from untiled atlas data.
    /// </summary>
    private static byte[]? ExtractRegion(byte[] atlas, int atlasWidth,
        int regionX, int regionY, int regionWidth, int regionHeight, uint format)
    {
        var blockSize = TextureUtilities.GetBlockSize(format);
        var atlasBlocksX = atlasWidth / 4;
        var regionBlocksX = regionWidth / 4;
        var regionBlocksY = regionHeight / 4;
        var startBlockX = regionX / 4;
        var startBlockY = regionY / 4;

        var output = new byte[regionBlocksX * regionBlocksY * blockSize];
        var destOffset = 0;

        for (var by = 0; by < regionBlocksY; by++)
        {
            var srcY = startBlockY + by;
            for (var bx = 0; bx < regionBlocksX; bx++)
            {
                var srcX = startBlockX + bx;
                var srcOffset = (srcY * atlasBlocksX + srcX) * blockSize;

                if (srcOffset + blockSize > atlas.Length) return null;

                Array.Copy(atlas, srcOffset, output, destOffset, blockSize);
                destOffset += blockSize;
            }
        }

        return output;
    }

    private static byte[] DecompressXMemCompress(byte[] compressed, uint expectedSize, out int bytesConsumed)
    {
        var buffer = new byte[expectedSize * 2];
        using var decompressor = new LzxDecompressor();
        var compressedLen = compressed.Length;
        var decompressedLen = buffer.Length;

        var result = decompressor.Decompress(
            compressed, 0, ref compressedLen,
            buffer, 0, ref decompressedLen);

        if (result != 0)
            throw new InvalidOperationException($"LzxDecompressor decompression failed: {result}");

        bytesConsumed = compressedLen;
        var output = new byte[decompressedLen];
        Array.Copy(buffer, output, decompressedLen);
        return output;
    }

    /// <summary>
    ///     Build a DDS file from texture data.
    /// </summary>
    internal static byte[] BuildDds(byte[] data, int width, int height, int mipLevels,
        MemoryTextureParser.TextureInfo texture)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // DDS magic
        writer.Write(0x20534444); // "DDS "

        // DDS_HEADER (124 bytes)
        writer.Write(124); // dwSize
        writer.Write(0x000A1007); // dwFlags (CAPS | HEIGHT | WIDTH | PIXELFORMAT | MIPMAPCOUNT | LINEARSIZE)
        writer.Write(height); // dwHeight
        writer.Write(width); // dwWidth

        var blockSize = TextureUtilities.GetBlockSize(texture.ActualFormat);
        var linearSize = Math.Max(1, width / 4) * Math.Max(1, height / 4) * blockSize;
        writer.Write(linearSize); // dwPitchOrLinearSize

        writer.Write(0); // dwDepth
        writer.Write(mipLevels); // dwMipMapCount

        // dwReserved1[11]
        for (var i = 0; i < 11; i++) writer.Write(0);

        // DDS_PIXELFORMAT (32 bytes)
        writer.Write(32); // dwSize
        writer.Write(4); // dwFlags = DDPF_FOURCC

        var fourCC = GetFourCC(texture.ActualFormat);
        writer.Write(fourCC);

        writer.Write(0); // dwRGBBitCount
        writer.Write(0); // dwRBitMask
        writer.Write(0); // dwGBitMask
        writer.Write(0); // dwBBitMask
        writer.Write(0); // dwABitMask

        // dwCaps
        var caps = 0x1000; // DDSCAPS_TEXTURE
        if (mipLevels > 1) caps |= 0x400008; // DDSCAPS_COMPLEX | DDSCAPS_MIPMAP

        writer.Write(caps);

        writer.Write(0); // dwCaps2
        writer.Write(0); // dwCaps3
        writer.Write(0); // dwCaps4
        writer.Write(0); // dwReserved2

        // Write texture data
        writer.Write(data);

        return ms.ToArray();
    }

    private static uint GetFourCC(uint format)
    {
        return format switch
        {
            0x52 or 0x82 or 0x86 or 0x12 => 0x31545844, // "DXT1"
            0x53 or 0x13 => 0x33545844, // "DXT3"
            0x54 or 0x71 or 0x88 or 0x14 => 0x35545844, // "DXT5"
            0x7B => 0x31495441, // "ATI1"
            _ => 0x35545844 // Default to DXT5
        };
    }
}
