using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace DDXConv;

/// <summary>
///     Non-regression correctness gate for the DDX → DDS decode path (LZX decompression + Xenon
///     untile + DDS assembly). Hashes the end-to-end decode output of every <c>.ddx</c> under a
///     directory and snapshots it to a golden manifest (<c>--write-golden</c>); a later run without
///     that flag re-decodes and asserts every hash still matches. Because the decode is exercised
///     exactly as the live viewer does it (<see cref="DdxParser.ConvertDdxToDds(byte[], ConversionOptions?)" />
///     with default options — the same call <c>NifTextureLoader.ConvertDdxIfNeeded</c> makes), a
///     change to the LZX decompressor or the untile path that alters any decoded byte is caught.
///     This is the gate that makes "output-preserving" DDX/LZX speedups provable: snapshot before,
///     change, re-verify identical.
/// </summary>
public static class DdxVerifyHarness
{
    // Stable sentinel recorded for any file whose decode throws or yields empty output. A
    // hash<->__FAIL__ transition (a file that used to decode now throwing, or vice versa) is itself
    // a regression the verify pass reports. The specific exception text is intentionally not part of
    // the key, so error-message wording changes don't show up as spurious diffs.
    private const string FailSentinel = "__FAIL__";

    /// <summary>How much of the corpus the MAE oracle measures during a run.</summary>
    public enum MaeMode
    {
        Off,

        /// <summary>Verify only: judge just the files whose decode hash changed (the default).</summary>
        Changed,

        /// <summary>Measure every file — used when writing a golden so it carries a full baseline.</summary>
        All
    }

    /// <summary>Oracle configuration for <see cref="Run" />; inert unless a PC reference tree is given.</summary>
    public sealed record OracleOptions
    {
        public string? PcReferenceRoot { get; init; }
        public MaeMode Mode { get; init; } = MaeMode.Changed;
        public string? ReportPath { get; init; }

        /// <summary>A changed file whose worst-mip MAE worsens by more than this fails the verify.</summary>
        public double Tolerance { get; init; } = 0.25;
    }

    /// <summary>
    ///     Returns 0 on success, 1 on a verify regression, 2 on a usage/IO error. Without a PC
    ///     reference tree, any hash mismatch is a failure (strict refactor gating). With one, hash
    ///     mismatches are judged by the MAE oracle instead: the gates are ok↔fail transitions and
    ///     files whose measured error got worse than the golden baseline.
    /// </summary>
    public static int Run(string ddxDir, string manifestPath, bool writeGolden, int limit,
        OracleOptions? oracle = null)
    {
        if (!Directory.Exists(ddxDir))
        {
            Console.WriteLine($"DDX directory not found: {ddxDir}");
            return 2;
        }

        var pcRef = oracle?.PcReferenceRoot;
        if (pcRef is not null && !Directory.Exists(pcRef))
        {
            Console.WriteLine($"PC reference directory not found: {pcRef}");
            return 2;
        }

        var files = Directory.GetFiles(ddxDir, "*.ddx", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.Ordinal);
        if (limit > 0 && files.Length > limit)
        {
            files = files[..limit];
        }

        if (files.Length == 0)
        {
            Console.WriteLine("No .ddx files found.");
            return 2;
        }

        // When writing a golden with a reference tree attached, measure everything so the
        // manifest carries a per-file MAE baseline for later delta judgment.
        var maeInline = pcRef is not null && oracle!.Mode != MaeMode.Off &&
                        (writeGolden || oracle.Mode == MaeMode.All);

        Console.WriteLine($"Hashing {files.Length:N0} DDX decode outputs (threads={Environment.ProcessorCount}" +
                          (maeInline ? ", MAE oracle inline" : string.Empty) + ")...");
        var results = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var coverage = new ConcurrentBag<FileFacts>();
        var done = 0;
        Parallel.ForEach(files, file =>
        {
            var rel = Path.GetRelativePath(ddxDir, file).Replace('\\', '/');
            string hash;
            var maeField = string.Empty;
            var facts = new FileFacts { Rel = rel };
            // One instance per file: this loop is parallel, and a shared counter inside a
            // data-loss detector would itself lose data.
            var diagnostics = new DecodeDiagnostics();
            facts.Diagnostics = diagnostics;
            try
            {
                var data = File.ReadAllBytes(file);
                facts.Magic = ReadMagic(data);
                var dds = new DdxParser().ConvertDdxToDds(data,
                    new ConversionOptions { Diagnostics = diagnostics });
                if (dds is { Length: > 0 })
                {
                    hash = Convert.ToHexString(SHA256.HashData(dds)).ToLowerInvariant();
                    DescribeDds(dds, ref facts);
                    if (maeInline)
                    {
                        maeField = MeasureMae(dds, pcRef!, rel);
                    }
                }
                else
                {
                    hash = FailSentinel;
                    facts.Ok = false;
                }
            }
            catch
            {
                hash = FailSentinel;
                facts.Ok = false;
            }

            results[rel] = $"{hash}|{diagnostics.ToManifestField()}" +
                           (maeField.Length > 0 ? $"|{maeField}" : string.Empty);
            coverage.Add(facts);
            var n = Interlocked.Increment(ref done);
            if (n % 5000 == 0)
            {
                Console.WriteLine($"  {n:N0}/{files.Length:N0}");
            }
        });

        PrintCoverage(coverage);

        return writeGolden
            ? WriteGolden(manifestPath, results)
            : Verify(manifestPath, results, ddxDir, oracle, coverage);
    }

    /// <summary>Measures one converted DDS against its PC pair; empty field when no pair exists.</summary>
    private static string MeasureMae(byte[] dds, string pcRefRoot, string rel)
    {
        var pcPath = MaeOracle.ResolvePcReference(pcRefRoot, rel);
        if (pcPath is null)
        {
            return string.Empty;
        }

        var comparison = MaeOracle.Compare(dds, pcPath);
        return comparison is null ? "mae=-" : comparison.ToManifestField();
    }

    private static int WriteGolden(string manifestPath, ConcurrentDictionary<string, string> results)
    {
        var dir = Path.GetDirectoryName(manifestPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using (var w = new StreamWriter(manifestPath, false))
        {
            w.WriteLine(
                $"# DDXConv decode golden manifest — {results.Count} entries — format: "
                + "relpath|sha256(dds)-or-__FAIL__|s=skippedCopies,u=unwrittenBlocks,"
                + "d=duplicateWrites,p=paddedBytes,t=truncatedReads,n=surfaceBlocks"
                + "[|m0=mip0Mae;mw=worstMipMae;mm=comparedMips;aw=worstAlphaMae, when a PC "
                + "reference tree was attached — the baseline later verifies judge deltas against]");
            foreach (var kv in results.OrderBy(static k => k.Key, StringComparer.Ordinal))
            {
                w.WriteLine($"{kv.Key}|{kv.Value}");
            }
        }

        var fails = results.Values.Count(static v => HashPart(v) == FailSentinel);
        Console.WriteLine(
            $"Wrote golden manifest: {manifestPath} ({results.Count:N0} entries, {fails:N0} decode failures recorded)");
        return 0;
    }

    private static int Verify(string manifestPath, ConcurrentDictionary<string, string> results,
        string ddxDir, OracleOptions? oracle, ConcurrentBag<FileFacts> coverage)
    {
        if (!File.Exists(manifestPath))
        {
            Console.WriteLine($"Manifest not found (run with --write-golden first): {manifestPath}");
            return 2;
        }

        var golden = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(manifestPath))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var bar = line.IndexOf('|');
            if (bar > 0)
            {
                golden[line[..bar]] = line[(bar + 1)..];
            }
        }

        int matched = 0, mismatched = 0, newFiles = 0, missing = 0, failTransitions = 0;
        var changed = new List<string>();
        foreach (var kv in results)
        {
            if (!golden.TryGetValue(kv.Key, out var g))
            {
                newFiles++;
                continue;
            }

            // Compare the decode hash only. The counters trailing it are diagnostics, and a
            // counter that moves while the bytes stay identical is not a change in output.
            var goldenHash = HashPart(g);
            var currentHash = HashPart(kv.Value);
            if (goldenHash == currentHash)
            {
                matched++;
                continue;
            }

            mismatched++;
            changed.Add(kv.Key);

            // A file that used to decode and now throws (or vice versa) is always a regression,
            // and would otherwise hide inside the mismatch count.
            if (goldenHash == FailSentinel || currentHash == FailSentinel)
            {
                failTransitions++;
            }
        }

        foreach (var key in golden.Keys)
        {
            if (!results.ContainsKey(key))
            {
                missing++;
            }
        }

        Console.WriteLine();
        Console.WriteLine("DDX decode verify:");
        Console.WriteLine($"  matched:                            {matched:N0}");
        Console.WriteLine($"  MISMATCHED:                         {mismatched:N0}");
        Console.WriteLine($"    decode ok<->__FAIL__ transitions:  {failTransitions:N0} (must be 0)");
        Console.WriteLine($"  new (not in golden):                {newFiles:N0}");
        Console.WriteLine($"  missing (in golden, not found now): {missing:N0}");
        foreach (var s in changed.Take(25))
        {
            Console.WriteLine($"    {s}: golden={Short(HashPart(golden[s]))} now={Short(HashPart(results[s]))}");
        }

        PrintCounterMovement(golden, results);

        // Without a reference tree every mismatch is a failure (strict refactor gating). With
        // one, changed files are judged by the MAE oracle against the golden's recorded
        // baseline: the write-coverage counters are structurally blind to wrong-but-in-range
        // reads, so pixels vs the PC reference — not counters — decide whether a change is a
        // fix or a regression.
        if (oracle?.PcReferenceRoot is null || oracle.Mode == MaeMode.Off)
        {
            return mismatched == 0 ? 0 : 1;
        }

        var regressed = JudgeChangedFiles(golden, results, changed, ddxDir, oracle, coverage);
        return failTransitions > 0 || regressed > 0 ? 1 : 0;
    }

    /// <summary>
    ///     MAE-judges changed files against the golden baseline and prints the classification.
    ///     Returns the number of files whose measured error got worse than the tolerance.
    /// </summary>
    private static int JudgeChangedFiles(Dictionary<string, string> golden,
        ConcurrentDictionary<string, string> results, List<string> changed, string ddxDir,
        OracleOptions oracle, ConcurrentBag<FileFacts> coverage)
    {
        if (changed.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("MAE oracle: no changed files to judge.");
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine($"MAE oracle: judging {changed.Count:N0} changed files against {oracle.PcReferenceRoot}");

        // Re-decode just the changed files unless the inline pass already measured everything.
        var current = new ConcurrentDictionary<string, MaeComparison?>(StringComparer.Ordinal);
        Parallel.ForEach(changed, rel =>
        {
            var maeField = MaePart(results[rel]);
            if (maeField.Length == 0 && HashPart(results[rel]) != FailSentinel)
            {
                try
                {
                    var dds = new DdxParser().ConvertDdxToDds(
                        File.ReadAllBytes(Path.Combine(ddxDir, rel)), new ConversionOptions());
                    maeField = MeasureMae(dds, oracle.PcReferenceRoot!, rel);
                }
                catch
                {
                    maeField = string.Empty;
                }
            }

            current[rel] = MaeComparison.Parse(maeField);
        });

        var formats = coverage.Where(static f => f.Rel is not null)
            .ToDictionary(static f => f.Rel!, static f => f.Format ?? "?", StringComparer.Ordinal);

        int improved = 0, neutral = 0, regressed = 0, extended = 0, noPair = 0, noBaseline = 0;
        var regressedSamples = new List<string>();
        var noPairFiles = new List<string>();
        var report = oracle.ReportPath is null ? null : new StreamWriter(oracle.ReportPath, false);
        try
        {
            report?.WriteLine("relpath,class,golden_m0,golden_mw,golden_aw,current_m0,current_mw,current_aw");
            foreach (var rel in changed.OrderBy(static r => r, StringComparer.Ordinal))
            {
                var baseline = MaeComparison.Parse(MaePart(golden[rel]));
                var now = current.GetValueOrDefault(rel);

                string cls;
                if (now is null)
                {
                    cls = "NO-PAIR";
                    noPair++;
                    noPairFiles.Add(rel);
                }
                else if (baseline is null)
                {
                    cls = "NO-BASELINE";
                    noBaseline++;
                }
                else
                {
                    cls = Judge(baseline, now, oracle.Tolerance);
                    switch (cls)
                    {
                        case "REGRESSED":
                            regressed++;
                            if (regressedSamples.Count < 25)
                            {
                                regressedSamples.Add(
                                    $"{rel}: m0 {baseline.Mip0Rgb:0.##}->{now.Mip0Rgb:0.##} mw {baseline.WorstRgb:0.##}->{now.WorstRgb:0.##} " +
                                    $"aw {baseline.WorstAlpha:0.##}->{now.WorstAlpha:0.##} mm {baseline.ComparedMips}->{now.ComparedMips}");
                            }

                            break;
                        case "IMPROVED":
                            improved++;
                            break;
                        case "EXTENDED":
                            extended++;
                            break;
                        default:
                            neutral++;
                            break;
                    }
                }

                report?.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"{rel},{cls},{Field(baseline?.Mip0Rgb)},{Field(baseline?.WorstRgb)},{Field(baseline?.WorstAlpha)},{Field(now?.Mip0Rgb)},{Field(now?.WorstRgb)},{Field(now?.WorstAlpha)}"));
            }
        }
        finally
        {
            report?.Dispose();
        }

        Console.WriteLine($"  IMPROVED:    {improved:N0}");
        Console.WriteLine($"  NEUTRAL:     {neutral:N0} (within ±{oracle.Tolerance:0.##})");
        Console.WriteLine($"  EXTENDED:    {extended:N0} (mip chain grew; measured levels within tolerance)");
        Console.WriteLine($"  REGRESSED:   {regressed:N0} (must be 0)");
        Console.WriteLine($"  NO-BASELINE: {noBaseline:N0} (golden had no MAE for these)");
        Console.WriteLine($"  NO-PAIR:     {noPair:N0} (no measurable PC reference — unjudged)");
        foreach (var s in regressedSamples)
        {
            Console.WriteLine($"    REGRESSED {s}");
        }

        PrintStratifiedSample(noPairFiles, formats);
        if (oracle.ReportPath is not null)
        {
            Console.WriteLine($"  full per-file report: {oracle.ReportPath}");
        }

        return regressed;
    }

    /// <summary>
    ///     Classifies a changed file's measured error against its baseline.
    ///     <para>
    ///         When both sides carry per-mip series, levels are compared pairwise over the
    ///         common prefix — the only apples-to-apples comparison when the number of decoded
    ///         mips changed, since a worst-over-all-mips aggregate can only rise when a
    ///         recovered chain adds newly-measured deep levels. Chain growth with clean common
    ///         levels is EXTENDED, not REGRESSED. Against an aggregate-only baseline the same
    ///         principle degrades gracefully: equal mip counts are judged on the worst-mip
    ///         delta, differing counts on mip 0 alone.
    ///     </para>
    /// </summary>
    internal static string Judge(MaeComparison baseline, MaeComparison now, double tolerance)
    {
        bool worse, better;
        if (baseline.RgbPerMip.Count > 0 && now.RgbPerMip.Count > 0)
        {
            var common = Math.Min(baseline.RgbPerMip.Count, now.RgbPerMip.Count);
            worse = better = false;
            for (var i = 0; i < common; i++)
            {
                var rgbDelta = now.RgbPerMip[i] - baseline.RgbPerMip[i];
                var alphaDelta = i < baseline.AlphaPerMip.Count && i < now.AlphaPerMip.Count
                    ? now.AlphaPerMip[i] - baseline.AlphaPerMip[i]
                    : 0;
                worse |= rgbDelta > tolerance || alphaDelta > tolerance;
                better |= rgbDelta < -tolerance || alphaDelta < -tolerance;
            }
        }
        else if (baseline.ComparedMips == now.ComparedMips)
        {
            worse = now.WorstRgb - baseline.WorstRgb > tolerance ||
                    now.WorstAlpha - baseline.WorstAlpha > tolerance;
            better = baseline.WorstRgb - now.WorstRgb > tolerance ||
                     baseline.WorstAlpha - now.WorstAlpha > tolerance;
        }
        else
        {
            // Aggregate-only baseline with a different mip count: only mip 0 is comparable.
            worse = now.Mip0Rgb - baseline.Mip0Rgb > tolerance;
            better = baseline.Mip0Rgb - now.Mip0Rgb > tolerance;
        }

        if (worse)
        {
            return "REGRESSED";
        }

        if (better)
        {
            return "IMPROVED";
        }

        return now.ComparedMips > baseline.ComparedMips ? "EXTENDED" : "NEUTRAL";
    }

    private static string Field(double? value)
    {
        return value is null or < 0
            ? string.Empty
            : value.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    ///     Stratified sample of unjudged changed files: up to two per (top directory, format)
    ///     stratum, so no single texture family can dominate the listing the way a sorted-stride
    ///     sample of this corpus does.
    /// </summary>
    private static void PrintStratifiedSample(List<string> noPairFiles, Dictionary<string, string> formats)
    {
        if (noPairFiles.Count == 0)
        {
            return;
        }

        Console.WriteLine("  NO-PAIR sample (stratified by directory × format, ≤2 each):");
        var shown = 0;
        foreach (var stratum in noPairFiles
                     .GroupBy(rel => $"{TopDirectory(rel)} × {formats.GetValueOrDefault(rel, "?")}")
                     .OrderBy(static g => g.Key, StringComparer.Ordinal))
        {
            foreach (var rel in stratum.Take(2))
            {
                Console.WriteLine($"    [{stratum.Key}] {rel}");
                if (++shown >= 40)
                {
                    Console.WriteLine($"    … ({noPairFiles.Count:N0} total)");
                    return;
                }
            }
        }
    }

    private static string TopDirectory(string rel)
    {
        var parts = rel.Split('/');
        return parts.Length switch
        {
            > 2 when parts[0].Equals("textures", StringComparison.OrdinalIgnoreCase) => parts[1],
            > 1 => parts[0],
            _ => "."
        };
    }

    /// <summary>Aggregate loss-counter movement golden → current, for attribution at a glance.</summary>
    private static void PrintCounterMovement(Dictionary<string, string> golden,
        ConcurrentDictionary<string, string> results)
    {
        static (int Lossy, int Padded, int Truncated) Tally(IEnumerable<string> values)
        {
            int lossy = 0, padded = 0, truncated = 0;
            foreach (var v in values)
            {
                var counters = CountersPart(v);
                if (CounterAbove(counters, 's') || CounterAbove(counters, 'u') || CounterAbove(counters, 'd'))
                {
                    lossy++;
                }

                if (CounterAbove(counters, 'p'))
                {
                    padded++;
                }

                if (CounterAbove(counters, 't'))
                {
                    truncated++;
                }
            }

            return (lossy, padded, truncated);
        }

        var g = Tally(golden.Values);
        var c = Tally(results.Values);
        Console.WriteLine("  loss-counter movement (files, golden -> now): " +
                          $"block-loss {g.Lossy:N0} -> {c.Lossy:N0}, padded {g.Padded:N0} -> {c.Padded:N0}, " +
                          $"truncated {g.Truncated:N0} -> {c.Truncated:N0}");
    }

    private static bool CounterAbove(string counters, char key)
    {
        foreach (var field in counters.Split(','))
        {
            var eq = field.IndexOf('=');
            if (eq == 1 && field[0] == key &&
                long.TryParse(field[(eq + 1)..], out var value) && value > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Manifest values are <c>hash</c>, <c>hash|counters</c>, or <c>hash|counters|mae</c>;
    ///     older goldens have fewer segments.
    /// </summary>
    internal static string HashPart(string manifestValue)
    {
        var bar = manifestValue.IndexOf('|');
        return bar < 0 ? manifestValue : manifestValue[..bar];
    }

    /// <summary>The counters segment, or empty for a counterless (pre-instrumentation) value.</summary>
    internal static string CountersPart(string manifestValue)
    {
        var first = manifestValue.IndexOf('|');
        if (first < 0)
        {
            return string.Empty;
        }

        var second = manifestValue.IndexOf('|', first + 1);
        return second < 0 ? manifestValue[(first + 1)..] : manifestValue[(first + 1)..second];
    }

    /// <summary>The MAE-baseline segment, or empty when the golden was written without the oracle.</summary>
    internal static string MaePart(string manifestValue)
    {
        var first = manifestValue.IndexOf('|');
        if (first < 0)
        {
            return string.Empty;
        }

        var second = manifestValue.IndexOf('|', first + 1);
        return second < 0 ? string.Empty : manifestValue[(second + 1)..];
    }

    /// <summary>
    ///     True when the golden entry recorded data loss for this file — a skipped block copy, an
    ///     unwritten or duplicated destination block, padding, or a truncated read. Diagnostic
    ///     only: the write-coverage counters are destination-side and cannot see a decode that
    ///     reads the wrong in-range source, so this is NOT a regression gate — the MAE oracle is.
    /// </summary>
    internal static bool WasLossy(string manifestValue)
    {
        foreach (var field in CountersPart(manifestValue).Split(','))
        {
            var eq = field.IndexOf('=');
            if (eq <= 0 || field[..eq] is not ("s" or "u" or "d" or "p" or "t"))
            {
                continue;
            }

            if (long.TryParse(field[(eq + 1)..], out var value) && value > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Decode-only throughput benchmark (no hashing) — measures the DDX→DDS decode path
    ///     itself. Bytes are pre-loaded so only CPU decode is timed. Returns 0.
    /// </summary>
    public static int TimeDecode(string ddxDir, int limit, int repeat)
    {
        if (!Directory.Exists(ddxDir))
        {
            Console.WriteLine($"DDX directory not found: {ddxDir}");
            return 2;
        }

        var files = Directory.GetFiles(ddxDir, "*.ddx", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.Ordinal);
        if (limit > 0 && files.Length > limit)
        {
            files = files[..limit];
        }

        if (files.Length == 0)
        {
            Console.WriteLine("No .ddx files found.");
            return 2;
        }

        var inputs = new List<byte[]>(files.Length);
        long inBytes = 0;
        foreach (var f in files)
        {
            try
            {
                var b = File.ReadAllBytes(f);
                inputs.Add(b);
                inBytes += b.Length;
            }
            catch
            {
                /* skip unreadable */
            }
        }

        Console.WriteLine(
            $"Decode timing: {inputs.Count:N0} DDX ({inBytes / (1024.0 * 1024.0):N1} MB in), repeat={Math.Max(1, repeat)}");

        // Warm up the JIT + any one-time tables, untimed.
        var warm = Math.Min(inputs.Count, 32);
        for (var i = 0; i < warm; i++)
        {
            try
            {
                _ = new DdxParser().ConvertDdxToDds(inputs[i]);
            }
            catch
            {
            }
        }

        var bestMs = double.MaxValue;
        long outBytes = 0;
        for (var pass = 0; pass < Math.Max(1, repeat); pass++)
        {
            long passOut = 0;
            var sw = Stopwatch.StartNew();
            foreach (var data in inputs)
            {
                try
                {
                    var dds = new DdxParser().ConvertDdxToDds(data);
                    if (dds != null)
                    {
                        passOut += dds.Length;
                    }
                }
                catch
                {
                    /* count as 0 */
                }
            }

            sw.Stop();
            if (sw.Elapsed.TotalMilliseconds < bestMs)
            {
                bestMs = sw.Elapsed.TotalMilliseconds;
                outBytes = passOut;
            }
        }

        var sec = bestMs / 1000.0;
        Console.WriteLine($"  best wall: {bestMs:N1} ms  ({inputs.Count / Math.Max(1e-9, sec):N0} files/s, " +
                          $"{inBytes / (1024.0 * 1024.0) / Math.Max(1e-9, sec):N1} MB/s in, " +
                          $"{outBytes / (1024.0 * 1024.0):N1} MB out)");
        return 0;
    }

    private static string Short(string hash)
    {
        return hash.Length <= 12 ? hash : hash[..12];
    }

    private static string ReadMagic(byte[] ddx)
    {
        if (ddx.Length < 4)
        {
            return "short";
        }

        if (ddx[0] == (byte)'3' && ddx[1] == (byte)'X' && ddx[2] == (byte)'D')
        {
            return ddx[3] switch { (byte)'O' => "3XDO", (byte)'R' => "3XDR", _ => "other" };
        }

        return "other";
    }

    private static void DescribeDds(byte[] dds, ref FileFacts facts)
    {
        facts.Ok = true;

        // Standard DDS: "DDS " + DDS_HEADER. height@12, width@16, ddspf.dwFourCC@84, DX10 ext@128.
        if (dds.Length < 128 || dds[0] != (byte)'D' || dds[1] != (byte)'D' || dds[2] != (byte)'S')
        {
            facts.Format = "non-dds";
            return;
        }

        facts.Height = BinaryPrimitives.ReadInt32LittleEndian(dds.AsSpan(12));
        facts.Width = BinaryPrimitives.ReadInt32LittleEndian(dds.AsSpan(16));

        var fourCc = Encoding.ASCII.GetString(dds, 84, 4).TrimEnd('\0', ' ');
        if (fourCc == "DX10" && dds.Length >= 132)
        {
            facts.Format = $"DX10:{BinaryPrimitives.ReadInt32LittleEndian(dds.AsSpan(128))}";
        }
        else
        {
            facts.Format = string.IsNullOrWhiteSpace(fourCc) ? "uncompressed" : fourCc;
        }
    }

    private static void PrintCoverage(ConcurrentBag<FileFacts> coverage)
    {
        var all = coverage.ToList();
        var ok = all.Where(static f => f.Ok).ToList();

        Console.WriteLine();
        Console.WriteLine($"Corpus coverage ({all.Count:N0} files, {ok.Count:N0} decoded ok):");

        Console.WriteLine("  by container magic:");
        foreach (var g in all.GroupBy(static f => f.Magic).OrderByDescending(static g => g.Count()))
        {
            Console.WriteLine($"    {g.Key,-8} {g.Count(),8:N0}");
        }

        Console.WriteLine("  by DDS format:");
        foreach (var g in ok.GroupBy(static f => f.Format ?? "?").OrderByDescending(static g => g.Count()))
        {
            Console.WriteLine($"    {g.Key,-14} {g.Count(),8:N0}");
        }

        // Square vs non-square, per container, so we can see e.g. non-square 3XDR is actually present.
        Console.WriteLine("  square vs non-square (decoded ok):");
        foreach (var g in ok.GroupBy(static f => f.Magic).OrderBy(static g => g.Key, StringComparer.Ordinal))
        {
            var square = g.Count(static f => f.Width == f.Height);
            var nonSquare = g.Count() - square;
            Console.WriteLine($"    {g.Key,-8} square {square,8:N0}   non-square {nonSquare,8:N0}");
        }

        if (ok.Count > 0)
        {
            var dims = ok.SelectMany(static f => new[] { f.Width, f.Height }).Where(static d => d > 0).ToList();
            if (dims.Count > 0)
            {
                Console.WriteLine($"  dimensions: min {dims.Min()}  max {dims.Max()}");
            }

            var distinctSizes = ok.Select(static f => (f.Width, f.Height)).Distinct().Count();
            Console.WriteLine($"  distinct (w,h) pairs: {distinctSizes:N0}");
        }

        PrintDataIntegrity(all);
    }

    /// <summary>
    ///     Reports the untile-is-a-permutation invariant across the corpus, bucketed by how the
    ///     surface's block grid relates to the 32-block GPU macro tile. That is the axis this class
    ///     of bug lives on, and none of the buckets above expose it.
    /// </summary>
    private static void PrintDataIntegrity(List<FileFacts> all)
    {
        var withDiagnostics = all.Where(static f => f.Diagnostics is not null).ToList();
        if (withDiagnostics.Count == 0)
        {
            return;
        }

        static string AlignmentBucket(FileFacts f)
        {
            var blocksW = Math.Max(1, (f.Width + 3) / 4);
            var blocksH = Math.Max(1, (f.Height + 3) / 4);
            if (blocksW < 32 && blocksH < 32) return "sub-tile (both < 32 blocks)";
            if (blocksW < 32 || blocksH < 32) return "one axis sub-tile";
            if (blocksW % 32 == 0 && blocksH % 32 == 0) return "tile-aligned";
            return "over-tile, NOT 32-aligned";
        }

        Console.WriteLine();
        Console.WriteLine("Data integrity (untiling must be a permutation of the block set):");
        var lossy = withDiagnostics.Where(static f => !f.Diagnostics.IsLossless).ToList();
        Console.WriteLine($"  files losing data:                  {lossy.Count:N0} / {withDiagnostics.Count:N0}");
        Console.WriteLine($"    skipped block copies:             {withDiagnostics.Count(static f => f.Diagnostics.SkippedBlockCopies > 0):N0} files");
        Console.WriteLine($"    unwritten destination blocks:     {withDiagnostics.Count(static f => f.Diagnostics.UnwrittenDestinationBlocks > 0):N0} files");
        Console.WriteLine($"    duplicate destination writes:     {withDiagnostics.Count(static f => f.Diagnostics.DuplicateDestinationWrites > 0):N0} files");
        Console.WriteLine($"    padded (short) main surface:      {withDiagnostics.Count(static f => f.Diagnostics.PaddedBytes > 0):N0} files");
        Console.WriteLine($"    truncated reads:                  {withDiagnostics.Count(static f => f.Diagnostics.TruncatedReads > 0):N0} files");

        var totalBlocks = withDiagnostics.Sum(static f => f.Diagnostics.SurfaceBlockTotal);
        var unwritten = withDiagnostics.Sum(static f => (long)f.Diagnostics.UnwrittenDestinationBlocks);
        if (totalBlocks > 0)
        {
            Console.WriteLine(
                $"  unwritten blocks overall:           {unwritten:N0} / {totalBlocks:N0} ({(double)unwritten / totalBlocks * 100:F3}%)");
        }

        Console.WriteLine("  by block-grid alignment vs the 32-block GPU tile:");
        foreach (var g in withDiagnostics.Where(static f => f.Ok)
                     .GroupBy(AlignmentBucket)
                     .OrderByDescending(static g => g.Count()))
        {
            var bad = g.Count(static f => !f.Diagnostics.IsLossless);
            Console.WriteLine($"    {g.Key,-28} {g.Count(),8:N0} files, {bad,8:N0} lossy ({(double)bad / g.Count() * 100:F1}%)");
        }

        foreach (var f in lossy.Take(10))
        {
            var d = f.Diagnostics;
            Console.WriteLine(
                $"    e.g. {f.Width}x{f.Height} {f.Format}: skipped={d.SkippedBlockCopies} unwritten={d.UnwrittenDestinationBlocks} " +
                $"dup={d.DuplicateDestinationWrites} padded={d.PaddedBytes} truncated={d.TruncatedReads}" +
                (d.TruncationReasons.Count > 0 ? $" [{d.TruncationReasons[0]}]" : string.Empty));
        }
    }

    // ── Coverage instrumentation ──────────────────────────────────────────────────────────────
    // Confirms the corpus actually exercises every code path a DDX/LZX change can touch: both DDX
    // container formats (3XDO Morton-swizzled vs 3XDR engine-tiled macro-block — different untile
    // math), both square and non-square dimensions (block-grid offset math differs), and the range
    // of block formats. Parsed from the input magic + the decoded DDS header (so it costs nothing
    // beyond the decode we already do).

    private struct FileFacts
    {
        public string? Rel; // corpus-relative path, forward slashes
        public string Magic; // "3XDO" / "3XDR" / "other"
        public string Format; // DDS fourCC or "DX10:<n>"
        public int Width;
        public int Height;
        public bool Ok;
        public DecodeDiagnostics Diagnostics;
    }
}
