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
            Console.WriteLine();
            Console.WriteLine("Standard Options:");
            Console.WriteLine("  --pc-friendly, -pc   Produce PC-ready normal maps (batch conversion only!)");
            Console.WriteLine("  --regen-mips, -g     Regenerate mip levels from top level");
            Console.WriteLine();
            Console.WriteLine("Memory Dump Options (for textures carved from memory dumps):");
            Console.WriteLine("  --memory, -m         Use memory texture parser (handles memory dump layouts)");
            Console.WriteLine("  --atlas, -a          Save full untiled atlas as separate DDS file");
            Console.WriteLine();
            Console.WriteLine("Developer Options:");
            Console.WriteLine("  --raw, -r            Save raw combined decompressed data as binary file");
            Console.WriteLine("  --save-mips          Save extracted mip levels from atlas");
            Console.WriteLine("  --no-untile-atlas    Do not untile/unswizzle the atlas (leave tiled)");
            Console.WriteLine("  --no-untile          Do not untile/unswizzle ANY data (for debugging)");
            Console.WriteLine("  --no-swap            Do not perform endian swap on data");
            Console.WriteLine("  --verbose, -v        Enable verbose output");
            return;
        }

        var inputPath = positional[0];
        var pcFriendly = opts.Contains("--pc-friendly") || opts.Contains("-pc");
        var regenMips = opts.Contains("--regen-mips") || opts.Contains("-g");
        var memoryMode = opts.Contains("--memory") || opts.Contains("-m");
        var saveAtlas = opts.Contains("--atlas") || opts.Contains("-a");
        var saveRaw = opts.Contains("--raw") || opts.Contains("-r");
        var saveMips = opts.Contains("--save-mips");
        var noUntileAtlas = opts.Contains("--no-untile-atlas");
        var noUntile = opts.Contains("--no-untile");
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
                    if (memoryMode)
                    {
                        // Use MemoryTextureParser for textures carved from memory dumps
                        var memoryParser = new MemoryTextureParser(verbose);
                        var result = memoryParser.ConvertFromMemory(ddxFile, outputBatchPath, saveAtlas, saveRaw);
                        
                        if (result.Success)
                        {
                            Console.WriteLine($"Converted {ddxFile} to {outputBatchPath}");
                        }
                        else
                        {
                            errors++;
                            Console.WriteLine($"Error converting {ddxFile}: {result.Error}");
                            failed.Add((ddxFile, result.Error ?? "Unknown error"));
                            continue;
                        }
                    }
                    else
                    {
                        // Use standard DdxParser for file-based .ddx files
                        var parser = new DdxParser(verbose);
                        parser.ConvertDdxToDds(ddxFile, outputBatchPath,
                            new ConversionOptions
                            {
                                SaveAtlas = saveAtlas,
                                SaveRaw = saveRaw,
                                SaveMips = saveMips,
                                NoUntileAtlas = noUntileAtlas,
                                NoUntile = noUntile,
                                SkipEndianSwap = skipEndianSwap
                            });
                        Console.WriteLine($"Converted {ddxFile} to {outputBatchPath}");
                    }
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

        var outputPath = positional.Count > 1 ? positional[1] : Path.ChangeExtension(inputPath, ".dds");

        try
        {
            if (memoryMode)
            {
                // Use MemoryTextureParser for textures carved from memory dumps
                var memoryParser = new MemoryTextureParser(verbose);
                var result = memoryParser.ConvertFromMemory(inputPath, outputPath, saveAtlas, saveRaw);
                
                if (result.Success)
                {
                    Console.WriteLine($"Successfully converted memory texture {inputPath} to {outputPath}");
                    if (result.AtlasData != null && saveAtlas)
                    {
                        Console.WriteLine($"  Atlas saved: {result.AtlasPath}");
                    }
                }
                else
                {
                    Console.WriteLine($"Conversion failed: {result.Error}");
                    return;
                }
            }
            else
            {
                // Use standard DdxParser for file-based .ddx files
                var parser = new DdxParser(verbose);
                parser.ConvertDdxToDds(inputPath, outputPath,
                    new ConversionOptions
                    {
                        SaveAtlas = saveAtlas,
                        SaveRaw = saveRaw,
                        SaveMips = saveMips,
                        NoUntileAtlas = noUntileAtlas,
                        NoUntile = noUntile,
                        SkipEndianSwap = skipEndianSwap
                    });
                Console.WriteLine($"Successfully converted {inputPath} to {outputPath}");
            }
            
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