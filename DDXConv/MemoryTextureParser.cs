using XCompression;

namespace DDXConv;

/// <summary>
///     Handles texture data carved from Xbox 360 memory dumps.
///     Memory dump textures differ from file-based DDX in several ways:
///     - May be partially loaded or in GPU-ready format
///     - Often have packed mip atlases in tiled memory layouts
///     - May be missing headers or have incomplete data
///     - Tiling patterns optimized for GPU access, not file storage
///     This parser handles these cases separately from the standard DDX file parser.
/// </summary>
public class MemoryTextureParser
{
    private readonly bool _verbose;

    public MemoryTextureParser(bool verbose = false)
    {
        _verbose = verbose;
    }

    /// <summary>
    ///     Convert a DDX file from a memory dump to DDS format.
    ///     This is a convenience overload that reads the file and handles output.
    /// </summary>
    /// <param name="inputPath">Path to the DDX file</param>
    /// <param name="outputPath">Path for the output DDS file</param>
    /// <param name="saveAtlas">If true, also save the full untiled atlas</param>
    /// <param name="saveRaw">If true, save raw decompressed data</param>
    /// <returns>Conversion result with DDS data</returns>
    public ConversionResult ConvertFromMemory(string inputPath, string outputPath, bool saveAtlas = false,
        bool saveRaw = false)
    {
        // Read input file
        byte[] data;
        try
        {
            data = File.ReadAllBytes(inputPath);
        }
        catch (Exception ex)
        {
            return new ConversionResult { Success = false, Error = $"Failed to read input file: {ex.Message}" };
        }

        // Convert
        var result = ConvertFromMemory(data, saveAtlas);
        if (!result.Success) return result;

        // Write output DDS
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllBytes(outputPath, result.DdsData!);
        }
        catch (Exception ex)
        {
            return new ConversionResult { Success = false, Error = $"Failed to write output file: {ex.Message}" };
        }

        ArgumentNullException.ThrowIfNull(outputPath);

        // Write atlas if requested and available
        string? atlasPath = null;
        if (saveAtlas && result.AtlasData != null)
        {
            atlasPath = outputPath.Replace(".dds", "_full_atlas.dds");
            try
            {
                File.WriteAllBytes(atlasPath, result.AtlasData);
            }
            catch (Exception ex)
            {
                if (_verbose) Console.WriteLine($"Warning: Failed to save atlas: {ex.Message}");
            }
        }

        // Write raw data if requested
        if (saveRaw && result.DdsData != null)
        {
            var rawPath = Path.ChangeExtension(outputPath, ".raw");
            try
            {
                File.WriteAllBytes(rawPath, result.DdsData);
            }
            catch (Exception ex)
            {
                if (_verbose) Console.WriteLine($"Warning: Failed to save raw data: {ex.Message}");
            }
        }

        // Return result with atlas path
        return new ConversionResult
        {
            Success = true,
            DdsData = result.DdsData,
            AtlasData = result.AtlasData,
            AtlasPath = atlasPath,
            Width = result.Width,
            Height = result.Height,
            MipLevels = result.MipLevels,
            Notes = result.Notes
        };
    }

    /// <summary>
    ///     Convert raw texture data from a memory dump to DDS format.
    /// </summary>
    /// <param name="data">Raw texture data (may include DDX header or be raw GPU data)</param>
    /// <param name="saveAtlas">If true, also return the full untiled atlas for debugging</param>
    /// <returns>Conversion result with DDS data</returns>
    public ConversionResult ConvertFromMemory(byte[] data, bool saveAtlas = false)
    {
        if (data == null || data.Length < 68) // Minimum DDX header size
            return new ConversionResult { Success = false, Error = "Data too small to be a valid texture" };

        // Check for DDX magic
        var magic = BitConverter.ToUInt32(data, 0);
        if (magic == 0x4F445833) // "3XDO"
            return ConvertDdxFromMemory(data, saveAtlas);

        if (magic == 0x52445833) // "3XDR"
            return new ConversionResult
            {
                Success = false,
                Error = "3XDR format not yet supported for memory textures"
            };

        // Not a DDX file - could be raw GPU texture data
        return new ConversionResult { Success = false, Error = $"Unknown texture format (magic: 0x{magic:X8})" };
    }

    /// <summary>
    ///     Convert DDX texture data from memory dump.
    ///     Handles the various layouts found in memory vs. file-based DDX.
    /// </summary>
    private ConversionResult ConvertDdxFromMemory(byte[] data, bool saveAtlas)
    {
        try
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);

            // Skip magic (already verified)
            reader.ReadUInt32();

            // Read priority bytes
            reader.ReadByte(); // priorityL
            reader.ReadByte(); // priorityC
            reader.ReadByte(); // priorityH

            // Read version
            var version = reader.ReadUInt16();
            if (version < 3)
                return new ConversionResult
                {
                    Success = false,
                    Error = $"DDX version {version} not supported (need >= 3)"
                };

            // Read D3DTexture header (go back 1 byte, read 52 bytes)
            reader.BaseStream.Seek(-1, SeekOrigin.Current);
            var textureHeader = reader.ReadBytes(52);

            // Skip to data start (0x44)
            reader.ReadBytes(8);

            // Parse texture info
            var texture = ParseD3DTextureHeader(textureHeader, out var width, out var height);

            if (_verbose) Console.WriteLine($"Memory texture: {width}x{height}, Format=0x{texture.ActualFormat:X2}");

            // Read remaining data
            var remainingBytes = (int)(reader.BaseStream.Length - reader.BaseStream.Position);
            var compressedData = reader.ReadBytes(remainingBytes);

            // Try to decompress
            byte[] mainData;
            try
            {
                mainData = DecompressTextureData(compressedData, width, height, texture.ActualFormat);
            }
            catch (Exception ex)
            {
                return new ConversionResult { Success = false, Error = $"Decompression failed: {ex.Message}" };
            }

            // Process the decompressed data based on its layout
            return ProcessMemoryTextureData(mainData, width, height, texture, saveAtlas);
        }
        catch (Exception ex)
        {
            return new ConversionResult { Success = false, Error = $"Parse error: {ex.Message}" };
        }
    }

    /// <summary>
    ///     Decompress texture data using XMemCompress.
    ///     Handles multiple compressed chunks common in memory dumps.
    /// </summary>
    private byte[] DecompressTextureData(byte[] compressed, int width, int height, uint format)
    {
        var expectedSize = (uint)TextureUtilities.CalculateMipSize(width, height, format);
        var chunks = new List<byte[]>();
        var totalConsumed = 0;

        // First chunk
        var firstChunk = DecompressXMemCompress(compressed, expectedSize, out var consumed);
        if (_verbose) Console.WriteLine($"Chunk 1: {consumed} compressed -> {firstChunk.Length} decompressed");

        chunks.Add(firstChunk);
        totalConsumed += consumed;

        // Try additional chunks
        while (totalConsumed < compressed.Length - 10)
            try
            {
                var remaining = new byte[compressed.Length - totalConsumed];
                Array.Copy(compressed, totalConsumed, remaining, 0, remaining.Length);

                var chunk = DecompressXMemCompress(remaining, expectedSize, out consumed);
                if (consumed == 0 || chunk.Length == 0) break;

                if (_verbose)
                    Console.WriteLine(
                        $"Chunk {chunks.Count + 1}: {consumed} compressed -> {chunk.Length} decompressed");

                chunks.Add(chunk);
                totalConsumed += consumed;
            }
            catch
            {
                break;
            }

        // Combine chunks
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
    private ConversionResult ProcessMemoryTextureData(byte[] data, int width, int height,
        TextureInfo texture, bool saveAtlas)
    {
        var mainSurfaceSize = TextureUtilities.CalculateMipSize(width, height, texture.ActualFormat);

        // Determine the layout based on data size
        if (data.Length == mainSurfaceSize)
        {
            // Exact match - could be packed mip atlas for half-size texture
            if (width >= 256 && height >= 256 && width == height)
                return TryExtractPackedMipAtlas(data, width, height, texture, saveAtlas);

            // Simple single surface
            var untiled = UntileTexture(data, width, height, texture.ActualFormat);
            var dds = BuildDds(untiled, width, height, 1, texture);
            return new ConversionResult
            {
                Success = true,
                DdsData = dds,
                Width = width,
                Height = height,
                MipLevels = 1
            };
        }

        if (data.Length == mainSurfaceSize * 2 && width >= 128 && height >= 128)
            // 2x main surface - packed mip atlas for half-size base
            return TryExtractPackedMipAtlas(data, width, height, texture, saveAtlas);

        if (data.Length > mainSurfaceSize)
            // Main surface + mips
            return ProcessMainPlusMips(data, width, height, texture, mainSurfaceSize);

        {
            // Partial data - try to salvage what we can
            if (_verbose) Console.WriteLine($"Partial data: {data.Length} bytes, expected {mainSurfaceSize}");

            // Just untile whatever we have
            var untiled = UntileTexture(data, width, height, texture.ActualFormat);
            var dds = BuildDds(untiled, width, height, 1, texture);
            return new ConversionResult
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
    private ConversionResult TryExtractPackedMipAtlas(byte[] data, int atlasWidth, int atlasHeight,
        TextureInfo texture, bool saveAtlas)
    {
        // Untile the full atlas
        var fullUntiled = UntileTexture(data, atlasWidth, atlasHeight, texture.ActualFormat);
        byte[]? atlasData = null;

        if (saveAtlas) atlasData = BuildDds(fullUntiled, atlasWidth, atlasHeight, 1, texture);

        // Try half-size base
        var baseWidth = atlasWidth / 2;
        var baseHeight = atlasHeight / 2;

        var mips = ExtractPackedMips(fullUntiled, atlasWidth, atlasHeight, baseWidth, baseHeight,
            texture.ActualFormat);

        if (mips is { Count: >= 2 })
        {
            // Success - combine mips into DDS
            var totalSize = mips.Sum(m => m.Length);
            var combined = new byte[totalSize];
            var offset = 0;
            foreach (var mip in mips)
            {
                Array.Copy(mip, 0, combined, offset, mip.Length);
                offset += mip.Length;
            }

            var dds = BuildDds(combined, baseWidth, baseHeight, mips.Count, texture);

            if (_verbose)
                Console.WriteLine($"Extracted packed mip atlas: {baseWidth}x{baseHeight} with {mips.Count} mip levels");

            return new ConversionResult
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

        // Fallback - return as single surface at original size
        var singleDds = BuildDds(fullUntiled, atlasWidth, atlasHeight, 1, texture);
        return new ConversionResult
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
    private List<byte[]>? ExtractPackedMips(byte[] atlasData, int atlasWidth, int atlasHeight,
        int baseWidth, int baseHeight, uint format)
    {
        _ = TextureUtilities.GetBlockSize(format); // blockSize not directly used here but validates format
        var mips = new List<byte[]>();

        // Extract base mip from top-left quadrant
        var baseMip = ExtractRegion(atlasData, atlasWidth, 0, 0, baseWidth, baseHeight, format);
        if (baseMip == null) return null;

        var expectedBaseSize = TextureUtilities.CalculateMipSize(baseWidth, baseHeight, format);
        if (baseMip.Length != expectedBaseSize) return null;

        mips.Add(baseMip);

        if (_verbose) Console.WriteLine($"  Mip 0: {baseWidth}x{baseHeight} at (0,0), {baseMip.Length} bytes");

        // Extract remaining mips from right column, stacked vertically
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

            if (_verbose) Console.WriteLine($"  Mip {mipLevel}: {mipW}x{mipH} at ({mipX},{mipY}), {mip.Length} bytes");

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
    private static ConversionResult ProcessMainPlusMips(byte[] data, int width, int height,
        TextureInfo texture, int mainSurfaceSize)
    {
        // Untile main surface
        var mainTiled = new byte[mainSurfaceSize];
        Array.Copy(data, 0, mainTiled, 0, mainSurfaceSize);
        var mainUntiled = UntileTexture(mainTiled, width, height, texture.ActualFormat);

        // Process remaining mips
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

        // Combine all mips
        var totalSize = mipDataList.Sum(m => m.Length);
        var combined = new byte[totalSize];
        var offset = 0;
        foreach (var mip in mipDataList)
        {
            Array.Copy(mip, 0, combined, offset, mip.Length);
            offset += mip.Length;
        }

        var dds = BuildDds(combined, width, height, mipDataList.Count, texture);
        return new ConversionResult
        {
            Success = true,
            DdsData = dds,
            Width = width,
            Height = height,
            MipLevels = mipDataList.Count
        };
    }

    #region XMemCompress

    private static byte[] DecompressXMemCompress(byte[] compressed, uint expectedSize, out int bytesConsumed)
    {
        var buffer = new byte[expectedSize * 2];

        using var context = new DecompressionContext();
        var compressedLen = compressed.Length;
        var decompressedLen = buffer.Length;

        var result = context.Decompress(
            compressed, 0, ref compressedLen,
            buffer, 0, ref decompressedLen);

        if (result != ErrorCode.None)
            throw new InvalidOperationException($"XMemCompress decompression failed: {result}");

        bytesConsumed = compressedLen;
        var output = new byte[decompressedLen];
        Array.Copy(buffer, output, decompressedLen);
        return output;
    }

    #endregion

    /// <summary>
    ///     Result of memory texture conversion.
    /// </summary>
    public class ConversionResult
    {
        public bool Success { get; init; }
        public byte[]? DdsData { get; init; }
        public byte[]? AtlasData { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public int MipLevels { get; init; }
        public string? Notes { get; init; }
        public string? Error { get; init; }
        public string? AtlasPath { get; init; }
    }

    /// <summary>
    ///     Basic texture info structure.
    /// </summary>
    public class TextureInfo
    {
        public int Width { get; init; }
        public int Height { get; init; }
        public byte DataFormat { get; init; }
        public byte ActualFormat { get; init; }
    }

    #region Texture Utilities

    /// <summary>
    ///     Parse D3DTexture header to extract dimensions and format.
    ///     Structure matches DdxParser.ParseD3DTextureHeaderWithDimensions
    /// </summary>
    private TextureInfo ParseD3DTextureHeader(byte[] header, out int width, out int height)
    {
        // Xbox 360 D3D texture header structure (52 bytes starting at file offset 0x08):
        // Offset 0-23 in header: D3DResource (Common, RefCount, Fence, ReadFence, Identifier, BaseFlush)
        // Offset 24-27 in header: MipFlush  
        // Offset 28-51 in header: Format (xe_gpu_texture_fetch_t, 6 dwords = 24 bytes)
        //
        // Format structure (from Xenia xenos.h):
        //   dword_0: pitch, tiling, clamp modes
        //   dword_1: format (bits 0-5), endianness, base_address
        //   dword_2: size_2d { width:13, height:13, stack_depth:6 }
        //   dword_3-5: swizzle, mip info, etc.
        //
        // IMPORTANT: Xbox 360 is big-endian, so Format dwords must be read as big-endian!

        // Dimensions dword is at formatDword[5] position = offset 16+20 = 36, stored as BIG-ENDIAN
        var dword5Bytes = new byte[4];
        Array.Copy(header, 36, dword5Bytes, 0, 4);
        Array.Reverse(dword5Bytes); // Convert from big-endian to little-endian
        var dword5 = BitConverter.ToUInt32(dword5Bytes, 0);

        // Decode size_2d structure (dimensions stored as size-1):
        // Bits 0-12: width - 1
        // Bits 13-25: height - 1  
        // Bits 26-31: stack_depth
        width = (int)((dword5 & 0x1FFF) + 1);
        height = (int)(((dword5 >> 13) & 0x1FFF) + 1);

        // Format dwords are stored as LITTLE-ENDIAN at offset 16 within header
        var formatDwords = new uint[6];
        for (var i = 0; i < 6; i++) formatDwords[i] = BitConverter.ToUInt32(header, 16 + i * 4);

        var dword3 = formatDwords[3];
        var dword4 = formatDwords[4];

        // The format appears to be in DWORD[3] byte 0 (bits 0-7)
        var dataFormat = (byte)(dword3 & 0xFF);

        // For 0x82 textures, check DWORD[4] high byte to distinguish DXT1 from DXT5
        var actualFormat = (byte)((dword4 >> 24) & 0xFF);
        if (actualFormat == 0) actualFormat = dataFormat;

        if (_verbose)
            Console.WriteLine(
                $"Parsed: {width}x{height}, DataFormat=0x{dataFormat:X2}, ActualFormat=0x{actualFormat:X2}");

        return new TextureInfo { Width = width, Height = height, DataFormat = dataFormat, ActualFormat = actualFormat };
    }

    /// <summary>
    ///     Untile/unswizzle Xbox 360 texture data.
    /// </summary>
    private static byte[] UntileTexture(byte[] src, int width, int height, uint format)
    {
        var blockSize = TextureUtilities.GetBlockSize(format);
        var blocksWide = width / 4;
        var blocksHigh = height / 4;
        var dst = new byte[src.Length];

        // Calculate log2 of bytes per pixel for tiling
        var log2Bpp = (uint)(blockSize / 4 + ((blockSize / 2) >> (blockSize / 4)));

        for (var y = 0; y < blocksHigh; y++)
        {
            var inputRowOffset = TiledOffset2DRow((uint)y, (uint)blocksWide, log2Bpp);

            for (var x = 0; x < blocksWide; x++)
            {
                var inputOffset = TiledOffset2DColumn((uint)x, (uint)y, log2Bpp, inputRowOffset);
                inputOffset >>= (int)log2Bpp;

                var dstOffset = (y * blocksWide + x) * blockSize;
                var srcOffset = (int)inputOffset * blockSize;

                if (srcOffset + blockSize <= src.Length && dstOffset + blockSize <= dst.Length)
                    Array.Copy(src, srcOffset, dst, dstOffset, blockSize);
            }
        }

        return dst;
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

    #endregion

    #region Tiling Math (from Xenia)

    // Xbox 360 tiling functions from Xenia emulator
    // https://github.com/xenia-project/xenia/blob/master/src/xenia/gpu/texture_conversion.cc
    private static uint TiledOffset2DRow(uint y, uint width, uint log2Bpp)
    {
        var macro = ((y >> 5) * ((width >> 5) << (int)log2Bpp)) << 11;
        var micro = ((y & 6) >> 1) << (int)log2Bpp << 7;
        return macro + ((micro + ((y & 8) << (7 + (int)log2Bpp))) ^ ((y & 1) << 4));
    }

    private static uint TiledOffset2DColumn(uint x, uint y, uint log2Bpp, uint rowOffset)
    {
        var macro = (x >> 5) << (int)log2Bpp << 11;
        var micro = ((x & 7) + ((x & 8) << 1)) << (int)log2Bpp;
        var offset = macro + (micro ^ (((y & 8) << 3) + ((y & 1) << 4)));
        return ((rowOffset + offset) << (int)log2Bpp) >> (int)log2Bpp;
    }

    #endregion

    #region DDS Building

    /// <summary>
    ///     Build a DDS file from texture data.
    /// </summary>
    private static byte[] BuildDds(byte[] data, int width, int height, int mipLevels, TextureInfo texture)
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

        // Calculate pitch/linear size
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

        // FourCC based on format
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

    #endregion
}
