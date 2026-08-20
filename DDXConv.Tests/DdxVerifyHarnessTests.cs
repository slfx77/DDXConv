using DDXConv.Tests.Support;
using Xunit;

namespace DDXConv.Tests;

public class DdxVerifyHarnessTests
{
    // ── HashPart / WasLossy manifest-value parsing ───────────────────────────────────────────

    [Theory]
    [InlineData("abc123", "abc123")] // old format: hash only
    [InlineData("abc123|s=0,u=0,d=0,p=0,t=0,n=64", "abc123")]
    [InlineData("__FAIL__", "__FAIL__")]
    [InlineData("__FAIL__|s=0,u=0,d=0,p=0,t=0,n=0", "__FAIL__")]
    public void HashPart_ReturnsHashForOldAndNewFormats(string value, string expected)
    {
        Assert.Equal(expected, DdxVerifyHarness.HashPart(value));
    }

    [Theory]
    [InlineData("h|s=1,u=0,d=0,p=0,t=0,n=64", true)]
    [InlineData("h|s=0,u=3,d=0,p=0,t=0,n=64", true)]
    [InlineData("h|s=0,u=0,d=2,p=0,t=0,n=64", true)]
    [InlineData("h|s=0,u=0,d=0,p=128,t=0,n=64", true)]
    [InlineData("h|s=0,u=0,d=0,p=0,t=1,n=64", true)]
    [InlineData("h|s=0,u=0,d=0,p=0,t=0,n=64", false)] // clean counters
    [InlineData("h|s=0,u=0,d=0,p=0,t=0,n=0", false)] // n is not a loss signal
    [InlineData("h", false)] // old format: no counters -> conservatively not lossy
    public void WasLossy_ReadsLossCountersOnly(string value, bool expected)
    {
        Assert.Equal(expected, DdxVerifyHarness.WasLossy(value));
    }

    // ── End-to-end golden write + verify over a synthetic corpus ─────────────────────────────

    private static string MakeCorpusDir(out string goodA, out string goodB, out string garbage)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ddxconv_harness_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        goodA = Path.Combine(dir, "a.ddx");
        File.WriteAllBytes(goodA, SyntheticDdx.Build3Xdo(128, 128, SyntheticDdx.Dxt1,
            SyntheticDdx.IndexStampedBlocks(32 * 32, 8)));

        goodB = Path.Combine(dir, "b.ddx");
        File.WriteAllBytes(goodB, SyntheticDdx.Build3Xdo(64, 64, SyntheticDdx.Dxt5,
            SyntheticDdx.IndexStampedBlocks(16 * 16, 16)));

        garbage = Path.Combine(dir, "broken.ddx");
        File.WriteAllBytes(garbage, [0x33, 0x58, 0x44, 0x4F, 1, 2, 3]); // 3XDO magic, truncated

        return dir;
    }

    [Fact]
    public void WriteGolden_RecordsFailSentinelWithCounters_AndVerifyAgainstSelfPasses()
    {
        var dir = MakeCorpusDir(out _, out _, out _);
        try
        {
            var manifest = Path.Combine(dir, "golden.manifest");
            Assert.Equal(0, DdxVerifyHarness.Run(dir, manifest, writeGolden: true, limit: 0));

            var rows = File.ReadAllLines(manifest)
                .Where(static l => l.Length > 0 && l[0] != '#')
                .ToDictionary(static l => l[..l.IndexOf('|')], static l => l[(l.IndexOf('|') + 1)..]);

            Assert.Equal(3, rows.Count);
            Assert.Equal("__FAIL__", DdxVerifyHarness.HashPart(rows["broken.ddx"]));

            // The failure row still carries a counters field (that is what broke the fail count
            // before HashPart was used), and the harness must count it as a failure regardless.
            Assert.Contains('|', rows["broken.ddx"]);
            Assert.Equal(1, rows.Values.Count(static v => DdxVerifyHarness.HashPart(v) == "__FAIL__"));

            // Decodes are deterministic: verifying against the just-written golden matches.
            Assert.Equal(0, DdxVerifyHarness.Run(dir, manifest, writeGolden: false, limit: 0));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Verify_DetectsHashMismatch_AndParsesCounterlessGoldens()
    {
        var dir = MakeCorpusDir(out var goodA, out _, out _);
        try
        {
            var manifest = Path.Combine(dir, "golden.manifest");
            Assert.Equal(0, DdxVerifyHarness.Run(dir, manifest, writeGolden: true, limit: 0));

            // Strip the counters from every row (simulating a pre-counters golden) and corrupt
            // one hash. Verify must still parse the old format and flag exactly that mismatch.
            var tampered = File.ReadAllLines(manifest)
                .Select(static l =>
                {
                    if (l.Length == 0 || l[0] == '#')
                    {
                        return l;
                    }

                    var firstBar = l.IndexOf('|');
                    var secondBar = l.IndexOf('|', firstBar + 1);
                    return secondBar < 0 ? l : l[..secondBar];
                })
                .Select(static l => l.StartsWith("a.ddx|", StringComparison.Ordinal)
                    ? "a.ddx|0000000000000000000000000000000000000000000000000000000000000000"
                    : l)
                .ToArray();
            File.WriteAllLines(manifest, tampered);

            Assert.Equal(1, DdxVerifyHarness.Run(dir, manifest, writeGolden: false, limit: 0));
            Assert.True(File.Exists(goodA));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ── V3: decompress-saturation telemetry predicate ────────────────────────────────────────

    [Fact]
    public void ExactFitDecompressBuffer_RecordsNoTruncation()
    {
        // 128x128 DXT1: hint = 8192 (tiled mip0) + 24576 (sequential mips) = 32768, so the
        // decompress buffer is 65536. A payload decompressing to exactly that fills the buffer
        // with zero unread input — an exact fit, not loss. The old predicate (buffer-full
        // alone) fired on every such file.
        var payload = SyntheticDdx.IndexStampedBlocks(65536 / 8, 8);
        var ddx = SyntheticDdx.Build3Xdo(128, 128, SyntheticDdx.Dxt1, payload);

        var diagnostics = new DecodeDiagnostics();
        var dds = new DdxParser().ConvertDdxToDds(ddx, new ConversionOptions { Diagnostics = diagnostics });

        Assert.NotNull(dds);

        // The synthetic 2x-surface payload is not a recognizable mip layout, so the chunk
        // processor legitimately reports its trailing bytes; the DECOMPRESSOR, whose predicate
        // this test pins, must stay silent on an exact fit.
        Assert.DoesNotContain(diagnostics.TruncationReasons,
            static r => r.Contains("decompress buffer saturated", StringComparison.Ordinal));
    }

    [Fact]
    public void SaturatedDecompressBufferWithUnreadInput_RecordsTruncation()
    {
        // Same exact-fit payload, but with a stray trailing byte the saturated call cannot
        // consume: buffer full AND input left unread is the real truncation signature.
        var payload = SyntheticDdx.IndexStampedBlocks(65536 / 8, 8);
        var ddx = SyntheticDdx.Build3Xdo(128, 128, SyntheticDdx.Dxt1, payload);
        Array.Resize(ref ddx, ddx.Length + 1);
        ddx[^1] = 0xAB;

        var diagnostics = new DecodeDiagnostics();
        var dds = new DdxParser().ConvertDdxToDds(ddx, new ConversionOptions { Diagnostics = diagnostics });

        Assert.NotNull(dds);
        Assert.Contains(diagnostics.TruncationReasons,
            static r => r.Contains("decompress buffer saturated", StringComparison.Ordinal));
    }
}
