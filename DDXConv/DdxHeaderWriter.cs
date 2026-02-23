namespace DDXConv;

/// <summary>
///     Handles DDS file writing and Xbox 360 D3D texture header parsing.
/// </summary>
internal sealed class DdxHeaderWriter(bool verboseLogging)
{
    private readonly bool _verboseLogging = verboseLogging;

    internal D3DTextureInfo ParseD3DTextureHeaderWithDimensions(byte[] header, out ushort width, out ushort height)
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

        // Format dwords at header offset 16 (file offset 0x18), stored little-endian

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

        // NOTE: The DDX file stores format dwords in a different layout than the raw
        // xe_gpu_texture_fetch_t structure documented in Xenia. Bit positions for Tiled,
        // Endian, and PackedMips read as 0/false for all tested textures, indicating the
        // byte ordering or field mapping differs. DDXConv determines tiling from the file
        // magic (3XDO=Morton, 3XDR=macro-block), endianness is always swapped, and mip
        // presence is detected from chunk count + data size heuristics.

        // Store the actual format for untiling
        info.ActualFormat = actualFormat != 0 ? actualFormat : info.DataFormat;

        // Determine DDS format
        info.Format = TextureUtilities.GetDxgiFormat(info.ActualFormat);

        // Calculate mip levels from dimensions — this is the theoretical max; the actual
        // count is validated against data size before writing (see CountMipLevelsFromDataSize)
        info.MipLevels = TextureUtilities.CalculateMipLevels(info.Width, info.Height);

        // Calculate main data size (before mip tail)
        info.MainDataSize =
            TextureUtilities.CalculateMainDataSize(info.Width, info.Height, info.ActualFormat, info.MipLevels);

        return info;
    }

    internal void WriteDdsFile(string outputPath, D3DTextureInfo texture, byte[] mainData)
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

    internal static uint CalculatePitch(uint width, uint format)
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
}
