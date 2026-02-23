namespace DDXConv;

/// <summary>
///     Handles texture data carved from Xbox 360 memory dumps.
///     Memory dump textures differ from file-based DDX in several ways:
///     - May be partially loaded or in GPU-ready format
///     - Often have packed mip atlases in tiled memory layouts
///     - May be missing headers or have incomplete data
///     - Tiling patterns optimized for GPU access, not file storage
///     This parser handles these cases separately from the standard DDX file parser.
/// </summary>
public class MemoryTextureParser(bool verbose = false)
{
    private readonly bool _verbose = verbose;

    /// <summary>
    ///     Convert a DDX file from a memory dump to DDS format.
    ///     This is a convenience overload that reads the file and handles output.
    /// </summary>
    /// <param name="inputPath">Path to the DDX file</param>
    /// <param name="outputPath">Path for the output DDS file</param>
    /// <param name="saveAtlas">If true, also save the full untiled atlas</param>
    /// <param name="saveRaw">If true, save raw decompressed data</param>
    /// <returns>Conversion result with DDS data</returns>
    public ConversionResult ConvertFromMemory(string inputPath, string outputPath, bool saveAtlas = false,
        bool saveRaw = false)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(inputPath);
        }
        catch (Exception ex)
        {
            return new ConversionResult { Success = false, Error = $"Failed to read input file: {ex.Message}" };
        }

        var result = ConvertFromMemory(data, saveAtlas);
        if (!result.Success) return result;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllBytes(outputPath, result.DdsData!);
        }
        catch (Exception ex)
        {
            return new ConversionResult { Success = false, Error = $"Failed to write output file: {ex.Message}" };
        }

        ArgumentNullException.ThrowIfNull(outputPath);

        var atlasPath = TrySaveAuxiliaryFiles(result, outputPath, saveAtlas, saveRaw);

        return new ConversionResult
        {
            Success = true,
            DdsData = result.DdsData,
            AtlasData = result.AtlasData,
            AtlasPath = atlasPath,
            Width = result.Width,
            Height = result.Height,
            MipLevels = result.MipLevels,
            Notes = result.Notes
        };
    }

    private string? TrySaveAuxiliaryFiles(ConversionResult result, string outputPath, bool saveAtlas, bool saveRaw)
    {
        string? atlasPath = null;
        if (saveAtlas && result.AtlasData != null)
        {
            atlasPath = outputPath.Replace(".dds", "_full_atlas.dds");
            TryWriteFile(atlasPath, result.AtlasData, "atlas");
        }

        if (saveRaw && result.DdsData != null)
        {
            var rawPath = Path.ChangeExtension(outputPath, ".raw");
            TryWriteFile(rawPath, result.DdsData, "raw data");
        }

        return atlasPath;
    }

    private void TryWriteFile(string path, byte[] data, string label)
    {
        try
        {
            File.WriteAllBytes(path, data);
        }
        catch (Exception ex)
        {
            if (_verbose) Console.WriteLine($"Warning: Failed to save {label}: {ex.Message}");
        }
    }

    /// <summary>
    ///     Convert raw texture data from a memory dump to DDS format.
    /// </summary>
    /// <param name="data">Raw texture data (may include DDX header or be raw GPU data)</param>
    /// <param name="saveAtlas">If true, also return the full untiled atlas for debugging</param>
    /// <returns>Conversion result with DDS data</returns>
    public ConversionResult ConvertFromMemory(byte[] data, bool saveAtlas = false)
    {
        if (data == null || data.Length < 68) // Minimum DDX header size
            return new ConversionResult { Success = false, Error = "Data too small to be a valid texture" };

        var magic = BitConverter.ToUInt32(data, 0);
        if (magic == 0x4F445833) // "3XDO"
            return ConvertDdxFromMemory(data, saveAtlas);

        if (magic == 0x52445833) // "3XDR"
            return Convert3xdrFromMemory(data);

        return new ConversionResult { Success = false, Error = $"Unknown texture format (magic: 0x{magic:X8})" };
    }

    /// <summary>
    ///     Convert DDX texture data from memory dump.
    ///     Handles the various layouts found in memory vs. file-based DDX.
    /// </summary>
    private ConversionResult ConvertDdxFromMemory(byte[] data, bool saveAtlas)
    {
        try
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);

            reader.ReadUInt32(); // magic (already verified)
            reader.ReadByte(); // priorityL
            reader.ReadByte(); // priorityC
            reader.ReadByte(); // priorityH

            var version = reader.ReadUInt16();
            if (version < 3)
                return new ConversionResult
                {
                    Success = false,
                    Error = $"DDX version {version} not supported (need >= 3)"
                };

            reader.BaseStream.Seek(-1, SeekOrigin.Current);
            var textureHeader = reader.ReadBytes(52);
            reader.ReadBytes(8);

            var texture = MemoryTextureBlockReader.ParseD3DTextureHeader(textureHeader, out var width, out var height,
                _verbose);

            if (_verbose) Console.WriteLine($"Memory texture: {width}x{height}, Format=0x{texture.ActualFormat:X2}");

            var remainingBytes = (int)(reader.BaseStream.Length - reader.BaseStream.Position);
            var compressedData = reader.ReadBytes(remainingBytes);

            byte[] mainData;
            try
            {
                mainData = MemoryTextureBlockReader.DecompressTextureData(compressedData, width, height,
                    texture.ActualFormat, _verbose);
            }
            catch (Exception ex)
            {
                return new ConversionResult { Success = false, Error = $"Decompression failed: {ex.Message}" };
            }

            return MemoryTextureBlockReader.ProcessMemoryTextureData(mainData, width, height, texture, saveAtlas,
                _verbose);
        }
        catch (Exception ex)
        {
            return new ConversionResult { Success = false, Error = $"Parse error: {ex.Message}" };
        }
    }

    /// <summary>
    ///     Convert 3XDR texture data from memory dump.
    ///     3XDR uses macro-block tiling (not Morton) and contains only mip0.
    /// </summary>
    private ConversionResult Convert3xdrFromMemory(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);

            reader.ReadUInt32(); // magic
            reader.ReadByte(); // priorityL
            reader.ReadByte(); // priorityC
            reader.ReadByte(); // priorityH

            var version = reader.ReadUInt16();
            if (version < 3)
            {
                return new ConversionResult
                {
                    Success = false,
                    Error = $"DDX version {version} not supported (need >= 3)"
                };
            }

            reader.BaseStream.Seek(-1, SeekOrigin.Current);
            var textureHeader = reader.ReadBytes(52);
            reader.ReadBytes(8);

            var texture = MemoryTextureBlockReader.ParseD3DTextureHeader(textureHeader, out var width, out var height,
                _verbose);

            if (_verbose)
            {
                Console.WriteLine($"3XDR memory texture: {width}x{height}, Format=0x{texture.ActualFormat:X2}");
            }

            var remainingBytes = (int)(reader.BaseStream.Length - reader.BaseStream.Position);
            var compressedData = reader.ReadBytes(remainingBytes);

            byte[] decompressed;
            try
            {
                decompressed = MemoryTextureBlockReader.DecompressTextureData(compressedData, width, height,
                    texture.ActualFormat, _verbose);
            }
            catch (Exception ex)
            {
                return new ConversionResult { Success = false, Error = $"Decompression failed: {ex.Message}" };
            }

            var blockSize = TextureUtilities.GetBlockSize(texture.ActualFormat);
            var untiled = TextureUtilities.UntileMacroBlocks(decompressed, width, height, blockSize);
            var textureData = TextureUtilities.SwapEndian16(untiled);

            var dds = MemoryTextureBlockReader.BuildDds(textureData, width, height, 1, texture);
            return new ConversionResult
            {
                Success = true,
                DdsData = dds,
                Width = width,
                Height = height,
                MipLevels = 1,
                Notes = "3XDR format (macro-block tiling, mip0 only)"
            };
        }
        catch (Exception ex)
        {
            return new ConversionResult { Success = false, Error = $"3XDR parse error: {ex.Message}" };
        }
    }

    /// <summary>
    ///     Try to parse NiXenonSourceTextureData metadata at a given offset in a memory dump.
    ///     Returns null if the data at the offset doesn't look like a valid structure.
    /// </summary>
    public static NiXenonTextureMetadata? TryParseTextureMetadata(byte[] dump, int offset)
    {
        if (offset + 136 > dump.Length)
        {
            return null;
        }

        var deleted = dump[offset + 112] != 0;
        var levelsSkipped = BitConverter.ToUInt32(dump, offset + 124);
        var originalSkip = BitConverter.ToUInt32(dump, offset + 128);

        if (levelsSkipped > 15 || originalSkip > 15)
        {
            return null;
        }

        return new NiXenonTextureMetadata
        {
            LevelsSkipped = levelsSkipped,
            OriginalSkipLevels = originalSkip,
            TextureDeleted = deleted,
            StructOffset = offset
        };
    }

    /// <summary>
    ///     Result of memory texture conversion.
    /// </summary>
    public class ConversionResult
    {
        public bool Success { get; init; }
        public byte[]? DdsData { get; init; }
        public byte[]? AtlasData { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public int MipLevels { get; init; }
        public int SkippedLevels { get; init; }
        public string? Notes { get; init; }
        public string? Error { get; init; }
        public string? AtlasPath { get; init; }
    }

    /// <summary>
    ///     Basic texture info structure.
    /// </summary>
    public class TextureInfo
    {
        public int Width { get; init; }
        public int Height { get; init; }
        public byte DataFormat { get; init; }
        public byte ActualFormat { get; init; }
    }

    /// <summary>
    ///     Parsed NiXenonSourceTextureData from a memory dump.
    /// </summary>
    /// <remarks>
    ///     PDB layout (NiXenonSourceTextureData, 136 bytes):
    ///     offset 112: m_bTextureDeleted (bool)
    ///     offset 124: m_uiLevelsSkipped (uint32) -- per-texture mip levels skipped
    ///     offset 128: m_uiOriginalSkipLevels (uint32)
    ///     Static members (from .data segment):
    ///     ms_uiSkipLevels -- global mip skip level applied to all textures
    /// </remarks>
    public class NiXenonTextureMetadata
    {
        public uint LevelsSkipped { get; init; }
        public uint OriginalSkipLevels { get; init; }
        public bool TextureDeleted { get; init; }
        public int StructOffset { get; init; }
    }
}
