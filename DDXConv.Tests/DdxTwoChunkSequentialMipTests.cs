using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace DDXConv.Tests;

public sealed class DdxTwoChunkSequentialMipTests
{
    [Fact]
    public void CountSequentialStoredMipLevels_PartialEyeChain_ReturnsMip1AndMip2()
    {
        const uint format = 0x52; // DXT1
        var blockSize = TextureUtilities.GetBlockSize(format);

        Assert.Equal(2, DdxChunkProcessor.CountSequentialStoredMipLevels(128, 128, format, blockSize, 16384));
        Assert.Equal(7, DdxChunkProcessor.CountSequentialStoredMipLevels(128, 128, format, blockSize, 24576));
        Assert.Equal(0, DdxChunkProcessor.CountSequentialStoredMipLevels(128, 128, format, blockSize, 12288));
    }

    [Fact]
    public void ConvertDdxToDds_EyeTexture_PreservesRealMip2WithoutFabricatingDeeperLevels()
    {
        var repoRoot = FindRepoRoot();
        var xboxEye = Path.Combine(repoRoot, "Sample", "Textures", "textures_360_final", "textures", "characters",
            "eyes", "eyedefault.ddx");
        var pcEye = Path.Combine(repoRoot, "Sample", "Unpacked_Builds", "PC_Final_Unpacked", "Data", "textures",
            "characters", "eyes", "eyedefault.dds");

        Assert.True(File.Exists(xboxEye), $"Missing sample file: {xboxEye}");
        Assert.True(File.Exists(pcEye), $"Missing sample file: {pcEye}");

        var tempDir = Path.Combine(Path.GetTempPath(), $"ddxconv_eye_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var outputDds = Path.Combine(tempDir, "eyedefault.dds");
            var pcCopy = Path.Combine(tempDir, "pc_eyedefault.dds");
            File.Copy(pcEye, pcCopy);

            var parser = new DdxParser();
            parser.ConvertDdxToDds(xboxEye, outputDds, new ConversionOptions());

            var xboxMipPngs = DdsPostProcessor.ExportMipImages(outputDds);
            var pcMipPngs = DdsPostProcessor.ExportMipImages(pcCopy);

            Assert.Equal(3, xboxMipPngs.Length);
            Assert.True(pcMipPngs.Length >= 3, "Expected at least mip0..mip2 in PC reference DDS.");

            using var xboxMip1 = Image.Load<Rgba32>(xboxMipPngs[1]);
            using var xboxMip2 = Image.Load<Rgba32>(xboxMipPngs[2]);
            using var pcMip1 = Image.Load<Rgba32>(pcMipPngs[1]);
            using var pcMip2 = Image.Load<Rgba32>(pcMipPngs[2]);

            Assert.Equal((64, 64), (xboxMip1.Width, xboxMip1.Height));
            Assert.Equal((32, 32), (xboxMip2.Width, xboxMip2.Height));

            Assert.InRange(ComputeMeanAbsoluteRgbError(xboxMip1, pcMip1), 0.0, 1.0);
            Assert.InRange(ComputeMeanAbsoluteRgbError(xboxMip2, pcMip2), 0.0, 1.0);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    private static double ComputeMeanAbsoluteRgbError(Image<Rgba32> actual, Image<Rgba32> expected)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);

        double total = 0;
        var samples = actual.Width * actual.Height * 3;

        for (var y = 0; y < actual.Height; y++)
            for (var x = 0; x < actual.Width; x++)
            {
                var actualPixel = actual[x, y];
                var expectedPixel = expected[x, y];
                total += Math.Abs(actualPixel.R - expectedPixel.R);
                total += Math.Abs(actualPixel.G - expectedPixel.G);
                total += Math.Abs(actualPixel.B - expectedPixel.B);
            }

        return total / samples;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "Sample")))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repo root containing the Sample directory.");
    }
}
