using BCnEncoder.Encoder;
using BCnEncoder.ImageSharp;
using BCnEncoder.Shared;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace DDXConv.Tests;

public class MaeOracleTests
{
    /// <summary>Encodes an image to BC-compressed DDS bytes, with a full mip chain by default.</summary>
    private static byte[] EncodeDds(Image<Rgba32> image, CompressionFormat format = CompressionFormat.Bc3,
        bool mips = true)
    {
        var encoder = new BcEncoder
        {
            OutputOptions =
            {
                Format = format,
                FileFormat = OutputFileFormat.Dds, // the default is KTX, not DDS
                GenerateMipMaps = mips,
                Quality = CompressionQuality.Fast
            }
        };
        using var ms = new MemoryStream();
        encoder.EncodeToStream(image, ms);
        return ms.ToArray();
    }

    private static Image<Rgba32> SolidImage(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        var image = new Image<Rgba32>(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                image[x, y] = new Rgba32(r, g, b, a);
            }
        }

        return image;
    }

    private static string WriteTemp(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), "maeoracle_" + Guid.NewGuid().ToString("N") + ".dds");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void IdenticalPair_MeasuresZero_AcrossAllMips()
    {
        using var image = SolidImage(64, 64, 120, 80, 40);
        var dds = EncodeDds(image);
        var pcPath = WriteTemp(dds);
        try
        {
            var result = MaeOracle.Compare(dds, pcPath);
            Assert.NotNull(result);
            Assert.True(result.ComparedMips >= 6, $"expected the full mip chain, got {result.ComparedMips}");
            Assert.Equal(0.0, result.Mip0Rgb, 3);
            Assert.Equal(0.0, result.WorstRgb, 3);
            Assert.Equal(0.0, result.WorstAlpha, 3);
        }
        finally
        {
            File.Delete(pcPath);
        }
    }

    [Fact]
    public void KnownChannelShift_MeasuresExactMae()
    {
        // 99 and 132 are exact under RGB565's 5-bit red quantization (99 -> 12 -> 99,
        // 132 -> 16 -> 132), and 97 is exact under 6-bit green, so the +33 red shift must read
        // back as exactly 33/3 = 11.0 mean-per-channel RGB MAE with alpha untouched.
        using var a = SolidImage(32, 32, 99, 97, 99);
        using var b = SolidImage(32, 32, 132, 97, 99);
        var pcPath = WriteTemp(EncodeDds(b));
        try
        {
            var result = MaeOracle.Compare(EncodeDds(a), pcPath);
            Assert.NotNull(result);
            Assert.Equal(11.0, result.Mip0Rgb, 1);
            Assert.Equal(11.0, result.WorstRgb, 1);
            Assert.Equal(0.0, result.WorstAlpha, 1);
        }
        finally
        {
            File.Delete(pcPath);
        }
    }

    [Fact]
    public void AlphaOnlyDivergence_IsCaughtByAlphaMae_NotRgb()
    {
        // The scripted oracle this replaces summed RGB only — DXT5 spec-in-alpha damage was
        // invisible. The alpha channel must be first-class.
        using var a = SolidImage(32, 32, 100, 100, 100, 255);
        using var b = SolidImage(32, 32, 100, 100, 100, 155);
        var pcPath = WriteTemp(EncodeDds(b));
        try
        {
            var result = MaeOracle.Compare(EncodeDds(a), pcPath);
            Assert.NotNull(result);
            Assert.Equal(0.0, result.WorstRgb, 1);
            Assert.Equal(100.0, result.WorstAlpha, 1);
        }
        finally
        {
            File.Delete(pcPath);
        }
    }

    [Fact]
    public void HalfResConverted_PairsWithReferenceMip1()
    {
        // Xbox normal maps ship at half the PC resolution: the converted 32x32 mip 0 must be
        // judged against the 64x64 reference's mip 1 (same dims), not skipped as a dim mismatch.
        using var converted = SolidImage(32, 32, 90, 90, 90);
        using var reference = SolidImage(64, 64, 90, 90, 90);
        var pcPath = WriteTemp(EncodeDds(reference));
        try
        {
            var result = MaeOracle.Compare(EncodeDds(converted), pcPath);
            Assert.NotNull(result);
            Assert.True(result.IsMeasurable, "half-res pair must be measurable via same-dims mips");
            Assert.Equal(0.0, result.WorstRgb, 1);
        }
        finally
        {
            File.Delete(pcPath);
        }
    }

    [Fact]
    public void DamagedDeepMip_IsCaughtByWorstMip_NotMip0()
    {
        // Mip-chain truncation hides below a clean mip 0 — the exact blind spot of a mip0-only
        // oracle. Take two identical encodes and zero out mip 1's blocks in one of them:
        // mip 0 stays exact while worst-mip must light up. BC3 64x64: mip 0 = 16x16 blocks x 16
        // bytes at offset 128; mip 1 = 8x8 blocks x 16 bytes right after it.
        using var image = SolidImage(64, 64, 200, 200, 200);
        var reference = EncodeDds(image);
        var damaged = (byte[])reference.Clone();
        var mip1Offset = 128 + 16 * 16 * 16;
        Array.Clear(damaged, mip1Offset, 8 * 8 * 16);

        var pcPath = WriteTemp(reference);
        try
        {
            var result = MaeOracle.Compare(damaged, pcPath);
            Assert.NotNull(result);
            Assert.Equal(0.0, result.Mip0Rgb, 1);
            Assert.True(result.WorstRgb > 50,
                $"worst-mip ({result.WorstRgb:0.##}) must expose the deep-mip damage mip0 hides");
        }
        finally
        {
            File.Delete(pcPath);
        }
    }

    [Fact]
    public void ZeroMipCountReference_IsPatchedAndDecoded()
    {
        // Vanilla PC interface DDS declare dwMipMapCount = 0 and carry only mip 0's data;
        // BCnEncoder returns an empty result for them until the count is patched to 1.
        using var image = SolidImage(32, 8, 60, 70, 80);
        var dds = EncodeDds(image, mips: false);

        var zeroMips = (byte[])dds.Clone();
        BitConverter.GetBytes(0).CopyTo(zeroMips, 28); // dwMipMapCount = 0
        BitConverter.GetBytes(BitConverter.ToInt32(zeroMips, 8) & ~0x20000).CopyTo(zeroMips, 8);

        var images = MaeOracle.DecodeDds(zeroMips);
        try
        {
            Assert.NotEmpty(images);
            Assert.Equal(32, images[0].Width);
            Assert.Equal(8, images[0].Height);
        }
        finally
        {
            foreach (var i in images)
            {
                i.Dispose();
            }
        }
    }

    [Fact]
    public void RawUncompressedReference_DecodesThroughPixelMasks()
    {
        // Hand-build an 8x2 uncompressed A8R8G8B8 DDS (no FourCC) and check exact pixel readback.
        const int w = 8, h = 2;
        var dds = new byte[128 + w * h * 4];
        dds[0] = (byte)'D';
        dds[1] = (byte)'D';
        dds[2] = (byte)'S';
        dds[3] = (byte)' ';
        BitConverter.GetBytes(124).CopyTo(dds, 4);
        BitConverter.GetBytes(0x1 | 0x2 | 0x4 | 0x1000).CopyTo(dds, 8);
        BitConverter.GetBytes(h).CopyTo(dds, 12);
        BitConverter.GetBytes(w).CopyTo(dds, 16);
        BitConverter.GetBytes(32).CopyTo(dds, 76); // ddspf dwSize
        BitConverter.GetBytes(0x41).CopyTo(dds, 80); // DDPF_RGB | DDPF_ALPHAPIXELS
        BitConverter.GetBytes(32).CopyTo(dds, 88); // bit count
        BitConverter.GetBytes(0x00FF0000).CopyTo(dds, 92);
        BitConverter.GetBytes(0x0000FF00).CopyTo(dds, 96);
        BitConverter.GetBytes(0x000000FF).CopyTo(dds, 100);
        BitConverter.GetBytes(unchecked((int)0xFF000000)).CopyTo(dds, 104);
        for (var i = 0; i < w * h; i++)
        {
            var off = 128 + i * 4;
            dds[off] = 10; // B
            dds[off + 1] = 20; // G
            dds[off + 2] = 30; // R
            dds[off + 3] = 200; // A
        }

        var images = MaeOracle.DecodeDds(dds);
        try
        {
            Assert.NotEmpty(images);
            Assert.Equal(new Rgba32(30, 20, 10, 200), images[0][0, 0]);
        }
        finally
        {
            foreach (var i in images)
            {
                i.Dispose();
            }
        }
    }

    [Theory]
    [InlineData("m0=0.12;mw=0.31;mm=3;aw=0.05", 0.12, 0.31, 3, 0.05)]
    [InlineData("m0=149.65;mw=149.65;mm=1;aw=15.62", 149.65, 149.65, 1, 15.62)]
    public void ManifestField_RoundTrips(string field, double m0, double mw, int mm, double aw)
    {
        var parsed = MaeComparison.Parse(field);
        Assert.NotNull(parsed);
        Assert.Equal(m0, parsed.Mip0Rgb, 3);
        Assert.Equal(mw, parsed.WorstRgb, 3);
        Assert.Equal(mm, parsed.ComparedMips);
        Assert.Equal(aw, parsed.WorstAlpha, 3);
        Assert.Equal(field, parsed.ToManifestField());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("mae=-")]
    [InlineData("garbage")]
    public void ManifestField_ParsesAbsentAndUnmeasurableAsNull(string? field)
    {
        Assert.Null(MaeComparison.Parse(field));
    }

    private static MaeComparison WithMips(params (double Rgb, double Alpha)[] mips)
    {
        var c = new MaeComparison();
        foreach (var (rgb, alpha) in mips)
        {
            if (c.ComparedMips == 0)
            {
                c.Mip0Rgb = rgb;
            }

            c.WorstRgb = Math.Max(c.WorstRgb, rgb);
            c.WorstAlpha = Math.Max(c.WorstAlpha, alpha);
            c.RgbPerMip.Add(rgb);
            c.AlphaPerMip.Add(alpha);
            c.ComparedMips++;
        }

        return c;
    }

    [Fact]
    public void Judge_ChainRecovery_IsExtendedNotRegressed()
    {
        // Recovering a truncated mip chain adds newly-measured deep levels, which can only
        // raise a worst-over-all-mips aggregate — the exact artifact that mislabeled 678 chain
        // recoveries as regressions before per-mip judgment existed. Levels both runs measured
        // are unchanged here, so the verdict must be EXTENDED.
        var baseline = WithMips((0.1, 0.0), (0.2, 0.0), (0.3, 0.0));
        var now = WithMips((0.1, 0.0), (0.2, 0.0), (0.3, 0.0), (2.5, 8.0), (6.0, 30.0));
        Assert.Equal("EXTENDED", DdxVerifyHarness.Judge(baseline, now, 0.25));
    }

    [Fact]
    public void Judge_CommonLevelWorsening_IsRegressed_EvenWhenChainGrows()
    {
        var baseline = WithMips((0.1, 0.0), (0.2, 0.0));
        var now = WithMips((0.1, 0.0), (4.0, 0.0), (0.3, 0.0)); // mip 1 got worse
        Assert.Equal("REGRESSED", DdxVerifyHarness.Judge(baseline, now, 0.25));
    }

    [Fact]
    public void Judge_AlphaWorsening_IsRegressed()
    {
        var baseline = WithMips((0.1, 0.0));
        var now = WithMips((0.1, 12.0));
        Assert.Equal("REGRESSED", DdxVerifyHarness.Judge(baseline, now, 0.25));
    }

    [Fact]
    public void Judge_AggregateOnlyBaseline_DifferentMipCount_JudgesMip0Only()
    {
        // Old goldens carry no per-mip series; with a different mip count only mip 0 is
        // comparable, and clean mip 0 with more mips is EXTENDED.
        var baseline = new MaeComparison { Mip0Rgb = 0.1, WorstRgb = 0.3, WorstAlpha = 0.0, ComparedMips = 3 };
        var now = WithMips((0.15, 0.0), (0.2, 0.0), (0.3, 0.0), (7.0, 20.0));
        Assert.Equal("EXTENDED", DdxVerifyHarness.Judge(baseline, now, 0.25));

        var worseNow = WithMips((3.0, 0.0), (0.2, 0.0), (0.3, 0.0), (7.0, 20.0));
        Assert.Equal("REGRESSED", DdxVerifyHarness.Judge(baseline, worseNow, 0.25));
    }

    [Fact]
    public void Judge_PerMipImprovement_IsImproved()
    {
        var baseline = WithMips((5.0, 0.0), (0.2, 0.0));
        var now = WithMips((0.1, 0.0), (0.2, 0.0));
        Assert.Equal("IMPROVED", DdxVerifyHarness.Judge(baseline, now, 0.25));
    }

    [Fact]
    public void ManifestField_RoundTrips_WithPerMipSeries()
    {
        var c = WithMips((0.12, 0.0), (0.2, 0.05), (149.653, 15.62));
        var field = c.ToManifestField();
        Assert.Contains(";r=0.12/0.2/149.653;a=0/0.05/15.62", field);

        var parsed = MaeComparison.Parse(field);
        Assert.NotNull(parsed);
        Assert.Equal(c.RgbPerMip, parsed.RgbPerMip);
        Assert.Equal(c.AlphaPerMip, parsed.AlphaPerMip);
        Assert.Equal(field, parsed.ToManifestField());
    }

    [Fact]
    public void ManifestValueSegments_SplitCorrectly()
    {
        const string v3 = "abc|s=0,u=0,d=0,p=0,t=0,n=64|m0=1.5;mw=2;mm=2;aw=0";
        Assert.Equal("abc", DdxVerifyHarness.HashPart(v3));
        Assert.Equal("s=0,u=0,d=0,p=0,t=0,n=64", DdxVerifyHarness.CountersPart(v3));
        Assert.Equal("m0=1.5;mw=2;mm=2;aw=0", DdxVerifyHarness.MaePart(v3));
        Assert.False(DdxVerifyHarness.WasLossy(v3));

        const string v2 = "abc|s=1,u=0,d=0,p=0,t=0,n=64";
        Assert.Equal("s=1,u=0,d=0,p=0,t=0,n=64", DdxVerifyHarness.CountersPart(v2));
        Assert.Equal(string.Empty, DdxVerifyHarness.MaePart(v2));
        Assert.True(DdxVerifyHarness.WasLossy(v2));

        Assert.Equal(string.Empty, DdxVerifyHarness.CountersPart("hashonly"));
    }
}
