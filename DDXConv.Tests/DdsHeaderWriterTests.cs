using Xunit;

namespace DDXConv.Tests;

public sealed class DdsHeaderWriterTests
{
    [Fact]
    public void BuildDdsBytes_Dxt5_SetsAlphaPixelFlag()
    {
        var writer = new DdxHeaderWriter(false);
        var texture = CreateTextureInfo(0x54);

        var dds = writer.BuildDdsBytes(texture, new byte[16]);

        Assert.Equal(0x5u, ReadUInt32(dds, 80));
        Assert.Equal(0x35545844u, ReadUInt32(dds, 84));
    }

    [Fact]
    public void BuildDdsBytes_Dxt1TransparentBlock_SetsAlphaPixelFlag()
    {
        var writer = new DdxHeaderWriter(false);
        var texture = CreateTextureInfo(0x52);

        // BC1/DXT1 transparent mode: color0 <= color1 and at least one texel uses index 3.
        var dds = writer.BuildDdsBytes(texture, [0x00, 0x00, 0x01, 0x00, 0x03, 0x00, 0x00, 0x00]);

        Assert.Equal(0x5u, ReadUInt32(dds, 80));
        Assert.Equal(0x31545844u, ReadUInt32(dds, 84));
    }

    [Fact]
    public void BuildDdsBytes_Dxt1OpaqueBlock_DoesNotSetAlphaPixelFlag()
    {
        var writer = new DdxHeaderWriter(false);
        var texture = CreateTextureInfo(0x52);

        // Opaque BC1 block: color0 > color1, so the transparent palette entry is unavailable.
        var dds = writer.BuildDdsBytes(texture, [0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);

        Assert.Equal(0x4u, ReadUInt32(dds, 80));
        Assert.Equal(0x31545844u, ReadUInt32(dds, 84));
    }

    [Fact]
    public void BuildDdsBytes_A8R8G8B8_WritesUncompressedAlphaPixelFormat()
    {
        var writer = new DdxHeaderWriter(false);
        var texture = CreateTextureInfo(0x06);

        var dds = writer.BuildDdsBytes(texture, new byte[4 * 4 * 4]);

        Assert.Equal(0x41u, ReadUInt32(dds, 80));
        Assert.Equal(0u, ReadUInt32(dds, 84));
        Assert.Equal(32u, ReadUInt32(dds, 88));
        Assert.Equal(0x00FF0000u, ReadUInt32(dds, 92));
        Assert.Equal(0x0000FF00u, ReadUInt32(dds, 96));
        Assert.Equal(0x000000FFu, ReadUInt32(dds, 100));
        Assert.Equal(0xFF000000u, ReadUInt32(dds, 104));
    }

    private static D3DTextureInfo CreateTextureInfo(uint actualFormat)
    {
        return new D3DTextureInfo
        {
            Width = 4,
            Height = 4,
            ActualFormat = actualFormat,
            Format = TextureUtilities.GetDxgiFormat(actualFormat),
            MipLevels = 1
        };
    }

    private static uint ReadUInt32(byte[] data, int offset)
        => BitConverter.ToUInt32(data, offset);
}
