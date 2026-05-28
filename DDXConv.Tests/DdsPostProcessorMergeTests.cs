using BCnEncoder.Encoder;
using BCnEncoder.ImageSharp;
using BCnEncoder.Shared;
using BCnEncoder.Shared.ImageFiles;
using DDXConv;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace DDXConv.Tests;

/// <summary>
///     Pins the in-memory normal-map merge that the asset packer uses to re-encode Xbox 360
///     BC5/ATI2 normal maps as DXT5/BC3. Vanilla FNV ships normals as DXT5 with the specular
///     map packed into the alpha channel, and the runtime DDS loader does NOT accept BC5 —
///     it leaves the texture slot unbound and the renderer draws whatever stale memory the
///     slot pointed at. Regressing this back to BC5 would reintroduce the Ulysses-outfit
///     texture-swap bug.
/// </summary>
public class DdsPostProcessorMergeTests
{
    [Fact]
    public void MergeNormalSpecularMapsFromMemory_ProducesDxt5FromBc5Input()
    {
        var bc5Bytes = BuildSyntheticBc5Normal(64, 64);

        var merged = DdsPostProcessor.MergeNormalSpecularMapsFromMemory(bc5Bytes, null);

        Assert.True(merged.Length >= 88, "Merged output must contain at least the DDS header");
        Assert.Equal((byte)'D', merged[0]);
        Assert.Equal((byte)'D', merged[1]);
        Assert.Equal((byte)'S', merged[2]);
        Assert.Equal((byte)' ', merged[3]);

        // FourCC at offset 84 must be DXT5 — the format vanilla FNV expects for normal maps.
        Assert.Equal((byte)'D', merged[84]);
        Assert.Equal((byte)'X', merged[85]);
        Assert.Equal((byte)'T', merged[86]);
        Assert.Equal((byte)'5', merged[87]);
    }

    [Fact]
    public void MergeNormalSpecularMapsFromMemory_StampsKranMarker()
    {
        var bc5Bytes = BuildSyntheticBc5Normal(64, 64);

        var merged = DdsPostProcessor.MergeNormalSpecularMapsFromMemory(bc5Bytes, null);

        // The on-disk variant stamps "KRAN" at 0x44; downstream sniffers (DdsTextureDecoder
        // etc.) check for it to identify packer-converted textures. The in-memory variant
        // must do the same so behavior matches.
        Assert.Equal((byte)'K', merged[0x44]);
        Assert.Equal((byte)'R', merged[0x45]);
        Assert.Equal((byte)'A', merged[0x46]);
        Assert.Equal((byte)'N', merged[0x47]);
    }

    [Fact]
    public void MergeNormalSpecularMapsFromMemory_AcceptsBc4Specular()
    {
        // BC4 spec map merged into the normal map's alpha channel — the FNV runtime treats
        // normal-map alpha as the specular/glossiness term. The merge has to accept BC4 input
        // as well as no-spec (gray fallback).
        var bc5Bytes = BuildSyntheticBc5Normal(64, 64);
        var bc4Bytes = BuildSyntheticBc4Specular(64, 64);

        var merged = DdsPostProcessor.MergeNormalSpecularMapsFromMemory(bc5Bytes, bc4Bytes);

        Assert.Equal((byte)'D', merged[84]);
        Assert.Equal((byte)'X', merged[85]);
        Assert.Equal((byte)'T', merged[86]);
        Assert.Equal((byte)'5', merged[87]);
    }

    private static byte[] BuildSyntheticBc5Normal(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                image[x, y] = new Rgba32((byte)(x % 256), (byte)(y % 256), 128, 255);
            }
        }

        return EncodeToDds(image, CompressionFormat.Bc5);
    }

    private static byte[] BuildSyntheticBc4Specular(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                image[x, y] = new Rgba32(200, 0, 0, 255);
            }
        }

        return EncodeToDds(image, CompressionFormat.Bc4);
    }

    private static byte[] EncodeToDds(Image<Rgba32> image, CompressionFormat format)
    {
        var encoder = new BcEncoder
        {
            OutputOptions =
            {
                GenerateMipMaps = false,
                Format = format,
                FileFormat = OutputFileFormat.Dds,
                Quality = CompressionQuality.Fast
            }
        };

        using var stream = new MemoryStream();
        encoder.EncodeToStream(image, stream);
        return stream.ToArray();
    }
}
