namespace DDXConv;

/// <summary>
/// Shared utilities for Xbox 360 texture processing.
/// Contains format detection, size calculations, and untiling algorithms.
/// </summary>
public static class TextureUtilities
{
    #region Format Detection

    /// <summary>
    /// Get the DDS FourCC code for a given Xbox 360 GPU texture format.
    /// </summary>
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
    /// Get the DXT block size in bytes for a given format.
    /// </summary>
    public static int GetBlockSize(uint format)
    {
        switch (format)
        {
            case 0x52: // DXT1
            case 0x7B: // ATI1/BC4
            case 0x82: // DXT1 variant
            case 0x86: // DXT1 variant
            case 0x12: // GPUTEXTUREFORMAT_DXT1
                return 8;

            case 0x53: // DXT3
            case 0x54: // DXT5
            case 0x71: // DXT5 variant (normal maps)
            case 0x88: // DXT5 variant
            case 0x13: // GPUTEXTUREFORMAT_DXT2/3
            case 0x14: // GPUTEXTUREFORMAT_DXT4/5
            default:
                return 16;
        }
    }

    /// <summary>
    /// Check if format is DXT1-like (8 bytes per block).
    /// </summary>
    public static bool IsDxt1Format(uint format)
    {
        return format switch
        {
            0x52 or 0x7B or 0x82 or 0x86 or 0x12 => true,
            _ => false
        };
    }

    #endregion

    #region Size Calculations

    /// <summary>
    /// Calculate the number of mip levels for given dimensions.
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
    /// Calculate byte size for a single mip level.
    /// </summary>
    public static uint CalculateMipSize(uint width, uint height, uint format)
    {
        switch (format)
        {
            case 0x52: // DXT1
            case 0x7B: // ATI1/BC4
            case 0x82: // DXT1 variant
            case 0x86: // DXT1 variant
            case 0x12: // GPUTEXTUREFORMAT_DXT1
                return Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 8;

            case 0x53: // DXT3
            case 0x54: // DXT5
            case 0x71: // DXT5 variant (normal maps)
            case 0x88: // DXT5 variant
            case 0x13: // GPUTEXTUREFORMAT_DXT2/3
            case 0x14: // GPUTEXTUREFORMAT_DXT4/5
                return Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 16;

            case 0x06: // A8R8G8B8 - 32 bits per pixel
                return width * height * 4;

            case 0x04: // R5G6B5 - 16 bits per pixel
                return width * height * 2;

            default:
                return width * height * 4; // Default to 32bpp
        }
    }

    /// <summary>
    /// Calculate byte size for a single mip level (int overload).
    /// </summary>
    public static int CalculateMipSize(int width, int height, uint format)
    {
        return (int)CalculateMipSize((uint)width, (uint)height, format);
    }

    /// <summary>
    /// Calculate total size of a mip chain from given dimensions down to smallest mip.
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
    /// Calculate total size of a full mip chain from the given dimensions down to 4x4.
    /// </summary>
    public static uint CalculateMipChainSize(int width, int height, uint format)
    {
        uint totalSize = 0;
        var w = width;
        var h = height;
        while (w >= 4 && h >= 4)
        {
            totalSize += (uint)CalculateMipSize(w, h, format);
            w /= 2;
            h /= 2;
        }
        return totalSize;
    }

    /// <summary>
    /// Calculate DDS row pitch for given width and format.
    /// </summary>
    public static uint CalculatePitch(uint width, uint format)
    {
        // For DXT formats, pitch is the row of 4x4 blocks
        return Math.Max(1, (width + 3) / 4) * (uint)GetBlockSize(format);
    }

    #endregion

    #region Xbox 360 Tiling/Unswizzle

    /// <summary>
    /// Unswizzle/untile Xbox 360 DXT texture data.
    /// Xbox 360 uses Morton order (Z-order curve) tiling for textures.
    /// </summary>
    public static byte[] UnswizzleDXTTexture(byte[] src, int width, int height, uint format)
    {
        int blockSize = GetBlockSize(format);
        int blocksWide = (width + 3) / 4;
        int blocksHigh = (height + 3) / 4;

        // Calculate expected size
        int expectedSize = blocksWide * blocksHigh * blockSize;
        if (src.Length < expectedSize)
        {
            // Pad if source is smaller
            var padded = new byte[expectedSize];
            Array.Copy(src, padded, src.Length);
            src = padded;
        }

        var dst = new byte[expectedSize];

        // Calculate log2 of bytes per pixel for tiling
        // DXT1: 8 bytes/block, 4x4 = 16 pixels, so 0.5 bytes/pixel -> log2 = -1, but we use block-based calculation
        // We actually use log2 of (blockSize / 4) since we're processing 4-pixel wide blocks
        uint log2Bpp = (uint)(blockSize / 4 + ((blockSize / 2) >> (blockSize / 4)));

        for (int y = 0; y < blocksHigh; y++)
        {
            uint inputRowOffset = TiledOffset2DRow((uint)y, (uint)blocksWide, log2Bpp);

            for (int x = 0; x < blocksWide; x++)
            {
                uint inputOffset = TiledOffset2DColumn((uint)x, (uint)y, log2Bpp, inputRowOffset);
                inputOffset >>= (int)log2Bpp;

                int dstOffset = (y * blocksWide + x) * blockSize;
                int srcOffset = (int)inputOffset * blockSize;

                if (srcOffset + blockSize <= src.Length && dstOffset + blockSize <= dst.Length)
                {
                    Array.Copy(src, srcOffset, dst, dstOffset, blockSize);
                }
            }
        }

        return dst;
    }

    /// <summary>
    /// Calculate tiled row offset for Xbox 360 texture.
    /// </summary>
    public static uint TiledOffset2DRow(uint y, uint width, uint log2Bpp)
    {
        uint macro = ((y >> 5) * ((width >> 5) << (int)log2Bpp)) << 11;
        uint micro = (((y & 6) >> 1) << (int)log2Bpp) << 6;
        return macro + ((micro + ((y & 8) << (3 + (int)log2Bpp)) + ((y & 1) << 4)));
    }

    /// <summary>
    /// Calculate tiled column offset for Xbox 360 texture.
    /// </summary>
    public static uint TiledOffset2DColumn(uint x, uint y, uint log2Bpp, uint baseOffset)
    {
        uint macro = ((x >> 5) << (int)log2Bpp) << 11;
        uint micro = ((x & 7) << (int)log2Bpp) << 6;
        uint offset = baseOffset + (macro + ((micro + ((x & 8) << (3 + (int)log2Bpp)) + ((x & 16) << 2) +
                                              ((x & ~31u) << (int)log2Bpp))));

        return ((offset >> 6) << 12) + ((y & 16) << 7) +
               ((offset & 0x3f) << 6) + (((x & 16) >> 2) | ((~(y & 16)) >> 2) & 4) +
               ((((y >> 3) ^ x) & 2) | (((y >> 2) ^ x) & 1));
    }

    #endregion

    #region Region Extraction

    /// <summary>
    /// Extract a rectangular region from atlas data.
    /// Handles DXT block alignment.
    /// </summary>
    public static byte[]? ExtractAtlasRegion(byte[] atlasData, int atlasWidth, int atlasHeight,
        int regionX, int regionY, int regionWidth, int regionHeight, uint format)
    {
        int blockSize = GetBlockSize(format);
        int blockWidth = 4; // DXT block size in pixels
        int blockHeight = 4;

        // Calculate block counts
        int atlasBlocksX = (atlasWidth + blockWidth - 1) / blockWidth;
        int atlasBlocksY = (atlasHeight + blockHeight - 1) / blockHeight;
        int regionBlocksX = (regionWidth + blockWidth - 1) / blockWidth;
        int regionBlocksY = (regionHeight + blockHeight - 1) / blockHeight;
        int startBlockX = regionX / blockWidth;
        int startBlockY = regionY / blockHeight;

        int outputSize = regionBlocksX * regionBlocksY * blockSize;
        var output = new byte[outputSize];

        int destOffset = 0;
        for (int by = 0; by < regionBlocksY; by++)
        {
            int srcBlockY = startBlockY + by;
            if (srcBlockY >= atlasBlocksY) break;

            for (int bx = 0; bx < regionBlocksX; bx++)
            {
                int srcBlockX = startBlockX + bx;
                if (srcBlockX >= atlasBlocksX) continue;

                int srcOffset = (srcBlockY * atlasBlocksX + srcBlockX) * blockSize;

                if (srcOffset + blockSize <= atlasData.Length && destOffset + blockSize <= output.Length)
                {
                    Array.Copy(atlasData, srcOffset, output, destOffset, blockSize);
                }
                destOffset += blockSize;
            }
        }

        return output;
    }

    /// <summary>
    /// Interleave two horizontal chunks into a single texture.
    /// </summary>
    public static byte[] InterleaveHorizontalChunks(byte[] leftChunk, byte[] rightChunk,
        int leftWidth, int rightWidth, int height, uint format)
    {
        int blockSize = GetBlockSize(format);
        int totalWidth = leftWidth + rightWidth;
        int totalBlocksWide = (totalWidth + 3) / 4;
        int leftBlocksWide = (leftWidth + 3) / 4;
        int rightBlocksWide = (rightWidth + 3) / 4;
        int blocksHigh = (height + 3) / 4;

        var result = new byte[totalBlocksWide * blocksHigh * blockSize];

        for (int y = 0; y < blocksHigh; y++)
        {
            // Copy left chunk blocks
            for (int x = 0; x < leftBlocksWide; x++)
            {
                int srcOffset = (y * leftBlocksWide + x) * blockSize;
                int dstOffset = (y * totalBlocksWide + x) * blockSize;
                if (srcOffset + blockSize <= leftChunk.Length && dstOffset + blockSize <= result.Length)
                {
                    Array.Copy(leftChunk, srcOffset, result, dstOffset, blockSize);
                }
            }

            // Copy right chunk blocks
            for (int x = 0; x < rightBlocksWide; x++)
            {
                int srcOffset = (y * rightBlocksWide + x) * blockSize;
                int dstOffset = (y * totalBlocksWide + leftBlocksWide + x) * blockSize;
                if (srcOffset + blockSize <= rightChunk.Length && dstOffset + blockSize <= result.Length)
                {
                    Array.Copy(rightChunk, srcOffset, result, dstOffset, blockSize);
                }
            }
        }

        return result;
    }

    #endregion
}
