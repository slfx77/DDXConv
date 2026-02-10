namespace DDXConv;

/// <summary>
///     Shared utilities for Xbox 360 texture processing.
///     Contains format detection, size calculations, and untiling algorithms.
/// </summary>
public static class TextureUtilities
{
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
    public static byte[] UnswizzleMortonDXT(byte[] src, int width, int height, uint format,
        bool swapEndian = true)
    {
        var blockSize = GetBlockSize(format);

        // Non-block-compressed formats not supported for untiling
        if (format is 0x06 or 0x04)
        {
            return src;
        }

        var blocksWide = width / 4;
        var blocksHigh = height / 4;
        var dst = new byte[src.Length];

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
                var dstOffset = (y * blocksWide + x) * blockSize;

                CopyBlock(src, srcOffset, dst, dstOffset, blockSize, swapEndian);
            }
        }

        return dst;
    }

    private static void CopyBlock(byte[] src, int srcOffset, byte[] dst, int dstOffset,
        int blockSize, bool swapEndian)
    {
        if (srcOffset + blockSize > src.Length || dstOffset + blockSize > dst.Length)
        {
            return;
        }

        if (swapEndian)
        {
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

    #endregion

    #region Tiling — 3XDR Macro-Block

    /// <summary>
    ///     Untile 3XDR macro-block tiled data to linear layout.
    ///     Does NOT perform endian swap — caller should use <see cref="SwapEndian16" /> separately.
    /// </summary>
    /// <remarks>
    ///     The 3XDR tiling pattern operates on 8x2 block groups with this transformation:
    ///     Xbox row 0: X 0 1 2 3 4 5 6 7 → PC (X,Y): (0,0)(1,0)(0,1)(1,1)(2,0)(3,0)(2,1)(3,1)
    ///     Xbox row 1: X 0 1 2 3 4 5 6 7 → PC (X,Y): (4,0)(5,0)(4,1)(5,1)(6,0)(7,0)(6,1)(7,1)
    /// </remarks>
    public static byte[] UntileMacroBlocks(byte[] src, int width, int height, int blockSize)
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

        for (var xboxIdx = 0; xboxIdx < blocksX * blocksY; xboxIdx++)
        {
            var xboxX = xboxIdx % blocksX;
            var xboxY = xboxIdx / blocksX;

            var pcIdx = GetPcBlockIndex(xboxX, xboxY, blocksX);

            var srcOffset = xboxIdx * blockSize;
            var dstOffset = pcIdx * blockSize;

            if (srcOffset + blockSize <= src.Length && dstOffset + blockSize <= dst.Length)
            {
                Array.Copy(src, srcOffset, dst, dstOffset, blockSize);
            }
        }

        return dst;
    }

    /// <summary>
    ///     Calculate PC block index from Xbox block position within 8x2 macro-block groups.
    /// </summary>
    private static int GetPcBlockIndex(int xboxX, int xboxY, int blocksX)
    {
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

    #endregion

    #region Endian Swap

    /// <summary>
    ///     Swap every 16-bit word in the data (Xbox 360 big-endian to PC little-endian).
    /// </summary>
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
            dst[src.Length - 1] = src[src.Length - 1];
        }

        return dst;
    }

    #endregion
}
