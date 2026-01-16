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

    #endregion
}
