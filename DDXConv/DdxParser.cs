using XCompression;

namespace DDXConv;

/// <summary>
///     Parameters for extracting a region from an atlas.
/// </summary>
internal readonly record struct AtlasRegionParams(
    int AtlasWidth,
    int AtlasHeight,
    int RegionX,
    int RegionY,
    int RegionWidth,
    int RegionHeight,
    uint Format);

/// <summary>
///     Parameters for unpacking a mip atlas.
/// </summary>
internal readonly record struct MipAtlasParams(
    int AtlasWidth,
    int AtlasHeight,
    uint Format,
    int MainWidth,
    int MainHeight,
    string? OutputPath = null,
    bool SaveMips = false);

public class DdxParser(bool verbose = false)
{
    private const uint MAGIC_3XDO = 0x4F445833;
    private const uint MAGIC_3XDR = 0x52445833;

    private static readonly byte[,] _pFirstTilingRectsA = new byte[4, 3]
    {
        { 0x00, 0x02, 0x03 }, { 0x12, 0x16, 0x18 }, { 0x04, 0x0C, 0x10 }, { 0x00, 0x02, 0x03 }
    };

    private static readonly TextureTileRectDef[] _pTextureTileRectsA =
    [
        new() { cByteOffsetX = 0, cLineOffsetY = 0, cBytesWide = 128, cLinesHigh = 8 },
        new() { cByteOffsetX = 128, cLineOffsetY = 16, cBytesWide = 128, cLinesHigh = 8 },
        new() { cByteOffsetX = 0, cLineOffsetY = 0, cBytesWide = 64, cLinesHigh = 8 },
        new() { cByteOffsetX = 0, cLineOffsetY = 0, cBytesWide = 64, cLinesHigh = 4 },
        new() { cByteOffsetX = 0, cLineOffsetY = 0, cBytesWide = 128, cLinesHigh = 4 },
        new() { cByteOffsetX = 256, cLineOffsetY = 0, cBytesWide = 128, cLinesHigh = 4 },
        new() { cByteOffsetX = 0, cLineOffsetY = 8, cBytesWide = 128, cLinesHigh = 4 },
        // entries continue as present in the extracted file
        new() { cByteOffsetX = 128, cLineOffsetY = 32, cBytesWide = 128, cLinesHigh = 8 },
        new() { cByteOffsetX = 128, cLineOffsetY = 48, cBytesWide = 128, cLinesHigh = 8 },
        new() { cByteOffsetX = 0, cLineOffsetY = 0, cBytesWide = 64, cLinesHigh = 8 },
        new() { cByteOffsetX = 0, cLineOffsetY = 16, cBytesWide = 64, cLinesHigh = 8 },
        new() { cByteOffsetX = 0, cLineOffsetY = 0, cBytesWide = 64, cLinesHigh = 8 }
    ];

    private static readonly byte[,] _pTilingRectCountsA = new byte[4, 3]
    {
        { 2, 1, 1 }, { 4, 2, 1 }, { 8, 4, 2 }, { 2, 1, 1 }
    };

    private readonly bool _verboseLogging = verbose;

    private ConversionOptions? _currentOptions;

    public void ConvertDdxToDds(string inputPath, string outputPath, ConversionOptions options)
    {
        using var reader = new BinaryReader(File.OpenRead(inputPath));
        var magic = reader.ReadUInt32();

        if (magic == MAGIC_3XDR)
        {
            // 3XDR is simpler than 3XDO: linear layout, mip0 only, just needs byte swap
            Convert3XdrToDds(reader, outputPath, options);
            return;
        }

        if (magic != MAGIC_3XDO) throw new InvalidDataException($"Unknown DDX magic: 0x{magic:X8}.");

        ArgumentNullException.ThrowIfNull(outputPath);

        ConvertDdxToDds(reader, outputPath, options, magic);
    }

    /// <summary>
    ///     Convert 3XDR (engine-tiled) format to DDS.
    ///     3XDR is simpler than 3XDO:
    ///     - Data is already linear (NOT Morton-swizzled)
    ///     - Contains only mip0 (no mip atlas)
    ///     - Only requires decompression + 16-bit byte swap for Xbox 360 big-endian
    /// </summary>
    private void Convert3XdrToDds(BinaryReader reader, string outputPath, ConversionOptions options)
    {
        _currentOptions = options;

        // Read header (same as 3XDO up to offset 0x44)
        _ = reader.ReadByte(); // priorityL
        _ = reader.ReadByte(); // priorityC
        _ = reader.ReadByte(); // priorityH
        var version = reader.ReadUInt16();

        if (version < 3)
            throw new NotSupportedException($"3XDR version {version} is not supported. Need version >= 3");

        // Read D3DTexture header (52 bytes from 0x08 to 0x3C)
        reader.BaseStream.Seek(-1, SeekOrigin.Current);
        var textureHeader = reader.ReadBytes(52);
        reader.ReadBytes(8); // Skip to 0x44

        var texture = ParseD3DTextureHeaderWithDimensions(textureHeader, out var width, out var height);

        if (_verboseLogging)
            Console.WriteLine($"3XDR: {width}x{height}, Format=0x{texture.ActualFormat:X2}");

        // Read all remaining compressed data
        var currentPos = reader.BaseStream.Position;
        var fileSize = reader.BaseStream.Length;
        var compressedData = reader.ReadBytes((int)(fileSize - currentPos));

        // Decompress - 3XDR has mip chain in linear layout
        var mip0Size = (uint)TextureUtilities.CalculateMipSize(width, height, texture.ActualFormat);
        var decompressed = DecompressXMemCompress(compressedData, mip0Size, out var consumed);

        if (_verboseLogging)
            Console.WriteLine(
                $"3XDR: Decompressed {consumed} bytes to {decompressed.Length} bytes (expected mip0={mip0Size})");

        // 3XDR data uses Xbox 360 2×2 macro block tiling and big-endian byte order
        // Step 1: Untile from 2×2 macro blocks to linear row-major layout
        // Step 2: Swap every 16-bit word for PC little-endian
        var blockSize = TextureUtilities.GetBlockSize(texture.ActualFormat);
        var untiled = Untile2x2MacroBlocks(decompressed, width, height, blockSize);
        var textureData = SwapEvery16Bits(untiled);

        // 3XDR files contain only mip0 - no mip chain
        var mipLevels = 1;

        // Update texture info
        texture.Width = width;
        texture.Height = height;
        texture.MipLevels = (byte)mipLevels;

        // Write DDS file
        WriteDdsFile(outputPath, texture, textureData);

        if (_verboseLogging)
            Console.WriteLine($"3XDR: Saved DDS to {outputPath} ({textureData.Length} bytes, {mipLevels} mip(s))");
    }

    /// <summary>
    ///     Swap every 16-bit word in the data (Xbox 360 to PC endian conversion).
    /// </summary>
    private static byte[] SwapEvery16Bits(byte[] src)
    {
        var dst = new byte[src.Length];
        for (var i = 0; i < src.Length - 1; i += 2)
        {
            dst[i] = src[i + 1];
            dst[i + 1] = src[i];
        }

        // Handle odd byte at end if present
        if ((src.Length & 1) == 1) dst[src.Length - 1] = src[src.Length - 1];

        return dst;
    }

    /// <summary>
    ///     Untile 3XDR blocks from Xbox 360 format to linear PC layout.
    ///     The 3XDR tiling pattern discovered through block mapping analysis:
    ///     Within 8×2 block groups, the Xbox layout maps to PC as follows:
    ///     Xbox row 0 (Y=0 within group):
    ///     Xbox X: 0  1  2  3  4  5  6  7
    ///     PC X:   0  1  0  1  2  3  2  3
    ///     PC Y:   0  0  1  1  0  0  1  1
    ///     Xbox row 1 (Y=1 within group):
    ///     Xbox X: 0  1  2  3  4  5  6  7
    ///     PC X:   4  5  4  5  6  7  6  7
    ///     PC Y:   0  0  1  1  0  0  1  1
    ///     Pattern: blocks are interleaved in 2×2 chunks across 8 columns
    /// </summary>
    private static byte[] Untile2x2MacroBlocks(byte[] src, int width, int height, int blockSize)
    {
        var blocksX = Math.Max(1, (width + 3) / 4);
        var blocksY = Math.Max(1, (height + 3) / 4);
        var mipSize = blocksX * blocksY * blockSize;

        var dst = new byte[mipSize];

        // For very small textures (< 8 blocks in X or < 2 in Y), no tiling needed
        if (blocksY < 2 || blocksX < 8)
        {
            Array.Copy(src, dst, Math.Min(src.Length, mipSize));
            return dst;
        }

        // Process each Xbox block and place it in the correct PC position
        for (var xboxIdx = 0; xboxIdx < blocksX * blocksY; xboxIdx++)
        {
            var xboxX = xboxIdx % blocksX;
            var xboxY = xboxIdx / blocksX;

            // Calculate PC position based on discovered mapping
            var pcIdx = GetPcBlockIndex(xboxX, xboxY, blocksX);

            var srcOffset = xboxIdx * blockSize;
            var dstOffset = pcIdx * blockSize;

            if (srcOffset + blockSize <= src.Length && dstOffset + blockSize <= dst.Length)
                Array.Copy(src, srcOffset, dst, dstOffset, blockSize);
        }

        return dst;
    }

    /// <summary>
    ///     Calculate PC block index from Xbox block position.
    ///     Based on empirical mapping analysis from block-by-block comparison.
    ///     The tiling operates on 8×2 block groups with this transformation:
    ///     Xbox row 0 (Y=0 within group):
    ///     Xbox X: 0  1  2  3  4  5  6  7
    ///     PC X:   0  1  0  1  2  3  2  3
    ///     PC Y:   0  0  1  1  0  0  1  1
    ///     Xbox row 1 (Y=1 within group):
    ///     Xbox X: 0  1  2  3  4  5  6  7
    ///     PC X:   4  5  4  5  6  7  6  7
    ///     PC Y:   0  0  1  1  0  0  1  1
    ///     Formula:
    ///     pcLocalX = localY * 4 + (localX / 4) * 2 + (localX % 2)
    ///     pcLocalY = (localX / 2) % 2
    ///     This is a small bit permutation (bit swizzle) within the 8x2 group.
    /// </summary>
    private static int GetPcBlockIndex(int xboxX, int xboxY, int blocksX)
    {
        // Determine which 8×2 group this block belongs to
        var groupX = xboxX / 8;
        var groupY = xboxY / 2;

        // Position within the 8×2 group
        var localX = xboxX % 8;
        var localY = xboxY % 2;

        // Apply the transformation formula derived from block mapping analysis.
        // Equivalent form:
        // pcLocalX = localY * 4 + (localX / 4) * 2 + (localX % 2)
        // pcLocalY = (localX / 2) % 2
        var pcLocalX = (localY << 2) | ((localX >> 2) << 1) | (localX & 1);
        var pcLocalY = (localX >> 1) & 1;

        // Convert back to absolute PC coordinates
        var pcX = groupX * 8 + pcLocalX;
        var pcY = groupY * 2 + pcLocalY;

        return pcY * blocksX + pcX;
    }

    // Some of this code build from analyzing NiXenonSourceTextureData::CreateFromDDXFile, some from file analysis
    private void ConvertDdxToDds(BinaryReader reader, string outputPath, ConversionOptions options, uint magic)
    {
        _currentOptions = options;

        _ = reader.ReadByte(); // priorityL
        _ = reader.ReadByte(); // priorityC
        _ = reader.ReadByte(); // priorityH

        // Read version
        var version = reader.ReadUInt16();

        if (version < 3) throw new NotSupportedException($"DDX version {version} is not supported. Need version >= 3");

        // After reading version, we're at offset 0x09
        // D3DTexture header (52 bytes) starts at offset 0x08 (1 byte back)
        // We need to go back 1 byte and read 52 bytes to get to 0x3C
        reader.BaseStream.Seek(-1, SeekOrigin.Current); // Go back 1 byte to 0x08

        var textureHeader = reader.ReadBytes(52); // Read 0x08 to 0x3C

        // Now we're at 0x3C, skip to 0x44 (8 more bytes)
        reader.ReadBytes(8);

        // Parse the D3DTexture header to extract dimensions and format from Format dwords
        var texture =
            ParseD3DTextureHeaderWithDimensions(textureHeader, out var width, out var height);

        if (_verboseLogging) Console.WriteLine($"Dimensions from D3D texture header: {width}x{height}");

        // For 3XDO files, the texture data starts immediately after the header at offset 0x44
        // There are no separate size fields - just read all remaining data
        var currentPos = reader.BaseStream.Position;
        var fileSize = reader.BaseStream.Length;
        var remainingBytes = (uint)(fileSize - currentPos);

        // Read all texture data
        var mainData = reader.ReadBytes((int)remainingBytes);

        // Calculate total expected size: atlas (2x resolution) + linear mips
        // Use ActualFormat instead of DataFormat for correct size calculation
        var atlasSize = (uint)TextureUtilities.CalculateMipSize(width, height, texture.ActualFormat);

        // Decompress all chunks in sequence
        var compressedData = mainData;
        var decompressedChunks = new List<byte[]>();
        var totalConsumed = 0;

        // Try to decompress first chunk
        var firstChunk = DecompressXMemCompress(compressedData, atlasSize, out var firstChunkCompressedSize);
        if (_verboseLogging)
            Console.WriteLine(
                $"Chunk 1: consumed {firstChunkCompressedSize} compressed bytes, got {firstChunk.Length} decompressed bytes");

        decompressedChunks.Add(firstChunk);
        totalConsumed += firstChunkCompressedSize;

        // Try to decompress additional chunks until we run out of data
        while (totalConsumed < compressedData.Length)
        {
            var offset = totalConsumed;
            var remainingSize = compressedData.Length - offset;

            if (remainingSize < 10) // Need at least some bytes for a valid XMemCompress chunk
                break;

            if (_verboseLogging)
                Console.WriteLine(
                    $"Attempting to decompress chunk {decompressedChunks.Count + 1} at offset {offset} ({remainingSize} bytes remaining)");

            try
            {
                var remainingCompressed = new byte[remainingSize];
                Array.Copy(compressedData, offset, remainingCompressed, 0, remainingSize);

                var chunk = DecompressXMemCompress(remainingCompressed, atlasSize, out var chunkCompressedSize);
                if (_verboseLogging)
                    Console.WriteLine(
                        $"Chunk {decompressedChunks.Count + 1}: consumed {chunkCompressedSize} compressed bytes, got {chunk.Length} decompressed bytes");

                decompressedChunks.Add(chunk);
                totalConsumed += chunkCompressedSize;

                if (chunkCompressedSize == 0) break; // Avoid infinite loop if nothing was consumed
            }
            catch (Exception ex)
            {
                if (_verboseLogging)
                    Console.WriteLine($"Failed to decompress chunk {decompressedChunks.Count + 1}: {ex.Message}");

                break;
            }
        }

        // Combine all decompressed chunks
        var totalDecompressed = decompressedChunks.Sum(c => c.Length);
        mainData = new byte[totalDecompressed];
        var writeOffset = 0;
        for (var i = 0; i < decompressedChunks.Count; i++)
        {
            Array.Copy(decompressedChunks[i], 0, mainData, writeOffset, decompressedChunks[i].Length);
            writeOffset += decompressedChunks[i].Length;
        }

        if (_verboseLogging)
            Console.WriteLine(
                $"Combined {decompressedChunks.Count} chunks = {mainData.Length} bytes total (consumed {totalConsumed}/{compressedData.Length} compressed bytes)");

        // Save raw combined data for analysis (optional)
        if (options != null && options.SaveRaw)
        {
            var rawPath = outputPath.Replace(".dds", "_raw.bin");
            File.WriteAllBytes(rawPath, mainData);
            if (_verboseLogging) Console.WriteLine($"Saved raw combined data to {rawPath}");
        }

        // Calculate expected main surface size with detected dimensions
        var mainSurfaceSize = (uint)TextureUtilities.CalculateMipSize(width, height, texture.ActualFormat);

        byte[] linearData = [];

        // Check if we have two chunks or one chunk
        // Two chunk format can be:
        // 1. Exactly 2 decompressed chunks (use actual decompressed boundaries)
        // 2. Main surface + smaller mip atlas (for large textures like 1024x1024)
        var isTwoChunkFormat = false;
        uint chunk1Size = 0;
        uint chunk2Size = 0;

        // When we have exactly 2 decompressed chunks, use their actual sizes
        // This is important because Xbox 360 tiling may pad textures (e.g., 128x64 stored as 128x128)
        if (decompressedChunks.Count == 2)
        {
            isTwoChunkFormat = true;
            chunk1Size = (uint)decompressedChunks[0].Length;
            chunk2Size = (uint)decompressedChunks[1].Length;
            if (_verboseLogging)
                Console.WriteLine(
                    $"Using actual decompressed chunk boundaries: chunk1={chunk1Size}, chunk2={chunk2Size}");
        }
        else if (decompressedChunks.Count > 1 && mainData.Length == atlasSize * 2)
        {
            // Small texture: two equal-sized chunks (only if we actually have multiple chunks)
            isTwoChunkFormat = true;
            chunk1Size = atlasSize;
            chunk2Size = atlasSize;
        }
        else if (decompressedChunks.Count > 1 && mainData.Length > mainSurfaceSize)
        {
            // Only treat as two-chunk format if we actually decompressed multiple chunks
            var remainingSize = mainData.Length - (int)mainSurfaceSize;

            isTwoChunkFormat = true;
            chunk1Size = (uint)remainingSize;
            chunk2Size = mainSurfaceSize;
            if (_verboseLogging)
                Console.WriteLine($"Detected two-chunk format: atlas={chunk1Size} + main={chunk2Size}");
        }

        if (isTwoChunkFormat)
        {
            // Two-chunk format
            // chunk1Size and chunk2Size were set by detection above
            // For all textures: chunk1 (smaller) = mip atlas, chunk2 (larger) = main surface
            if (_verboseLogging) Console.WriteLine($"Two-chunk format confirmed ({mainData.Length} bytes)");

            // Determine block size for atlas dimension calculation
            var blockSize = texture.ActualFormat switch
            {
                // DXT1
                0x52 or 0x7B or 0x82 or 0x86 or 0x12 => 8,
                // DXT3
                0x53 or 0x54 or 0x71 or 0x88 or 0x13 or 0x14 => 16,
                _ => 16
            };
            var chunk1 = new byte[chunk1Size];
            var chunk2 = new byte[chunk2Size];
            Array.Copy(mainData, 0, chunk1, 0, chunk1Size);
            Array.Copy(mainData, chunk1Size, chunk2, 0, chunk2Size);

            // Determine atlas dimensions
            int atlasWidth, atlasHeight;

            if (width <= 256 && height <= 256)
            {
                // Small texture: by default assume atlas same size as main
                atlasWidth = width;
                atlasHeight = height;

                // But sometimes the mip atlas is larger than the main surface (e.g. 128x128 textures
                // where the atlas is 256x128 or 256x192 etc.). If chunk1 size doesn't match a same-sized
                // atlas, attempt to derive atlas dimensions from the decompressed size.
                var chunk1Blocks = (int)chunk1Size / blockSize;
                var baseBlocks = width / 4 * (height / 4);
                if (chunk1Blocks > baseBlocks)
                {
                    // Find a reasonable width in blocks (>= main texture width blocks) that divides chunk1Blocks
                    var widthBlocksBase = Math.Max(1, width / 4);
                    var chosenWidthBlocks = widthBlocksBase;
                    for (var wb = widthBlocksBase; wb <= 128; wb++) // limit search to 128 blocks (512px)
                    {
                        if (chunk1Blocks % wb != 0) continue;

                        var hb = chunk1Blocks / wb;
                        var candidateW = wb * 4;
                        var candidateH = hb * 4;
                        // require candidate dimensions to be multiples of 4 and at least main dims
                        if (candidateW >= width && candidateH >= height && candidateW <= 2048 && candidateH <= 2048)
                        {
                            chosenWidthBlocks = wb;
                            break;
                        }
                    }

                    atlasWidth = chosenWidthBlocks * 4;
                    atlasHeight = chunk1Blocks / chosenWidthBlocks * 4;
                }

                // Special-case: if first chunk size exactly equals 256x256 atlas blocks, prefer that.
                var blocks256 = 256 / 4 * (256 / 4); // 64*64
                if (chunk1Blocks == blocks256 && width == 128 && height == 128)
                {
                    atlasWidth = 256;
                    atlasHeight = 256;
                }
            }
            else
            {
                // Large texture: calculate atlas dimensions from actual chunk1 size
                // The atlas contains mips, determine dimensions by reverse-calculating from size

                // For non-square textures, atlas width is typically 5/8 of main width
                // For square textures, atlas is same dimensions as main
                if (width == height)
                {
                    // Square: atlas same as main (1024x1024 for 1024x1024 texture)
                    atlasWidth = width;
                    atlasHeight = height;
                }
                else if (width > height)
                {
                    // Wider than tall: e.g., 512x256 -> atlas 320x256
                    atlasWidth = width * 5 / 8;
                    atlasHeight = height;
                }
                else
                {
                    // Taller than wide: atlas width * 5/8, height same
                    atlasWidth = width;
                    atlasHeight = height * 5 / 8;
                }
            }

            if (_verboseLogging)
                Console.WriteLine(
                    $"Untiling chunk1 ({chunk1Size} bytes) as atlas {atlasWidth}x{atlasHeight} and chunk2 ({chunk2Size} bytes) as main {width}x{height}");

            byte[] untiledAtlas;
            var untiledMain = UnswizzleDXTTexture(chunk2, width, height, texture.ActualFormat);

            // If this is a 3XDR (engine-tiled) file, assemble the atlas using the engine tables
            if (options != null && options.NoUntileAtlas)
            {
                // Debug option - skip untile/unswizzle and treat the chunk as linear
                if (_verboseLogging) Console.WriteLine("NoUntileAtlas option set - skipping unswizzle for atlas");

                untiledAtlas = chunk1;
            }
            else if (magic == MAGIC_3XDR)
            {
                var assembled =
                    ApplyEngineTilingFor3xdr(chunk1, atlasWidth, atlasHeight, texture.ActualFormat, texture);
                untiledAtlas = UnswizzleDXTTexture(assembled, atlasWidth, atlasHeight, texture.ActualFormat);
            }
            else
            {
                // 3XDO - existing path: unswizzle the chunk as a whole
                untiledAtlas = UnswizzleDXTTexture(chunk1, atlasWidth, atlasHeight, texture.ActualFormat);
            }

            if (_verboseLogging)
                Console.WriteLine($"Untiled both chunks to {untiledAtlas.Length} and {untiledMain.Length} bytes");

            // Save the untiled atlas as a DDS file for inspection (optional)
            if (options != null && options.SaveAtlas)
            {
                var atlasPath = outputPath.Replace(".dds", "_atlas.dds");
                var atlasTexture = new D3DTextureInfo
                {
                    Width = (ushort)atlasWidth,
                    Height = (ushort)atlasHeight,
                    Format = texture.Format,
                    ActualFormat = texture.ActualFormat,
                    DataFormat = texture.DataFormat,
                    MipLevels = 1
                };
                WriteDdsFile(atlasPath, atlasTexture, untiledAtlas);
                if (_verboseLogging) Console.WriteLine($"Saved untiled atlas to {atlasPath}");
            }

            // Extract mips from atlas
            var mips = UnpackMipAtlas(untiledAtlas, new MipAtlasParams(
                atlasWidth, atlasHeight, texture.ActualFormat,
                (int)texture.Width, (int)texture.Height,
                outputPath, options?.SaveMips ?? false));
            if (_verboseLogging) Console.WriteLine($"Extracted {mips.Length} bytes of mips from atlas");

            // Crop main surface to actual texture size (Xbox 360 often pads to power-of-2)
            var actualMainSize = TextureUtilities.CalculateMipSize(width, height, texture.ActualFormat);
            var croppedMain = untiledMain.Length > actualMainSize
                ? untiledMain[..actualMainSize]
                : untiledMain;

            linearData = new byte[croppedMain.Length + mips.Length];
            Array.Copy(croppedMain, 0, linearData, 0, croppedMain.Length);
            Array.Copy(mips, 0, linearData, croppedMain.Length, mips.Length);

            if (_verboseLogging)
                Console.WriteLine(
                    $"Combined {croppedMain.Length} bytes main surface + {mips.Length} bytes mips = {linearData.Length} total");
        }
        else
        {
            // Single-chunk format: could be main surface only, or main + partial mips
            if (_verboseLogging)
                Console.WriteLine(
                    $"Single-chunk format detected ({mainData.Length} bytes, expected {mainSurfaceSize} for {width}x{height})");

            // Check if data might be two square chunks instead of one rectangular
            // E.g., header says 512x256 but data is actually 2x 256x256
            // This pattern is only used for format 0x71 (normal maps with mip atlas)
            var halfSize = mainData.Length / 2;
            var squareSize = (int)Math.Sqrt((double)halfSize / 16) * 4;
            var couldBeTwoSquares = texture.ActualFormat == 0x71 &&
                                    squareSize * squareSize / 16 * 16 == halfSize &&
                                    width == squareSize * 2 && height == squareSize;

            // Check if we have more data than just the main surface (partial mips)
            if (mainData.Length > mainSurfaceSize)
            {
                // Has partial mips
                if (_verboseLogging) Console.WriteLine($"Detected extra data: {mainData.Length} > {mainSurfaceSize}");

                // Check if this is large texture (>=512) with sequential mips
                if (width >= 512 && height >= 512)
                {
                    if (_verboseLogging)
                        Console.WriteLine("Large texture detected - treating extra as sequential mip data");

                    // Untile main surface
                    var mainSurfaceTiled = new byte[mainSurfaceSize];
                    Array.Copy(mainData, 0, mainSurfaceTiled, 0, (int)mainSurfaceSize);
                    var mainSurfaceUntiled =
                        UnswizzleDXTTexture(mainSurfaceTiled, width, height, texture.ActualFormat);

                    // Process remaining mips sequentially
                    var remainingSize = mainData.Length - (int)mainSurfaceSize;
                    var remainingData = new byte[remainingSize];
                    Array.Copy(mainData, (int)mainSurfaceSize, remainingData, 0, remainingSize);

                    // Calculate how many mip levels we have
                    var mipLevels = 1;
                    var processedMipData = 0;
                    var mipWidth = width / 2;
                    var mipHeight = height / 2;

                    while (mipWidth >= 4 && mipHeight >= 4 && processedMipData < remainingSize)
                    {
                        var mipSize = TextureUtilities.CalculateMipSize(mipWidth, mipHeight, texture.ActualFormat);
                        if (processedMipData + mipSize > remainingSize) break;

                        processedMipData += mipSize;
                        mipLevels++;
                        mipWidth /= 2;
                        mipHeight /= 2;
                    }

                    if (_verboseLogging)
                        Console.WriteLine($"Found {mipLevels} mip levels (main + {mipLevels - 1} mips)");

                    // Untile each mip sequentially
                    var mipDataList = new List<byte[]> { mainSurfaceUntiled };

                    var mipOffset = 0;
                    mipWidth = width / 2;
                    mipHeight = height / 2;
                    for (var i = 1; i < mipLevels; i++)
                    {
                        var mipSize = TextureUtilities.CalculateMipSize(mipWidth, mipHeight, texture.ActualFormat);
                        var mipTiled = new byte[mipSize];
                        Array.Copy(remainingData, mipOffset, mipTiled, 0, mipSize);
                        var mipUntiled = UnswizzleDXTTexture(mipTiled, mipWidth, mipHeight, texture.ActualFormat);
                        mipDataList.Add(mipUntiled);
                        mipOffset += mipSize;
                        mipWidth /= 2;
                        mipHeight /= 2;
                    }

                    // Combine all mips
                    var totalSize = mipDataList.Sum(m => m.Length);
                    linearData = new byte[totalSize];
                    var offset = 0;
                    foreach (var mip in mipDataList)
                    {
                        Array.Copy(mip, 0, linearData, offset, mip.Length);
                        offset += mip.Length;
                    }

                    texture.MipLevels = (byte)mipLevels;
                    if (_verboseLogging) Console.WriteLine($"Set MipLevels to {texture.MipLevels}");
                }
                else if (decompressedChunks.Count == 1 && mainData.Length == mainSurfaceSize * 2)
                {
                    // Single chunk with exactly 2× main surface size - this is main + mip atlas
                    // The first half (mainSurfaceSize bytes) is the main surface
                    var mainSurfaceBytes = (int)mainSurfaceSize;

                    if (_verboseLogging)
                        Console.WriteLine("Single chunk with 2x main surface - extracting first half as main");

                    // Extract main surface portion and untile it
                    var mainSurfaceData = mainData[..mainSurfaceBytes];
                    var processedMain = UnswizzleDXTTexture(mainSurfaceData, width, height, texture.ActualFormat);

                    // Extract atlas portion and untile it
                    var atlasData = mainData[mainSurfaceBytes..];
                    var processedAtlas = UnswizzleDXTTexture(atlasData, width, height, texture.ActualFormat);

                    if (_verboseLogging)
                        Console.WriteLine(
                            $"Processed main: {processedMain.Length} bytes, atlas: {processedAtlas.Length} bytes");

                    var mips = UnpackMipAtlas(processedAtlas, new MipAtlasParams(
                        width, height, texture.ActualFormat,
                        width, height, outputPath, options?.SaveMips ?? false));
                    if (_verboseLogging) Console.WriteLine($"Extracted {mips.Length} bytes of mips from atlas");

                    linearData = new byte[processedMain.Length + mips.Length];
                    Array.Copy(processedMain, 0, linearData, 0, processedMain.Length);
                    Array.Copy(mips, 0, linearData, processedMain.Length, mips.Length);

                    // Count mip levels
                    var mipLevels = 1;
                    var mw = width / 2;
                    var mh = height / 2;
                    var mipDataSize = 0;
                    while (mw >= 4 && mh >= 4 && mipDataSize < mips.Length)
                    {
                        mipDataSize += TextureUtilities.CalculateMipSize(mw, mh, texture.ActualFormat);
                        mipLevels++;
                        mw /= 2;
                        mh /= 2;
                    }

                    texture.MipLevels = (byte)mipLevels;
                    if (_verboseLogging)
                        Console.WriteLine(
                            $"Combined {processedMain.Length} bytes main + {mips.Length} bytes mips, MipLevels={texture.MipLevels}");
                }
                else
                {
                    // Small texture - might use horizontal split for 256x256
                    if (_verboseLogging) Console.WriteLine("Attempting horizontal split for small texture");

                    // The chunks might be split horizontally - try to determine split point
                    // For a 256x256 texture split into 192x256 + 64x256:
                    var chunk1Width = 192;
                    var chunk2Width = 64;
                    var chunkHeight = 256;

                    var horizontalChunk1Size = TextureUtilities.CalculateMipSize(chunk1Width, chunkHeight, texture.ActualFormat);
                    var horizontalChunk2Size = mainData.Length - horizontalChunk1Size;

                    if (_verboseLogging)
                        Console.WriteLine(
                            $"Trying horizontal split: {chunk1Width}x{chunkHeight} ({horizontalChunk1Size} bytes) + {chunk2Width}x{chunkHeight} ({horizontalChunk2Size} bytes)");

                    if (horizontalChunk1Size + horizontalChunk2Size == mainData.Length && horizontalChunk2Size > 0)
                    {
                        var chunk1Tiled = new byte[horizontalChunk1Size];
                        var chunk2Tiled = new byte[horizontalChunk2Size];
                        Array.Copy(mainData, 0, chunk1Tiled, 0, horizontalChunk1Size);
                        Array.Copy(mainData, horizontalChunk1Size, chunk2Tiled, 0, horizontalChunk2Size);

                        var chunk1Untiled =
                            UnswizzleDXTTexture(chunk1Tiled, chunk1Width, chunkHeight, texture.ActualFormat);
                        var chunk2Untiled =
                            UnswizzleDXTTexture(chunk2Tiled, chunk2Width, chunkHeight, texture.ActualFormat);

                        if (_verboseLogging)
                            Console.WriteLine($"Untiled chunks: {chunk1Untiled.Length} + {chunk2Untiled.Length} bytes");

                        // Now we need to interleave these horizontally to form 256x256
                        // chunk1 is left 192 pixels, chunk2 is right 64 pixels
                        linearData = InterleaveHorizontalChunks(chunk1Untiled, chunk2Untiled, chunk1Width, chunk2Width,
                            chunkHeight, texture.ActualFormat);
                        if (_verboseLogging) Console.WriteLine($"Interleaved to {linearData.Length} bytes");

                        texture.MipLevels = 1;
                        if (_verboseLogging) Console.WriteLine($"Set MipLevels to {texture.MipLevels}");
                    }
                    else
                    {
                        // Fallback: try untiling just the main surface portion
                        if (_verboseLogging)
                            Console.WriteLine("Horizontal split didn't match, trying simple split at mainSurfaceSize");

                        var mainSurfaceTiled = new byte[mainSurfaceSize];
                        Array.Copy(mainData, 0, mainSurfaceTiled, 0, (int)mainSurfaceSize);

                        var mainSurfaceUntiled =
                            UnswizzleDXTTexture(mainSurfaceTiled, width, height, texture.ActualFormat);
                        if (_verboseLogging)
                            Console.WriteLine($"Untiled main surface: {mainSurfaceUntiled.Length} bytes");

                        // The remaining data might be packed mips - try to extract them
                        var remainingSize = mainData.Length - (int)mainSurfaceSize;
                        if (_verboseLogging)
                            Console.WriteLine($"Remaining data: {remainingSize} bytes (might be packed mips)");

                        // For a 128x128 mip: 16384 bytes
                        // Check if we have exactly one mip's worth
                        var expectedMip1Size = TextureUtilities.CalculateMipSize(width / 2, height / 2, texture.ActualFormat);
                        if (remainingSize == expectedMip1Size)
                        {
                            if (_verboseLogging)
                                Console.WriteLine("Remaining data matches 128x128 mip size, extracting...");

                            var mipTiled = new byte[remainingSize];
                            Array.Copy(mainData, (int)mainSurfaceSize, mipTiled, 0, remainingSize);

                            var mipUntiled =
                                UnswizzleDXTTexture(mipTiled, width / 2, height / 2, texture.ActualFormat);
                            if (_verboseLogging) Console.WriteLine($"Untiled mip: {mipUntiled.Length} bytes");

                            linearData = new byte[mainSurfaceUntiled.Length + mipUntiled.Length];
                            Array.Copy(mainSurfaceUntiled, 0, linearData, 0, mainSurfaceUntiled.Length);
                            Array.Copy(mipUntiled, 0, linearData, mainSurfaceUntiled.Length, mipUntiled.Length);
                            texture.MipLevels = 2;
                        }
                        else
                        {
                            // Don't know the layout, just use main surface
                            if (_verboseLogging)
                                Console.WriteLine("WARNING: Unknown mip layout, using only main surface");

                            linearData = mainSurfaceUntiled;
                            texture.MipLevels = 1;
                        }

                        if (_verboseLogging) Console.WriteLine($"Set MipLevels to {texture.MipLevels}");
                    }
                }
            }
            else if (mainData.Length < mainSurfaceSize)
            {
                // Data is smaller than expected - this might be an atlas-only chunk from memory carving
                if (_verboseLogging)
                    Console.WriteLine(
                        $"WARNING: Data size smaller than expected: {mainData.Length} < {mainSurfaceSize}");

                // Check if this looks like an atlas chunk (roughly half of full texture size with mips)
                // Atlas typically contains mip levels 1+ packed together
                var estimatedAtlasSize =
                    TextureUtilities.CalculateMipSize(width, height, texture.ActualFormat); // Size of one surface at this resolution
                var isLikelyAtlas = mainData.Length >= estimatedAtlasSize / 4 && mainData.Length <= estimatedAtlasSize;

                if (isLikelyAtlas && width >= 128 && height >= 128)
                {
                    // Treat as atlas-only - try to extract mips from it
                    if (_verboseLogging)
                        Console.WriteLine(
                            $"Detected atlas-only data ({mainData.Length} bytes) - attempting mip extraction");

                    // Try to find atlas dimensions that fit the data
                    int atlasWidth = width;
                    int atlasHeight = height;

                    // Common atlas sizes for different main texture sizes:
                    // 512x512 main -> 512x512 atlas (131072 bytes for DXT5)
                    // 256x256 main -> 256x256 atlas (32768 bytes for DXT5)
                    // 1024x1024 main -> 1024x1024 atlas (524288 bytes for DXT5)
                    var expectedAtlasDataSize = TextureUtilities.CalculateMipSize(width, height, texture.ActualFormat);

                    if (mainData.Length == expectedAtlasDataSize)
                    {
                        atlasWidth = width;
                        atlasHeight = height;
                    }
                    else if (mainData.Length == expectedAtlasDataSize / 2)
                    {
                        // Half-size atlas - might be 512x256 for 512x512 texture
                        atlasWidth = width;
                        atlasHeight = height / 2;
                    }
                    else if (mainData.Length == expectedAtlasDataSize / 4)
                    {
                        // Quarter-size - smaller atlas
                        atlasWidth = width / 2;
                        atlasHeight = height / 2;
                    }

                    if (_verboseLogging) Console.WriteLine($"Using atlas dimensions: {atlasWidth}x{atlasHeight}");

                    // Untile the atlas
                    var untiledAtlas = UnswizzleDXTTexture(mainData, atlasWidth, atlasHeight, texture.ActualFormat);
                    if (_verboseLogging) Console.WriteLine($"Untiled atlas to {untiledAtlas.Length} bytes");

                    // Xbox 360 mip atlas layout for a WxH texture:
                    // The atlas is typically W x H/2 and contains mip levels 1+ (not the main surface)
                    // The largest mip (W/2 x H/2) is stored SPLIT:
                    //   - Top half (W/2 x H/4) in top-left of atlas
                    //   - Bottom half (W/2 x H/4) in top-right of atlas
                    // Smaller mips are arranged below:
                    //   - Mip 2 (W/4 x H/4) at (0, H/4)
                    //   - Mip 3 (W/8 x H/8) at (W/4, H/4) 
                    //   - etc.

                    var largestMipWidth = width / 2; // 256 for 512x512 texture
                    var largestMipHeight = height / 2; // 256 for 512x512 texture
                    var halfHeight = largestMipHeight / 2; // 128 - each half of the split mip

                    if (_verboseLogging)
                        Console.WriteLine(
                            $"Reconstructing mips from atlas, largest: {largestMipWidth}x{largestMipHeight}");

                    // Extract and reconstruct all mips from atlas
                    var mipDataList = new List<byte[]>();
                    var totalMipSize = 0;
                    var mipCount = 0;

                    // Mip 0 (from atlas perspective, which is mip 1 of original): W/2 x H/2, split into two halves
                    var topHalf = ExtractAtlasRegion(untiledAtlas, new AtlasRegionParams(
                        atlasWidth, atlasHeight, 0, 0, largestMipWidth, halfHeight, texture.ActualFormat));
                    var bottomHalf = ExtractAtlasRegion(untiledAtlas, new AtlasRegionParams(
                        atlasWidth, atlasHeight, largestMipWidth, 0, largestMipWidth, halfHeight, texture.ActualFormat));

                    var expectedHalfSize = TextureUtilities.CalculateMipSize(largestMipWidth, halfHeight, texture.ActualFormat);

                    if (topHalf != null && bottomHalf != null &&
                        topHalf.Length == expectedHalfSize && bottomHalf.Length == expectedHalfSize)
                    {
                        var mip0 = new byte[topHalf.Length + bottomHalf.Length];
                        Array.Copy(topHalf, 0, mip0, 0, topHalf.Length);
                        Array.Copy(bottomHalf, 0, mip0, topHalf.Length, bottomHalf.Length);
                        mipDataList.Add(mip0);
                        totalMipSize += mip0.Length;
                        mipCount++;
                        if (_verboseLogging)
                            Console.WriteLine(
                                $"  Mip 0: {largestMipWidth}x{largestMipHeight} ({mip0.Length} bytes) - reconstructed from split halves");

                        // Extract smaller mips from below the split mip
                        // They start at Y = halfHeight (H/4), arranged left to right
                        var mipX = 0;
                        var mipY = halfHeight;
                        var mipW = largestMipWidth / 2; // 128 for 512 original
                        var mipH = largestMipHeight / 2; // 128 for 512 original

                        while (mipW >= 4 && mipH >= 4 && mipX + mipW <= atlasWidth && mipY + mipH <= atlasHeight)
                        {
                            var mipData = ExtractAtlasRegion(untiledAtlas, new AtlasRegionParams(
                                atlasWidth, atlasHeight, mipX, mipY, mipW, mipH, texture.ActualFormat));

                            if (mipData != null)
                            {
                                var expectedSize = TextureUtilities.CalculateMipSize(mipW, mipH, texture.ActualFormat);
                                if (mipData.Length == expectedSize)
                                {
                                    mipDataList.Add(mipData);
                                    totalMipSize += mipData.Length;
                                    mipCount++;
                                    if (_verboseLogging)
                                        Console.WriteLine(
                                            $"  Mip {mipCount - 1}: {mipW}x{mipH} at ({mipX},{mipY}) ({mipData.Length} bytes)");
                                }
                            }

                            // Move to next mip position
                            mipX += mipW;
                            if (mipX + mipW / 2 > atlasWidth)
                            {
                                // Wrap to next row
                                mipX = 0;
                                mipY += mipH;
                            }

                            mipW /= 2;
                            mipH /= 2;
                        }

                        // Combine all mips into linear data
                        linearData = new byte[totalMipSize];
                        var offset = 0;
                        foreach (var mip in mipDataList)
                        {
                            Array.Copy(mip, 0, linearData, offset, mip.Length);
                            offset += mip.Length;
                        }

                        texture.Width = (ushort)largestMipWidth;
                        texture.Height = (ushort)largestMipHeight;
                        texture.MipLevels = (byte)mipCount;

                        if (_verboseLogging)
                            Console.WriteLine(
                                $"Reconstructed {mipCount} mip levels, total {totalMipSize} bytes (partial recovery from atlas)");
                    }
                    else
                    {
                        // Fallback: output the whole atlas as-is
                        if (_verboseLogging)
                            Console.WriteLine(
                                $"Could not reconstruct mips (topHalf={topHalf?.Length}, bottomHalf={bottomHalf?.Length}, expected={expectedHalfSize}), output full atlas");

                        linearData = untiledAtlas;
                        texture.Width = (ushort)atlasWidth;
                        texture.Height = (ushort)atlasHeight;
                        texture.MipLevels = 1;
                    }

                    // Also save the full atlas as separate file if save-mips requested
                    if (options?.SaveMips == true && outputPath != null)
                    {
                        var atlasPath = outputPath.Replace(".dds", "_atlas.dds");
                        var atlasTexture = new D3DTextureInfo
                        {
                            Width = (ushort)atlasWidth,
                            Height = (ushort)atlasHeight,
                            MipLevels = 1,
                            ActualFormat = texture.ActualFormat,
                            DataFormat = texture.DataFormat
                        };
                        WriteDdsFile(atlasPath, atlasTexture, untiledAtlas);
                        if (_verboseLogging) Console.WriteLine($"Saved full atlas to {atlasPath}");
                    }

                    if (_verboseLogging)
                        Console.WriteLine(
                            $"Output {texture.Width}x{texture.Height} with {texture.MipLevels} mips (partial recovery from atlas-only data)");
                }
                else
                {
                    // Too small or wrong dimensions - just untile what we have
                    var untiled = UnswizzleDXTTexture(mainData, width, height, texture.ActualFormat);
                    if (_verboseLogging) Console.WriteLine($"Untiled to {untiled.Length} bytes");

                    linearData = untiled;
                    texture.MipLevels = 1;
                }

                if (_verboseLogging) Console.WriteLine($"Set MipLevels to {texture.MipLevels}");
            }
            else if (mainData.Length == mainSurfaceSize * 2)
            {
                // Exactly 2x the expected size - could be:
                // 1. Two separate chunks (atlas + main) for a complete texture
                // 2. A single packed mip atlas for width/2 x height/2 (memory-carved partial texture)

                // Check if this could be a packed mip atlas for the next smaller mip level
                // For 256x256 header with 65536 bytes: could be 128x128 mip chain packed in 256x256 tiled space
                var halfWidth = width / 2;
                var halfHeight = height / 2;

                // If the data size is close to what a full mip chain for width/2 x height/2 would need
                // when packed into the full width x height tile space, treat it as a packed mip atlas
                var couldBeMipAtlasForSmallerBase = halfWidth >= 64 && halfHeight >= 64 &&
                                                    mainData.Length ==
                                                    mainSurfaceSize * 2; // 2x because it's tiled in WxH space

                if (couldBeMipAtlasForSmallerBase && _verboseLogging)
                    Console.WriteLine(
                        $"Data size {mainData.Length} could be packed mip atlas for {halfWidth}x{halfHeight} in {width}x{height} tile space");

                // Try to detect which case by untiling as full WxH and checking for mip atlas pattern
                var fullUntiled = UnswizzleDXTTexture(mainData, width, height, texture.ActualFormat);

                // Check if this looks like a packed mip atlas (base in top-left quadrant)
                // by extracting what would be the base mip and checking for reasonable content
                var treatedAsMipAtlas = false;

                if (couldBeMipAtlasForSmallerBase)
                {
                    // Try extracting mips from the full untiled data as if it were a packed atlas
                    var mipAtlasResult = TryExtractPackedMipAtlas(fullUntiled, width, height,
                        halfWidth, halfHeight, texture.ActualFormat, _verboseLogging);

                    if (mipAtlasResult != null)
                    {
                        if (_verboseLogging)
                            Console.WriteLine(
                                $"Successfully extracted packed mip atlas: {halfWidth}x{halfHeight} base with {mipAtlasResult.MipCount} mip levels");

                        linearData = mipAtlasResult.Data;
                        texture.Width = (ushort)halfWidth;
                        texture.Height = (ushort)halfHeight;
                        texture.MipLevels = (byte)mipAtlasResult.MipCount;
                        treatedAsMipAtlas = true;
                    }
                }

                if (!treatedAsMipAtlas)
                {
                    // Standard two-chunk processing
                    if (_verboseLogging) Console.WriteLine("Data is exactly 2x expected size - treating as two chunks");

                    // Split into two equal chunks
                    var chunk1TiledAlt = new byte[mainData.Length / 2];
                    var chunk2TiledAlt = new byte[mainData.Length / 2];
                    Array.Copy(mainData, 0, chunk1TiledAlt, 0, mainData.Length / 2);
                    Array.Copy(mainData, mainData.Length / 2, chunk2TiledAlt, 0, mainData.Length / 2);

                    var chunk1UntiledAlt = UnswizzleDXTTexture(chunk1TiledAlt, width, height, texture.ActualFormat);
                    var chunk2UntiledAlt = UnswizzleDXTTexture(chunk2TiledAlt, width, height, texture.ActualFormat);
                    if (_verboseLogging)
                        Console.WriteLine(
                            $"Untiled chunks to {chunk1UntiledAlt.Length} + {chunk2UntiledAlt.Length} bytes");

                    // Chunk 1 might have mips packed
                    var mipsAlt = UnpackMipAtlas(chunk1UntiledAlt, new MipAtlasParams(
                        width, height, texture.ActualFormat,
                        width, height, outputPath, options?.SaveMips ?? false));
                    if (_verboseLogging) Console.WriteLine($"Extracted {mipsAlt.Length} bytes of mips from chunk 1");

                    linearData = new byte[chunk2UntiledAlt.Length + mipsAlt.Length];
                    Array.Copy(chunk2UntiledAlt, 0, linearData, 0, chunk2UntiledAlt.Length);
                    Array.Copy(mipsAlt, 0, linearData, chunk2UntiledAlt.Length, mipsAlt.Length);

                    if (_verboseLogging)
                        Console.WriteLine(
                            $"Combined {chunk2UntiledAlt.Length} bytes main + {mipsAlt.Length} bytes mips = {linearData.Length} total");
                }
            }
            // Check if data might be two square chunks before assuming exact match
            else if (couldBeTwoSquares)
            {
                if (_verboseLogging)
                    Console.WriteLine(
                        $"Exact size match but might be two {squareSize}x{squareSize} chunks instead of {width}x{height}");

                if (_verboseLogging)
                    Console.WriteLine(
                        $"Processing as {squareSize}x{squareSize} texture with mips in chunk 1, main surface in chunk 2");

                var chunk1Tiled = new byte[halfSize];
                var chunk2Tiled = new byte[halfSize];
                Array.Copy(mainData, 0, chunk1Tiled, 0, halfSize);
                Array.Copy(mainData, halfSize, chunk2Tiled, 0, halfSize);

                var chunk1Untiled = UnswizzleDXTTexture(chunk1Tiled, squareSize, squareSize, texture.ActualFormat);
                var chunk2Untiled = UnswizzleDXTTexture(chunk2Tiled, squareSize, squareSize, texture.ActualFormat);
                if (_verboseLogging)
                    Console.WriteLine($"Untiled chunks to {chunk1Untiled.Length} + {chunk2Untiled.Length} bytes");

                // Chunk 1 has mip atlas, chunk 2 has main surface
                var mips = UnpackMipAtlas(chunk1Untiled, new MipAtlasParams(
                    squareSize, squareSize, texture.ActualFormat,
                    squareSize, squareSize, outputPath, options?.SaveMips ?? false));
                if (_verboseLogging) Console.WriteLine($"Extracted {mips.Length} bytes of mips from chunk 1");

                linearData = new byte[chunk2Untiled.Length + mips.Length];
                Array.Copy(chunk2Untiled, 0, linearData, 0, chunk2Untiled.Length);
                Array.Copy(mips, 0, linearData, chunk2Untiled.Length, mips.Length);

                texture.Width = (ushort)squareSize;
                texture.Height = (ushort)squareSize;
                texture.MipLevels = TextureUtilities.CalculateMipLevels((uint)squareSize, (uint)squareSize);
                if (_verboseLogging)
                    Console.WriteLine(
                        $"Final texture: {texture.Width}x{texture.Height} with {texture.MipLevels} mip levels");

                if (_verboseLogging)
                    Console.WriteLine(
                        $"Total data: {linearData.Length} bytes ({chunk2Untiled.Length} main + {mips.Length} mips)");
            }
            else
            {
                // Exact match - could be:
                // 1. A proper WxH texture (just untile)
                // 2. For certain sizes (256x256, 512x512): a W/2 x H/2 texture with mips packed in WxH tiled space
                //    This happens when memory-carved DDX headers overstate the size

                // Check for 128x128 texture with mip atlas
                var atlasSize128 = 24576;
                var mainSize128 = 8192;
                if (width == 128 && height == 128 && mainData.Length == atlasSize128 + mainSize128)
                {
                    if (_verboseLogging)
                        Console.WriteLine("Detected 128x128 texture with mip atlas (24576 + 8192 bytes)");

                    var chunk1 = new byte[atlasSize128];
                    var chunk2 = new byte[mainSize128];
                    Array.Copy(mainData, 0, chunk1, 0, atlasSize128);
                    Array.Copy(mainData, atlasSize128, chunk2, 0, mainSize128);

                    // Untile: atlas is 256x192, main is 128x128
                    var untiledAtlas = UnswizzleDXTTexture(chunk1, 256, 192, texture.ActualFormat);
                    var untiledMain = UnswizzleDXTTexture(chunk2, 128, 128, texture.ActualFormat);

                    if (_verboseLogging) Console.WriteLine($"Untiled atlas (256x192) to {untiledAtlas.Length} bytes");

                    if (_verboseLogging) Console.WriteLine($"Untiled main (128x128) to {untiledMain.Length} bytes");

                    // Save the untiled atlas for inspection
                    var atlasPath = outputPath.Replace(".dds", "_atlas_untiled.bin");
                    File.WriteAllBytes(atlasPath, untiledAtlas);
                    if (_verboseLogging) Console.WriteLine($"Saved untiled atlas to {atlasPath}");

                    // Extract mips from atlas
                    var mips = UnpackMipAtlas(untiledAtlas, new MipAtlasParams(
                        256, 192, texture.ActualFormat,
                        128, 128, outputPath, options?.SaveMips ?? false));
                    if (_verboseLogging) Console.WriteLine($"Extracted {mips.Length} bytes of mips from atlas");

                    // Combine main + mips
                    linearData = new byte[untiledMain.Length + mips.Length];
                    Array.Copy(untiledMain, 0, linearData, 0, untiledMain.Length);
                    Array.Copy(mips, 0, linearData, untiledMain.Length, mips.Length);

                    texture.Width = 128;
                    texture.Height = 128;
                    texture.MipLevels = TextureUtilities.CalculateMipLevels(128, 128);
                    if (_verboseLogging)
                        Console.WriteLine(
                            $"Final: 128x128 with {texture.MipLevels} mip levels, {linearData.Length} bytes total");
                }
                // Check for W/2 x H/2 packed in WxH tiled space (e.g., 128x128 in 256x256 tile space)
                else if (width >= 256 && height >= 256 && width == height)
                {
                    // Try to detect packed mip atlas for half-size base
                    var halfW = width / 2;
                    var halfH = height / 2;

                    // Untile as full WxH first
                    var fullUntiled = UnswizzleDXTTexture(mainData, width, height, texture.ActualFormat);

                    // Try extracting as packed mip atlas
                    var mipAtlasResult = TryExtractPackedMipAtlas(fullUntiled, width, height, halfW,
                        halfH, texture.ActualFormat, _verboseLogging);

                    if (mipAtlasResult is { MipCount: >= 2 } && mainData.Length != mainSurfaceSize)
                    {
                        if (_verboseLogging)
                            Console.WriteLine(
                                $"Detected packed mip atlas: {halfW}x{halfH} base with {mipAtlasResult.MipCount} mip levels in {width}x{height} tile space");

                        linearData = mipAtlasResult.Data;
                        texture.Width = (ushort)halfW;
                        texture.Height = (ushort)halfH;
                        texture.MipLevels = (byte)mipAtlasResult.MipCount;
                    }
                    else
                    {
                        // Not a packed mip atlas, just untile as-is
                        if (_verboseLogging) Console.WriteLine($"Not a packed mip atlas, untiling as {width}x{height}");

                        linearData = fullUntiled;
                        texture.MipLevels = 1;
                    }
                }
                else
                {
                    // Just untile as-is
                    var untiled = UnswizzleDXTTexture(mainData, width, height, texture.ActualFormat);
                    if (_verboseLogging) Console.WriteLine($"Untiled to {untiled.Length} bytes");

                    linearData = untiled;
                    texture.MipLevels = 1;
                    if (_verboseLogging) Console.WriteLine($"Set MipLevels to {texture.MipLevels}");
                }
            }
        }

        // Convert to DDS and write
        if (outputPath != null) WriteDdsFile(outputPath, texture, linearData);
    }

    private byte[] DecompressXMemCompress(byte[] compressedData, uint uncompressedSize, out int bytesConsumed)
    {
        var decompressedData = new byte[uncompressedSize * 2]; // Double the buffer to be safe

        using (var context = new DecompressionContext())
        {
            var compressedLen = compressedData.Length;
            var decompressedLen = decompressedData.Length;

            var result = context.Decompress(
                compressedData, 0, ref compressedLen,
                decompressedData, 0, ref decompressedLen);

            if (result != ErrorCode.None)
                throw new InvalidOperationException($"XMemCompress decompression failed: {result}");

            if (_verboseLogging) Console.WriteLine($"Decompressed {compressedLen} -> {decompressedLen} bytes");

            bytesConsumed = compressedLen;

            // Trim to actual decompressed size
            if (decompressedLen < decompressedData.Length) Array.Resize(ref decompressedData, decompressedLen);
        }

        return decompressedData;
    }

    private D3DTextureInfo ParseD3DTextureHeaderWithDimensions(byte[] header, out ushort width, out ushort height)
    {
        // Xbox 360 D3D texture header structure (52 bytes starting at file offset 0x10):
        // The header we receive here is the 52 bytes from 0x10-0x43
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

        // Our header starts at file offset 0x08
        // Dimensions dword is at formatDword[5] position = offset 16+20 = 36, stored as BIG-ENDIAN
        var dword5Bytes = new byte[4];
        Array.Copy(header, 36, dword5Bytes, 0, 4);
        Array.Reverse(dword5Bytes); // Convert from big-endian to little-endian
        var dword5 = BitConverter.ToUInt32(dword5Bytes, 0);

        // Decode size_2d structure (dimensions stored as size-1):
        // Bits 0-12: width - 1
        // Bits 13-25: height - 1  
        // Bits 26-31: stack_depth
        width = (ushort)((dword5 & 0x1FFF) + 1);
        height = (ushort)(((dword5 >> 13) & 0x1FFF) + 1);

        if (_verboseLogging) Console.WriteLine($"Parsed from Format dword_5: 0x{dword5:X8} -> {width}x{height}");

        // Now parse the rest using the old file analysis-based method
        return ParseD3DTextureHeader(header, width, height);
    }

    private D3DTextureInfo ParseD3DTextureHeader(byte[] header, ushort width, ushort height)
    {
        // Xbox 360 D3D texture header structure
        // The header we receive is 52 bytes starting at file offset 0x08
        // Dimensions are passed separately - they're extracted from Format dword_2

        var info = new D3DTextureInfo
        {
            // Set dimensions from parameters
            Width = width,
            Height = height
        };

        // Our header starts at file offset 0x08
        // The .old version read header from file 0x10 and read formatDwords from offset 8 within that header = file 0x18
        // So we need to read formatDwords from file 0x18, which is offset 0x18-0x08 = 0x10 = 16 within our header
        // Format dwords are stored as LITTLE-ENDIAN (already in Intel byte order), NO byte reversal needed

        var formatDwords = new uint[6];
        for (var i = 0; i < 6; i++) formatDwords[i] = BitConverter.ToUInt32(header, 16 + i * 4);

        var dword0 = formatDwords[0];
        var dword3 = formatDwords[3];
        var dword4 = formatDwords[4];

        // The format appears to be in DWORD[3] byte 0 (bits 0-7)
        // But for format 0x82, the actual texture format (DXT1/DXT5) is in DWORD[4] byte 3
        info.DataFormat = dword3 & 0xFF;

        // For 0x82 textures, check DWORD[4] high byte to distinguish DXT1 from DXT5
        var actualFormat = (dword4 >> 24) & 0xFF;
        if (_verboseLogging)
            Console.WriteLine(
                $"Format detection: DataFormat=0x{info.DataFormat:X2}, DWORD[4]=0x{dword4:X8}, ActualFormat=0x{actualFormat:X2}");

        info.Endian = (dword0 >> 26) & 0x3;
        info.Tiled = ((dword0 >> 19) & 1) != 0;

        // Store the actual format for untiling
        info.ActualFormat = actualFormat != 0 ? actualFormat : info.DataFormat;

        // Determine DDS format
        info.Format = TextureUtilities.GetDxgiFormat(info.ActualFormat);

        // Calculate mip levels from dimensions
        info.MipLevels = TextureUtilities.CalculateMipLevels(info.Width, info.Height);

        // Calculate main data size (before mip tail)
        info.MainDataSize = TextureUtilities.CalculateMainDataSize(info.Width, info.Height, info.ActualFormat, info.MipLevels);

        return info;
    }

    /// <summary>
    ///     Extract a rectangular region from atlas data.
    ///     Handles DXT block alignment.
    /// </summary>
    private static byte[]? ExtractAtlasRegion(byte[] atlasData, AtlasRegionParams p)
    {
        var blockSize = TextureUtilities.GetBlockSize(p.Format);
        var blockWidth = 4; // DXT block size in pixels
        var blockHeight = 4;

        // Calculate block counts
        var atlasBlocksX = (p.AtlasWidth + blockWidth - 1) / blockWidth;
        var atlasBlocksY = (p.AtlasHeight + blockHeight - 1) / blockHeight;
        var regionBlocksX = (p.RegionWidth + blockWidth - 1) / blockWidth;
        var regionBlocksY = (p.RegionHeight + blockHeight - 1) / blockHeight;
        var startBlockX = p.RegionX / blockWidth;
        var startBlockY = p.RegionY / blockHeight;

        var outputSize = regionBlocksX * regionBlocksY * blockSize;
        var output = new byte[outputSize];

        var destOffset = 0;
        for (var by = 0; by < regionBlocksY; by++)
        {
            var srcBlockY = startBlockY + by;
            if (srcBlockY >= atlasBlocksY) break;

            for (var bx = 0; bx < regionBlocksX; bx++)
            {
                var srcBlockX = startBlockX + bx;
                if (srcBlockX >= atlasBlocksX) continue;

                var srcOffset = (srcBlockY * atlasBlocksX + srcBlockX) * blockSize;

                if (srcOffset + blockSize <= atlasData.Length && destOffset + blockSize <= output.Length)
                    Array.Copy(atlasData, srcOffset, output, destOffset, blockSize);

                destOffset += blockSize;
            }
        }

        return output;
    }

    /// <summary>
    ///     Try to extract mip levels from a packed mip atlas.
    ///     The atlas has the base mip (baseWidth x baseHeight) in the top-left,
    ///     with smaller mips packed vertically in the right column.
    /// </summary>
    private static PackedMipAtlasResult? TryExtractPackedMipAtlas(byte[] untiledData, int atlasWidth, int atlasHeight,
        int baseWidth, int baseHeight, uint format, bool verbose)
    {
        // Xbox 360 packed mip atlas layout for baseWidth x baseHeight texture in atlasWidth x atlasHeight space:
        // Example for 128x128 base in 256x256 space:
        //   [128x128 base (0,0)]  [64x64 mip1 (128,0)]
        //                         [32x32 mip2 (128,64)]
        //                         [16x16 mip3 (128,96)]
        //                         [8x8 mip4 (128,112)]
        //                         [4x4 mip5 (128,120)]
        // All smaller mips are in the RIGHT column (x = baseWidth), stacked vertically

        _ = TextureUtilities.GetBlockSize(format);

        var mipDataList = new List<byte[]>();
        var totalMipSize = 0;

        // Extract base mip from top-left quadrant
        var baseMip = ExtractAtlasRegion(untiledData, new AtlasRegionParams(
            atlasWidth, atlasHeight, 0, 0, baseWidth, baseHeight, format));
        if (baseMip == null) return null;

        var expectedBaseSize = TextureUtilities.CalculateMipSize(baseWidth, baseHeight, format);
        if (baseMip.Length != expectedBaseSize)
        {
            if (verbose)
                Console.WriteLine($"Base mip size mismatch: got {baseMip.Length}, expected {expectedBaseSize}");

            return null;
        }

        mipDataList.Add(baseMip);
        totalMipSize += baseMip.Length;
        if (verbose) Console.WriteLine($"  Mip 0 (base): {baseWidth}x{baseHeight} at (0,0), {baseMip.Length} bytes");

        // Xbox 360 mip atlas layout for 128x128 base in 256x256 space:
        // [128x128 base (0,0)]    [64x64 mip1 (128,0)]
        //                         [32x32 mip2 (128,64)]
        //                         [16x16 mip3 (144,128)]
        //                         [8x8 mip4 (136,128)]
        //                         [4x4 mip5 (132,128)]
        // Note: mips 3-5 are at row 128, arranged right-to-left by size

        // Mip 1: right of base at (baseWidth, 0)
        var mip1X = baseWidth;
        var mip1Y = 0;
        var mip1W = baseWidth / 2;
        var mip1H = baseHeight / 2;

        if (mip1W >= 4 && mip1X + mip1W <= atlasWidth)
        {
            var mip1 = ExtractAtlasRegion(untiledData, new AtlasRegionParams(
                atlasWidth, atlasHeight, mip1X, mip1Y, mip1W, mip1H, format));
            if (mip1 != null && mip1.Length == TextureUtilities.CalculateMipSize(mip1W, mip1H, format))
            {
                mipDataList.Add(mip1);
                totalMipSize += mip1.Length;
                if (verbose) Console.WriteLine($"  Mip 1: {mip1W}x{mip1H} at ({mip1X},{mip1Y}), {mip1.Length} bytes");
            }
        }

        // Mip 2: at (0, baseHeight) - bottom left
        var mip2X = 0;
        var mip2Y = baseHeight;
        var mip2W = baseWidth / 4;
        var mip2H = baseHeight / 4;

        if (mip2W >= 4 && mip2X + mip2W <= atlasWidth && mip2Y + mip2H <= atlasHeight)
        {
            var mip2 = ExtractAtlasRegion(untiledData, new AtlasRegionParams(
                atlasWidth, atlasHeight, mip2X, mip2Y, mip2W, mip2H, format));
            if (mip2 != null && mip2.Length == TextureUtilities.CalculateMipSize(mip2W, mip2H, format))
            {
                mipDataList.Add(mip2);
                totalMipSize += mip2.Length;
                if (verbose) Console.WriteLine($"  Mip 2: {mip2W}x{mip2H} at ({mip2X},{mip2Y}), {mip2.Length} bytes");
            }
        }

        // Mips 3-5: at row baseHeight, arranged right-to-left by size
        // For 128x128 base: 16x16 at (144,128), 8x8 at (136,128), 4x4 at (132,128)
        // Pattern: starting at x = baseWidth + 4 (for mystery 4-wide column), sizes packed left-to-right smallest first
        // x positions: 4x4 at baseWidth+4, 8x8 at baseWidth+8, 16x16 at baseWidth+16

        var mipRow = baseHeight;

        // 16x16 mip at (baseWidth + 16, baseHeight)
        var mip3W = baseWidth / 8; // 16 for 128 base
        var mip3H = baseHeight / 8;
        var mip3X = baseWidth + mip3W; // 128 + 16 = 144
        var mip3Y = mipRow;

        if (mip3W >= 4 && mip3X + mip3W <= atlasWidth && mip3Y + mip3H <= atlasHeight)
        {
            var mip3 = ExtractAtlasRegion(untiledData, new AtlasRegionParams(
                atlasWidth, atlasHeight, mip3X, mip3Y, mip3W, mip3H, format));
            if (mip3 != null && mip3.Length == TextureUtilities.CalculateMipSize(mip3W, mip3H, format))
            {
                mipDataList.Add(mip3);
                totalMipSize += mip3.Length;
                if (verbose) Console.WriteLine($"  Mip 3: {mip3W}x{mip3H} at ({mip3X},{mip3Y}), {mip3.Length} bytes");
            }
        }

        // 8x8 mip at (baseWidth + 8, baseHeight)
        var mip4W = baseWidth / 16; // 8 for 128 base
        var mip4H = baseHeight / 16;
        var mip4X = baseWidth + mip4W; // 128 + 8 = 136
        var mip4Y = mipRow;

        if (mip4W >= 4 && mip4X + mip4W <= atlasWidth && mip4Y + mip4H <= atlasHeight)
        {
            var mip4 = ExtractAtlasRegion(untiledData, new AtlasRegionParams(
                atlasWidth, atlasHeight, mip4X, mip4Y, mip4W, mip4H, format));
            if (mip4 != null && mip4.Length == TextureUtilities.CalculateMipSize(mip4W, mip4H, format))
            {
                mipDataList.Add(mip4);
                totalMipSize += mip4.Length;
                if (verbose) Console.WriteLine($"  Mip 4: {mip4W}x{mip4H} at ({mip4X},{mip4Y}), {mip4.Length} bytes");
            }
        }

        // 4x4 mip at (baseWidth + 4, baseHeight)
        var mip5W = baseWidth / 32; // 4 for 128 base
        var mip5H = baseHeight / 32;
        var mip5X = baseWidth + mip5W; // 128 + 4 = 132
        var mip5Y = mipRow;

        if (mip5W >= 4 && mip5X + mip5W <= atlasWidth && mip5Y + mip5H <= atlasHeight)
        {
            var mip5 = ExtractAtlasRegion(untiledData, new AtlasRegionParams(
                atlasWidth, atlasHeight, mip5X, mip5Y, mip5W, mip5H, format));
            if (mip5 != null && mip5.Length == TextureUtilities.CalculateMipSize(mip5W, mip5H, format))
            {
                mipDataList.Add(mip5);
                totalMipSize += mip5.Length;
                if (verbose) Console.WriteLine($"  Mip 5: {mip5W}x{mip5H} at ({mip5X},{mip5Y}), {mip5.Length} bytes");
            }
        }

        if (mipDataList.Count < 2)
        {
            if (verbose) Console.WriteLine("Could not extract enough mip levels from packed atlas");

            return null;
        }

        // Combine all mips
        var result = new byte[totalMipSize];
        var offset = 0;
        foreach (var mip in mipDataList)
        {
            Array.Copy(mip, 0, result, offset, mip.Length);
            offset += mip.Length;
        }

        if (verbose) Console.WriteLine($"  Total: {mipDataList.Count} mip levels, {totalMipSize} bytes");

        return new PackedMipAtlasResult { Data = result, MipCount = mipDataList.Count };
    }

    private void WriteDdsFile(string outputPath, D3DTextureInfo texture, byte[] mainData)
    {
        if (_verboseLogging)
            Console.WriteLine(
                $"Writing DDS: Format=0x{texture.Format:X8}, ActualFormat=0x{texture.ActualFormat:X2}, DataFormat=0x{texture.DataFormat:X2}, MipLevels={texture.MipLevels}");

        using var writer = new BinaryWriter(File.Create(outputPath));
        // Write DDS header
        WriteDdsHeader(writer, texture);

        // Write texture data
        writer.Write(mainData);
    }

    private void WriteDdsHeader(BinaryWriter writer, D3DTextureInfo texture)
    {
        // DDS magic
        writer.Write(0x20534444); // "DDS "

        // DDS_HEADER
        writer.Write(124); // dwSize

        uint flags = 0x1 | 0x2 | 0x4 | 0x1000; // DDSD_CAPS | DDSD_HEIGHT | DDSD_WIDTH | DDSD_PIXELFORMAT
        if (texture.MipLevels > 1) flags |= 0x20000; // DDSD_MIPMAPCOUNT

        writer.Write(flags); // dwFlags
        writer.Write(texture.Height); // dwHeight
        writer.Write(texture.Width); // dwWidth

        var pitch = CalculatePitch(texture.Width, texture.ActualFormat);
        writer.Write(pitch); // dwPitchOrLinearSize

        writer.Write(0); // dwDepth
        writer.Write(texture.MipLevels); // dwMipMapCount

        // dwReserved1[11]
        for (var i = 0; i < 9; i++) writer.Write(0);

        writer.Write(0x4E41524B); // add branding "KRAN" in same location as nvidia's "NVTT"
        writer.Write(0);

        // DDS_PIXELFORMAT
        WriteDdsPixelFormat(writer, texture.Format);

        // dwCaps
        uint caps = 0x1000; // DDSCAPS_TEXTURE
        if (texture.MipLevels > 1) caps |= 0x400000 | 0x8; // DDSCAPS_MIPMAP | DDSCAPS_COMPLEX

        writer.Write(caps);
        writer.Write(0); // dwCaps2
        writer.Write(0); // dwCaps3
        writer.Write(0); // dwCaps4
        writer.Write(0); // dwReserved2
    }

    private void WriteDdsPixelFormat(BinaryWriter writer, uint fourccCode)
    {
        if (_verboseLogging) Console.WriteLine($"WriteDdsPixelFormat: fourccCode=0x{fourccCode:X8}");

        writer.Write(32); // dwSize
        writer.Write(0x4); // dwFlags = DDPF_FOURCC
        writer.Write(fourccCode); // FourCC code (DDS format: 0x31545844=DXT1, 0x35545844=DXT5)
        writer.Write(0); // dwRGBBitCount
        writer.Write(0); // dwRBitMask
        writer.Write(0); // dwGBitMask
        writer.Write(0); // dwBBitMask
        writer.Write(0); // dwABitMask
    }

    private static uint CalculatePitch(uint width, uint format)
    {
        return format switch
        {
            // DXT1
            0x12 => Math.Max(1, (width + 3) / 4) * 8,
            // DXT3
            0x13 or 0x14 => Math.Max(1, (width + 3) / 4) * 16,
            // A8R8G8B8
            0x06 => width * 4,
            // R5G6B5
            0x04 => width * 2,
            _ => width * 4
        };
    }

    private byte[] UnswizzleDXTTexture(byte[] src, int width, int height, uint format)
    {
        // Determine block size based on format
        int blockSize;
        switch (format)
        {
            case 0x52: // DXT1
            case 0x7B: // ATI1/BC4 (single channel, same block size as DXT1)
            case 0x82: // DXT1 variant
            case 0x86: // DXT1 variant
            case 0x12: // GPUTEXTUREFORMAT_DXT1
                blockSize = 8;
                break;

            case 0x53: // DXT3
            case 0x54: // DXT5
            case 0x71: // DXT5 variant (normal maps)
            case 0x88: // DXT5 variant
            case 0x13: // GPUTEXTUREFORMAT_DXT2/3
            case 0x14: // GPUTEXTUREFORMAT_DXT4/5
                blockSize = 16;
                break;

            default:
                return src; // Unknown format, return as-is
        }

        var blocksWide = width / 4;
        var blocksHigh = height / 4;
        var dst = new byte[src.Length];

        // Xbox 360 tiling algorithm from Xenia emulator
        // Bytes per pixel (log2) - for DXT blocks
        var log2Bpp = (uint)(blockSize / 4 + ((blockSize / 2) >> (blockSize / 4)));

        for (var y = 0; y < blocksHigh; y++)
        {
            var inputRowOffset = TiledOffset2DRow((uint)y, (uint)blocksWide, log2Bpp);

            for (var x = 0; x < blocksWide; x++)
            {
                var inputOffset = TiledOffset2DColumn((uint)x, (uint)y, log2Bpp, inputRowOffset);
                inputOffset >>= (int)log2Bpp;

                var srcOffset = (int)(inputOffset * blockSize);
                var dstOffset = (y * blocksWide + x) * blockSize;

                if (srcOffset + blockSize <= src.Length && dstOffset + blockSize <= dst.Length)
                {
                    // Copy block and fix endianness for each 16-bit word if the option permits
                    if (_currentOptions == null || !_currentOptions.SkipEndianSwap)
                        for (var i = 0; i < blockSize; i += 2)
                        {
                            // Xbox 360 is big-endian, swap bytes
                            dst[dstOffset + i] = src[srcOffset + i + 1];
                            dst[dstOffset + i + 1] = src[srcOffset + i];
                        }
                    else
                        // Skip swapping - just copy raw block bytes
                        for (var i = 0; i < blockSize; i++)
                            dst[dstOffset + i] = src[srcOffset + i];
                }
            }
        }

        return dst;
    }

    // Xbox 360 tiling functions from Xenia emulator
    // https://github.com/xenia-project/xenia/blob/master/src/xenia/gpu/texture_conversion.cc
    private static uint TiledOffset2DRow(uint y, uint width, uint log2Bpp)
    {
        var macro = (y / 32 * (width / 32)) << (int)(log2Bpp + 7);
        var micro = (y & 6) << 2 << (int)log2Bpp;
        return macro + ((micro & ~0xFu) << 1) + (micro & 0xF) +
               ((y & 8) << (int)(3 + log2Bpp)) + ((y & 1) << 4);
    }

    private static uint TiledOffset2DColumn(uint x, uint y, uint log2Bpp, uint baseOffset)
    {
        var macro = (x / 32) << (int)(log2Bpp + 7);
        var micro = (x & 7) << (int)log2Bpp;
        var offset = baseOffset + macro + ((micro & ~0xFu) << 1) + (micro & 0xF);
        return ((offset & ~0x1FFu) << 3) + ((offset & 0x1C0) << 2) + (offset & 0x3F) +
               ((y & 16) << 7) + (((((y & 8) >> 2) + (x >> 3)) & 3) << 6);
    }

    private static byte[] InterleaveHorizontalChunks(byte[] leftChunk, byte[] rightChunk, int leftWidth, int rightWidth,
        int height, uint format)
    {
        // Interleave two chunks horizontally to form a complete texture
        // leftChunk is leftWidth pixels wide, rightChunk is rightWidth pixels wide
        var totalWidth = leftWidth + rightWidth;
        var blockSize = format is 0x82 or 0x52 or 0x7B ? 8 : 16;

        var leftBlocksWide = leftWidth / 4;
        var rightBlocksWide = rightWidth / 4;
        var totalBlocksWide = totalWidth / 4;
        var blocksHigh = height / 4;

        var result = new byte[totalBlocksWide * blocksHigh * blockSize];

        // Copy blocks row by row
        for (var row = 0; row < blocksHigh; row++)
        {
            var dstRowOffset = row * totalBlocksWide * blockSize;
            var leftSrcRowOffset = row * leftBlocksWide * blockSize;
            var rightSrcRowOffset = row * rightBlocksWide * blockSize;

            // Copy left chunk blocks for this row
            Array.Copy(leftChunk, leftSrcRowOffset, result, dstRowOffset, leftBlocksWide * blockSize);

            // Copy right chunk blocks for this row
            Array.Copy(rightChunk, rightSrcRowOffset, result, dstRowOffset + leftBlocksWide * blockSize,
                rightBlocksWide * blockSize);
        }

        return result;
    }

    private byte[] UnpackMipAtlas(byte[] atlasData, MipAtlasParams p)
    {
        var width = p.AtlasWidth;
        var height = p.AtlasHeight;
        var format = p.Format;
        var mainWidth = p.MainWidth;
        var mainHeight = p.MainHeight;
        var outputPath = p.OutputPath;
        var saveMips = p.SaveMips;

        // Early exit for tiny textures (smaller than minimum DXT block size of 4x4)
        // These textures cannot have valid mips and don't need atlas unpacking
        if (mainWidth < 4 || mainHeight < 4 || width < 4 || height < 4)
        {
            if (_verboseLogging)
                Console.WriteLine(
                    $"UnpackMipAtlas: skipping for tiny texture ({mainWidth}x{mainHeight}), returning empty mips");

            return [];
        }

        // Determine block size based on format
        var blockSize = format switch
        {
            // DXT1
            0x52 or 0x7B or 0x82 or 0x86 or 0x12 => 8,
            // DXT3
            0x53 or 0x54 or 0x71 or 0x88 or 0x13 or 0x14 => 16,
            _ => 16 // Default to DXT5 block size
        };
        var atlasWidthInBlocks = width / 4;

        // Actual texture is half the atlas width (for square textures)
        // But for 256x192 atlas, actual texture is 128x128
        // And for 512x384 atlas, actual texture is 1024x1024
        var actualWidth = mainWidth;
        var actualHeight = mainHeight;
        var actualFromMain = false;

        // If the atlas matches the main texture width or height, assume it's a stacked atlas
        // (vertical or horizontal stacking) and keep main dimensions instead of deriving
        // from the atlas ratio (which can yield strange sizes like 128x409).
        // We also set actualFromMain=true when atlas width < mainWidth but heights match,
        // which indicates a horizontally-stacked mip atlas (e.g., 320x16 atlas for 512x16 texture).
        if ((width == mainWidth && height >= mainHeight) || (height == mainHeight && width >= mainWidth) ||
            (height == mainHeight && width < mainWidth) || (width == mainWidth && height < mainHeight))
        {
            actualWidth = mainWidth;
            actualHeight = mainHeight;
            actualFromMain = true;
        }

        // If we have a specific atlas mapping for 256x192 atlas => actual is 128x128
        // Otherwise, default to the provided mainWidth/mainHeight so we don't accidentally
        // calculate a nonsensical actual texture size (e.g., 128x409)
        // Handle special case of 256x192 atlas for 128x128 texture
        if (!actualFromMain && width == 256 && height == 192)
        {
            actualWidth = 128;
            actualHeight = 128;
        }
        // Handle special case of 512x384 atlas for 1024x1024 texture
        else if (!actualFromMain && width == 512 && height == 384)
        {
            actualWidth = 1024;
            actualHeight = 1024;
        }
        // For non-square atlases, determine the actual texture dimensions
        // 320x256 atlas -> 512x256 texture (width = atlas_width * 8/5, height = atlas_height)
        else if (!actualFromMain && width == 320 && height == 256)
        {
            actualWidth = 512;
            actualHeight = 256;
        }
        // For wider-than-tall atlases where width = 5/4 of height, actual texture is 8/5 * atlas_width
        else if (!actualFromMain && width * 4 == height * 5)
        {
            actualWidth = width * 8 / 5;
            actualHeight = height;
        }
        // For taller-than-wide atlases where height = 5/4 of width, actual texture is 8/5 * atlas_height
        else if (!actualFromMain && height * 4 == width * 5)
        {
            actualWidth = width;
            actualHeight = height * 8 / 5;
        }
        else if (width == height && mainWidth == width && mainHeight == height)
        {
            // Square atlas: assume actual texture is same dimensions (small/medium textures)
            actualWidth = width;
            actualHeight = height;
        }
        else if (!actualFromMain)
        {
            // For other non-square atlases, deduce from the ratio
            // Atlas dimensions for non-square textures seem to be: atlas_width ≈ tex_width * 5/8
            if (width > height)
            {
                // Wider than tall
                actualWidth = width * 8 / 5;
                actualHeight = height;
            }
            else
            {
                // Taller than wide
                actualWidth = width;
                actualHeight = height * 8 / 5;
            }
        }

        // Calculate total size needed for all mips linearly packed
        var mipCount = TextureUtilities.CalculateMipLevels((uint)actualWidth, (uint)actualHeight);
        var totalSize = TextureUtilities.CalculateMainDataSize((uint)actualWidth, (uint)actualHeight, format, mipCount);
        var output = new byte[totalSize];
        var outputOffset = 0;

        // Debug: log expected total size and per-mip sizes
        if (_verboseLogging)
            Console.WriteLine($"UnpackMipAtlas: expected mip count={mipCount}, totalSize={totalSize} bytes");

        var debugW = actualWidth;
        var debugH = actualHeight;
        for (var m = 0; m < mipCount; m++)
        {
            var s = TextureUtilities.CalculateMipSize(debugW, debugH, format);
            if (_verboseLogging) Console.WriteLine($"  mip {m}: {debugW}x{debugH} -> {s} bytes");

            debugW = Math.Max(1, debugW / 2);
            debugH = Math.Max(1, debugH / 2);
        }

        // Mip positions in blocks (each block is 4x4 pixels)
        // For 256x256 atlas (64x64 blocks) containing 128x128 texture (32x32 blocks):
        // For 256x192 atlas (64x48 blocks) containing 128x128 texture (32x32 blocks):
        // For 320x256 atlas (80x64 blocks) containing 512x256 texture mips:
        // For 1024x1024 atlas containing 1024x1024 texture - mips are packed within
        // User measurements (in pixels): 0,0 | 512,0 | 0,256 | 256,256 | 512,256 | 640,256 | 768,256 | 912,256 | 904,256 | 900,256 | 896,264 | 896,260
        if (_verboseLogging)
            Console.WriteLine(
                $"UnpackMipAtlas: width={width}, height={height}, actualTexture={actualWidth}x{actualHeight}, using {GetMipLayoutName(width, height)} mip layout");

        // Special handling for 1024x1024 atlas with split mips
        if (width == 1024 && height == 1024)
        {
            // Mip 0 (512x512): split into top 512x256 at (0,0) and bottom 512x256 at (512,0)
            // Extract top half
            var mip0Start = outputOffset;
            if (_verboseLogging)
                Console.WriteLine("Extracting mip 0 (split): 512x512 - top half at (0,0), bottom half at (512,0)");

            // 512/4 * 256/4 = 128 * 64 blocks for each half
            for (var by = 0; by < 64; by++)
                for (var bx = 0; bx < 128; bx++)
                {
                    var srcOffset = (by * atlasWidthInBlocks + bx) * blockSize;
                    if (srcOffset + blockSize <= atlasData.Length && outputOffset + blockSize <= output.Length)
                        Array.Copy(atlasData, srcOffset, output, outputOffset, blockSize);

                    outputOffset += blockSize;
                }

            // Extract bottom half at (512, 0) = block (128, 0)
            for (var by = 0; by < 64; by++)
                for (var bx = 0; bx < 128; bx++)
                {
                    var srcBlockX = 128 + bx;
                    var srcBlockY = by;
                    var srcOffset = (srcBlockY * atlasWidthInBlocks + srcBlockX) * blockSize;
                    if (srcOffset + blockSize <= atlasData.Length && outputOffset + blockSize <= output.Length)
                        Array.Copy(atlasData, srcOffset, output, outputOffset, blockSize);

                    outputOffset += blockSize;
                }

            // After top/bottom halves for mip 0, save mip 0 if requested
            var mip0End = outputOffset;
            if (saveMips && outputPath != null)
                try
                {
                    var mipByteCount0 = mip0End - mip0Start;
                    var mipData0 = new byte[mipByteCount0];
                    Array.Copy(output, mip0Start, mipData0, 0, mipByteCount0);
                    var mipTexture0 = new D3DTextureInfo
                    {
                        Width = 512,
                        Height = 512,
                        Format = TextureUtilities.GetDxgiFormat(format),
                        ActualFormat = format,
                        DataFormat = format,
                        MipLevels = 1
                    };
                    var mipPath0 = outputPath.Replace(".dds", "_mip0.dds");
                    WriteDdsFile(mipPath0, mipTexture0, mipData0);
                    if (_verboseLogging) Console.WriteLine($"Saved mip 0 to {mipPath0}");
                }
                catch (Exception ex)
                {
                    if (_verboseLogging) Console.WriteLine($"Failed to save mip 0: {ex.Message}");
                }

            // Remaining mips: positions from user
            // Mip 1 (256x256): split top at (0,256), bottom at (256,256)
            if (_verboseLogging)
                Console.WriteLine("Extracting mip 1 (split): 256x256 - top half at (0,256), bottom half at (256,256)");

            var mip1Start = outputOffset;
            // Top half: (0, 256) = block (0, 64), size 256x128 = 64x32 blocks
            for (var by = 0; by < 32; by++)
                for (var bx = 0; bx < 64; bx++)
                {
                    var srcOffset = ((64 + by) * atlasWidthInBlocks + bx) * blockSize;
                    if (srcOffset + blockSize <= atlasData.Length && outputOffset + blockSize <= output.Length)
                        Array.Copy(atlasData, srcOffset, output, outputOffset, blockSize);

                    outputOffset += blockSize;
                }

            // Bottom half: (256, 256) = block (64, 64)
            for (var by = 0; by < 32; by++)
                for (var bx = 0; bx < 64; bx++)
                {
                    var srcOffset = ((64 + by) * atlasWidthInBlocks + 64 + bx) * blockSize;
                    if (srcOffset + blockSize <= atlasData.Length && outputOffset + blockSize <= output.Length)
                        Array.Copy(atlasData, srcOffset, output, outputOffset, blockSize);

                    outputOffset += blockSize;
                }

            // After top/bottom halves for mip 1, save mip 1 if requested
            var mip1End = outputOffset;
            if (saveMips && outputPath != null)
                try
                {
                    var mipByteCount1 = mip1End - mip1Start;
                    var mipData1 = new byte[mipByteCount1];
                    Array.Copy(output, mip1Start, mipData1, 0, mipByteCount1);
                    var mipTexture1 = new D3DTextureInfo
                    {
                        Width = 256,
                        Height = 256,
                        Format = TextureUtilities.GetDxgiFormat(format),
                        ActualFormat = format,
                        DataFormat = format,
                        MipLevels = 1
                    };
                    var mipPath1 = outputPath.Replace(".dds", "_mip1.dds");
                    WriteDdsFile(mipPath1, mipTexture1, mipData1);
                    if (_verboseLogging) Console.WriteLine($"Saved mip 1 to {mipPath1}");
                }
                catch (Exception ex)
                {
                    if (_verboseLogging) Console.WriteLine($"Failed to save mip 1: {ex.Message}");
                }

            // Remaining non-split mips
            var remainingMips = new (int x, int y, int w, int h)[]
            {
                (512, 256, 128, 128), // Mip 2: 128x128 at (512,256)
                (640, 256, 64, 64), // Mip 3: 64x64 at (640,256)
                (768, 256, 32, 32), // Mip 4: 32x32 at (768,256)
                (912, 256, 16, 16), // Mip 5: 16x16 at (912,256)
                (904, 256, 8, 8), // Mip 6: 8x8 at (904,256)
                (900, 256, 4, 4), // Mip 7: 4x4 at (900,256)
                (896, 264, 4, 4), // Mip 8: 2x2 at (896,264) - sub-block, extract as 4x4 block
                (896, 260, 4, 4) // Mip 9: 1x1 at (896,260) - sub-block, extract as 4x4 block
            };

            for (var i = 0; i < remainingMips.Length; i++)
            {
                var (mipX, mipY, mipW, mipH) = remainingMips[i];
                var mipXInBlocks = mipX / 4;
                var mipYInBlocks = mipY / 4;
                var mipWidthInBlocks = mipW / 4;
                var mipHeightInBlocks = mipH / 4;

                if (_verboseLogging)
                    Console.WriteLine($"Extracting mip {i + 2}: {mipW}x{mipH} from atlas position ({mipX}, {mipY})");

                for (var by = 0; by < mipHeightInBlocks; by++)
                    for (var bx = 0; bx < mipWidthInBlocks; bx++)
                    {
                        var srcBlockX = mipXInBlocks + bx;
                        var srcBlockY = mipYInBlocks + by;
                        var srcOffset = (srcBlockY * atlasWidthInBlocks + srcBlockX) * blockSize;

                        if (srcOffset + blockSize <= atlasData.Length && outputOffset + blockSize <= output.Length)
                            Array.Copy(atlasData, srcOffset, output, outputOffset, blockSize);

                        outputOffset += blockSize;
                    }

                // Save each of these small non-split mips, if requested
                if (saveMips && outputPath != null)
                    try
                    {
                        var thisMipEnd = outputOffset;
                        // compute start by subtracting the mip size
                        var thisMipSize = TextureUtilities.CalculateMipSize(mipW, mipH, format);
                        var thisMipStart = Math.Max(0, thisMipEnd - thisMipSize);
                        var mdata = new byte[thisMipSize];
                        Array.Copy(output, thisMipStart, mdata, 0, thisMipSize);
                        var mtex = new D3DTextureInfo
                        {
                            Width = (uint)mipW,
                            Height = (uint)mipH,
                            Format = TextureUtilities.GetDxgiFormat(format),
                            ActualFormat = format,
                            DataFormat = format,
                            MipLevels = 1
                        };
                        var mpath = outputPath.Replace(".dds", $"_mip{i + 2}.dds");
                        WriteDdsFile(mpath, mtex, mdata);
                        if (_verboseLogging) Console.WriteLine($"Saved mip {i + 2} to {mpath}");
                    }
                    catch (Exception ex)
                    {
                        if (_verboseLogging) Console.WriteLine($"Failed to save small mip {i + 2}: {ex.Message}");
                    }
            }

            // Trim to actual extracted size
            var trimmed = new byte[outputOffset];
            Array.Copy(output, 0, trimmed, 0, outputOffset);
            return trimmed;
        }

        var mipPositions = GetMipPositions(width, height);

        // Track which atlas blocks we've consumed we can pick remaining blocks if needed
        var usedBlocks = new bool[height / 4, width / 4];

        // If this atlas is used alongside a separate main surface chunk (two-chunk format),
        // we need to adjust static mappings: remove any mapping that corresponds to the main-size
        // mip, and ensure dynamic packing starts from the next mip (half size) so the top-left
        // atlas position is the biggest mip stored in the atlas (not the main surface).
        if (actualFromMain)
        {
            var list = new List<(int x, int y, int w, int h)>(mipPositions);
            var mainBlocksW = Math.Max(1, mainWidth / 4);
            var mainBlocksH = Math.Max(1, mainHeight / 4);
            // Remove the first matching mapping that occupies the main-sized block so atlas top-left
            // refers to a smaller mip
            for (var i = 0; i < list.Count; i++)
                if (list[i].w == mainBlocksW && list[i].h == mainBlocksH)
                {
                    if (_verboseLogging)
                        Console.WriteLine(
                            "UnpackMipAtlas: removing top-level mapping for main-size mip since main surface is separate");

                    list.RemoveAt(i);
                    break;
                }

            if (list.Count < mipPositions.Length) mipPositions = [.. list];
        }

        // If our hard-coded mipPositions don't fit in the atlas, fall back to a dynamic
        // packing layout (left-to-right, top-to-bottom) using the main texture's mip chain.
        var atlasWBlocks = width / 4;
        var atlasHBlocks = height / 4;
        var mappingFits = true;
        foreach (var (x, y, w, h) in mipPositions)
            if (x + w > atlasWBlocks || y + h > atlasHBlocks)
            {
                mappingFits = false;
                break;
            }

        if (!mappingFits)
        {
            if (_verboseLogging)
                Console.WriteLine("UnpackMipAtlas: default layout doesn't fit atlas - using dynamic packing");

            var dyn = new List<(int x, int y, int w, int h)>();
            int curX = 0, curY = 0, rowH = 0;
            var stackedVertical = actualFromMain && width == mainWidth;
            var stackedHorizontal = actualFromMain && height == mainHeight;
            int mW = actualFromMain ? Math.Max(1, mainWidth / 2) : mainWidth,
                mH = actualFromMain ? Math.Max(1, mainHeight / 2) : mainHeight;

            _ = Math.Max(1, mainWidth / 4);
            var mainBlocksH = Math.Max(1, mainHeight / 4);
            var mips = (int)TextureUtilities.CalculateMipLevels((uint)mainWidth, (uint)mainHeight);
            for (var i = 0; i < mips; i++)
            {
                var mbW = Math.Max(1, (mW + 3) / 4);
                var mbH = Math.Max(1, (mH + 3) / 4);

                if (mbW > atlasWBlocks || mbH > atlasHBlocks) break; // can't place this mip

                if (stackedVertical)
                {
                    // always place at X=0 and stack vertically
                    curX = 0;
                }
                else if (stackedHorizontal)
                {
                    // always place at Y=0 and stack horizontally
                    curY = 0;
                }
                else
                {
                    if (curX + mbW > atlasWBlocks)
                    {
                        curX = 0;
                        curY += rowH;
                        rowH = 0;
                    }
                }

                if (curY + mbH > atlasHBlocks) break; // out of room

                dyn.Add((curX, curY, mbW, mbH));
                if (stackedVertical)
                    curY += mainBlocksH; // increment by one 'main height' slot to match engine layout
                else if (stackedHorizontal)
                    curX += mbW;
                else
                    curX += mbW;

                rowH = Math.Max(rowH, mbH);

                mW = Math.Max(1, mW / 2);
                mH = Math.Max(1, mH / 2);
            }

            if (dyn.Count > 0) mipPositions = [.. dyn];
        }

        for (var mipLevel = 0; mipLevel < mipPositions.Length; mipLevel++)
        {
            var (mipXInBlocks, mipYInBlocks, mipWidthInBlocks, mipHeightInBlocks) = mipPositions[mipLevel];
            var mipWidth = mipWidthInBlocks * 4;
            var mipHeight = mipHeightInBlocks * 4;

            if (mipWidth < 4 || mipHeight < 4) break; // Can't have mips smaller than DXT block size

            if (_verboseLogging)
                Console.WriteLine(
                    $"Extracting mip {mipLevel}: {mipWidth}x{mipHeight} from atlas position ({mipXInBlocks * 4}, {mipYInBlocks * 4})");

            // If the atlas is used alongside a separate main surface chunk, skip extracting the main-level mip
            if (mipWidth == mainWidth && mipHeight == mainHeight)
            {
                if (_verboseLogging)
                    Console.WriteLine(
                        $"Skipping main-size mip {mipLevel} ({mipWidth}x{mipHeight}) because main surface is separate.");

                continue;
            }

            var mipStartOffset = outputOffset; // mark where this mip starts

            // Extract this mip from the atlas
            for (var by = 0; by < mipHeightInBlocks; by++)
                for (var bx = 0; bx < mipWidthInBlocks; bx++)
                {
                    var srcBlockX = mipXInBlocks + bx;
                    var srcBlockY = mipYInBlocks + by;
                    var srcOffset = (srcBlockY * atlasWidthInBlocks + srcBlockX) * blockSize;

                    if (srcBlockX >= 0 && srcBlockX < atlasWBlocks && srcBlockY >= 0 && srcBlockY < atlasHBlocks
                        && srcOffset + blockSize <= atlasData.Length && outputOffset + blockSize <= output.Length)
                    {
                        Array.Copy(atlasData, srcOffset, output, outputOffset, blockSize);
                        usedBlocks[srcBlockY, srcBlockX] = true;
                        if (saveMips && bx == 0 && by == 0 && _verboseLogging)
                            Console.WriteLine(
                                $"Mip {mipLevel} first block srcBlock=({srcBlockX},{srcBlockY}) srcOffset={srcOffset} dstOffset={outputOffset}");
                    }

                    outputOffset += blockSize;
                }

            // After copying all blocks for the mip, write out the mip as a separate DDS if requested
            var mipEndOffset = outputOffset;
            var mipByteCount = mipEndOffset - mipStartOffset;
            if (saveMips && mipByteCount > 0 && outputPath != null)
                try
                {
                    var mipData = new byte[mipByteCount];
                    Array.Copy(output, mipStartOffset, mipData, 0, mipByteCount);
                    var mipTexture = new D3DTextureInfo
                    {
                        Width = (uint)mipWidth,
                        Height = (uint)mipHeight,
                        Format = TextureUtilities.GetDxgiFormat(format),
                        ActualFormat = format,
                        DataFormat = format,
                        MipLevels = 1
                    };

                    var mipPath = outputPath.Replace(".dds", $"_mip{mipLevel}.dds");
                    WriteDdsFile(mipPath, mipTexture, mipData);
                    if (_verboseLogging) Console.WriteLine($"Saved mip {mipLevel} to {mipPath}");
                }
                catch (Exception ex)
                {
                    if (_verboseLogging) Console.WriteLine($"Failed to save mip {mipLevel}: {ex.Message}");
                }
        }

        // If we didn't extract the full mip tail, try to fill remaining bytes from unused atlas blocks
        var mip0Size = TextureUtilities.CalculateMipSize(actualWidth, actualHeight, format);
        var desiredTailBytes = (int)totalSize - mip0Size; // bytes excluding main surface

        if (_verboseLogging)
            Console.WriteLine(
                $"UnpackMipAtlas: extracted so far {outputOffset} bytes, desired tail {desiredTailBytes} bytes");

        if (outputOffset < desiredTailBytes)
        {
            if (_verboseLogging)
                Console.WriteLine("UnpackMipAtlas: filling remaining mip tail from unused atlas blocks");

            for (var by = 0; by < height / 4 && outputOffset < desiredTailBytes; by++)
                for (var bx = 0; bx < atlasWidthInBlocks && outputOffset < desiredTailBytes; bx++)
                {
                    if (usedBlocks[by, bx]) continue;

                    var srcOffset = (by * atlasWidthInBlocks + bx) * blockSize;
                    if (srcOffset + blockSize <= atlasData.Length && outputOffset + blockSize <= output.Length)
                    {
                        Array.Copy(atlasData, srcOffset, output, outputOffset, blockSize);
                        usedBlocks[by, bx] = true;
                        outputOffset += blockSize;
                    }
                }

            if (_verboseLogging)
                Console.WriteLine(
                    $"UnpackMipAtlas: after filling, extracted {outputOffset} bytes (desired {desiredTailBytes})");
        }

        if (_verboseLogging)
            Console.WriteLine($"UnpackMipAtlas: final extracted {outputOffset} bytes (buffer {output.Length} bytes)");

        // Trim to actual extracted size
        var finalTrimmed = new byte[outputOffset];
        Array.Copy(output, 0, finalTrimmed, 0, outputOffset);
        return finalTrimmed;
    }

    // Attempt to reverse-engineer the engine's 3XDR tiling pattern, just gives different wrong output.
    private static byte[] ApplyEngineTilingFor3xdr(byte[] src, int atlasWidthPixels, int atlasHeightPixels, uint format,
        D3DTextureInfo texture)
    {
        var blockSize = format is 0x52 or 0x82 or 0x12 ? 8 : 16;
        var atlasWidthBlocks = atlasWidthPixels / 4;
        var atlasHeightBlocks = atlasHeightPixels / 4;
        var rowStrideBytes = atlasWidthBlocks * blockSize;

        var dst = new byte[rowStrideBytes * atlasHeightBlocks];

        var srcOffset = 0;

        // Map DataFormat -> a row in the tiling tables (heuristic)
        var dataFormatIndex = (int)(texture.DataFormat & 0x3F);
        var tmbRow = MapDataFormatToTmbRow(dataFormatIndex); // 0..3

        // Iterate columns (3 columns per row) and copy the rects sequentially
        for (var col = 0; col < 3; col++)
        {
            int firstRectIndex = _pFirstTilingRectsA[tmbRow, col];
            int rectCount = _pTilingRectCountsA[tmbRow, col];

            for (var r = 0; r < rectCount; r++)
            {
                var rectIdx = firstRectIndex + r;
                if (rectIdx < 0 || rectIdx >= _pTextureTileRectsA.Length) continue;

                var rect = _pTextureTileRectsA[rectIdx];

                var cBytesWide = rect.cBytesWide;
                var cLinesHigh = rect.cLinesHigh;
                var cByteOffsetX = rect.cByteOffsetX;
                var cLineOffsetY = rect.cLineOffsetY;

                var dstRowBase = cLineOffsetY * rowStrideBytes;

                for (var line = 0; line < cLinesHigh; line++)
                {
                    var dstPos = dstRowBase + line * rowStrideBytes + cByteOffsetX;
                    if (dstPos + cBytesWide <= dst.Length && srcOffset + cBytesWide <= src.Length)
                        Buffer.BlockCopy(src, srcOffset, dst, dstPos, cBytesWide);

                    srcOffset += cBytesWide;
                }
            }
        }

        return dst;
    }

    private static int MapDataFormatToTmbRow(int dataFormatIndex)
    {
        // Heuristic mapping: group formats into 4 rows
        // Row 0: DXT1-like
        // Row 1: DXT5/DXT3-like
        // Row 2: BC4/BC5-like
        // Row 3: fallback
        return dataFormatIndex switch
        {
            0x52 or 0x82 or 0x86 or 0x12 => 0,
            0x53 or 0x54 or 0x13 or 0x14 or 0x88 or 0x71 => 1,
            0x7B => 2,
            _ => 3
        };
    }

    /// <summary>
    ///     Gets the mip layout name for verbose logging.
    /// </summary>
    private static string GetMipLayoutName(int width, int height)
    {
        return (width, height) switch
        {
            (256, 192) => "256x192",
            (1024, 1024) => "1024x1024",
            (128, 320) => "128x320",
            (320, 256) => "320x256",
            _ => "calculated"
        };
    }

    /// <summary>
    ///     Gets the mip positions for a given atlas size.
    ///     Returns positions as (x, y, w, h) in blocks (4x4 pixels per block).
    /// </summary>
    private static (int x, int y, int w, int h)[] GetMipPositions(int width, int height)
    {
        return (width, height) switch
        {
            (256, 192) =>
            [
                (0, 0, 16, 16), // Mip 0: 64x64 at (0,0)
                (32, 0, 8, 8), // Mip 1: 32x32 at (128,0)
                (4, 32, 4, 4), // Mip 2: 16x16 at (16,128)
                (2, 32, 2, 2), // Mip 3: 8x8 at (8,128)
                (1, 32, 1, 1), // Mip 4: 4x4 at (4,128)
                (0, 34, 1, 1), // Mip 5: 2x2 at (0,136) - sub-block
                (0, 33, 1, 1) // Mip 6: 1x1 at (0,132) - sub-block
            ],
            (128, 320) =>
            [
                // Measured for 128x512 texture with 128x320 atlas
                // Convert to blocks (4x4 pixels per block): x/4, y/4, w/4, h/4
                (0, 0, 16, 64), // Mip: 64x256 at (0,0)
                (0, 64, 8, 16) // Mip: 32x64 at (0,256)
            ],
            (320, 256) =>
            [
                // 512x256 texture mips manually measured
                (0, 0, 64, 32), // Mip 0: 256x128 at (0,0)
                (0, 32, 32, 16), // Mip 1: 128x64 at (0,128)
                (32, 32, 16, 8), // Mip 2: 64x32 at (128,128)
                (64, 36, 8, 4), // Mip 3: 32x16 at (256,144)
                (64, 34, 4, 2), // Mip 4: 16x8 at (256,136)
                (64, 33, 2, 1), // Mip 5: 8x4 at (256,132)
                (68, 32, 1, 1), // Mip 6: 4x2 at (272,128) - sub-block
                (66, 32, 1, 1), // Mip 7: 2x1 at (264,128) - sub-block
                (65, 32, 1, 1) // Mip 8: 1x1 at (260,128) - sub-block
            ],
            _ =>
            [
                // Default 256x256 atlas for 128x128 texture
                (0, 0, 32, 32), // Mip 0: 128x128 at (0,0)
                (32, 0, 16, 16), // Mip 1: 64x64 at (128,0)
                (0, 32, 8, 8), // Mip 2: 32x32 at (0,128)
                (36, 32, 4, 4), // Mip 3: 16x16 at (144,128)
                (34, 32, 2, 2), // Mip 4: 8x8 at (136,128)
                (33, 32, 1, 1), // Mip 5: 4x4 at (132,128)
                (32, 34, 1, 1), // Mip 6: 2x2 at (128,136) - sub-block
                (32, 33, 1, 1) // Mip 7: 1x1 at (128,132) - sub-block
            ]
        };
    }

    /// <summary>
    ///     Result of packed mip atlas extraction.
    /// </summary>
    private sealed class PackedMipAtlasResult
    {
        public byte[] Data { get; set; } = [];
        public int MipCount { get; set; }
    }

    // Engine tiling tables extracted from the native CreateFromDDX implementation
    private struct TextureTileRectDef
    {
        public int cByteOffsetX;
        public int cLineOffsetY;
        public int cBytesWide;
        public int cLinesHigh;
    }
}
