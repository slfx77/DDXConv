namespace DDXConv;

/// <summary>
///     Writes DDS (DirectDraw Surface) files.
///     DDS is the standard texture format used for DirectX games.
/// </summary>
public static class DdsWriter
{
    // DDS file constants
    private const uint DDS_MAGIC = 0x20534444; // "DDS "
    
    // DDS header flags (DDSD_*
    private const uint DDSD_CAPS = 0x1;
    private const uint DDSD_HEIGHT = 0x2;
    private const uint DDSD_WIDTH = 0x4;
    private const uint DDSD_PIXELFORMAT = 0x1000;
    private const uint DDSD_MIPMAPCOUNT = 0x20000;
    private const uint DDSD_LINEARSIZE = 0x80000;
    
    // DDS pixel format flags
    private const uint DDPF_FOURCC = 0x4;
    
    // DDS caps flags (DDSCAPS_*
    private const uint DDSCAPS_TEXTURE = 0x1000;
    private const uint DDSCAPS_MIPMAP = 0x400000;
    private const uint DDSCAPS_COMPLEX = 0x8;

    /// <summary>
    ///     Write a DDS file with the given texture data.
    /// </summary>
    public static void WriteDdsFile(string outputPath, D3DTextureInfo texture, byte[] mainData)
    {
        ArgumentNullException.ThrowIfNull(texture);

        using var stream = File.Create(outputPath);
        using var writer = new BinaryWriter(stream);

        // DDS magic
        writer.Write(DDS_MAGIC);

        // DDS header (124 bytes)
        WriteDdsHeader(writer, texture);

        // Write texture data
        writer.Write(mainData);
    }

    /// <summary>
    ///     Write a DDS file with explicit dimensions and format.
    /// </summary>
    public static void WriteDdsFile(string outputPath, int width, int height, int mipLevels, uint format, byte[] data)
    {
        var texture = new D3DTextureInfo
        {
            Width = (ushort)width,
            Height = (ushort)height,
            MipLevels = (byte)mipLevels,
            ActualFormat = format,
            Format = TextureUtilities.GetDxgiFormat(format)
        };
        WriteDdsFile(outputPath, texture, data);
    }

    private static void WriteDdsHeader(BinaryWriter writer, D3DTextureInfo texture)
    {
        var flags = DDSD_CAPS | DDSD_HEIGHT | DDSD_WIDTH | DDSD_PIXELFORMAT | DDSD_LINEARSIZE;
        if (texture.MipLevels > 1) flags |= DDSD_MIPMAPCOUNT;

        var caps = DDSCAPS_TEXTURE;
        if (texture.MipLevels > 1) caps |= DDSCAPS_MIPMAP | DDSCAPS_COMPLEX;

        // Size (always 124 for standard DDS header)
        writer.Write(124u);
        // Flags
        writer.Write(flags);
        // Height
        writer.Write(texture.Height);
        // Width
        writer.Write(texture.Width);
        // Pitch or linear size (size of top-level mip)
        writer.Write(TextureUtilities.CalculateMipSize(texture.Width, texture.Height, texture.ActualFormat));
        // Depth
        writer.Write(0u);
        // Mip map count
        writer.Write(texture.MipLevels);

        // Reserved (11 dwords)
        for (var i = 0; i < 9; i++) writer.Write(0u);
        writer.Write(0x4E41524B); // add branding "KRAN" in same location as nvidia's "NVTT"
        writer.Write(0u);

        // Pixel format
        WriteDdsPixelFormat(writer, texture.Format);

        // Caps
        writer.Write(caps);
        // Caps2
        writer.Write(0u);
        // Caps3
        writer.Write(0u);
        // Caps4
        writer.Write(0u);
        // Reserved
        writer.Write(0u);
    }

    private static void WriteDdsPixelFormat(BinaryWriter writer, uint fourccCode)
    {
        // Size (always 32)
        writer.Write(32u);
        // Flags
        writer.Write(DDPF_FOURCC);
        // FourCC
        writer.Write(fourccCode);
        // RGB bit count (not used for compressed)
        writer.Write(0u);
        // R/G/B/A bit masks (not used for compressed)
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
    }
}
