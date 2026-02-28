using DDXConv.Compression;

namespace DDXConv;

public class DdxParser(bool verbose = false)
{
    private const uint MAGIC_3XDO = 0x4F445833;
    private const uint MAGIC_3XDR = 0x52445833;

    private readonly bool _verboseLogging = verbose;

    private readonly DdxHeaderWriter _headerWriter = new(verbose);
    private readonly DdxMipAtlasUnpacker _mipAtlasUnpacker = new(verbose);

    private ConversionOptions? _currentOptions;

    /// <summary>
    ///     Convert a DDX file on disk to a DDS file on disk.
    /// </summary>
    public void ConvertDdxToDds(string inputPath, string outputPath, ConversionOptions options)
    {
        using var reader = new BinaryReader(File.OpenRead(inputPath));
        var magic = reader.ReadUInt32();

        if (magic == MAGIC_3XDR)
        {
            var (texture, data) = Convert3Xdr(reader, options);
            _headerWriter.WriteDdsFile(outputPath, texture, data);
            if (_verboseLogging)
                Console.WriteLine($"3XDR: Saved DDS to {outputPath} ({data.Length} bytes, {texture.MipLevels} mip(s))");
            return;
        }

        if (magic != MAGIC_3XDO) throw new InvalidDataException($"Unknown DDX magic: 0x{magic:X8}.");

        var (tex3Xdo, linearData) = ConvertDdx(reader, outputPath, options, magic);
        _headerWriter.WriteDdsFile(outputPath, tex3Xdo, linearData);
    }

    /// <summary>
    ///     Convert DDX data in memory to DDS data in memory.
    ///     No temp files or disk I/O — the entire pipeline runs in memory.
    /// </summary>
    public byte[] ConvertDdxToDds(byte[] inputData, ConversionOptions? options = null)
    {
        using var ms = new MemoryStream(inputData);
        using var reader = new BinaryReader(ms);
        var magic = reader.ReadUInt32();

        var opts = options ?? new ConversionOptions();

        if (magic == MAGIC_3XDR)
        {
            var (texture, data) = Convert3Xdr(reader, opts);
            return _headerWriter.BuildDdsBytes(texture, data);
        }

        if (magic != MAGIC_3XDO) throw new InvalidDataException($"Unknown DDX magic: 0x{magic:X8}.");

        // Pass null outputPath — auxiliary file writes (raw dump, atlas) are skipped
        var (tex, linearData) = ConvertDdx(reader, null, opts, magic);
        return _headerWriter.BuildDdsBytes(tex, linearData);
    }

    /// <summary>
    ///     Convert 3XDR (engine-tiled) format.
    ///     3XDR is simpler than 3XDO:
    ///     - Data is already linear (NOT Morton-swizzled)
    ///     - Contains only mip0 (no mip atlas)
    ///     - Only requires decompression + 16-bit byte swap for Xbox 360 big-endian
    /// </summary>
    private (D3DTextureInfo Texture, byte[] Data) Convert3Xdr(BinaryReader reader, ConversionOptions options)
    {
        _currentOptions = options;

        // Read header (same as 3XDO up to offset 0x44)
        _ = reader.ReadByte(); // priorityL
        _ = reader.ReadByte(); // priorityC
        _ = reader.ReadByte(); // priorityH
        var version = reader.ReadUInt16();

        if (version < 3)
            throw new NotSupportedException($"3XDR version {version} is not supported. Need version >= 3");

        // Read D3DTexture header (52 bytes from 0x08 to 0x3C)
        reader.BaseStream.Seek(-1, SeekOrigin.Current);
        var textureHeader = reader.ReadBytes(52);
        reader.ReadBytes(8); // Skip to 0x44

        var texture = _headerWriter.ParseD3DTextureHeaderWithDimensions(textureHeader, out var width, out var height);

        if (_verboseLogging)
            Console.WriteLine($"3XDR: {width}x{height}, Format=0x{texture.ActualFormat:X2}");

        // Read all remaining compressed data
        var currentPos = reader.BaseStream.Position;
        var fileSize = reader.BaseStream.Length;
        var compressedData = reader.ReadBytes((int)(fileSize - currentPos));

        // Decompress - 3XDR has mip chain in linear layout
        var mip0Size = (uint)TextureUtilities.CalculateMipSize(width, height, texture.ActualFormat);
        var decompressed = DecompressXMemCompress(compressedData, mip0Size, out var consumed);

        if (_verboseLogging)
            Console.WriteLine(
                $"3XDR: Decompressed {consumed} bytes to {decompressed.Length} bytes (expected mip0={mip0Size})");

        // 3XDR data uses Xbox 360 2x2 macro block tiling and big-endian byte order
        var blockSize = TextureUtilities.GetBlockSize(texture.ActualFormat);
        var untiled = TextureUtilities.UntileMacroBlocks(decompressed, width, height, blockSize);
        var textureData = TextureUtilities.SwapEndian16(untiled);

        // Update texture info
        texture.Width = width;
        texture.Height = height;
        texture.MipLevels = 1;

        return (texture, textureData);
    }

    /// <summary>
    ///     Convert 3XDO (Morton-swizzled) format.
    ///     When outputPath is null, auxiliary file writes (raw dump, atlas debug) are skipped.
    /// </summary>
    // Some of this code built from analyzing NiXenonSourceTextureData::CreateFromDDXFile, some from file analysis
    private (D3DTextureInfo Texture, byte[] Data) ConvertDdx(
        BinaryReader reader, string? outputPath, ConversionOptions options, uint magic)
    {
        _currentOptions = options;

        _ = reader.ReadByte(); // priorityL
        _ = reader.ReadByte(); // priorityC
        _ = reader.ReadByte(); // priorityH

        // Read version
        var version = reader.ReadUInt16();

        if (version < 3) throw new NotSupportedException($"DDX version {version} is not supported. Need version >= 3");

        // D3DTexture header (52 bytes at offset 0x08) - back up 1 byte past the version field
        reader.BaseStream.Seek(-1, SeekOrigin.Current);

        var textureHeader = reader.ReadBytes(52); // 0x08-0x3C

        // Skip 8 bytes to reach 0x44
        reader.ReadBytes(8);

        // Parse the D3DTexture header to extract dimensions and format from Format dwords
        var texture =
            _headerWriter.ParseD3DTextureHeaderWithDimensions(textureHeader, out var width, out var height);

        if (_verboseLogging) Console.WriteLine($"Dimensions from D3D texture header: {width}x{height}");

        // For 3XDO files, the texture data starts immediately after the header at offset 0x44
        var currentPos = reader.BaseStream.Position;
        var fileSize = reader.BaseStream.Length;
        var remainingBytes = (uint)(fileSize - currentPos);

        // Read all texture data
        var mainData = reader.ReadBytes((int)remainingBytes);

        // Calculate total expected size: atlas (2x resolution) + linear mips
        var atlasSize = (uint)TextureUtilities.CalculateMipSize(width, height, texture.ActualFormat);

        // Decompress all chunks in sequence
        var compressedData = mainData;
        var decompressedChunks = new List<byte[]>();
        var totalConsumed = 0;

        // Try to decompress first chunk
        var firstChunk = DecompressXMemCompress(compressedData, atlasSize, out var firstChunkCompressedSize);
        if (_verboseLogging)
            Console.WriteLine(
                $"Chunk 1: consumed {firstChunkCompressedSize} compressed bytes, got {firstChunk.Length} decompressed bytes");

        decompressedChunks.Add(firstChunk);
        totalConsumed += firstChunkCompressedSize;

        // Try to decompress additional chunks until we run out of data
        while (totalConsumed < compressedData.Length)
        {
            var offset = totalConsumed;
            var remainingSize = compressedData.Length - offset;

            if (remainingSize < 10) break;

            if (_verboseLogging)
                Console.WriteLine(
                    $"Attempting to decompress chunk {decompressedChunks.Count + 1} at offset {offset} ({remainingSize} bytes remaining)");

            try
            {
                var remainingCompressed = new byte[remainingSize];
                Array.Copy(compressedData, offset, remainingCompressed, 0, remainingSize);

                var chunk = DecompressXMemCompress(remainingCompressed, atlasSize, out var chunkCompressedSize);
                if (_verboseLogging)
                    Console.WriteLine(
                        $"Chunk {decompressedChunks.Count + 1}: consumed {chunkCompressedSize} compressed bytes, got {chunk.Length} decompressed bytes");

                decompressedChunks.Add(chunk);
                totalConsumed += chunkCompressedSize;

                if (chunkCompressedSize == 0) break;
            }
            catch (Exception ex)
            {
                if (_verboseLogging)
                    Console.WriteLine($"Failed to decompress chunk {decompressedChunks.Count + 1}: {ex.Message}");

                break;
            }
        }

        // Combine all decompressed chunks
        var totalDecompressed = decompressedChunks.Sum(c => c.Length);
        mainData = new byte[totalDecompressed];
        var writeOffset = 0;
        for (var i = 0; i < decompressedChunks.Count; i++)
        {
            Array.Copy(decompressedChunks[i], 0, mainData, writeOffset, decompressedChunks[i].Length);
            writeOffset += decompressedChunks[i].Length;
        }

        if (_verboseLogging)
            Console.WriteLine(
                $"Combined {decompressedChunks.Count} chunks = {mainData.Length} bytes total (consumed {totalConsumed}/{compressedData.Length} compressed bytes)");

        // Save raw combined data for analysis (optional)
        if (options.SaveRaw && outputPath != null)
        {
            var rawPath = outputPath.Replace(".dds", "_raw.bin");
            File.WriteAllBytes(rawPath, mainData);
            if (_verboseLogging) Console.WriteLine($"Saved raw combined data to {rawPath}");
        }

        // Calculate expected main surface size with detected dimensions
        var mainSurfaceSize = (uint)TextureUtilities.CalculateMipSize(width, height, texture.ActualFormat);

        // Wire up the mip atlas unpacker's WriteDdsFile callback
        _mipAtlasUnpacker.WriteDdsFileCallback = _headerWriter.WriteDdsFile;

        // Create chunk processor with delegates
        var chunkProcessor = new DdxChunkProcessor(_verboseLogging)
        {
            UnswizzleDxtTexture = UnswizzleDXTTexture,
            UnpackMipAtlas = _mipAtlasUnpacker.UnpackMipAtlas,
            WriteDdsFile = _headerWriter.WriteDdsFile
        };

        // Process chunks into final linear texture data
        var linearData = chunkProcessor.ProcessChunks(
            mainData, decompressedChunks, texture,
            width, height, mainSurfaceSize,
            outputPath, options, magic);

        // Validate mip count against actual data size
        var validatedMips = DdxMipAtlasUnpacker.CountMipLevelsFromDataSize(
            texture.Width, texture.Height, texture.ActualFormat, linearData.Length);
        if (validatedMips < texture.MipLevels)
        {
            if (_verboseLogging)
                Console.WriteLine(
                    $"Correcting mip count: header claimed {texture.MipLevels} but data only contains {validatedMips}");
            texture.MipLevels = validatedMips;

            // Trim trailing data that doesn't belong to any complete mip level
            var expectedSize =
                (int)TextureUtilities.CalculateMainDataSize(texture.Width, texture.Height, texture.ActualFormat,
                    validatedMips);
            if (linearData.Length > expectedSize)
                linearData = linearData[..expectedSize];
        }

        return (texture, linearData);
    }

    private byte[] DecompressXMemCompress(byte[] compressedData, uint uncompressedSize, out int bytesConsumed)
    {
        var decompressedData = new byte[uncompressedSize * 2];
        using var decompressor = new LzxDecompressor();
        var compressedLen = compressedData.Length;
        var decompressedLen = decompressedData.Length;

        var result = decompressor.Decompress(
            compressedData, 0, ref compressedLen,
            decompressedData, 0, ref decompressedLen);

        if (result != 0)
            throw new InvalidOperationException($"LzxDecompressor decompression failed: {result}");

        if (_verboseLogging) Console.WriteLine($"Decompressed {compressedLen} -> {decompressedLen} bytes");

        bytesConsumed = compressedLen;
        if (decompressedLen < decompressedData.Length) Array.Resize(ref decompressedData, decompressedLen);
        return decompressedData;
    }

    private byte[] UnswizzleDXTTexture(byte[] src, int width, int height, uint format)
    {
        var swapEndian = _currentOptions == null || !_currentOptions.SkipEndianSwap;
        return TextureUtilities.UnswizzleMortonDXT(src, width, height, format, swapEndian);
    }
}
