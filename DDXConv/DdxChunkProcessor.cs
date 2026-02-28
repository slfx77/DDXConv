namespace DDXConv;

/// <summary>
///     Processes decompressed texture chunks (two-chunk and single-chunk formats)
///     from Xbox 360 DDX files, including atlas dimension detection, mip extraction,
///     and various layout heuristics.
/// </summary>
internal sealed class DdxChunkProcessor(bool verboseLogging)
{
    private readonly bool _verboseLogging = verboseLogging;

    /// <summary>
    ///     Delegate for unswizzling DXT texture data (Morton order to linear).
    /// </summary>
    internal required Func<byte[], int, int, uint, byte[]> UnswizzleDxtTexture { get; init; }

    /// <summary>
    ///     Delegate for unpacking mip atlas data.
    /// </summary>
    internal required Func<byte[], MipAtlasParams, byte[]> UnpackMipAtlas { get; init; }

    /// <summary>
    ///     Delegate for writing DDS files (used for atlas debug output).
    /// </summary>
    internal required Action<string, D3DTextureInfo, byte[]> WriteDdsFile { get; init; }

    /// <summary>
    ///     Process decompressed chunks into final linear texture data.
    ///     Returns the processed texture data and updates the texture info (dimensions, mip levels).
    /// </summary>
    internal byte[] ProcessChunks(
        byte[] mainData,
        List<byte[]> decompressedChunks,
        D3DTextureInfo texture,
        int width, int height,
        uint mainSurfaceSize,
        string? outputPath,
        ConversionOptions? options,
        uint magic)
    {
        byte[] linearData = [];

        // Check if we have two chunks or one chunk
        var isTwoChunkFormat = false;
        uint chunk1Size = 0;
        uint chunk2Size = 0;

        if (decompressedChunks.Count == 2)
        {
            isTwoChunkFormat = true;
            chunk1Size = (uint)decompressedChunks[0].Length;
            chunk2Size = (uint)decompressedChunks[1].Length;
            if (_verboseLogging)
                Console.WriteLine(
                    $"Using actual decompressed chunk boundaries: chunk1={chunk1Size}, chunk2={chunk2Size}");
        }
        else if (decompressedChunks.Count > 1 && mainData.Length == (uint)TextureUtilities.CalculateMipSize(width, height, texture.ActualFormat) * 2)
        {
            var atlasSize = (uint)TextureUtilities.CalculateMipSize(width, height, texture.ActualFormat);
            isTwoChunkFormat = true;
            chunk1Size = atlasSize;
            chunk2Size = atlasSize;
        }
        else if (decompressedChunks.Count > 1 && mainData.Length > mainSurfaceSize)
        {
            var remainingSize = mainData.Length - (int)mainSurfaceSize;

            isTwoChunkFormat = true;
            chunk1Size = (uint)remainingSize;
            chunk2Size = mainSurfaceSize;
            if (_verboseLogging)
                Console.WriteLine($"Detected two-chunk format: atlas={chunk1Size} + main={chunk2Size}");
        }

        if (isTwoChunkFormat)
        {
            linearData = ProcessTwoChunkFormat(mainData, texture, width, height,
                chunk1Size, chunk2Size, outputPath, options, magic);
        }
        else
        {
            linearData = ProcessSingleChunkFormat(mainData, decompressedChunks, texture, width, height,
                mainSurfaceSize, outputPath, options);
        }

        return linearData;
    }

    /// <summary>
    ///     Process two-chunk format: chunk1 = mip atlas, chunk2 = main surface.
    /// </summary>
    private byte[] ProcessTwoChunkFormat(
        byte[] mainData,
        D3DTextureInfo texture,
        int width, int height,
        uint chunk1Size, uint chunk2Size,
        string? outputPath,
        ConversionOptions? options,
        uint magic)
    {
        if (_verboseLogging) Console.WriteLine($"Two-chunk format confirmed ({mainData.Length} bytes)");

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
            atlasWidth = width;
            atlasHeight = height;

            var chunk1Blocks = (int)chunk1Size / blockSize;
            var baseBlocks = width / 4 * (height / 4);
            if (chunk1Blocks > baseBlocks)
            {
                var widthBlocksBase = Math.Max(1, width / 4);
                var chosenWidthBlocks = widthBlocksBase;
                for (var wb = widthBlocksBase; wb <= 128; wb++)
                {
                    if (chunk1Blocks % wb != 0) continue;

                    var hb = chunk1Blocks / wb;
                    var candidateW = wb * 4;
                    var candidateH = hb * 4;
                    if (candidateW >= width && candidateH >= height && candidateW <= 2048 && candidateH <= 2048)
                    {
                        chosenWidthBlocks = wb;
                        break;
                    }
                }

                atlasWidth = chosenWidthBlocks * 4;
                atlasHeight = chunk1Blocks / chosenWidthBlocks * 4;
            }

            var blocks256 = 256 / 4 * (256 / 4);
            if (chunk1Blocks == blocks256 && width == 128 && height == 128)
            {
                atlasWidth = 256;
                atlasHeight = 256;
            }
        }
        else
        {
            if (width == height)
            {
                atlasWidth = width;
                atlasHeight = height;
            }
            else if (width > height)
            {
                atlasWidth = width * 5 / 8;
                atlasHeight = height;
            }
            else
            {
                atlasWidth = width;
                atlasHeight = height * 5 / 8;
            }
        }

        if (_verboseLogging)
            Console.WriteLine(
                $"Untiling chunk1 ({chunk1Size} bytes) as atlas {atlasWidth}x{atlasHeight} and chunk2 ({chunk2Size} bytes) as main {width}x{height}");

        byte[] untiledAtlas;
        var untiledMain = UnswizzleDxtTexture(chunk2, width, height, texture.ActualFormat);

        if (options != null && options.NoUntileAtlas)
        {
            if (_verboseLogging) Console.WriteLine("NoUntileAtlas option set - skipping unswizzle for atlas");
            untiledAtlas = chunk1;
        }
        else if (magic == 0x52445833) // MAGIC_3XDR
        {
            var bs = TextureUtilities.GetBlockSize(texture.ActualFormat);
            var untiled = TextureUtilities.UntileMacroBlocks(chunk1, atlasWidth, atlasHeight, bs);
            untiledAtlas = TextureUtilities.SwapEndian16(untiled);
        }
        else
        {
            untiledAtlas = UnswizzleDxtTexture(chunk1, atlasWidth, atlasHeight, texture.ActualFormat);
        }

        if (_verboseLogging)
            Console.WriteLine($"Untiled both chunks to {untiledAtlas.Length} and {untiledMain.Length} bytes");

        if (options != null && options.SaveAtlas && outputPath != null)
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

        var mips = UnpackMipAtlas(untiledAtlas, new MipAtlasParams(
            atlasWidth, atlasHeight, texture.ActualFormat,
            (int)texture.Width, (int)texture.Height,
            outputPath, options?.SaveMips ?? false));
        if (_verboseLogging) Console.WriteLine($"Extracted {mips.Length} bytes of mips from atlas");

        var actualMainSize = TextureUtilities.CalculateMipSize(width, height, texture.ActualFormat);
        var croppedMain = untiledMain.Length > actualMainSize
            ? untiledMain[..actualMainSize]
            : untiledMain;

        var linearData = new byte[croppedMain.Length + mips.Length];
        Array.Copy(croppedMain, 0, linearData, 0, croppedMain.Length);
        Array.Copy(mips, 0, linearData, croppedMain.Length, mips.Length);

        if (_verboseLogging)
            Console.WriteLine(
                $"Combined {croppedMain.Length} bytes main surface + {mips.Length} bytes mips = {linearData.Length} total");

        return linearData;
    }

    /// <summary>
    ///     Process single-chunk format with various layout heuristics.
    /// </summary>
    private byte[] ProcessSingleChunkFormat(
        byte[] mainData,
        List<byte[]> decompressedChunks,
        D3DTextureInfo texture,
        int width, int height,
        uint mainSurfaceSize,
        string? outputPath,
        ConversionOptions? options)
    {
        byte[] linearData = [];

        if (_verboseLogging)
            Console.WriteLine(
                $"Single-chunk format detected ({mainData.Length} bytes, expected {mainSurfaceSize} for {width}x{height})");

        // Check if data might be two square chunks
        var halfSize = mainData.Length / 2;
        var squareSize = (int)Math.Sqrt((double)halfSize / 16) * 4;
        var couldBeTwoSquares = texture.ActualFormat == 0x71 &&
                                squareSize * squareSize / 16 * 16 == halfSize &&
                                width == squareSize * 2 && height == squareSize;

        if (mainData.Length > mainSurfaceSize)
        {
            linearData = ProcessOversizedSingleChunk(mainData, decompressedChunks, texture, width, height,
                mainSurfaceSize, outputPath, options);
        }
        else if (mainData.Length < mainSurfaceSize)
        {
            linearData = ProcessUndersizedSingleChunk(mainData, texture, width, height,
                mainSurfaceSize, outputPath, options);
        }
        else if (mainData.Length == mainSurfaceSize * 2)
        {
            linearData = ProcessDoubleMainSize(mainData, texture, width, height,
                mainSurfaceSize, outputPath, options);
        }
        else if (couldBeTwoSquares)
        {
            linearData = ProcessTwoSquareChunks(mainData, texture, squareSize, halfSize, outputPath, options);
        }
        else
        {
            linearData = ProcessExactSizeMatch(mainData, texture, width, height,
                mainSurfaceSize, outputPath, options);
        }

        return linearData;
    }

    private byte[] ProcessOversizedSingleChunk(
        byte[] mainData,
        List<byte[]> decompressedChunks,
        D3DTextureInfo texture,
        int width, int height,
        uint mainSurfaceSize,
        string? outputPath,
        ConversionOptions? options)
    {
        if (_verboseLogging) Console.WriteLine($"Detected extra data: {mainData.Length} > {mainSurfaceSize}");

        if (width >= 512 && height >= 512)
            return ProcessLargeTextureSequentialMips(mainData, texture, width, height, mainSurfaceSize);

        if (decompressedChunks.Count == 1 && mainData.Length == mainSurfaceSize * 2)
            return ProcessSingleChunkDoubleSize(mainData, texture, width, height, mainSurfaceSize, outputPath, options);

        return ProcessSmallTextureHorizontalSplit(mainData, texture, width, height, mainSurfaceSize, outputPath, options);
    }

    private byte[] ProcessLargeTextureSequentialMips(
        byte[] mainData, D3DTextureInfo texture, int width, int height, uint mainSurfaceSize)
    {
        if (_verboseLogging)
            Console.WriteLine("Large texture detected - treating extra as sequential mip data");

        var mainSurfaceTiled = new byte[mainSurfaceSize];
        Array.Copy(mainData, 0, mainSurfaceTiled, 0, (int)mainSurfaceSize);
        var mainSurfaceUntiled = UnswizzleDxtTexture(mainSurfaceTiled, width, height, texture.ActualFormat);

        var remainingSize = mainData.Length - (int)mainSurfaceSize;
        var remainingData = new byte[remainingSize];
        Array.Copy(mainData, (int)mainSurfaceSize, remainingData, 0, remainingSize);

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

        var mipDataList = new List<byte[]> { mainSurfaceUntiled };

        var mipOffset = 0;
        mipWidth = width / 2;
        mipHeight = height / 2;
        for (var i = 1; i < mipLevels; i++)
        {
            var mipSize = TextureUtilities.CalculateMipSize(mipWidth, mipHeight, texture.ActualFormat);
            var mipTiled = new byte[mipSize];
            Array.Copy(remainingData, mipOffset, mipTiled, 0, mipSize);
            var mipUntiled = UnswizzleDxtTexture(mipTiled, mipWidth, mipHeight, texture.ActualFormat);
            mipDataList.Add(mipUntiled);
            mipOffset += mipSize;
            mipWidth /= 2;
            mipHeight /= 2;
        }

        var totalSize = mipDataList.Sum(m => m.Length);
        var linearData = new byte[totalSize];
        var offset = 0;
        foreach (var mip in mipDataList)
        {
            Array.Copy(mip, 0, linearData, offset, mip.Length);
            offset += mip.Length;
        }

        texture.MipLevels = (byte)mipLevels;
        if (_verboseLogging) Console.WriteLine($"Set MipLevels to {texture.MipLevels}");
        return linearData;
    }

    private byte[] ProcessSingleChunkDoubleSize(
        byte[] mainData, D3DTextureInfo texture, int width, int height, uint mainSurfaceSize,
        string? outputPath, ConversionOptions? options)
    {
        var mainSurfaceBytes = (int)mainSurfaceSize;

        if (_verboseLogging)
            Console.WriteLine("Single chunk with 2x main surface - extracting first half as main");

        var mainSurfaceData = mainData[..mainSurfaceBytes];
        var processedMain = UnswizzleDxtTexture(mainSurfaceData, width, height, texture.ActualFormat);

        var atlasData = mainData[mainSurfaceBytes..];
        var processedAtlas = UnswizzleDxtTexture(atlasData, width, height, texture.ActualFormat);

        if (_verboseLogging)
            Console.WriteLine(
                $"Processed main: {processedMain.Length} bytes, atlas: {processedAtlas.Length} bytes");

        var mips = UnpackMipAtlas(processedAtlas, new MipAtlasParams(
            width, height, texture.ActualFormat,
            width, height, outputPath, options?.SaveMips ?? false));
        if (_verboseLogging) Console.WriteLine($"Extracted {mips.Length} bytes of mips from atlas");

        var linearData = new byte[processedMain.Length + mips.Length];
        Array.Copy(processedMain, 0, linearData, 0, processedMain.Length);
        Array.Copy(mips, 0, linearData, processedMain.Length, mips.Length);

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
        return linearData;
    }

    private byte[] ProcessSmallTextureHorizontalSplit(
        byte[] mainData, D3DTextureInfo texture, int width, int height, uint mainSurfaceSize,
        string? outputPath, ConversionOptions? options)
    {
        if (_verboseLogging) Console.WriteLine("Attempting horizontal split for small texture");

        var chunk1Width = 192;
        var chunk2Width = 64;
        var chunkHeight = 256;

        var horizontalChunk1Size =
            TextureUtilities.CalculateMipSize(chunk1Width, chunkHeight, texture.ActualFormat);
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

            var chunk1Untiled = UnswizzleDxtTexture(chunk1Tiled, chunk1Width, chunkHeight, texture.ActualFormat);
            var chunk2Untiled = UnswizzleDxtTexture(chunk2Tiled, chunk2Width, chunkHeight, texture.ActualFormat);

            if (_verboseLogging)
                Console.WriteLine($"Untiled chunks: {chunk1Untiled.Length} + {chunk2Untiled.Length} bytes");

            var linearData = InterleaveHorizontalChunks(chunk1Untiled, chunk2Untiled, chunk1Width, chunk2Width,
                chunkHeight, texture.ActualFormat);
            if (_verboseLogging) Console.WriteLine($"Interleaved to {linearData.Length} bytes");

            texture.MipLevels = 1;
            if (_verboseLogging) Console.WriteLine($"Set MipLevels to {texture.MipLevels}");
            return linearData;
        }

        // Fallback: try untiling just the main surface portion
        if (_verboseLogging)
            Console.WriteLine("Horizontal split didn't match, trying simple split at mainSurfaceSize");

        var mainSurfaceTiled = new byte[mainSurfaceSize];
        Array.Copy(mainData, 0, mainSurfaceTiled, 0, (int)mainSurfaceSize);

        var mainSurfaceUntiled = UnswizzleDxtTexture(mainSurfaceTiled, width, height, texture.ActualFormat);
        if (_verboseLogging)
            Console.WriteLine($"Untiled main surface: {mainSurfaceUntiled.Length} bytes");

        var remainingSize = mainData.Length - (int)mainSurfaceSize;
        if (_verboseLogging)
            Console.WriteLine($"Remaining data: {remainingSize} bytes (might be packed mips)");

        var expectedMip1Size = TextureUtilities.CalculateMipSize(width / 2, height / 2, texture.ActualFormat);
        if (remainingSize == expectedMip1Size)
        {
            if (_verboseLogging)
                Console.WriteLine("Remaining data matches 128x128 mip size, extracting...");

            var mipTiled = new byte[remainingSize];
            Array.Copy(mainData, (int)mainSurfaceSize, mipTiled, 0, remainingSize);

            var mipUntiled = UnswizzleDxtTexture(mipTiled, width / 2, height / 2, texture.ActualFormat);
            if (_verboseLogging) Console.WriteLine($"Untiled mip: {mipUntiled.Length} bytes");

            var linearData = new byte[mainSurfaceUntiled.Length + mipUntiled.Length];
            Array.Copy(mainSurfaceUntiled, 0, linearData, 0, mainSurfaceUntiled.Length);
            Array.Copy(mipUntiled, 0, linearData, mainSurfaceUntiled.Length, mipUntiled.Length);
            texture.MipLevels = 2;
            if (_verboseLogging) Console.WriteLine($"Set MipLevels to {texture.MipLevels}");
            return linearData;
        }

        if (_verboseLogging)
            Console.WriteLine("WARNING: Unknown mip layout, using only main surface");

        texture.MipLevels = 1;
        if (_verboseLogging) Console.WriteLine($"Set MipLevels to {texture.MipLevels}");
        return mainSurfaceUntiled;
    }

    private byte[] ProcessUndersizedSingleChunk(
        byte[] mainData, D3DTextureInfo texture, int width, int height,
        uint mainSurfaceSize, string? outputPath, ConversionOptions? options)
    {
        if (_verboseLogging)
            Console.WriteLine(
                $"WARNING: Data size smaller than expected: {mainData.Length} < {mainSurfaceSize}");

        var estimatedAtlasSize = TextureUtilities.CalculateMipSize(width, height, texture.ActualFormat);
        var isLikelyAtlas = mainData.Length >= estimatedAtlasSize / 4 && mainData.Length <= estimatedAtlasSize;

        if (isLikelyAtlas && width >= 128 && height >= 128)
            return ProcessAtlasOnlyData(mainData, texture, width, height, outputPath, options);

        // Too small or wrong dimensions - just untile what we have
        var untiled = UnswizzleDxtTexture(mainData, width, height, texture.ActualFormat);
        if (_verboseLogging) Console.WriteLine($"Untiled to {untiled.Length} bytes");

        texture.MipLevels = 1;
        if (_verboseLogging) Console.WriteLine($"Set MipLevels to {texture.MipLevels}");
        return untiled;
    }

    private byte[] ProcessAtlasOnlyData(
        byte[] mainData, D3DTextureInfo texture, int width, int height,
        string? outputPath, ConversionOptions? options)
    {
        if (_verboseLogging)
            Console.WriteLine(
                $"Detected atlas-only data ({mainData.Length} bytes) - attempting mip extraction");

        int atlasWidth = width;
        int atlasHeight = height;

        var expectedAtlasDataSize = TextureUtilities.CalculateMipSize(width, height, texture.ActualFormat);

        if (mainData.Length == expectedAtlasDataSize)
        {
            atlasWidth = width;
            atlasHeight = height;
        }
        else if (mainData.Length == expectedAtlasDataSize / 2)
        {
            atlasWidth = width;
            atlasHeight = height / 2;
        }
        else if (mainData.Length == expectedAtlasDataSize / 4)
        {
            atlasWidth = width / 2;
            atlasHeight = height / 2;
        }

        if (_verboseLogging) Console.WriteLine($"Using atlas dimensions: {atlasWidth}x{atlasHeight}");

        var untiledAtlas = UnswizzleDxtTexture(mainData, atlasWidth, atlasHeight, texture.ActualFormat);
        if (_verboseLogging) Console.WriteLine($"Untiled atlas to {untiledAtlas.Length} bytes");

        var largestMipWidth = width / 2;
        var largestMipHeight = height / 2;
        var halfHeight = largestMipHeight / 2;

        if (_verboseLogging)
            Console.WriteLine(
                $"Reconstructing mips from atlas, largest: {largestMipWidth}x{largestMipHeight}");

        var topHalf = DdxMipAtlasUnpacker.ExtractAtlasRegion(untiledAtlas, new AtlasRegionParams(
            atlasWidth, atlasHeight, 0, 0, largestMipWidth, halfHeight, texture.ActualFormat));
        var bottomHalf = DdxMipAtlasUnpacker.ExtractAtlasRegion(untiledAtlas, new AtlasRegionParams(
            atlasWidth, atlasHeight, largestMipWidth, 0, largestMipWidth, halfHeight,
            texture.ActualFormat));

        var expectedHalfSize =
            TextureUtilities.CalculateMipSize(largestMipWidth, halfHeight, texture.ActualFormat);

        byte[] linearData;

        if (topHalf != null && bottomHalf != null &&
            topHalf.Length == expectedHalfSize && bottomHalf.Length == expectedHalfSize)
        {
            linearData = ReconstructMipsFromSplitHalves(untiledAtlas, texture, atlasWidth, atlasHeight,
                largestMipWidth, largestMipHeight, halfHeight, topHalf, bottomHalf);
        }
        else
        {
            if (_verboseLogging)
                Console.WriteLine(
                    $"Could not reconstruct mips (topHalf={topHalf?.Length}, bottomHalf={bottomHalf?.Length}, expected={expectedHalfSize}), output full atlas");

            linearData = untiledAtlas;
            texture.Width = (ushort)atlasWidth;
            texture.Height = (ushort)atlasHeight;
            texture.MipLevels = 1;
        }

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

        return linearData;
    }

    private byte[] ReconstructMipsFromSplitHalves(
        byte[] untiledAtlas, D3DTextureInfo texture, int atlasWidth, int atlasHeight,
        int largestMipWidth, int largestMipHeight, int halfHeight,
        byte[] topHalf, byte[] bottomHalf)
    {
        var mipDataList = new List<byte[]>();
        var totalMipSize = 0;
        var mipCount = 0;

        var mip0 = new byte[topHalf.Length + bottomHalf.Length];
        Array.Copy(topHalf, 0, mip0, 0, topHalf.Length);
        Array.Copy(bottomHalf, 0, mip0, topHalf.Length, bottomHalf.Length);
        mipDataList.Add(mip0);
        totalMipSize += mip0.Length;
        mipCount++;
        if (_verboseLogging)
            Console.WriteLine(
                $"  Mip 0: {largestMipWidth}x{largestMipHeight} ({mip0.Length} bytes) - reconstructed from split halves");

        var mipX = 0;
        var mipY = halfHeight;
        var mipW = largestMipWidth / 2;
        var mipH = largestMipHeight / 2;

        while (mipW >= 4 && mipH >= 4 && mipX + mipW <= atlasWidth && mipY + mipH <= atlasHeight)
        {
            var mipData = DdxMipAtlasUnpacker.ExtractAtlasRegion(untiledAtlas, new AtlasRegionParams(
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

            mipX += mipW;
            if (mipX + mipW / 2 > atlasWidth)
            {
                mipX = 0;
                mipY += mipH;
            }

            mipW /= 2;
            mipH /= 2;
        }

        var linearData = new byte[totalMipSize];
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

        return linearData;
    }

    private byte[] ProcessDoubleMainSize(
        byte[] mainData, D3DTextureInfo texture, int width, int height,
        uint mainSurfaceSize, string? outputPath, ConversionOptions? options)
    {
        var halfWidth = width / 2;
        var halfHeight = height / 2;

        var couldBeMipAtlasForSmallerBase = halfWidth >= 64 && halfHeight >= 64 &&
                                            mainData.Length == mainSurfaceSize * 2;

        if (couldBeMipAtlasForSmallerBase && _verboseLogging)
            Console.WriteLine(
                $"Data size {mainData.Length} could be packed mip atlas for {halfWidth}x{halfHeight} in {width}x{height} tile space");

        var fullUntiled = UnswizzleDxtTexture(mainData, width, height, texture.ActualFormat);

        var treatedAsMipAtlas = false;
        byte[] linearData = [];

        if (couldBeMipAtlasForSmallerBase)
        {
            var mipAtlasResult = DdxMipAtlasUnpacker.TryExtractPackedMipAtlas(fullUntiled, width, height,
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
            if (_verboseLogging) Console.WriteLine("Data is exactly 2x expected size - treating as two chunks");

            var chunk1TiledAlt = new byte[mainData.Length / 2];
            var chunk2TiledAlt = new byte[mainData.Length / 2];
            Array.Copy(mainData, 0, chunk1TiledAlt, 0, mainData.Length / 2);
            Array.Copy(mainData, mainData.Length / 2, chunk2TiledAlt, 0, mainData.Length / 2);

            var chunk1UntiledAlt = UnswizzleDxtTexture(chunk1TiledAlt, width, height, texture.ActualFormat);
            var chunk2UntiledAlt = UnswizzleDxtTexture(chunk2TiledAlt, width, height, texture.ActualFormat);
            if (_verboseLogging)
                Console.WriteLine(
                    $"Untiled chunks to {chunk1UntiledAlt.Length} + {chunk2UntiledAlt.Length} bytes");

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

        return linearData;
    }

    private byte[] ProcessTwoSquareChunks(
        byte[] mainData, D3DTextureInfo texture, int squareSize, int halfSize,
        string? outputPath, ConversionOptions? options)
    {
        if (_verboseLogging)
            Console.WriteLine(
                $"Processing as {squareSize}x{squareSize} texture with mips in chunk 1, main surface in chunk 2");

        var chunk1Tiled = new byte[halfSize];
        var chunk2Tiled = new byte[halfSize];
        Array.Copy(mainData, 0, chunk1Tiled, 0, halfSize);
        Array.Copy(mainData, halfSize, chunk2Tiled, 0, halfSize);

        var chunk1Untiled = UnswizzleDxtTexture(chunk1Tiled, squareSize, squareSize, texture.ActualFormat);
        var chunk2Untiled = UnswizzleDxtTexture(chunk2Tiled, squareSize, squareSize, texture.ActualFormat);
        if (_verboseLogging)
            Console.WriteLine($"Untiled chunks to {chunk1Untiled.Length} + {chunk2Untiled.Length} bytes");

        var mips = UnpackMipAtlas(chunk1Untiled, new MipAtlasParams(
            squareSize, squareSize, texture.ActualFormat,
            squareSize, squareSize, outputPath, options?.SaveMips ?? false));
        if (_verboseLogging) Console.WriteLine($"Extracted {mips.Length} bytes of mips from chunk 1");

        var linearData = new byte[chunk2Untiled.Length + mips.Length];
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

        return linearData;
    }

    private byte[] ProcessExactSizeMatch(
        byte[] mainData, D3DTextureInfo texture, int width, int height,
        uint mainSurfaceSize, string? outputPath, ConversionOptions? options)
    {
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

            var untiledAtlas = UnswizzleDxtTexture(chunk1, 256, 192, texture.ActualFormat);
            var untiledMain = UnswizzleDxtTexture(chunk2, 128, 128, texture.ActualFormat);

            if (_verboseLogging) Console.WriteLine($"Untiled atlas (256x192) to {untiledAtlas.Length} bytes");
            if (_verboseLogging) Console.WriteLine($"Untiled main (128x128) to {untiledMain.Length} bytes");

            if (outputPath != null)
            {
                var atlasPath = outputPath.Replace(".dds", "_atlas_untiled.bin");
                File.WriteAllBytes(atlasPath, untiledAtlas);
                if (_verboseLogging) Console.WriteLine($"Saved untiled atlas to {atlasPath}");
            }

            var mips = UnpackMipAtlas(untiledAtlas, new MipAtlasParams(
                256, 192, texture.ActualFormat,
                128, 128, outputPath, options?.SaveMips ?? false));
            if (_verboseLogging) Console.WriteLine($"Extracted {mips.Length} bytes of mips from atlas");

            var linearData = new byte[untiledMain.Length + mips.Length];
            Array.Copy(untiledMain, 0, linearData, 0, untiledMain.Length);
            Array.Copy(mips, 0, linearData, untiledMain.Length, mips.Length);

            texture.Width = 128;
            texture.Height = 128;
            texture.MipLevels = TextureUtilities.CalculateMipLevels(128, 128);
            if (_verboseLogging)
                Console.WriteLine(
                    $"Final: 128x128 with {texture.MipLevels} mip levels, {linearData.Length} bytes total");
            return linearData;
        }

        // Check for W/2 x H/2 packed in WxH tiled space
        if (width >= 256 && height >= 256 && width == height)
        {
            var halfW = width / 2;
            var halfH = height / 2;

            var fullUntiled = UnswizzleDxtTexture(mainData, width, height, texture.ActualFormat);

            var mipAtlasResult = DdxMipAtlasUnpacker.TryExtractPackedMipAtlas(fullUntiled, width, height, halfW,
                halfH, texture.ActualFormat, _verboseLogging);

            if (mipAtlasResult is { MipCount: >= 2 } && mainData.Length != mainSurfaceSize)
            {
                if (_verboseLogging)
                    Console.WriteLine(
                        $"Detected packed mip atlas: {halfW}x{halfH} base with {mipAtlasResult.MipCount} mip levels in {width}x{height} tile space");

                texture.Width = (ushort)halfW;
                texture.Height = (ushort)halfH;
                texture.MipLevels = (byte)mipAtlasResult.MipCount;
                return mipAtlasResult.Data;
            }

            if (_verboseLogging) Console.WriteLine($"Not a packed mip atlas, untiling as {width}x{height}");
            texture.MipLevels = 1;
            return fullUntiled;
        }

        // Just untile as-is
        var untiled = UnswizzleDxtTexture(mainData, width, height, texture.ActualFormat);
        if (_verboseLogging) Console.WriteLine($"Untiled to {untiled.Length} bytes");

        texture.MipLevels = 1;
        if (_verboseLogging) Console.WriteLine($"Set MipLevels to {texture.MipLevels}");
        return untiled;
    }

    /// <summary>
    ///     Interleave two chunks horizontally to form a complete texture.
    /// </summary>
    private static byte[] InterleaveHorizontalChunks(byte[] leftChunk, byte[] rightChunk, int leftWidth, int rightWidth,
        int height, uint format)
    {
        var totalWidth = leftWidth + rightWidth;
        var blockSize = format is 0x82 or 0x52 or 0x7B ? 8 : 16;

        var leftBlocksWide = leftWidth / 4;
        var rightBlocksWide = rightWidth / 4;
        var totalBlocksWide = totalWidth / 4;
        var blocksHigh = height / 4;

        var result = new byte[totalBlocksWide * blocksHigh * blockSize];

        for (var row = 0; row < blocksHigh; row++)
        {
            var dstRowOffset = row * totalBlocksWide * blockSize;
            var leftSrcRowOffset = row * leftBlocksWide * blockSize;
            var rightSrcRowOffset = row * rightBlocksWide * blockSize;

            Array.Copy(leftChunk, leftSrcRowOffset, result, dstRowOffset, leftBlocksWide * blockSize);
            Array.Copy(rightChunk, rightSrcRowOffset, result, dstRowOffset + leftBlocksWide * blockSize,
                rightBlocksWide * blockSize);
        }

        return result;
    }
}
