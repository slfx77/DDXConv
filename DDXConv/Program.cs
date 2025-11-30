using System;
using System.IO;

namespace DDXConv;
internal class Program
{
    private static void Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("run DDXConv --help for usage information.");
            return;
        }

        var opts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var positional = new List<string>();
        foreach (var a in args)
            if (a.StartsWith('-')) opts.Add(a);
            else positional.Add(a);
        
        if (opts.Contains("--help") || opts.Contains("-h"))
        {
            Console.WriteLine("Single File: DDXConv <input_file> [output_file] [options]");
            Console.WriteLine("      Batch: DDXConv <input_directory> <output_directory> [options]");
            Console.WriteLine("Standard Options:");
            Console.WriteLine("  --pc-friendly, -pc   Produce PC-ready normal maps (batch conversion only!)");
            Console.WriteLine("  --regen-mips, -g     Regenerate mip levels from top level");
            Console.WriteLine("Developer Options:");
            Console.WriteLine("  --atlas, -a          Save untiled mip atlas as separate DDS file");
            Console.WriteLine("  --raw, -r            Save raw combined decompressed data as binary file");
            Console.WriteLine("  --save-mips          Save extracted mip levels from atlas");
            Console.WriteLine("  --no-untile-atlas    Do not untile/unswizzle the atlas (leave tiled)");
            Console.WriteLine("  --no-swap            Do not perform endian swap on data");
            Console.WriteLine("  --verbose, -v        Enable verbose output");
            return;
        }

        var inputPath = positional[0];
        var pcFriendly = opts.Contains("--pc-friendly") || opts.Contains("-pc");
        var regenMips = opts.Contains("--regen-mips") || opts.Contains("-g");
        var saveAtlas = opts.Contains("--atlas") || opts.Contains("-a");
        var saveRaw = opts.Contains("--raw") || opts.Contains("-r");
        var saveMips = opts.Contains("--save-mips");
        var noUntileAtlas = opts.Contains("--no-untile-atlas");
        var skipEndianSwap = opts.Contains("--no-swap");
        var verbose = opts.Contains("--verbose") || opts.Contains("-v");

        if (Directory.Exists(inputPath))
        {
            // Batch convert all .ddx files in the directory
            if (positional.Count < 2)
            {
                Console.WriteLine("Batch mode requires output directory as second positional argument.");
                return;
            }

            var outputDir = positional[1];
            Directory.CreateDirectory(outputDir);

            var ddxFiles = Directory.GetFiles(inputPath, "*.ddx", SearchOption.AllDirectories);

            var errors = 0;
            var failed = new List<(string name, string error)>();
            var invalids = 0;

            foreach (var ddxFile in ddxFiles)
            {
                var relativePath = Path.GetRelativePath(inputPath, ddxFile);
                var outputBatchPath = Path.Combine(outputDir, Path.ChangeExtension(relativePath, ".dds"));
                Directory.CreateDirectory(Path.GetDirectoryName(outputBatchPath)!);

                try
                {
                    var parser = new DdxParser(verbose);
                    parser.ConvertDdxToDds(ddxFile, outputBatchPath,
                        new ConversionOptions
                        {
                            SaveAtlas = saveAtlas, SaveRaw = saveRaw, SaveMips = saveMips,
                            NoUntileAtlas = noUntileAtlas, SkipEndianSwap = skipEndianSwap
                        });
                    Console.WriteLine($"Converted {ddxFile} to {outputBatchPath}");
                    if (regenMips) DdsPostProcessor.RegenerateMips(outputBatchPath);
                }
                catch (NotSupportedException)
                {
                    invalids++;
                }
                catch (Exception ex)
                {
                    errors++;
                    Console.WriteLine($"Error converting {ddxFile}: {ex.Message}");
                    failed.Add((ddxFile, ex.Message));
                }
            }

            Console.WriteLine(
                $"Batch conversion completed. Successfully converted {ddxFiles.Length - errors - invalids} out of {ddxFiles.Length} files ({errors} failures, {invalids} unsupported).");
            foreach (var tex in failed) Console.Write($"- {tex.name}: {tex.error}\n");

            if (pcFriendly)
            {
                // cycle through output directory and process normal + specular maps
                var ddsFiles = Directory.GetFiles(outputDir, "*_n.dds", SearchOption.AllDirectories);
                foreach (var ddsFile in ddsFiles)
                {
                    // check if file with same name but _s.dds exists
                    var specFile = ddsFile.Replace("_n.dds", "_s.dds");
                    try
                    {
                        DdsPostProcessor.MergeNormalSpecularMaps(ddsFile, File.Exists(specFile) ? specFile : null);
                        Console.WriteLine($"Converted to PC-friendly normal map: {ddsFile}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error converting to PC-friendly normal map {ddsFile}: {ex.Message}");
                    }
                }
            }
            
            return;
        }

        if (!File.Exists(inputPath))
        {
            Console.WriteLine($"Input file or directory not found: {inputPath}");
            return;
        }

        var outputPath = args.Length > 1 ? args[1] : Path.ChangeExtension(inputPath, ".dds");

        try
        {
            var parser = new DdxParser(verbose);
            parser.ConvertDdxToDds(inputPath, outputPath,
                new ConversionOptions
                {
                    SaveAtlas = saveAtlas, SaveRaw = saveRaw, SaveMips = saveMips, NoUntileAtlas = noUntileAtlas,
                    SkipEndianSwap = skipEndianSwap
                });
            Console.WriteLine($"Successfully converted {inputPath} to {outputPath}");
            // Regenerate mips unless disabled or if PC-friendly normal map case (no reason to add another re-encode since normal merge regenerates mips)
            if (!regenMips || ((inputPath.EndsWith("_s.dds") || inputPath.EndsWith("_n.dds")) && pcFriendly)) return;
            DdsPostProcessor.RegenerateMips(outputPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
    }
}