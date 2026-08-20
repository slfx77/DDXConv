using DDXConv.Tests.Support;
using Xunit;

namespace DDXConv.Tests;

/// <summary>
///     MAE control instruments: textures whose decode hash has been stable across every decode
///     fix and whose measured error against the PC reference is small. A control must both stay
///     small AND stay put — drift in either direction means the decoder or the oracle moved
///     under everyone's feet. (The previous scripted controls were dead instrumentation: one
///     pointed at a nonexistent path, the other always skipped on a resolution mismatch, and
///     neither ever produced a number.)
/// </summary>
public sealed class DdxMaeControlTests
{
    /// <summary>
    ///     Pinned from the post-fix corpus baseline (D6 golden, 2026-08-17); every file verified
    ///     hash-identical to the PRISTINE pre-fix golden, so these decode the same bytes they
    ///     did before any of the round-1/round-2 changes.
    /// </summary>
    public static TheoryData<string, double, double, double> Controls => new()
    {
        // relative path, expected m0, expected worst-mip, expected worst-alpha
        { @"architecture\strip\nv_thetops-sign03", 0.0, 0.0, 0.0 },
        { @"dlc03\effects\dlc03shockscroll", 0.0, 0.0, 0.0 },
        { @"weapons\2handautomatic\1stpersonlaserpdw_g", 0.002, 0.011, 0.0 },
        { @"architecture\repcon\repfxdustmyst", 0.0, 0.0, 0.373 },
        // 512² Xbox vs 1024² PC: measurable via the same-dims mip pairing (Xbox mip N ↔ PC
        // mip N+1) — the dims mismatch that permanently killed the old scripted control.
        { @"architecture\barracks\barracks01", 0.99, 1.37, 0.0 }
    };

    [Theory]
    [MemberData(nameof(Controls))]
    public void Controls_StaySmallAndImmobile(string relativePath, double expectedM0, double expectedWorst,
        double expectedWorstAlpha)
    {
        var repoRoot = SampleAssetGuard.RequireSampleRoot();
        var xboxPath = Path.Combine(repoRoot, @"Sample\Textures\textures_360_final\textures",
            relativePath + ".ddx");
        var pcPath = Path.Combine(repoRoot, @"Sample\Unpacked_Builds\PC_Final_Unpacked\Data\textures",
            relativePath + ".dds");
        Assert.True(File.Exists(xboxPath), $"Missing Xbox control file: {xboxPath}");
        Assert.True(File.Exists(pcPath), $"Missing PC control reference: {pcPath}");

        var dds = new DdxParser().ConvertDdxToDds(File.ReadAllBytes(xboxPath));
        var comparison = MaeOracle.Compare(dds, pcPath);

        Assert.NotNull(comparison);
        Assert.True(comparison.IsMeasurable, "control must be measurable — a skipped control measures nothing");
        Assert.Equal(expectedM0, comparison.Mip0Rgb, 0.1);
        Assert.Equal(expectedWorst, comparison.WorstRgb, 0.1);
        Assert.Equal(expectedWorstAlpha, comparison.WorstAlpha, 0.1);
    }
}
