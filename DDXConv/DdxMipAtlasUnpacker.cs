namespace DDXConv;

/// <summary>
///     Extracts mip levels from Xbox 360 packed mip atlases.
///     Handles various atlas layouts (256x192, 320x256, 1024x1024, etc.)
///     and reconstructs individual mip surfaces from tiled atlas data.
/// </summary>
internal sealed class DdxMipAtlasUnpacker(bool verboseLogging)
{
    private readonly bool _verboseLogging = verboseLogging;

    /// <summary>
    ///     Callback for writing individual mip DDS files during atlas unpacking.
    /// </summary>
    internal Action<string, D3DTextureInfo, byte[]>? WriteDdsFileCallback { get; set; }

    /// <summary>
    ///     Extract a rectangular region from atlas data.
    ///     Handles DXT block alignment.
    /// </summary>
    internal static byte[]? ExtractAtlasRegion(byte[] atlasData, AtlasRegionParams p)
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
    internal static PackedMipAtlasResult? TryExtractPackedMipAtlas(byte[] untiledData, int atlasWidth, int atlasHeight,
        int baseWidth, int baseHeight, uint format, bool verbose)
    {
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

    /// <summary>
    ///     Count how many complete mip levels fit in the given data size.
    /// </summary>
    internal static uint CountMipLevelsFromDataSize(uint width, uint height, uint format, int dataLength)
    {
        uint levels = 0;
        var w = width;
        var h = height;
        var offset = 0;

        while (true)
        {
            var mipSize = (int)TextureUtilities.CalculateMipSize(w, h, format);
            if (offset + mipSize > dataLength) break;
            offset += mipSize;
            levels++;
            if (w == 1 && h == 1) break;
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
        }

        return Math.Max(1, levels);
    }

    internal byte[] UnpackMipAtlas(byte[] atlasData, MipAtlasParams p)
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
        if ((width == mainWidth && height >= mainHeight) || (height == mainHeight && width >= mainWidth) ||
            (height == mainHeight && width < mainWidth) || (width == mainWidth && height < mainHeight))
        {
            actualWidth = mainWidth;
            actualHeight = mainHeight;
            actualFromMain = true;
        }

        // Handle specific atlas size -> actual texture size mappings
        if (!actualFromMain && width == 256 && height == 192)
        {
            actualWidth = 128;
            actualHeight = 128;
        }
        else if (!actualFromMain && width == 512 && height == 384)
        {
            actualWidth = 1024;
            actualHeight = 1024;
        }
        else if (!actualFromMain && width == 320 && height == 256)
        {
            actualWidth = 512;
            actualHeight = 256;
        }
        else if (!actualFromMain && width * 4 == height * 5)
        {
            actualWidth = width * 8 / 5;
            actualHeight = height;
        }
        else if (!actualFromMain && height * 4 == width * 5)
        {
            actualWidth = width;
            actualHeight = height * 8 / 5;
        }
        else if (width == height && mainWidth == width && mainHeight == height)
        {
            actualWidth = width;
            actualHeight = height;
        }
        else if (!actualFromMain)
        {
            if (width > height)
            {
                actualWidth = width * 8 / 5;
                actualHeight = height;
            }
            else
            {
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

        if (_verboseLogging)
            Console.WriteLine(
                $"UnpackMipAtlas: width={width}, height={height}, actualTexture={actualWidth}x{actualHeight}, using {GetMipLayoutName(width, height)} mip layout");

        // Special handling for 1024x1024 atlas with split mips
        if (width == 1024 && height == 1024)
            return Unpack1024x1024Atlas(atlasData, output, outputOffset, atlasWidthInBlocks,
                blockSize, format, saveMips, outputPath);

        var mipPositions = GetMipPositions(width, height);

        // Track which atlas blocks we've consumed so we can pick remaining blocks if needed
        var usedBlocks = new bool[height / 4, width / 4];

        // If this atlas is used alongside a separate main surface chunk (two-chunk format),
        // adjust static mappings: remove any mapping that corresponds to the main-size mip
        if (actualFromMain)
        {
            var list = new List<(int x, int y, int w, int h)>(mipPositions);
            var mainBlocksW = Math.Max(1, mainWidth / 4);
            var mainBlocksH = Math.Max(1, mainHeight / 4);
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

        // If our hard-coded mipPositions don't fit in the atlas, fall back to dynamic packing
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

            mipPositions = BuildDynamicMipPositions(actualFromMain, width, height, mainWidth, mainHeight,
                atlasWBlocks, atlasHBlocks);
        }

        for (var mipLevel = 0; mipLevel < mipPositions.Length; mipLevel++)
        {
            var (mipXInBlocks, mipYInBlocks, mipWidthInBlocks, mipHeightInBlocks) = mipPositions[mipLevel];
            var mipWidth = mipWidthInBlocks * 4;
            var mipHeight = mipHeightInBlocks * 4;

            if (mipWidth < 4 || mipHeight < 4) break;

            if (_verboseLogging)
                Console.WriteLine(
                    $"Extracting mip {mipLevel}: {mipWidth}x{mipHeight} from atlas position ({mipXInBlocks * 4}, {mipYInBlocks * 4})");

            // Skip main-level mip if main surface is separate
            if (mipWidth == mainWidth && mipHeight == mainHeight)
            {
                if (_verboseLogging)
                    Console.WriteLine(
                        $"Skipping main-size mip {mipLevel} ({mipWidth}x{mipHeight}) because main surface is separate.");

                continue;
            }

            var mipStartOffset = outputOffset;

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

            // Write out the mip as a separate DDS if requested
            var mipEndOffset = outputOffset;
            var mipByteCount = mipEndOffset - mipStartOffset;
            if (saveMips && mipByteCount > 0 && outputPath != null)
                SaveIndividualMip(output, mipStartOffset, mipByteCount, mipWidth, mipHeight, format, outputPath,
                    mipLevel);
        }

        // Fill remaining bytes from unused atlas blocks if needed
        var mip0Size = TextureUtilities.CalculateMipSize(actualWidth, actualHeight, format);
        var desiredTailBytes = (int)totalSize - mip0Size;

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
            Console.WriteLine($"UnpackMipAtlas: final {outputOffset} bytes (buffer {output.Length} bytes)");

        // Trim to actual extracted size
        var result = new byte[outputOffset];
        Array.Copy(output, 0, result, 0, outputOffset);
        return result;
    }

    /// <summary>
    ///     Special handling for 1024x1024 atlas with split mips.
    /// </summary>
    private byte[] Unpack1024x1024Atlas(byte[] atlasData, byte[] output, int outputOffset,
        int atlasWidthInBlocks, int blockSize, uint format, bool saveMips, string? outputPath)
    {
        // Mip 0 (512x512): split into top 512x256 at (0,0) and bottom 512x256 at (512,0)
        var mip0Start = outputOffset;
        if (_verboseLogging)
            Console.WriteLine("Extracting mip 0 (split): 512x512 - top half at (0,0), bottom half at (512,0)");

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

        var mip0End = outputOffset;
        if (saveMips && outputPath != null)
            SaveIndividualMip(output, mip0Start, mip0End - mip0Start, 512, 512, format, outputPath, 0);

        // Mip 1 (256x256): split top at (0,256), bottom at (256,256)
        if (_verboseLogging)
            Console.WriteLine("Extracting mip 1 (split): 256x256 - top half at (0,256), bottom half at (256,256)");

        var mip1Start = outputOffset;
        for (var by = 0; by < 32; by++)
        for (var bx = 0; bx < 64; bx++)
        {
            var srcOffset = ((64 + by) * atlasWidthInBlocks + bx) * blockSize;
            if (srcOffset + blockSize <= atlasData.Length && outputOffset + blockSize <= output.Length)
                Array.Copy(atlasData, srcOffset, output, outputOffset, blockSize);

            outputOffset += blockSize;
        }

        for (var by = 0; by < 32; by++)
        for (var bx = 0; bx < 64; bx++)
        {
            var srcOffset = ((64 + by) * atlasWidthInBlocks + 64 + bx) * blockSize;
            if (srcOffset + blockSize <= atlasData.Length && outputOffset + blockSize <= output.Length)
                Array.Copy(atlasData, srcOffset, output, outputOffset, blockSize);

            outputOffset += blockSize;
        }

        var mip1End = outputOffset;
        if (saveMips && outputPath != null)
            SaveIndividualMip(output, mip1Start, mip1End - mip1Start, 256, 256, format, outputPath, 1);

        // Remaining non-split mips
        var remainingMips = new (int x, int y, int w, int h)[]
        {
            (512, 256, 128, 128), // Mip 2
            (640, 256, 64, 64), // Mip 3
            (768, 256, 32, 32), // Mip 4
            (912, 256, 16, 16), // Mip 5
            (904, 256, 8, 8), // Mip 6
            (900, 256, 4, 4), // Mip 7
            (896, 264, 4, 4), // Mip 8 (2x2 sub-block)
            (896, 260, 4, 4) // Mip 9 (1x1 sub-block)
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

            if (saveMips && outputPath != null)
            {
                var thisMipSize = TextureUtilities.CalculateMipSize(mipW, mipH, format);
                var thisMipStart = Math.Max(0, outputOffset - thisMipSize);
                SaveIndividualMip(output, thisMipStart, thisMipSize, mipW, mipH, format, outputPath, i + 2);
            }
        }

        // Trim to actual extracted size
        var trimmed = new byte[outputOffset];
        Array.Copy(output, 0, trimmed, 0, outputOffset);
        return trimmed;
    }

    /// <summary>
    ///     Build dynamic mip positions when hard-coded layouts don't fit the atlas.
    /// </summary>
    private static (int x, int y, int w, int h)[] BuildDynamicMipPositions(bool actualFromMain, int width, int height,
        int mainWidth, int mainHeight, int atlasWBlocks, int atlasHBlocks)
    {
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

            if (mbW > atlasWBlocks || mbH > atlasHBlocks) break;

            if (stackedVertical)
            {
                curX = 0;
            }
            else if (stackedHorizontal)
            {
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

            if (curY + mbH > atlasHBlocks) break;

            dyn.Add((curX, curY, mbW, mbH));
            if (stackedVertical)
                curY += mainBlocksH;
            else if (stackedHorizontal)
                curX += mbW;
            else
                curX += mbW;

            rowH = Math.Max(rowH, mbH);

            mW = Math.Max(1, mW / 2);
            mH = Math.Max(1, mH / 2);
        }

        return dyn.Count > 0 ? [.. dyn] : [];
    }

    /// <summary>
    ///     Save an individual mip level as a separate DDS file.
    /// </summary>
    private void SaveIndividualMip(byte[] output, int startOffset, int byteCount,
        int mipWidth, int mipHeight, uint format, string outputPath, int mipLevel)
    {
        try
        {
            var mipData = new byte[byteCount];
            Array.Copy(output, startOffset, mipData, 0, byteCount);
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
            WriteDdsFileCallback?.Invoke(mipPath, mipTexture, mipData);
            if (_verboseLogging) Console.WriteLine($"Saved mip {mipLevel} to {mipPath}");
        }
        catch (Exception ex)
        {
            if (_verboseLogging) Console.WriteLine($"Failed to save mip {mipLevel}: {ex.Message}");
        }
    }

    /// <summary>
    ///     Gets the mip layout name for verbose logging.
    /// </summary>
    internal static string GetMipLayoutName(int width, int height)
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
    internal static (int x, int y, int w, int h)[] GetMipPositions(int width, int height)
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
                (0, 0, 16, 64), // Mip: 64x256 at (0,0)
                (0, 64, 8, 16) // Mip: 32x64 at (0,256)
            ],
            (320, 256) =>
            [
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
    internal sealed class PackedMipAtlasResult
    {
        public byte[] Data { get; set; } = [];
        public int MipCount { get; set; }
    }
}
