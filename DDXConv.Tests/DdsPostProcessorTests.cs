using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace DDXConv.Tests;

public sealed class DdsPostProcessorTests
{
    [Fact]
    public void ExportMipImages_Bc1MipChain_WritesOnePngPerMip()
    {
        var writer = new DdxHeaderWriter(false);
        var texture = new D3DTextureInfo
        {
            Width = 4,
            Height = 4,
            ActualFormat = 0x52,
            Format = TextureUtilities.GetDxgiFormat(0x52),
            MipLevels = 2
        };

        byte[] ddsBytes = writer.BuildDdsBytes(texture,
        [
            0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        ]);

        var tempDir = Path.Combine(Path.GetTempPath(), $"ddxconv_mips_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var ddsPath = Path.Combine(tempDir, "eye.dds");
            File.WriteAllBytes(ddsPath, ddsBytes);

            var pngPaths = DdsPostProcessor.ExportMipImages(ddsPath);

            Assert.Equal(2, pngPaths.Length);
            Assert.All(pngPaths, path => Assert.True(File.Exists(path)));

            using var mip0 = Image.Load<Rgba32>(pngPaths[0]);
            using var mip1 = Image.Load<Rgba32>(pngPaths[1]);

            Assert.Equal(4, mip0.Width);
            Assert.Equal(4, mip0.Height);
            Assert.Equal(2, mip1.Width);
            Assert.Equal(2, mip1.Height);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }
}
