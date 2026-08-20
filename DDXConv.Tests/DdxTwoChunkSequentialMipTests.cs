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
    public void ConvertDdxToDds_EyeTexture_DecodesTheFullEightMipChain()
    {
        // History: this test pinned 3 exported mips while the decompression hint stopped at the
        // linear mip-0 size — the 24,576-byte sequential mip chunk was silently truncated at
        // 16,384 and levels 3+ were lost. Round 1 of the decode fixes predicted this count
        // would grow once the hint covered the chain; the sizing fix made it 8 (mip0..mip7),
        // every level now validated against the PC reference below.
        var repoRoot = Support.SampleAssetGuard.RequireSampleRoot();
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

            Assert.Equal(8, xboxMipPngs.Length);
            Assert.True(pcMipPngs.Length >= 8, "Expected the full mip0..mip7 chain in the PC reference DDS.");

            for (var level = 0; level < 8; level++)
            {
                using var xboxMip = Image.Load<Rgba32>(xboxMipPngs[level]);
                using var pcMip = Image.Load<Rgba32>(pcMipPngs[level]);

                var expectedDim = Math.Max(1, 128 >> level);
                Assert.Equal((expectedDim, expectedDim), (xboxMip.Width, xboxMip.Height));

                var mae = ComputeMeanAbsoluteRgbError(xboxMip, pcMip);
                Assert.True(mae <= 1.0, $"mip {level}: MAE {mae:0.###} vs PC reference exceeds 1.0");
            }
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

}
