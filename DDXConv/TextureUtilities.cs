namespace DDXConv;

/// <summary>
///     Shared utilities for Xbox 360 texture processing.
///     Contains format detection, size calculations, and untiling algorithms.
/// </summary>
public static class TextureUtilities
{
    #region Endian Swap

    /// <summary>
    ///     Swap every 16-bit word in the data (Xbox 360 big-endian to PC little-endian).
    /// </summary>
    /// <remarks>
    ///     Kept scalar deliberately. A SIMD (<c>Vector128.Shuffle</c>) variant was benchmarked over
    ///     the full on-disk DDX corpus and was net-neutral-to-slower: the DDX decode is dominated by
    ///     LZX Huffman decoding, not the byte swap, so vectorizing the swap doesn't move wall-time,
    ///     and the per-block form (see <see cref="CopyBlock" />) measured ~5% slower because each
    ///     call swaps only one 8/16-byte block — too small to amortize the intrinsic setup.
    /// </remarks>
    public static byte[] SwapEndian16(byte[] src)
    {
        var dst = new byte[src.Length];
        for (var i = 0; i < src.Length - 1; i += 2)
        {
            dst[i] = src[i + 1];
            dst[i + 1] = src[i];
        }

        if ((src.Length & 1) == 1)
        {
            dst[^1] = src[^1];
        }

        return dst;
    }

    #endregion

    #region Format Detection

    /// <summary>
    ///     Get the DDS FourCC code for a given Xbox 360 GPU texture format.
    /// </summary>
    /// <remarks>
    ///     Maps Xbox 360 GPU texture formats to D3D formats.
    ///     For 0x82 base format, the actual format is determined by DWORD[4] of the texture header.
    /// </remarks>
    public static uint GetDxgiFormat(uint gpuFormat)
    {
        return gpuFormat switch
        {
            0x52 => 0x31545844, // DXT1
            0x53 => 0x33545844, // DXT3  
            0x54 => 0x35545844, // DXT5
            0x71 => 0x32495441, // ATI2 (BC5) - Xbox 360 normal map format
            0x7B => 0x31495441, // ATI1 (BC4) - Single channel format (specular maps)
            0x82 => 0x31545844, // DXT1 (default when DWORD[4] is 0)
            0x86 => 0x31545844, // DXT1 variant
            0x88 => 0x35545844, // DXT5 variant
            0x12 => 0x31545844, // GPUTEXTUREFORMAT_DXT1
            0x13 => 0x33545844, // GPUTEXTUREFORMAT_DXT2/3
            0x14 => 0x35545844, // GPUTEXTUREFORMAT_DXT4/5
            0x06 => 0x18280046, // GPUTEXTUREFORMAT_8_8_8_8 -> A8R8G8B8
            0x04 => 0x28280044, // GPUTEXTUREFORMAT_5_6_5 -> R5G6B5
            _ => 0x31545844 // Default to DXT1
        };
    }

    /// <summary>
    ///     Get the DXT block size in bytes for a given format.
    /// </summary>
    /// <remarks>
    ///     DXT1/ATI1/BC4 formats use 8 bytes per 4x4 block.
    ///     DXT3/DXT5/ATI2/BC5 formats use 16 bytes per 4x4 block.
    /// </remarks>
    public static int GetBlockSize(uint format)
    {
        return format switch
        {
            // DXT1, ATI1/BC4 (single channel, same block size as DXT1)
            0x52 or 0x7B or 0x82 or 0x86 or 0x12 => 8,
            // DXT3, DXT5, ATI2/BC5 (normal maps), and variants
            _ => 16
        };
    }

    internal static bool FormatHasAlphaChannel(uint format)
    {
        return format switch
        {
            // DXT3 / DXT5 and A8R8G8B8 always carry alpha information.
            0x53 or 0x54 or 0x88 or 0x13 or 0x14 or 0x06 => true,
            _ => false
        };
    }

    internal static bool DetectAlphaUsage(uint format, byte[] data, uint width, uint height)
    {
        if (FormatHasAlphaChannel(format))
        {
            return true;
        }

        if (format is not (0x52 or 0x82 or 0x86 or 0x12) ||
            data.Length < 8)
        {
            return false;
        }

        var mip0Size = (int)CalculateMipSize(width, height, format);
        var bytesToScan = Math.Min(data.Length, mip0Size);
        return ContainsDxt1Transparency(data, bytesToScan);
    }

    private static bool ContainsDxt1Transparency(byte[] data, int length)
    {
        for (var offset = 0; offset + 7 < length; offset += 8)
        {
            var color0 = (ushort)(data[offset] | (data[offset + 1] << 8));
            var color1 = (ushort)(data[offset + 2] | (data[offset + 3] << 8));
            if (color0 > color1)
            {
                continue;
            }

            var lookup = (uint)(data[offset + 4] |
                                (data[offset + 5] << 8) |
                                (data[offset + 6] << 16) |
                                (data[offset + 7] << 24));
            for (var i = 0; i < 16; i++)
            {
                if ((lookup & 0x3) == 0x3)
                {
                    return true;
                }

                lookup >>= 2;
            }
        }

        return false;
    }

    #endregion

    #region Size Calculations

    /// <summary>
    ///     Calculate the number of mip levels for given dimensions.
    /// </summary>
    public static uint CalculateMipLevels(uint width, uint height)
    {
        uint levels = 1;
        var w = width;
        var h = height;

        while (w > 1 || h > 1)
        {
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
            levels++;
        }

        return levels;
    }

    /// <summary>
    ///     Calculate byte size for a single mip level.
    /// </summary>
    /// <remarks>
    ///     Format size mappings:
    ///     - 0x52, 0x7B, 0x82, 0x86, 0x12: DXT1/ATI1 - 8 bytes per 4x4 block
    ///     - 0x53, 0x54, 0x71, 0x88, 0x13, 0x14: DXT3/DXT5/ATI2 - 16 bytes per 4x4 block
    ///     - 0x06: A8R8G8B8 - 32 bits per pixel
    ///     - 0x04: R5G6B5 - 16 bits per pixel
    /// </remarks>
    public static uint CalculateMipSize(uint width, uint height, uint format)
    {
        return format switch
        {
            // DXT1, ATI1/BC4 (single channel, same block size as DXT1)
            0x52 or 0x7B or 0x82 or 0x86 or 0x12 => Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 8,
            // DXT3, DXT5, DXT5 variant (normal maps), and GPUTEXTUREFORMAT variants
            0x53 or 0x54 or 0x71 or 0x88 or 0x13 or 0x14 => Math.Max(1, (width + 3) / 4) *
                                                            Math.Max(1, (height + 3) / 4) * 16,
            // A8R8G8B8 - 32 bits per pixel
            0x06 => width * height * 4,
            // R5G6B5 - 16 bits per pixel
            0x04 => width * height * 2,
            _ => width * height * 4 // Default to 32bpp
        };
    }

    /// <summary>
    ///     Calculate byte size for a single mip level (int overload).
    /// </summary>
    public static int CalculateMipSize(int width, int height, uint format)
    {
        return (int)CalculateMipSize((uint)width, (uint)height, format);
    }

    /// <summary>
    ///     Calculate the GPU tile-aligned size of a single mip level in bytes.
    ///     Xbox 360 GPU aligns both block dimensions to multiples of 32 (128 texels).
    /// </summary>
    /// <remarks>
    ///     From Xenia's CalculateExtent: guest textures round block_pitch_h and block_pitch_v
    ///     up to multiples of 32 blocks. This determines the actual GPU memory footprint.
    /// </remarks>
    public static int CalculateTiledMipSize(int width, int height, uint format)
    {
        // Non-block formats have no 4x4 block grid; treating pixels as 16-byte blocks would
        // produce a nonsense "tiled" size, so the linear size is the honest answer here.
        if (format is 0x06 or 0x04)
        {
            return CalculateMipSize(width, height, format);
        }

        var blockSize = GetBlockSize(format);
        var blocksW = Math.Max(1, (width + 3) / 4);
        var blocksH = Math.Max(1, (height + 3) / 4);
        var tiledBlocksW = (blocksW + 31) & ~31; // round up to multiple of 32
        var tiledBlocksH = (blocksH + 31) & ~31;
        return tiledBlocksW * tiledBlocksH * blockSize;
    }

    /// <summary>
    ///     Total byte size of the sequential tile-aligned mip surfaces that follow mip 0 in the
    ///     on-disk DDX layout (Xenia model): each mip level ≥ 1 is independently tiled at
    ///     32-block-aligned dimensions, until the packed tail (min texel dim ≤ 16) which shares
    ///     a single tile-aligned surface for all remaining levels. Non-block formats fall back
    ///     to the linear chain total.
    /// </summary>
    internal static int ComputeSequentialTiledMipTotal(int baseWidth, int baseHeight, uint format)
    {
        var totalLevels = (int)CalculateMipLevels((uint)baseWidth, (uint)baseHeight);
        var total = 0;

        if (format is 0x06 or 0x04)
        {
            for (var level = 1; level < totalLevels; level++)
            {
                total += CalculateMipSize(Math.Max(1, baseWidth >> level), Math.Max(1, baseHeight >> level), format);
            }

            return total;
        }

        for (var level = 1; level < totalLevels; level++)
        {
            var mipW = Math.Max(4, baseWidth >> level);
            var mipH = Math.Max(4, baseHeight >> level);
            total += CalculateTiledMipSize(mipW, mipH, format);

            if (Math.Min(mipW, mipH) <= 16)
            {
                // Packed tail: one tile-aligned surface, shared by all remaining mips, sized
                // from the tail-base level's dims. For aspect ratios ≤ 8 that is the classic
                // 32×32-block tile; wider/taller chains (e.g. a 512×32 base whose tail starts
                // at 256×16) span multiple macro tiles, which the old hardcoded single tile
                // under-counted.
                break;
            }
        }

        return total;
    }

    /// <summary>
    ///     Calculate total size of a mip chain from given dimensions down to smallest mip.
    /// </summary>
    public static uint CalculateMainDataSize(uint width, uint height, uint format, uint mipLevels)
    {
        uint totalSize = 0;
        var w = width;
        var h = height;

        for (var i = 0; i < mipLevels; i++)
        {
            var mipSize = CalculateMipSize(w, h, format);
            totalSize += mipSize;

            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
        }

        return totalSize;
    }

    /// <summary>
    ///     Get the mip tail base level for Xbox 360 tiled textures.
    ///     The mip tail is where small mip levels are packed into a single GPU tile.
    /// </summary>
    /// <remarks>
    ///     Based on XGGetMipTailBaseLevel from XGraphics (statically linked in Fallout NV).
    ///     For block-compressed formats, the GPU tile is 32x32 texels (8x8 blocks).
    ///     The mip tail starts at the first level whose dimensions fit within a single tile.
    ///     All levels from this point down are packed together.
    ///     For a 512x512 DXT5 texture: mip tail starts at level 4 (32x32 texels).
    ///     For a 256x256 DXT1 texture: mip tail starts at level 3 (32x32 texels).
    /// </remarks>
    public static uint GetMipTailBaseLevel(uint width, uint height, uint format)
    {
        // Xbox 360 GPU tile is 32x32 texels for all formats
        const uint tileTexels = 32;

        uint level = 0;
        var w = width;
        var h = height;

        while (w > tileTexels || h > tileTexels)
        {
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
            level++;
        }

        return level;
    }

    #endregion

    #region Tiling — Morton/Z-Order (3XDO)

    /// <summary>
    ///     Unswizzle a Morton/Z-order tiled DXT texture (3XDO format) to linear layout.
    ///     Optionally performs 16-bit endian swap during the untiling pass.
    /// </summary>
    /// <remarks>
    ///     Xbox 360 tiling algorithm derived from Xenia emulator.
    ///     This is the proven implementation that successfully converts ~19,665 textures.
    /// </remarks>
    public static byte[] UnswizzleMortonDxt(byte[] src, int width, int height, uint format,
        bool swapEndian = true, DecodeDiagnostics? diagnostics = null)
    {
        var blockSize = GetBlockSize(format);

        // Non-block-compressed formats not supported for untiling
        if (format is 0x06 or 0x04)
        {
            return src;
        }

        var blocksWide = width / 4;
        var blocksHigh = height / 4;
        var linearSize = blocksWide * blocksHigh * blockSize;
        var dst = new byte[linearSize];

        // Write-coverage map over destination block slots. Only allocated when someone is
        // watching, so the normal decode path is unchanged.
        var writeCounts = diagnostics is null ? null : new byte[Math.Max(0, blocksWide * blocksHigh)];

        // log2 of bytes per pixel for tiling math
        var log2Bpp = (uint)(blockSize / 4 + ((blockSize / 2) >> (blockSize / 4)));

        for (var y = 0; y < blocksHigh; y++)
        {
            var inputRowOffset = TiledOffset2DRow((uint)y, (uint)blocksWide, log2Bpp);

            for (var x = 0; x < blocksWide; x++)
            {
                var inputOffset = TiledOffset2DColumn((uint)x, (uint)y, log2Bpp, inputRowOffset);
                inputOffset >>= (int)log2Bpp;

                var srcOffset = (int)(inputOffset * blockSize);
                var blockIndex = y * blocksWide + x;
                var dstOffset = blockIndex * blockSize;

                if (CopyBlock(src, srcOffset, dst, dstOffset, blockSize, swapEndian))
                {
                    if (writeCounts is not null && writeCounts[blockIndex] < byte.MaxValue)
                    {
                        writeCounts[blockIndex]++;
                    }
                }
                else
                {
                    diagnostics?.RecordSkippedBlockCopy();
                }
            }
        }

        if (writeCounts is not null)
        {
            diagnostics!.RecordSurfaceCoverage(writeCounts);
        }

        return dst;
    }

    /// <summary>
    ///     Untile a surface whose block grid is not a multiple of the 32-block GPU macro tile, by
    ///     untiling at the tile-aligned extent and then cropping the padding away.
    ///     <para>
    ///         The Xenia offset math addresses the padded footprint, so running it at the logical
    ///         width makes every block past the first macro tile resolve outside the buffer, where
    ///         <see cref="CopyBlock" /> drops it and leaves black. The mip-N path has always done
    ///         this correctly (<c>DdxChunkProcessor.ExtractSequentialTiledMips</c>); mip 0 did not.
    ///     </para>
    ///     <para>
    ///         A source shorter than the aligned extent is decoded on the aligned layout
    ///         anyway: <see cref="CopyBlock" /> bounds-checks every read, so missing source
    ///         blocks become zeroed destination blocks with honest skip/unwritten counters —
    ///         byte-identical to pad-then-untile, without a silent detour through the plain
    ///         path (which reads the wrong blocks entirely for unaligned shapes).
    ///     </para>
    /// </summary>
    public static byte[] UnswizzleMortonDxtAligned(byte[] src, int width, int height, uint format,
        bool swapEndian = true, DecodeDiagnostics? diagnostics = null)
    {
        if (format is 0x06 or 0x04)
        {
            return src;
        }

        var blockSize = GetBlockSize(format);
        var blocksW = Math.Max(1, (width + 3) / 4);
        var blocksH = Math.Max(1, (height + 3) / 4);
        var tiledBlocksW = (blocksW + 31) & ~31;
        var tiledBlocksH = (blocksH + 31) & ~31;

        // When the XG mip-tail base level is 0, level 0 is NOT at the tile origin: the whole mip
        // chain shares one tile-aligned surface and level 0 sits at ComputeTailLevelOffset(0,…),
        // i.e. 4 blocks along the longer axis. Reading from (0,0) lands on levels 2/3 and on slots
        // nothing ever wrote — which is exactly the black-rectangle artifact. Verified against
        // chalk.ddx (8x8 DXT1): its only non-zero blocks are at (4,0),(5,0),(4,1),(5,1).
        var (originX, originY) = ComputeXgTailBaseLevel(width, height) == 0
            ? ComputeTailLevelOffset(0, width, height)
            : (0, 0);

        if (tiledBlocksW == blocksW && tiledBlocksH == blocksH && originX == 0 && originY == 0)
        {
            return UnswizzleMortonDxt(src, width, height, format, swapEndian, diagnostics);
        }

        var untiled = UnswizzleMortonDxt(src, tiledBlocksW * 4, tiledBlocksH * 4, format, swapEndian,
            diagnostics);

        // Crop out this level's rect: blocksW x blocksH starting at the tail origin.
        var cropped = new byte[blocksW * blocksH * blockSize];
        for (var row = 0; row < blocksH; row++)
        {
            var srcRow = originY + row;
            if (srcRow >= tiledBlocksH)
            {
                break;
            }

            var available = Math.Min(blocksW, tiledBlocksW - originX);
            if (available <= 0)
            {
                break;
            }

            Array.Copy(untiled, (srcRow * tiledBlocksW + originX) * blockSize,
                cropped, row * blocksW * blockSize, available * blockSize);
        }

        return cropped;
    }

    /// <summary>
    ///     Deswizzle a sub-tile texture (&lt;32×32 blocks) using compacted Xenia tiling.
    ///     DDX files store sub-tile data as Xenia tiling offsets sorted and packed sequentially
    ///     (address-space gaps removed). We compute each block's Xenia offset, rank them,
    ///     and read from the corresponding compact position.
    /// </summary>
    private static byte[] UnswizzleSubTile(byte[] src, byte[] dst,
        int blocksWide, int blocksHigh, int blockSize, uint log2Bpp, bool swapEndian,
        DecodeDiagnostics? diagnostics = null)
    {
        var totalBlocks = blocksWide * blocksHigh;
        var writeCounts = diagnostics is null ? null : new byte[Math.Max(0, totalBlocks)];

        // Compute Xenia tiling offset for each block
        var entries = new (uint offset, int x, int y)[totalBlocks];
        for (var y = 0; y < blocksHigh; y++)
        {
            var rowOffset = TiledOffset2DRow((uint)y, (uint)blocksWide, log2Bpp);
            for (var x = 0; x < blocksWide; x++)
            {
                var colOffset = TiledOffset2DColumn((uint)x, (uint)y, log2Bpp, rowOffset);
                entries[y * blocksWide + x] = (colOffset, x, y);
            }
        }

        // Sort by Xenia offset to determine compact storage order
        Array.Sort(entries, (a, b) => a.offset.CompareTo(b.offset));

        // Scatter: compact position (rank) -> linear (x, y)
        for (var rank = 0; rank < totalBlocks; rank++)
        {
            var (_, x, y) = entries[rank];
            var srcOffset = rank * blockSize;
            var blockIndex = y * blocksWide + x;
            if (CopyBlock(src, srcOffset, dst, blockIndex * blockSize, blockSize, swapEndian))
            {
                if (writeCounts is not null && writeCounts[blockIndex] < byte.MaxValue)
                {
                    writeCounts[blockIndex]++;
                }
            }
            else
            {
                diagnostics?.RecordSkippedBlockCopy();
            }
        }

        if (writeCounts is not null)
        {
            diagnostics!.RecordSurfaceCoverage(writeCounts);
        }

        return dst;
    }

    /// <summary>
    ///     Copies one compression block, optionally byte-swapping. Returns false when the copy was
    ///     skipped because either offset fell outside its buffer — the caller must treat that as
    ///     data loss, not as a no-op, because the destination is left zeroed and a zeroed block
    ///     decodes to pure black.
    /// </summary>
    private static bool CopyBlock(byte[] src, int srcOffset, byte[] dst, int dstOffset,
        int blockSize, bool swapEndian)
    {
        if (srcOffset < 0 || dstOffset < 0 ||
            srcOffset + blockSize > src.Length || dstOffset + blockSize > dst.Length)
        {
            return false;
        }

        if (swapEndian)
        {
            // Scalar pairwise swap. NOT vectorized on purpose: this runs on one 8/16-byte block and
            // is called once per block in the untile loops, so a per-call Vector128 slice/create/
            // shuffle/copy measured ~5% SLOWER than this loop on the 3XDO untile path — the payload is
            // too small to amortize SIMD setup, and the decode is LZX-bound, not swap-bound.
            for (var i = 0; i < blockSize; i += 2)
            {
                dst[dstOffset + i] = src[srcOffset + i + 1];
                dst[dstOffset + i + 1] = src[srcOffset + i];
            }
        }
        else
        {
            Array.Copy(src, srcOffset, dst, dstOffset, blockSize);
        }

        return true;
    }

    /// <summary>
    ///     Byte offset of one block inside a tiled surface of the given block pitch — the same
    ///     addressing the untile uses, exposed so presence of individual blocks in a short
    ///     payload can be computed analytically (a pure function of the payload length) rather
    ///     than inferred from decode side effects.
    /// </summary>
    internal static long TiledBlockByteOffset(int blockX, int blockY, int pitchBlocks, uint format)
    {
        var blockSize = GetBlockSize(format);
        var log2Bpp = (uint)(blockSize / 4 + ((blockSize / 2) >> (blockSize / 4)));
        var rowOffset = TiledOffset2DRow((uint)blockY, (uint)pitchBlocks, log2Bpp);
        var element = TiledOffset2DColumn((uint)blockX, (uint)blockY, log2Bpp, rowOffset) >> (int)log2Bpp;
        return element * blockSize;
    }

    // Xbox 360 tiling functions from Xenia emulator
    // https://github.com/xenia-project/xenia/blob/master/src/xenia/gpu/texture_conversion.cc
    private static uint TiledOffset2DRow(uint y, uint width, uint log2Bpp)
    {
        // XGAddress2DTiledOffset rounds the pitch UP to a whole macro tile — `(pitch + 0x1f) >> 5`
        // in the decompiled XGraphics (texture_upload_decompiled_xenon.txt, XGAddress2DTiledOffset).
        // Xenia's published form writes `width / 32` because it is handed an already-rounded pitch;
        // this port was not, so any pitch that is not a multiple of 32 blocks got a macro row
        // stride that was too small, and rows below the first tile band read the wrong tile.
        // Provably identical when width % 32 == 0, which is the overwhelming majority of the corpus.
        var macro = (y / 32 * ((width + 31) / 32)) << (int)(log2Bpp + 7);
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

    #endregion

    #region XG Mip Atlas Layout

    /// <summary>
    ///     Compute Xbox 360 mip atlas layout using the XG algorithm decompiled from the Fallout NV
    ///     MemDebug XEX. Returns (x, y, w, h) positions in DXT block coordinates for each mip level.
    /// </summary>
    /// <remarks>
    ///     Non-tail mips use a recursive L-shaped placement verified against hard-coded tables:
    ///     - Square (w==h): mip1 RIGHT, mip2 BELOW, recurse in corner
    ///     - Wider (w&gt;h): mip1 BELOW, mip2 RIGHT of mip1, recurse in corner
    ///     Tail mips use the D3D::GetMipTailLevelOffsetCoords algorithm from the Xbox 360 SDK.
    ///     The tail base level is computed via XGGetMipTailBaseLevel: max(0, ceil(log2(min(w,h))) - 4).
    /// </remarks>
    /// <param name="baseWidth">Base texture width in pixels.</param>
    /// <param name="baseHeight">Base texture height in pixels.</param>
    /// <param name="startLevel">First mip level in the atlas (0 = all mips, 1 = skip base for actualFromMain).</param>
    public static (int x, int y, int w, int h)[] ComputeXgMipLayout(
        int baseWidth, int baseHeight, int startLevel = 0)
    {
        const int blockPx = 4; // DXT block = 4x4 pixels

        var tailBase = ComputeXgTailBaseLevel(baseWidth, baseHeight);
        var totalLevels = (int)CalculateMipLevels((uint)baseWidth, (uint)baseHeight);

        var positions = new List<(int x, int y, int w, int h)>();

        // Collect non-tail mip block dimensions
        var nonTailMips = new List<(int w, int h)>();
        for (var level = startLevel; level < Math.Min(tailBase, totalLevels); level++)
        {
            var mipW = Math.Max(blockPx, baseWidth >> level);
            var mipH = Math.Max(blockPx, baseHeight >> level);
            nonTailMips.Add((mipW / blockPx, mipH / blockPx));
        }

        // Place non-tail mips using recursive L-shape
        var (tailX, tailY) = PlaceNonTailMipsRecursive(nonTailMips, 0, 0, 0, positions);

        // Place tail mips using D3D::GetMipTailLevelOffsetCoords
        if (tailBase < totalLevels)
        {
            var tailW = Math.Max(1, baseWidth >> tailBase);
            var tailH = Math.Max(1, baseHeight >> tailBase);

            for (var level = tailBase; level < totalLevels; level++)
            {
                var levelInTail = level - tailBase;
                var (tx, ty) = ComputeTailLevelOffset(levelInTail, tailW, tailH);

                var mipW = Math.Max(1, baseWidth >> level);
                var mipH = Math.Max(1, baseHeight >> level);
                var wBlocks = Math.Max(1, (mipW + blockPx - 1) / blockPx);
                var hBlocks = Math.Max(1, (mipH + blockPx - 1) / blockPx);

                positions.Add((tailX + tx, tailY + ty, wBlocks, hBlocks));
            }
        }

        return positions.ToArray();
    }

    /// <summary>
    ///     Compute the mip tail base level using the XGGetMipTailBaseLevel formula:
    ///     max(0, ceil(log2(min(width, height))) - 4). The tail starts when the smaller
    ///     dimension reaches 16 texels (4 DXT blocks); consistently, min ≤ 16 ⇔ tail base 0.
    ///     <para>
    ///         CEIL log2, per the decompiled reference (texture_upload_decompiled_xenon.txt
    ///         ~11341, <c>32 - LZCOUNT(dim - 1)</c> per axis). Floor diverges for
    ///         non-power-of-two dims 17–31, applying a tail origin to level 0 where the
    ///         hardware layout has none.
    ///     </para>
    /// </summary>
    internal static int ComputeXgTailBaseLevel(int width, int height)
    {
        var minDim = Math.Min(width, height);
        return Math.Max(0, CeilLog2(minDim) - 4);
    }

    /// <summary>
    ///     Compute the (x, y) block offset of a mip level within the mip tail tile.
    ///     Based on D3D::GetMipTailLevelOffsetCoords decompiled from Fallout NV MemDebug XEX.
    /// </summary>
    /// <remarks>
    ///     The mip tail is a 32×32 texel (8×8 block) GPU tile. Mips are packed at fixed offsets:
    ///     For square DXT tail: level 0→(4,0), 1→(2,0), 2→(1,0), 3→(0,2), 4→(0,1).
    ///     For non-square: the wider dimension determines which axis gets the primary offsets.
    /// </remarks>
    public static (int xBlocks, int yBlocks) ComputeTailLevelOffset(
        int levelInTail, int tailBaseWidth, int tailBaseHeight)
    {
        const int blockW = 4, blockH = 4;

        int x = 0, y = 0;

        // Determine which dimension is wider using ceil_log2 comparison
        // matches: -(uint)(0x20U - LZCOUNT(param_3 + -1) < 0x20U - LZCOUNT(param_2 + -1)) & 1
        var widthIsWider = CeilLog2(tailBaseHeight) < CeilLog2(tailBaseWidth);

        if (levelInTail < 3)
        {
            // First 3 tail levels: fixed 16-texel offsets in the primary dimension
            var offset = 16 >> levelInTail;
            if (!widthIsWider)
                x = offset;
            else
                y = offset;
        }
        else
        {
            // Levels 3+: offset in the flipped (secondary) dimension
            var w = NextPowerOf2(tailBaseWidth);
            var h = NextPowerOf2(tailBaseHeight);

            var dimValue = !widthIsWider ? h : w;
            var shifted = dimValue >> (levelInTail - 2);

            if (!widthIsWider)
                y = shifted;
            else
                x = shifted;
        }

        return (x / blockW, y / blockH);
    }

    private static (int tailX, int tailY) PlaceNonTailMipsRecursive(
        List<(int w, int h)> mips, int idx, int ox, int oy,
        List<(int x, int y, int w, int h)> positions)
    {
        if (idx >= mips.Count)
            return (ox, oy);

        var (w0, h0) = mips[idx];
        positions.Add((ox, oy, w0, h0));

        if (idx + 1 >= mips.Count)
            return (ox + w0, oy + h0);

        var (w1, h1) = mips[idx + 1];

        if (w0 > h0)
        {
            // Wider: mip1 BELOW entry0, mip2 RIGHT of mip1
            positions.Add((ox, oy + h0, w1, h1));
            if (idx + 2 < mips.Count)
            {
                var (w2, h2) = mips[idx + 2];
                positions.Add((ox + w1, oy + h0, w2, h2));
            }
        }
        else
        {
            // Square or taller: mip1 RIGHT of entry0, mip2 BELOW entry0
            positions.Add((ox + w0, oy, w1, h1));
            if (idx + 2 < mips.Count)
            {
                var (w2, h2) = mips[idx + 2];
                positions.Add((ox, oy + h0, w2, h2));
            }
        }

        // Recurse in corner quadrant for remaining non-tail mips
        return PlaceNonTailMipsRecursive(mips, idx + 3, ox + w0, oy + h0, positions);
    }

    private static int CeilLog2(int value)
    {
        if (value <= 1) return 0;
        return 32 - int.LeadingZeroCount(value - 1);
    }

    private static int NextPowerOf2(int value)
    {
        if (value <= 1) return 1;
        return 1 << CeilLog2(value);
    }

    #endregion

    #region Tiling — 3XDR Macro-Block

    /// <summary>
    ///     Untile 3XDR macro-block tiled data to linear layout with optional endian swap.
    ///     Uses the Xbox 360 GPU block-level tiling, which rearranges DXT block coordinates
    ///     via a specific bit permutation derived from empirical analysis.
    /// </summary>
    /// <remarks>
    ///     The Xbox 360 GPU tiles DXT blocks using a bit-level coordinate rearrangement.
    ///     For block coordinates (x, y) with n-bit values, the tiled-to-linear mapping is:
    ///     px = x[2] x[3] y[1] x[1]  (for 4-bit coordinates)
    ///     py = y[3] y[2] y[0] x[0]
    ///     This was derived by matching 44 uniquely-identifiable DXT5 color blocks between
    ///     Xbox 360 3XDR output and PC reference DDS files, verified bijective on 16x16 grids.
    /// </remarks>
    public static byte[] UntileMacroBlocks(byte[] src, int width, int height, int blockSize,
        bool swapEndian = false, uint gpuFormat = 0, DecodeDiagnostics? diagnostics = null)
    {
        var blocksX = Math.Max(1, (width + 3) / 4);
        var blocksY = Math.Max(1, (height + 3) / 4);
        var mipSize = blocksX * blocksY * blockSize;

        var dst = new byte[mipSize];

        // ATI2/BC5 sub-tile textures use compact Xenia tiling (8-byte sub-block tiling)
        if (blocksX < 32 && blocksY < 32 && IsAti2Format(gpuFormat))
        {
            var log2Bpp = (uint)(blockSize / 4 + ((blockSize / 2) >> (blockSize / 4)));
            return UnswizzleSubTile(src, dst, blocksX, blocksY, blockSize, log2Bpp, swapEndian,
                diagnostics);
        }

        var writeCounts = diagnostics is null ? null : new byte[blocksX * blocksY];

        for (var xboxIdx = 0; xboxIdx < blocksX * blocksY; xboxIdx++)
        {
            var xboxX = xboxIdx % blocksX;
            var xboxY = xboxIdx / blocksX;

            var pcIdx = GetPcBlockIndex(xboxX, xboxY, blocksX, blockSize, gpuFormat);

            // Bounds are checked inside CopyBlock — the duplicate guard that used to live here
            // swallowed the skip before it could be counted.
            if (CopyBlock(src, xboxIdx * blockSize, dst, pcIdx * blockSize, blockSize, swapEndian))
            {
                if (writeCounts is not null && pcIdx >= 0 && pcIdx < writeCounts.Length &&
                    writeCounts[pcIdx] < byte.MaxValue)
                {
                    writeCounts[pcIdx]++;
                }
            }
            else
            {
                diagnostics?.RecordSkippedBlockCopy();
            }
        }

        if (writeCounts is not null)
        {
            diagnostics!.RecordSurfaceCoverage(writeCounts);
        }

        return dst;
    }

    /// <summary>
    ///     Calculate PC (linear) block index from Xbox (tiled) block coordinates.
    ///     The tiling pattern depends on the effective tile unit size:
    ///     - 8-byte tile unit (DXT1, ATI1, ATI2): 8×2 macro-block groups
    ///     - 16-byte tile unit (DXT5, DXT3): bit-level coordinate permutation
    ///     ATI2/BC5 uses 8-byte tiling despite 16-byte blocks because it stores two
    ///     independent BC4 channels — the GPU tiles each 8-byte sub-block independently.
    /// </summary>
    internal static int GetPcBlockIndex(int xboxX, int xboxY, int blocksX, int blockSize = 16,
        uint gpuFormat = 0)
    {
        // ATI2/BC5 (0x71) uses 8-byte tiling despite having 16-byte blocks,
        // because it's two independent BC4 sub-blocks tiled at 8-byte granularity
        var effectiveTileUnit = IsAti2Format(gpuFormat) ? 8 : blockSize;

        if (effectiveTileUnit <= 8)
        {
            // DXT1/ATI1/ATI2 (8-byte tile unit): 8×2 macro-block tiling
            var groupX = xboxX / 8;
            var groupY = xboxY / 2;
            var localX = xboxX % 8;
            var localY = xboxY % 2;
            var pcLocalX = (localY << 2) | ((localX >> 2) << 1) | (localX & 1);
            var pcLocalY = (localX >> 1) & 1;
            var pcX = groupX * 8 + pcLocalX;
            var pcY = groupY * 2 + pcLocalY;
            return pcY * blocksX + pcX;
        }

        // DXT5/DXT3 (16-byte tile unit): bit-level coordinate permutation.
        // Core pattern (verified against PC reference DDS files):
        //   pc_x[0]=x[1], pc_x[1]=y[1], pc_y[0]=x[0], pc_y[1]=y[0]
        // For ≥16-wide grids (4+ bit coordinates), bits 2-3 have an x[2]/x[3] swap:
        //   pc_x[2]=x[3], pc_x[3]=x[2]  (derived from 44 DXT5 block matches at 16×16)
        // For <16-wide grids (3-bit coordinates), higher bits pass through:
        //   pc_x[2]=x[2], pc_y[2]=y[2]  (verified against PC reference at 8×8)
        int pcX16, pcY16;
        if (blocksX >= 16)
        {
            pcX16 = (xboxX & ~0xF) |
                    (((xboxX >> 2) & 1) << 3) | (((xboxX >> 3) & 1) << 2) |
                    (((xboxY >> 1) & 1) << 1) | ((xboxX >> 1) & 1);
            pcY16 = (xboxY & ~3) |
                    ((xboxY & 1) << 1) | (xboxX & 1);
        }
        else
        {
            // Small grids (< 16 blocks wide): pass through higher bits unchanged
            pcX16 = (xboxX & ~0x7) |
                    (((xboxX >> 2) & 1) << 2) |
                    (((xboxY >> 1) & 1) << 1) | ((xboxX >> 1) & 1);
            pcY16 = (xboxY & ~0x3) |
                    ((xboxY & 1) << 1) | (xboxX & 1);
        }

        return pcY16 * blocksX + pcX16;
    }

    /// <summary>
    ///     Check if the GPU format is ATI2/BC5 (dual-channel normal map format).
    /// </summary>
    private static bool IsAti2Format(uint gpuFormat)
    {
        return gpuFormat == 0x71;
    }

    #endregion
}
