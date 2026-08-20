namespace DDXConv;

/// <summary>
///     Processes decompressed texture chunks (two-chunk and single-chunk formats)
///     from Xbox 360 DDX files, including atlas dimension detection, mip extraction,
///     and various layout heuristics.
/// </summary>
internal sealed class DdxChunkProcessor(bool verboseLogging)
{
    /// <summary>
    ///     Delegate for unswizzling DXT texture data (Morton order to linear) with real-surface
    ///     semantics: tile-aligned extent and packed-tail origin crop. Use for every decode of
    ///     an actual GPU surface at its own dimensions.
    /// </summary>
    internal required Func<byte[], int, int, uint, byte[]> UnswizzleDxtTexture { get; init; }

    /// <summary>
    ///     Delegate for the plain logical-dims untile, for call sites whose dimensions are
    ///     heuristic guesses rather than real surfaces (memory-dump atlas extraction, the
    ///     legacy large-texture linear mip walk). These historically relied on the aligned
    ///     wrapper's short-source fallback landing them on the plain path; with that fallback
    ///     gone, routing them explicitly keeps their behavior frozen instead of silently
    ///     decoding a guessed rectangle on an aligned layout it never had.
    /// </summary>
    internal required Func<byte[], int, int, uint, byte[]> UnswizzleDxtTextureHeuristic { get; init; }

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
        byte[] linearData;

        // Check if we have two chunks or one chunk
        var isTwoChunkFormat = false;
        uint chunk1Size = 0;
        uint chunk2Size = 0;

        if (decompressedChunks.Count == 2)
        {
            isTwoChunkFormat = true;
            chunk1Size = (uint)decompressedChunks[0].Length;
            chunk2Size = (uint)decompressedChunks[1].Length;
            if (verboseLogging)
                Console.WriteLine(
                    $"Using actual decompressed chunk boundaries: chunk1={chunk1Size}, chunk2={chunk2Size}");
        }
        else if (decompressedChunks.Count > 1 && mainData.Length ==
                 (uint)TextureUtilities.CalculateMipSize(width, height, texture.ActualFormat) * 2)
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
            if (verboseLogging)
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
    ///     Process two-chunk format: chunk1 = mip data, chunk2 = main surface.
    ///     On-disk DDX: chunk1 contains sequential independently-tiled mip surfaces.
    ///     Memory dumps: chunk1 contains XG atlas (GPU in-memory layout).
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
        if (verboseLogging) Console.WriteLine($"Two-chunk format confirmed ({mainData.Length} bytes)");

        var blockSize = TextureUtilities.GetBlockSize(texture.ActualFormat);
        var chunk1 = new byte[chunk1Size];
        var chunk2 = new byte[chunk2Size];
        Array.Copy(mainData, 0, chunk1, 0, chunk1Size);
        Array.Copy(mainData, chunk1Size, chunk2, 0, chunk2Size);

        // A short main surface is decoded as-is: the aligned unswizzle bounds-checks every
        // read, so missing source blocks become zeroed destination blocks with honest
        // skip/unwritten counters. Padding to the LINEAR size here used to lock unaligned
        // shapes out of the aligned decode path entirely (linear < tiled extent, always).
        var untiledMain = UnswizzleDxtTexture(chunk2, width, height, texture.ActualFormat);

        // Determine mip extraction strategy based on chunk1 size.
        // On-disk DDX stores mips as sequential tile-aligned surfaces (Xenia model),
        // and some files only include a prefix of the mip chain. Memory dumps store
        // mips as an XG atlas (GPU in-memory layout).
        var expectedSequentialSize = TextureUtilities.ComputeSequentialTiledMipTotal(width, height, texture.ActualFormat);
        var sequentialStoredMipLevels =
            magic != 0x52445833
                ? CountSequentialStoredMipLevels(width, height, texture.ActualFormat, blockSize, (int)chunk1Size)
                : 0;
        var isSequentialMips = sequentialStoredMipLevels > 0;

        byte[] mips;
        if (isSequentialMips)
        {
            // On-disk DDX: chunk1 = sequential independently-tiled mip surfaces.
            if (verboseLogging)
                Console.WriteLine(
                    chunk1Size == expectedSequentialSize
                        ? $"Sequential tiled mips: chunk1={chunk1Size} matches expected={expectedSequentialSize}"
                        : $"Sequential tiled mips: chunk1={chunk1Size} stores {sequentialStoredMipLevels} mip level(s) as a prefix of expected={expectedSequentialSize}");

            mips = options is { NoUntileAtlas: true }
                ? chunk1 // raw tiled data requested
                : ExtractSequentialTiledMips(chunk1, width, height, texture.ActualFormat, blockSize);

            texture.MipLevels = (byte)Math.Min(255, sequentialStoredMipLevels + 1);
        }
        else
        {
            // Memory dump or 3XDR: fall back to atlas-based extraction
            mips = ExtractMipsViaAtlas(chunk1, chunk1Size, texture, width, height,
                blockSize, magic, outputPath, options);
        }

        if (verboseLogging) Console.WriteLine($"Extracted {mips.Length} bytes of mips");

        var actualMainSize = TextureUtilities.CalculateMipSize(width, height, texture.ActualFormat);
        var croppedMain = untiledMain.Length > actualMainSize
            ? untiledMain[..actualMainSize]
            : untiledMain;

        var linearData = new byte[croppedMain.Length + mips.Length];
        Array.Copy(croppedMain, 0, linearData, 0, croppedMain.Length);
        Array.Copy(mips, 0, linearData, croppedMain.Length, mips.Length);

        if (verboseLogging)
            Console.WriteLine(
                $"Combined {croppedMain.Length} bytes main surface + {mips.Length} bytes mips = {linearData.Length} total");

        return linearData;
    }

    internal static int CountSequentialStoredMipLevels(int baseWidth, int baseHeight, uint format, int blockSize,
        int dataSize)
    {
        if (dataSize <= 0) return 0;

        var totalLevels = (int)TextureUtilities.CalculateMipLevels((uint)baseWidth, (uint)baseHeight);
        var consumed = 0;
        var storedMipLevels = 0;

        for (var level = 1; level < totalLevels; level++)
        {
            var mipW = Math.Max(4, baseWidth >> level);
            var mipH = Math.Max(4, baseHeight >> level);

            if (Math.Min(mipW, mipH) <= 16)
            {
                // Packed tail: one tile-aligned surface sized from the tail-base level's dims
                // (multiple macro tiles for aspect > 8, one 32x32-block tile otherwise).
                var packedTailSize = TextureUtilities.CalculateTiledMipSize(mipW, mipH, format);
                return consumed + packedTailSize == dataSize ? totalLevels - 1 : 0;
            }

            var mipSize = TextureUtilities.CalculateTiledMipSize(mipW, mipH, format);
            if (consumed + mipSize > dataSize) return 0;

            consumed += mipSize;
            storedMipLevels++;

            if (consumed == dataSize) return storedMipLevels;
        }

        return 0;
    }

    /// <summary>
    ///     Extract mips from sequential tile-aligned surfaces (on-disk DDX format).
    ///     Each mip is independently tiled at 32-block-aligned dimensions.
    /// </summary>
    private byte[] ExtractSequentialTiledMips(
        byte[] tiledMipData, int baseWidth, int baseHeight, uint format, int blockSize)
    {
        using var output = new MemoryStream();
        var offset = 0;
        var totalLevels = (int)TextureUtilities.CalculateMipLevels((uint)baseWidth, (uint)baseHeight);

        for (var level = 1; level < totalLevels && offset < tiledMipData.Length; level++)
        {
            var mipW = Math.Max(4, baseWidth >> level);
            var mipH = Math.Max(4, baseHeight >> level);

            if (Math.Min(mipW, mipH) <= 16)
            {
                // Packed tail: all remaining mips in a single 32×32-block tile
                ExtractPackedTailMips(tiledMipData, offset, baseWidth, baseHeight,
                    level, totalLevels, format, blockSize, output);
                break;
            }

            // Non-packed mip: independently tiled surface
            var tiledSize = TextureUtilities.CalculateTiledMipSize(mipW, mipH, format);
            if (offset + tiledSize > tiledMipData.Length) break;

            var tiledSlice = new byte[tiledSize];
            Array.Copy(tiledMipData, offset, tiledSlice, 0, tiledSize);

            // Untile at tile-aligned dimensions
            var blocksW = Math.Max(1, mipW / 4);
            var blocksH = Math.Max(1, mipH / 4);
            var tiledBlocksW = (blocksW + 31) & ~31;
            var tiledBlocksH = (blocksH + 31) & ~31;
            var untiled = TextureUtilities.UnswizzleMortonDxt(
                tiledSlice, tiledBlocksW * 4, tiledBlocksH * 4, format);

            // Crop tile padding → write only actual mip blocks
            if (tiledBlocksW == blocksW)
            {
                // No horizontal padding — fast path
                output.Write(untiled, 0, blocksW * blocksH * blockSize);
            }
            else
            {
                // Crop: copy row by row, skipping padding blocks
                for (var row = 0; row < blocksH; row++)
                {
                    var srcOff = row * tiledBlocksW * blockSize;
                    output.Write(untiled, srcOff, blocksW * blockSize);
                }
            }

            if (verboseLogging)
                Console.WriteLine(
                    $"  Mip {level}: {mipW}x{mipH} ({blocksW}x{blocksH} blocks, tiled {tiledBlocksW}x{tiledBlocksH}) = {tiledSize} bytes");

            offset += tiledSize;
        }

        return output.ToArray();
    }

    /// <summary>
    ///     Extract packed tail mips from the shared tail surface.
    ///     Uses ComputeTailLevelOffset to find each mip's position within it. The surface is
    ///     the tile-aligned extent of the tail-base level's dims — one 32×32-block tile for
    ///     aspect ratios ≤ 8, multiple macro tiles for wide/tall chains (whose tail offsets
    ///     land beyond the first tile, e.g. block x=32 for a 256×16 tail base).
    /// </summary>
    private void ExtractPackedTailMips(
        byte[] tiledMipData, int offset, int baseWidth, int baseHeight,
        int startLevel, int totalLevels, uint format, int blockSize, MemoryStream output,
        int? tailBaseLevel = null)
    {
        // The tail's shared surface is sized/addressed from the TAIL BASE level's dims, which
        // usually equals the first level extracted (sequential chains) but not always: a
        // tail-base-0 single-chunk surface holds mip 0 too, and extraction starts at level 1.
        var tailBase = tailBaseLevel ?? startLevel;
        var tailBaseW = Math.Max(1, baseWidth >> tailBase);
        var tailBaseH = Math.Max(1, baseHeight >> tailBase);
        var tailTiledBlocksW = (Math.Max(1, (tailBaseW + 3) / 4) + 31) & ~31;
        var tailTiledBlocksH = (Math.Max(1, (tailBaseH + 3) / 4) + 31) & ~31;
        var tileSize = tailTiledBlocksW * tailTiledBlocksH * blockSize;
        if (offset + tileSize > tiledMipData.Length) return;

        var tileData = new byte[tileSize];
        Array.Copy(tiledMipData, offset, tileData, 0, tileSize);
        var untiledTile = TextureUtilities.UnswizzleMortonDxt(
            tileData, tailTiledBlocksW * 4, tailTiledBlocksH * 4, format);

        if (verboseLogging)
            Console.WriteLine(
                $"  Packed tail at level {startLevel}: {tailBaseW}x{tailBaseH} base " +
                $"({tailTiledBlocksW}x{tailTiledBlocksH} tiled blocks), {totalLevels - startLevel} mips in tile");

        for (var level = startLevel; level < totalLevels; level++)
        {
            var mipW = Math.Max(1, baseWidth >> level);
            var mipH = Math.Max(1, baseHeight >> level);
            var blocksW = Math.Max(1, (mipW + 3) / 4);
            var blocksH = Math.Max(1, (mipH + 3) / 4);

            var (ox, oy) = TextureUtilities.ComputeTailLevelOffset(
                level - tailBase, tailBaseW, tailBaseH);

            for (var row = 0; row < blocksH; row++)
            {
                var srcOff = ((oy + row) * tailTiledBlocksW + ox) * blockSize;
                if (srcOff + blocksW * blockSize <= untiledTile.Length)
                    output.Write(untiledTile, srcOff, blocksW * blockSize);
            }
        }
    }

    /// <summary>
    ///     Fall-back atlas-based mip extraction for memory dumps and 3XDR format.
    ///     Uses the original atlas dimension heuristics and UnpackMipAtlas.
    /// </summary>
    private byte[] ExtractMipsViaAtlas(
        byte[] chunk1, uint chunk1Size, D3DTextureInfo texture,
        int width, int height, int blockSize, uint magic,
        string? outputPath, ConversionOptions? options)
    {
        // Determine atlas dimensions (legacy heuristic for memory dump XG atlas)
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

        if (verboseLogging)
            Console.WriteLine(
                $"Atlas path: untiling chunk1 ({chunk1Size} bytes) as {atlasWidth}x{atlasHeight}");

        byte[] untiledAtlas;
        if (options is { NoUntileAtlas: true })
        {
            untiledAtlas = chunk1;
        }
        else if (magic == 0x52445833) // MAGIC_3XDR
        {
            var bs = TextureUtilities.GetBlockSize(texture.ActualFormat);
            var untiled = TextureUtilities.UntileMacroBlocks(chunk1, atlasWidth, atlasHeight, bs,
                gpuFormat: texture.ActualFormat);
            untiledAtlas = TextureUtilities.SwapEndian16(untiled);
        }
        else
        {
            untiledAtlas = UnswizzleDxtTextureHeuristic(chunk1, atlasWidth, atlasHeight, texture.ActualFormat);
        }

        if (options is { SaveAtlas: true } && outputPath != null)
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
            if (verboseLogging) Console.WriteLine($"Saved untiled atlas to {atlasPath}");
        }

        return UnpackMipAtlas(untiledAtlas, new MipAtlasParams(
            atlasWidth, atlasHeight, texture.ActualFormat,
            (int)texture.Width, (int)texture.Height,
            outputPath, options?.SaveMips ?? false));
    }

    /// <summary>
    ///     Process single-chunk format. Surfaces whose stored layout differs from the logical
    ///     one (either axis sub-tile, over-tile unaligned, or packed-tail origin) take the
    ///     unified aligned path; aligned surfaces fall through to the size heuristics.
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
        byte[] linearData;

        if (verboseLogging)
            Console.WriteLine(
                $"Single-chunk format detected ({mainData.Length} bytes, expected {mainSurfaceSize} for {width}x{height})");

        // One predicate replaces the shape special-cases that were patched narrowly three
        // times: whenever the stored surface differs from the logical one, decode on the
        // stored layout and crop — for oversized, exact and undersized payloads alike.
        var layout = SurfaceLayout.For(width, height, texture.ActualFormat);
        if (layout.RequiresAlignedMip0)
            return ProcessAlignedSurfaceChain(mainData, texture, width, height, layout, options);

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

    /// <summary>
    ///     Unified single-chunk decode for every surface whose stored layout differs from the
    ///     logical one: unswizzle the full stored extent (bounds-checked, so short payloads
    ///     leave counted skips instead of silently rerouting) and crop mip 0 out of it.
    ///     Replaces ProcessSubTileSequentialMips (both-axes sub-tile only) and the
    ///     ProcessSmallTextureHorizontalSplit heuristics (a hardcoded 192×256+64×256 split
    ///     whose match condition was vacuously true for any sufficiently large buffer, and a
    ///     "simple split" that sliced the first linear-size bytes of a tiled stream — for a
    ///     one-axis-sub-tile file that slice is entirely disjoint from where level 0's blocks
    ///     actually live).
    /// </summary>
    private byte[] ProcessAlignedSurfaceChain(
        byte[] mainData, D3DTextureInfo texture, int width, int height, SurfaceLayout layout,
        ConversionOptions? options)
    {
        if (verboseLogging)
            Console.WriteLine(
                $"Aligned-surface decode: {width}x{height} logical {layout.BlocksW}x{layout.BlocksH} " +
                $"blocks in {layout.TiledBlocksW}x{layout.TiledBlocksH} stored, origin ({layout.OriginXBlocks},{layout.OriginYBlocks}), " +
                $"payload {mainData.Length}/{layout.TiledMip0Bytes} bytes");

        var blockSize = TextureUtilities.GetBlockSize(texture.ActualFormat);
        var mip0Linear = UnswizzleDxtTexture(mainData, width, height, texture.ActualFormat);
        texture.MipLevels = 1;

        if (mainData.Length < layout.TiledMip0Bytes)
        {
            // Short payload: mip 0 carries counted skips for every block whose tiled slot lies
            // beyond the payload. Where the missing blocks form an exact row or column band,
            // emit the smaller surface that IS present instead of shipping zero-filled black
            // (user ruling: honest smaller output over fabricated full-size output).
            return TryShrinkShortMip0(mip0Linear, mainData.Length, texture, width, height, layout, options);
        }

        var totalLevels = (int)TextureUtilities.CalculateMipLevels((uint)width, (uint)height);
        if (layout.TailBaseLevel == 0)
        {
            // The whole chain shares this one tail surface: every level sits at its own
            // ComputeTailLevelOffset inside the extent mip 0 was cropped from.
            if (totalLevels > 1)
            {
                using var output = new MemoryStream();
                output.Write(mip0Linear, 0, mip0Linear.Length);
                ExtractPackedTailMips(mainData, 0, width, height,
                    startLevel: 1, totalLevels, texture.ActualFormat, blockSize, output, tailBaseLevel: 0);
                texture.MipLevels = (byte)Math.Min(255, totalLevels);
                if (verboseLogging)
                    Console.WriteLine($"Tail-base-0 chain: extracted {totalLevels - 1} mip level(s) from the shared surface");

                RecordUnrecognizedTrailing(mainData.Length - layout.TiledMip0Bytes, width, height, options);
                return output.ToArray();
            }

            RecordUnrecognizedTrailing(mainData.Length - layout.TiledMip0Bytes, width, height, options);
            return mip0Linear;
        }

        // Tail base >= 1: the chain follows mip 0 as sequential tile-aligned surfaces.
        var trailingLength = mainData.Length - layout.TiledMip0Bytes;
        if (trailingLength == 0)
        {
            return mip0Linear;
        }

        var storedMips = CountSequentialStoredMipLevels(width, height, texture.ActualFormat, blockSize,
            trailingLength);
        if (storedMips > 0)
        {
            var mips = ExtractSequentialTiledMips(mainData[layout.TiledMip0Bytes..], width, height,
                texture.ActualFormat, blockSize);
            texture.MipLevels = (byte)Math.Min(255, storedMips + 1);
            if (verboseLogging)
                Console.WriteLine($"Trailing {trailingLength} bytes = sequential tiled chain of {storedMips} mip level(s)");
            return Combine(mip0Linear, mips);
        }

        RecordUnrecognizedTrailing(trailingLength, width, height, options);
        return mip0Linear;
    }

    /// <summary>
    ///     Smaller-DDS emission for a genuinely short mip 0. Presence of each logical block is
    ///     computed analytically from the payload length via the tiled addressing (deterministic,
    ///     no decode side effects). The surface shrinks only when the present set is EXACTLY the
    ///     leading rows (full width) or the leading columns (full height) — a hole or scattered
    ///     coverage keeps full dimensions with zero fill, because shrinking would discard real
    ///     data. Row crops win ties (the data is a prefix, no repacking).
    /// </summary>
    private byte[] TryShrinkShortMip0(byte[] mip0Linear, int payloadLength, D3DTextureInfo texture,
        int width, int height, SurfaceLayout layout, ConversionOptions? options)
    {
        var blockSize = TextureUtilities.GetBlockSize(texture.ActualFormat);
        var present = new bool[layout.BlocksW * layout.BlocksH];
        var presentCount = 0;
        for (var by = 0; by < layout.BlocksH; by++)
        {
            for (var bx = 0; bx < layout.BlocksW; bx++)
            {
                var offset = TextureUtilities.TiledBlockByteOffset(
                    layout.OriginXBlocks + bx, layout.OriginYBlocks + by, layout.TiledBlocksW, texture.ActualFormat);
                if (offset + blockSize <= payloadLength)
                {
                    present[by * layout.BlocksW + bx] = true;
                    presentCount++;
                }
            }
        }

        if (presentCount == layout.BlocksW * layout.BlocksH)
        {
            // Every logical block's slot is inside the payload — nothing to shrink; whatever
            // loss exists is a layout question, not missing tail data.
            return mip0Linear;
        }

        var rowCrop = 0;
        while (rowCrop < layout.BlocksH &&
               AllPresentInRange(present, rowCrop * layout.BlocksW, layout.BlocksW))
        {
            rowCrop++;
        }

        var colCrop = 0;
        while (colCrop < layout.BlocksW && ColumnFullyPresent(present, layout.BlocksW, layout.BlocksH, colCrop))
        {
            colCrop++;
        }

        var rowExact = presentCount == rowCrop * layout.BlocksW;
        var colExact = presentCount == colCrop * layout.BlocksH;
        var rowArea = rowExact ? rowCrop * layout.BlocksW : 0;
        var colArea = colExact ? colCrop * layout.BlocksH : 0;

        if (rowArea == 0 && colArea == 0)
        {
            options?.Diagnostics?.RecordTruncatedRead(
                $"mip0 short ({payloadLength}/{layout.TiledMip0Bytes}B) with scattered coverage: keeping {width}x{height} zero-filled");
            return mip0Linear;
        }

        if (rowArea >= colArea)
        {
            var newHeight = rowCrop * 4;
            texture.Height = (ushort)newHeight;
            texture.MipLevels = 1;
            options?.Diagnostics?.RecordTruncatedRead(
                $"mip0 short ({payloadLength}/{layout.TiledMip0Bytes}B): emitting {width}x{newHeight} of {width}x{height}");
            if (verboseLogging)
                Console.WriteLine($"Short mip0: emitting complete {width}x{newHeight} row band");
            return mip0Linear[..(rowCrop * layout.BlocksW * blockSize)];
        }

        var newWidth = colCrop * 4;
        texture.Width = (ushort)newWidth;
        texture.MipLevels = 1;
        options?.Diagnostics?.RecordTruncatedRead(
            $"mip0 short ({payloadLength}/{layout.TiledMip0Bytes}B): emitting {newWidth}x{height} of {width}x{height}");
        if (verboseLogging)
            Console.WriteLine($"Short mip0: emitting complete {newWidth}x{height} column band");

        var repacked = new byte[colCrop * layout.BlocksH * blockSize];
        for (var by = 0; by < layout.BlocksH; by++)
        {
            Array.Copy(mip0Linear, by * layout.BlocksW * blockSize,
                repacked, by * colCrop * blockSize, colCrop * blockSize);
        }

        return repacked;
    }

    private static bool AllPresentInRange(bool[] present, int start, int count)
    {
        for (var i = 0; i < count; i++)
        {
            if (!present[start + i])
            {
                return false;
            }
        }

        return true;
    }

    private static bool ColumnFullyPresent(bool[] present, int blocksW, int blocksH, int column)
    {
        for (var by = 0; by < blocksH; by++)
        {
            if (!present[by * blocksW + column])
            {
                return false;
            }
        }

        return true;
    }

    private void RecordUnrecognizedTrailing(int trailingLength, int width, int height, ConversionOptions? options)
    {
        if (trailingLength <= 0)
        {
            return;
        }

        options?.Diagnostics?.RecordTruncatedRead(
            $"unrecognized trailing {trailingLength} bytes after aligned mip0 ({width}x{height})");
        if (verboseLogging)
            Console.WriteLine($"WARNING: {trailingLength} unrecognized trailing bytes after aligned surface");
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
        if (verboseLogging) Console.WriteLine($"Detected extra data: {mainData.Length} > {mainSurfaceSize}");

        if (width >= 512 && height >= 512)
            return ProcessLargeTextureSequentialMips(mainData, texture, width, height, mainSurfaceSize);

        if (decompressedChunks.Count == 1 && mainData.Length == mainSurfaceSize * 2)
            return ProcessSingleChunkDoubleSize(mainData, texture, width, height, mainSurfaceSize, outputPath, options);

        // Aligned surface with trailing data (the aligned-layout shapes never reach here).
        // Probe the trailing bytes for an exact sequential tiled mip chain; then for an exact
        // linear chain; otherwise keep mip 0 and record what was left, never guess.
        var blockSize = TextureUtilities.GetBlockSize(texture.ActualFormat);
        var trailingLength = mainData.Length - (int)mainSurfaceSize;

        var mainSurfaceTiled = mainData[..(int)mainSurfaceSize];
        var mainSurfaceUntiled = UnswizzleDxtTexture(mainSurfaceTiled, width, height, texture.ActualFormat);

        var storedMips = CountSequentialStoredMipLevels(width, height, texture.ActualFormat, blockSize,
            trailingLength);
        if (storedMips > 0)
        {
            var mips = ExtractSequentialTiledMips(mainData[(int)mainSurfaceSize..], width, height,
                texture.ActualFormat, blockSize);
            texture.MipLevels = (byte)Math.Min(255, storedMips + 1);
            if (verboseLogging)
                Console.WriteLine(
                    $"Trailing {trailingLength} bytes = sequential tiled chain of {storedMips} mip level(s)");
            return Combine(mainSurfaceUntiled, mips);
        }

        var linearMip1Size = TextureUtilities.CalculateMipSize(width / 2, height / 2, texture.ActualFormat);
        if (trailingLength == linearMip1Size)
        {
            var mipUntiled = UnswizzleDxtTextureHeuristic(mainData[(int)mainSurfaceSize..], width / 2, height / 2,
                texture.ActualFormat);
            texture.MipLevels = 2;
            if (verboseLogging)
                Console.WriteLine($"Trailing {trailingLength} bytes = one linear mip surface");
            return Combine(mainSurfaceUntiled, mipUntiled);
        }

        options?.Diagnostics?.RecordTruncatedRead(
            $"unrecognized trailing {trailingLength} bytes after mip0 ({width}x{height})");
        if (verboseLogging)
            Console.WriteLine($"WARNING: Unknown mip layout ({trailingLength} trailing bytes), using only main surface");

        texture.MipLevels = 1;
        return mainSurfaceUntiled;
    }

    private static byte[] Combine(byte[] first, byte[] second)
    {
        var combined = new byte[first.Length + second.Length];
        Array.Copy(first, 0, combined, 0, first.Length);
        Array.Copy(second, 0, combined, first.Length, second.Length);
        return combined;
    }

    private byte[] ProcessLargeTextureSequentialMips(
        byte[] mainData, D3DTextureInfo texture, int width, int height, uint mainSurfaceSize)
    {
        if (verboseLogging)
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

        if (verboseLogging)
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
            var mipUntiled = UnswizzleDxtTextureHeuristic(mipTiled, mipWidth, mipHeight, texture.ActualFormat);
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
        if (verboseLogging) Console.WriteLine($"Set MipLevels to {texture.MipLevels}");
        return linearData;
    }

    private byte[] ProcessSingleChunkDoubleSize(
        byte[] mainData, D3DTextureInfo texture, int width, int height, uint mainSurfaceSize,
        string? outputPath, ConversionOptions? options)
    {
        var mainSurfaceBytes = (int)mainSurfaceSize;

        if (verboseLogging)
            Console.WriteLine("Single chunk with 2x main surface - extracting first half as main");

        var mainSurfaceData = mainData[..mainSurfaceBytes];
        var processedMain = UnswizzleDxtTexture(mainSurfaceData, width, height, texture.ActualFormat);

        var atlasData = mainData[mainSurfaceBytes..];
        var processedAtlas = UnswizzleDxtTexture(atlasData, width, height, texture.ActualFormat);

        if (verboseLogging)
            Console.WriteLine(
                $"Processed main: {processedMain.Length} bytes, atlas: {processedAtlas.Length} bytes");

        var mips = UnpackMipAtlas(processedAtlas, new MipAtlasParams(
            width, height, texture.ActualFormat,
            width, height, outputPath, options?.SaveMips ?? false));
        if (verboseLogging) Console.WriteLine($"Extracted {mips.Length} bytes of mips from atlas");

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
        if (verboseLogging)
            Console.WriteLine(
                $"Combined {processedMain.Length} bytes main + {mips.Length} bytes mips, MipLevels={texture.MipLevels}");
        return linearData;
    }

    private byte[] ProcessUndersizedSingleChunk(
        byte[] mainData, D3DTextureInfo texture, int width, int height,
        uint mainSurfaceSize, string? outputPath, ConversionOptions? options)
    {
        if (verboseLogging)
            Console.WriteLine(
                $"WARNING: Data size smaller than expected: {mainData.Length} < {mainSurfaceSize}");

        var estimatedAtlasSize = TextureUtilities.CalculateMipSize(width, height, texture.ActualFormat);
        var isLikelyAtlas = mainData.Length >= estimatedAtlasSize / 4 && mainData.Length <= estimatedAtlasSize;

        if (isLikelyAtlas && width >= 128 && height >= 128)
            return ProcessAtlasOnlyData(mainData, texture, width, height, outputPath, options);

        // Too small or wrong dimensions - just untile what we have
        var untiled = UnswizzleDxtTexture(mainData, width, height, texture.ActualFormat);
        if (verboseLogging) Console.WriteLine($"Untiled to {untiled.Length} bytes");

        texture.MipLevels = 1;
        if (verboseLogging) Console.WriteLine($"Set MipLevels to {texture.MipLevels}");
        return untiled;
    }

    private byte[] ProcessAtlasOnlyData(
        byte[] mainData, D3DTextureInfo texture, int width, int height,
        string? outputPath, ConversionOptions? options)
    {
        if (verboseLogging)
            Console.WriteLine(
                $"Detected atlas-only data ({mainData.Length} bytes) - attempting mip extraction");

        var atlasWidth = width;
        var atlasHeight = height;

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

        if (verboseLogging) Console.WriteLine($"Using atlas dimensions: {atlasWidth}x{atlasHeight}");

        var untiledAtlas = UnswizzleDxtTextureHeuristic(mainData, atlasWidth, atlasHeight, texture.ActualFormat);
        if (verboseLogging) Console.WriteLine($"Untiled atlas to {untiledAtlas.Length} bytes");

        var largestMipWidth = width / 2;
        var largestMipHeight = height / 2;
        var halfHeight = largestMipHeight / 2;

        if (verboseLogging)
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
            if (verboseLogging)
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
            if (verboseLogging) Console.WriteLine($"Saved full atlas to {atlasPath}");
        }

        if (verboseLogging)
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
        if (verboseLogging)
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
                    if (verboseLogging)
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

        if (verboseLogging)
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

        if (couldBeMipAtlasForSmallerBase && verboseLogging)
            Console.WriteLine(
                $"Data size {mainData.Length} could be packed mip atlas for {halfWidth}x{halfHeight} in {width}x{height} tile space");

        var fullUntiled = UnswizzleDxtTexture(mainData, width, height, texture.ActualFormat);

        var treatedAsMipAtlas = false;
        byte[] linearData = [];

        if (couldBeMipAtlasForSmallerBase)
        {
            var mipAtlasResult = DdxMipAtlasUnpacker.TryExtractPackedMipAtlas(fullUntiled, width, height,
                halfWidth, halfHeight, texture.ActualFormat, verboseLogging);

            if (mipAtlasResult != null)
            {
                if (verboseLogging)
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
            if (verboseLogging) Console.WriteLine("Data is exactly 2x expected size - treating as two chunks");

            var chunk1TiledAlt = new byte[mainData.Length / 2];
            var chunk2TiledAlt = new byte[mainData.Length / 2];
            Array.Copy(mainData, 0, chunk1TiledAlt, 0, mainData.Length / 2);
            Array.Copy(mainData, mainData.Length / 2, chunk2TiledAlt, 0, mainData.Length / 2);

            var chunk1UntiledAlt = UnswizzleDxtTexture(chunk1TiledAlt, width, height, texture.ActualFormat);
            var chunk2UntiledAlt = UnswizzleDxtTexture(chunk2TiledAlt, width, height, texture.ActualFormat);
            if (verboseLogging)
                Console.WriteLine(
                    $"Untiled chunks to {chunk1UntiledAlt.Length} + {chunk2UntiledAlt.Length} bytes");

            var mipsAlt = UnpackMipAtlas(chunk1UntiledAlt, new MipAtlasParams(
                width, height, texture.ActualFormat,
                width, height, outputPath, options?.SaveMips ?? false));
            if (verboseLogging) Console.WriteLine($"Extracted {mipsAlt.Length} bytes of mips from chunk 1");

            linearData = new byte[chunk2UntiledAlt.Length + mipsAlt.Length];
            Array.Copy(chunk2UntiledAlt, 0, linearData, 0, chunk2UntiledAlt.Length);
            Array.Copy(mipsAlt, 0, linearData, chunk2UntiledAlt.Length, mipsAlt.Length);

            if (verboseLogging)
                Console.WriteLine(
                    $"Combined {chunk2UntiledAlt.Length} bytes main + {mipsAlt.Length} bytes mips = {linearData.Length} total");
        }

        return linearData;
    }

    private byte[] ProcessTwoSquareChunks(
        byte[] mainData, D3DTextureInfo texture, int squareSize, int halfSize,
        string? outputPath, ConversionOptions? options)
    {
        if (verboseLogging)
            Console.WriteLine(
                $"Processing as {squareSize}x{squareSize} texture with mips in chunk 1, main surface in chunk 2");

        var chunk1Tiled = new byte[halfSize];
        var chunk2Tiled = new byte[halfSize];
        Array.Copy(mainData, 0, chunk1Tiled, 0, halfSize);
        Array.Copy(mainData, halfSize, chunk2Tiled, 0, halfSize);

        var chunk1Untiled = UnswizzleDxtTexture(chunk1Tiled, squareSize, squareSize, texture.ActualFormat);
        var chunk2Untiled = UnswizzleDxtTexture(chunk2Tiled, squareSize, squareSize, texture.ActualFormat);
        if (verboseLogging)
            Console.WriteLine($"Untiled chunks to {chunk1Untiled.Length} + {chunk2Untiled.Length} bytes");

        var mips = UnpackMipAtlas(chunk1Untiled, new MipAtlasParams(
            squareSize, squareSize, texture.ActualFormat,
            squareSize, squareSize, outputPath, options?.SaveMips ?? false));
        if (verboseLogging) Console.WriteLine($"Extracted {mips.Length} bytes of mips from chunk 1");

        var linearData = new byte[chunk2Untiled.Length + mips.Length];
        Array.Copy(chunk2Untiled, 0, linearData, 0, chunk2Untiled.Length);
        Array.Copy(mips, 0, linearData, chunk2Untiled.Length, mips.Length);

        texture.Width = (ushort)squareSize;
        texture.Height = (ushort)squareSize;
        texture.MipLevels = TextureUtilities.CalculateMipLevels((uint)squareSize, (uint)squareSize);
        if (verboseLogging)
            Console.WriteLine(
                $"Final texture: {texture.Width}x{texture.Height} with {texture.MipLevels} mip levels");

        if (verboseLogging)
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
            if (verboseLogging)
                Console.WriteLine("Detected 128x128 texture with mip atlas (24576 + 8192 bytes)");

            var chunk1 = new byte[atlasSize128];
            var chunk2 = new byte[mainSize128];
            Array.Copy(mainData, 0, chunk1, 0, atlasSize128);
            Array.Copy(mainData, atlasSize128, chunk2, 0, mainSize128);

            var untiledAtlas = UnswizzleDxtTextureHeuristic(chunk1, 256, 192, texture.ActualFormat);
            var untiledMain = UnswizzleDxtTexture(chunk2, 128, 128, texture.ActualFormat);

            if (verboseLogging) Console.WriteLine($"Untiled atlas (256x192) to {untiledAtlas.Length} bytes");
            if (verboseLogging) Console.WriteLine($"Untiled main (128x128) to {untiledMain.Length} bytes");

            if (outputPath != null)
            {
                var atlasPath = outputPath.Replace(".dds", "_atlas_untiled.bin");
                File.WriteAllBytes(atlasPath, untiledAtlas);
                if (verboseLogging) Console.WriteLine($"Saved untiled atlas to {atlasPath}");
            }

            var mips = UnpackMipAtlas(untiledAtlas, new MipAtlasParams(
                256, 192, texture.ActualFormat,
                128, 128, outputPath, options?.SaveMips ?? false));
            if (verboseLogging) Console.WriteLine($"Extracted {mips.Length} bytes of mips from atlas");

            var linearData = new byte[untiledMain.Length + mips.Length];
            Array.Copy(untiledMain, 0, linearData, 0, untiledMain.Length);
            Array.Copy(mips, 0, linearData, untiledMain.Length, mips.Length);

            texture.Width = 128;
            texture.Height = 128;
            texture.MipLevels = TextureUtilities.CalculateMipLevels(128, 128);
            if (verboseLogging)
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
                halfH, texture.ActualFormat, verboseLogging);

            if (mipAtlasResult is { MipCount: >= 2 } && mainData.Length != mainSurfaceSize)
            {
                if (verboseLogging)
                    Console.WriteLine(
                        $"Detected packed mip atlas: {halfW}x{halfH} base with {mipAtlasResult.MipCount} mip levels in {width}x{height} tile space");

                texture.Width = (ushort)halfW;
                texture.Height = (ushort)halfH;
                texture.MipLevels = (byte)mipAtlasResult.MipCount;
                return mipAtlasResult.Data;
            }

            if (verboseLogging) Console.WriteLine($"Not a packed mip atlas, untiling as {width}x{height}");
            texture.MipLevels = 1;
            return fullUntiled;
        }

        // Just untile as-is
        var untiled = UnswizzleDxtTexture(mainData, width, height, texture.ActualFormat);
        if (verboseLogging) Console.WriteLine($"Untiled to {untiled.Length} bytes");

        texture.MipLevels = 1;
        if (verboseLogging) Console.WriteLine($"Set MipLevels to {texture.MipLevels}");
        return untiled;
    }

}
