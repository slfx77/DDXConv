using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace DDXConv.Tests;

public sealed class DdxRepresentativeMipRegressionTests
{
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    private const string XboxTexturesRoot = @"Sample\Textures\textures_360_final\textures";
    private const string PcTexturesRoot = @"Sample\Unpacked_Builds\PC_Final_Unpacked\Data\textures";

    public static TheoryData<RegressionCase> RepresentativeCases =>
    [
        new RegressionCase("3xdo_base", @"architecture\bittersprings\nv_bs-refugee-tent02", 10,
            CompareMode.ExactPrefixRgb),
        new RegressionCase("3xdo_d", @"architecture\freeside\freesidewelcomesign_d", 10,
            CompareMode.ExactPrefixRgb),
        new RegressionCase("3xdo_e", @"architecture\goodsprings\nv_storeglass_e", 9, CompareMode.ExactPrefixRgb),
        new RegressionCase("3xdo_em", @"architecture\barrier\nv_noso_neonsignconnection03_em", 9,
            CompareMode.ExactPrefixRgb),
        new RegressionCase("3xdo_g", @"architecture\freeside\freesidewelcomesign_g", 9,
            CompareMode.ExactPrefixRgb),
        new RegressionCase("3xdo_hl", @"characters\hair\beardcircle_hl", 10, CompareMode.ExactPrefixRgb),
        // Re-baselined 3 -> 8 when the decompression hint grew to cover the sequential mip
        // chain: the old mip0-sized hint truncated this 128x128's 24,576-byte mip chunk at
        // 16,384 and silently dropped levels 3+ (same mechanism as the eye-texture test).
        new RegressionCase("3xdo_lod", @"architecture\barrier\nv_noso_neonsignconnection01_lod", 8,
            CompareMode.ExactPrefixRgb),
        new RegressionCase("3xdo_m", @"architecture\bittersprings\nv_signcallville_m", 9,
            CompareMode.ExactPrefixRgb),
        new RegressionCase("3xdo_n", @"architecture\barracks\barracks01_n", 9, CompareMode.CountOnly),
        new RegressionCase("3xdo_o", @"weapons\scope\duplexscope_o", 11, CompareMode.ExactPrefixRgb),
        new RegressionCase("3xdo_p", @"architecture\citadel\citadelrubblepile01_p", 10,
            CompareMode.ExactPrefixRgb),
        new RegressionCase("3xdo_s", @"dungeons\nvlucky38\nvpenthouse01_s", 9, CompareMode.CountOnly),
        new RegressionCase("3xdr_base", @"architecture\barracks\blackdoor", 1, CompareMode.ExactPrefixRgb),
        new RegressionCase("3xdr_d", @"architecture\legion\nv_soldierlod_d", 1, CompareMode.ExactPrefixRgb),
        new RegressionCase("3xdr_e", @"architecture\hooverdam\hvdamtarazzofloor_e", 1,
            CompareMode.ExactPrefixRgb),
        new RegressionCase("3xdr_em", @"clutter\food\vodka_em", 1, CompareMode.ExactPrefixRgb),
        new RegressionCase("3xdr_g", @"architecture\hooverdam\recessedlighting_g", 1,
            CompareMode.ExactPrefixRgb),
        new RegressionCase("3xdr_lod", @"architecture\barracks\barracks01_lod", 1,
            CompareMode.ExactPrefixRgb),
        new RegressionCase("3xdr_m", @"architecture\strip\nv_vault21_sign03_m", 1,
            CompareMode.ExactPrefixRgb),
        new RegressionCase("3xdr_n", @"architecture\barracks\barracks01_lod_n", 1, CompareMode.CountOnly),
        new RegressionCase("3xdr_p", @"architecture\noso\nv_noso_rowhouse_rubble_p", 1,
            CompareMode.ExactPrefixRgb),
        new RegressionCase("3xdr_s", @"dungeons\nvlucky38\nvpenthouseconcrete_s", 1,
            CompareMode.ExactPrefixRgb),

        // ── Layout-divergent surfaces (round-2 decode fixes). These shapes decoded to black
        // rectangles (or garbage) before the aligned-extent + tail-origin + routing fixes; each
        // one pins the class that used to fail. MAE budgets cover deep-mip drift on tiny levels.
        new RegressionCase("tail_sq_chalk", @"clutter\chalk", 4, CompareMode.ExactPrefixRgb),
        new RegressionCase("tail_sq_white", @"architecture\barracks\white", 5, CompareMode.ExactPrefixRgb),
        new RegressionCase("tail_sq_shade", @"shared\shadefade01lod", 5, CompareMode.ExactPrefixRgb),
        new RegressionCase("oneaxis_wide_e", @"effects\chromedull_e", 9, CompareMode.ExactPrefixRgb, 2.5),
        new RegressionCase("oneaxis_wide_fx", @"effects\fxlightrays", 8, CompareMode.ExactPrefixRgb, 2.0),
        new RegressionCase("oneaxis_wide_g", @"traps\lasertripwire01_g", 10, CompareMode.ExactPrefixRgb, 2.5),
        new RegressionCase("oneaxis_tall_fx", @"effects\explosionspark01", 8, CompareMode.ExactPrefixRgb, 3.0),
        new RegressionCase("oneaxis_tall_ui", @"interface\shared\background\dotted", 10,
            CompareMode.ExactPrefixRgb, 2.0),
        // PC reference declares dwMipMapCount=0, so its exporter yields no PNGs — this case
        // pins the recovered mip count; content is covered by the corpus MAE oracle.
        new RegressionCase("tail_strip_fade", @"interface\shared\line\fade_to_left", 9,
            CompareMode.CountOnly),
        new RegressionCase("chain_sinwave", @"clutter\sinwave01", 8, CompareMode.ExactPrefixRgb, 1.5)
    ];

    [Theory]
    [MemberData(nameof(RepresentativeCases))]
    public void RepresentativeCases_PreserveExpectedMipBehavior(RegressionCase regressionCase)
    {
        var repoRoot = Support.SampleAssetGuard.RequireSampleRoot();
        var xboxPath = Path.Combine(repoRoot, XboxTexturesRoot, regressionCase.RelativePath + ".ddx");
        var pcPath = Path.Combine(repoRoot, PcTexturesRoot, regressionCase.RelativePath + ".dds");

        Assert.True(File.Exists(xboxPath), $"Missing Xbox sample file: {xboxPath}");
        Assert.True(File.Exists(pcPath), $"Missing PC reference file: {pcPath}");

        var artifactRoot = Path.Combine(repoRoot, "TestOutput", "ddx_regression_matrix", regressionCase.Label);
        ResetDirectory(artifactRoot);

        var outputDds = Path.Combine(artifactRoot, Path.GetFileNameWithoutExtension(xboxPath) + ".dds");
        var pcCopy = Path.Combine(artifactRoot, "pc_" + Path.GetFileName(pcPath));
        File.Copy(pcPath, pcCopy, overwrite: true);

        var parser = new DdxParser();
        parser.ConvertDdxToDds(xboxPath, outputDds, new ConversionOptions());

        var xboxMipPngs = DdsPostProcessor.ExportMipImages(outputDds);
        var pcMipPngs = DdsPostProcessor.ExportMipImages(pcCopy);

        Assert.Equal(regressionCase.ExpectedMipCount, xboxMipPngs.Length);

        var metrics = new RegressionMetrics(
            regressionCase.Label,
            regressionCase.Mode.ToString(),
            regressionCase.ExpectedMipCount,
            xboxMipPngs.Length,
            pcMipPngs.Length,
            []);

        var compareCount = Math.Min(xboxMipPngs.Length, pcMipPngs.Length);
        var perMipMae = new List<double>(compareCount);
        for (var mip = 0; mip < compareCount; mip++)
        {
            using var xboxMip = Image.Load<Rgba32>(xboxMipPngs[mip]);
            using var pcMip = Image.Load<Rgba32>(pcMipPngs[mip]);

            if (regressionCase.Mode == CompareMode.ExactPrefixRgb)
            {
                Assert.Equal(pcMip.Width, xboxMip.Width);
                Assert.Equal(pcMip.Height, xboxMip.Height);

                perMipMae.Add(ComputeMeanAbsoluteRgbError(xboxMip, pcMip));
            }
            else
            {
                perMipMae.Add(xboxMip.Width == pcMip.Width && xboxMip.Height == pcMip.Height
                    ? ComputeMeanAbsoluteRgbError(xboxMip, pcMip)
                    : -1.0);
            }
        }

        metrics = metrics with { PerMipMeanAbsoluteRgbError = perMipMae };
        File.WriteAllText(Path.Combine(artifactRoot, "metrics.json"),
            JsonSerializer.Serialize(metrics, IndentedJsonOptions));

        WriteComparisonSheet(regressionCase.Label, artifactRoot, xboxMipPngs, pcMipPngs);

        if (regressionCase.Mode == CompareMode.ExactPrefixRgb)
            for (var mip = 0; mip < compareCount; mip++)
            {
                using var xboxMip = Image.Load<Rgba32>(xboxMipPngs[mip]);
                var maxAllowedError = regressionCase.MaeBudget ??
                                      (Math.Max(xboxMip.Width, xboxMip.Height) <= 4 ? 4.0 : 1.0);
                Assert.InRange(perMipMae[mip], 0.0, maxAllowedError);
            }
    }

    private static void WriteComparisonSheet(string label, string artifactRoot, string[] xboxMipPngs,
        string[] pcMipPngs)
    {
        var rowCount = Math.Min(Math.Min(xboxMipPngs.Length, pcMipPngs.Length), 6);
        if (rowCount == 0)
        {
            // PC interface DDS with dwMipMapCount=0 export no mip PNGs — nothing to sheet.
            return;
        }

        const int cellSize = 160;
        const int gap = 12;
        using var sheet = new Image<Rgba32>(cellSize * 2 + gap, rowCount * (cellSize + gap) - gap, Color.White);

        for (var row = 0; row < rowCount; row++)
        {
            using var xboxMip = Image.Load<Rgba32>(xboxMipPngs[row]);
            using var pcMip = Image.Load<Rgba32>(pcMipPngs[row]);

            using var scaledXbox = ResizeForCell(xboxMip, cellSize);
            using var scaledPc = ResizeForCell(pcMip, cellSize);

            var y = row * (cellSize + gap);
            PasteCentered(sheet, scaledXbox, 0, y, cellSize, cellSize);
            PasteCentered(sheet, scaledPc, cellSize + gap, y, cellSize, cellSize);
        }

        sheet.SaveAsPng(Path.Combine(artifactRoot, $"{label}_comparison.png"));
    }

    private static Image<Rgba32> ResizeForCell(Image<Rgba32> image, int cellSize)
    {
        var scale = Math.Min((double)cellSize / image.Width, (double)cellSize / image.Height);
        var width = Math.Max(1, (int)Math.Round(image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(image.Height * scale));
        return image.Clone(ctx => ctx.Resize(width, height, KnownResamplers.NearestNeighbor));
    }

    private static void PasteCentered(Image<Rgba32> canvas, Image<Rgba32> image, int cellX, int cellY, int cellW,
        int cellH)
    {
        var x = cellX + (cellW - image.Width) / 2;
        var y = cellY + (cellH - image.Height) / 2;
        canvas.Mutate(ctx => ctx.DrawImage(image, new Point(x, y), 1f));
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

    private static void ResetDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, true);

        Directory.CreateDirectory(path);
    }

    /// <param name="MaeBudget">
    ///     Optional per-case ceiling for the per-mip RGB MAE, overriding the defaults
    ///     (1.0, or 4.0 for mips no larger than 4px). Used by shapes whose deepest mips
    ///     legitimately drift a little further from the PC encoder's chain.
    /// </param>
    public sealed record RegressionCase(
        string Label, string RelativePath, int ExpectedMipCount, CompareMode Mode, double? MaeBudget = null);

    public enum CompareMode
    {
        ExactPrefixRgb,
        CountOnly
    }

    private sealed record RegressionMetrics(
        string Label,
        string Mode,
        int ExpectedMipCount,
        int ActualMipCount,
        int ReferenceMipCount,
        IReadOnlyList<double> PerMipMeanAbsoluteRgbError);
}
