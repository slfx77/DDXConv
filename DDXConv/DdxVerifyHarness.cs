using System.Buffers.Binary;
using System.Collections.Concurrent;
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

    /// <summary>Returns 0 on success (golden written, or verify with no mismatches), 1 on a verify
    /// mismatch, 2 on a usage/IO error.</summary>
    public static int Run(string ddxDir, string manifestPath, bool writeGolden, int limit)
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

        Console.WriteLine($"Hashing {files.Length:N0} DDX decode outputs (threads={Environment.ProcessorCount})...");
        var results = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var coverage = new ConcurrentBag<FileFacts>();
        var done = 0;
        Parallel.ForEach(files, file =>
        {
            var rel = Path.GetRelativePath(ddxDir, file).Replace('\\', '/');
            string hash;
            var facts = new FileFacts();
            try
            {
                var data = File.ReadAllBytes(file);
                facts.Magic = ReadMagic(data);
                var dds = new DdxParser().ConvertDdxToDds(data);
                if (dds is { Length: > 0 })
                {
                    hash = Convert.ToHexString(SHA256.HashData(dds)).ToLowerInvariant();
                    DescribeDds(dds, ref facts);
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

            results[rel] = hash;
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
            : Verify(manifestPath, results);
    }

    private static int WriteGolden(string manifestPath, ConcurrentDictionary<string, string> results)
    {
        var dir = Path.GetDirectoryName(manifestPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using (var w = new StreamWriter(manifestPath, append: false))
        {
            w.WriteLine($"# DDXConv decode golden manifest — {results.Count} entries — format: relpath|sha256(dds)-or-__FAIL__");
            foreach (var kv in results.OrderBy(static k => k.Key, StringComparer.Ordinal))
            {
                w.WriteLine($"{kv.Key}|{kv.Value}");
            }
        }

        var fails = results.Values.Count(static v => v == FailSentinel);
        Console.WriteLine(
            $"Wrote golden manifest: {manifestPath} ({results.Count:N0} entries, {fails:N0} decode failures recorded)");
        return 0;
    }

    private static int Verify(string manifestPath, ConcurrentDictionary<string, string> results)
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

        int matched = 0, mismatched = 0, newFiles = 0, missing = 0;
        var samples = new List<string>();
        foreach (var kv in results)
        {
            if (!golden.TryGetValue(kv.Key, out var g))
            {
                newFiles++;
            }
            else if (g == kv.Value)
            {
                matched++;
            }
            else
            {
                mismatched++;
                if (samples.Count < 25)
                {
                    samples.Add($"{kv.Key}: golden={Short(g)} now={Short(kv.Value)}");
                }
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
        Console.WriteLine($"  MISMATCHED:                         {mismatched:N0} (must be 0)");
        Console.WriteLine($"  new (not in golden):                {newFiles:N0}");
        Console.WriteLine($"  missing (in golden, not found now): {missing:N0}");
        foreach (var s in samples)
        {
            Console.WriteLine($"    {s}");
        }

        return mismatched == 0 ? 0 : 1;
    }

    /// <summary>Decode-only throughput benchmark (no hashing) — measures the DDX→DDS decode path
    /// itself. Bytes are pre-loaded so only CPU decode is timed. Returns 0.</summary>
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
            catch { /* skip unreadable */ }
        }

        Console.WriteLine($"Decode timing: {inputs.Count:N0} DDX ({inBytes / (1024.0 * 1024.0):N1} MB in), repeat={Math.Max(1, repeat)}");

        // Warm up the JIT + any one-time tables, untimed.
        var warm = Math.Min(inputs.Count, 32);
        for (var i = 0; i < warm; i++)
        {
            try { _ = new DdxParser().ConvertDdxToDds(inputs[i]); } catch { }
        }

        var bestMs = double.MaxValue;
        long outBytes = 0;
        for (var pass = 0; pass < Math.Max(1, repeat); pass++)
        {
            long passOut = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
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
                catch { /* count as 0 */ }
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

    private static string Short(string hash) => hash.Length <= 12 ? hash : hash[..12];

    // ── Coverage instrumentation ──────────────────────────────────────────────────────────────
    // Confirms the corpus actually exercises every code path a DDX/LZX change can touch: both DDX
    // container formats (3XDO Morton-swizzled vs 3XDR engine-tiled macro-block — different untile
    // math), both square and non-square dimensions (block-grid offset math differs), and the range
    // of block formats. Parsed from the input magic + the decoded DDS header (so it costs nothing
    // beyond the decode we already do).

    private struct FileFacts
    {
        public string Magic;     // "3XDO" / "3XDR" / "other"
        public string Format;    // DDS fourCC or "DX10:<n>"
        public int Width;
        public int Height;
        public bool Ok;
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
    }
}
