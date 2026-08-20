using DDXConv.Compression;

namespace DDXConv;

public class DdxParser(bool verbose = false)
{
    private const uint Magic3Xdo = 0x4F445833;
    private const uint Magic3Xdr = 0x52445833;

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

        if (magic == Magic3Xdr)
        {
            var (texture, data) = Convert3Xdr(reader, options);
            _headerWriter.WriteDdsFile(outputPath, texture, data);
            if (verbose)
                Console.WriteLine($"3XDR: Saved DDS to {outputPath} ({data.Length} bytes, {texture.MipLevels} mip(s))");
            return;
        }

        if (magic != Magic3Xdo) throw new InvalidDataException($"Unknown DDX magic: 0x{magic:X8}.");

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

        if (magic == Magic3Xdr)
        {
            var (texture, data) = Convert3Xdr(reader, opts);
            return _headerWriter.BuildDdsBytes(texture, data);
        }

        if (magic != Magic3Xdo) throw new InvalidDataException($"Unknown DDX magic: 0x{magic:X8}.");

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
        _headerWriter.Diagnostics = options.Diagnostics;

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

        if (verbose)
            Console.WriteLine($"3XDR: {width}x{height}, Format=0x{texture.ActualFormat:X2}");

        // Read all remaining compressed data
        var currentPos = reader.BaseStream.Position;
        var fileSize = reader.BaseStream.Length;
        var compressedData = reader.ReadBytes((int)(fileSize - currentPos));

        // Decompress - 3XDR has mip chain in linear layout.
        // Size from the tile-aligned extent for the same reason as the 3XDO path: the stored
        // surface is the GPU footprint, and an under-sized buffer truncates the decode silently.
        var mip0Size = (uint)TextureUtilities.CalculateMipSize(width, height, texture.ActualFormat);
        var decompressHint = (uint)Math.Max(
            mip0Size, TextureUtilities.CalculateTiledMipSize(width, height, texture.ActualFormat));
        var decompressed = DecompressXMemCompress(compressedData, decompressHint, out var consumed);

        if (verbose)
            Console.WriteLine(
                $"3XDR: Decompressed {consumed} bytes to {decompressed.Length} bytes (expected mip0={mip0Size})");

        // 3XDR data uses Xbox 360 GPU block-level tiling and big-endian byte order
        byte[] textureData;
        if (options.NoUntile)
        {
            textureData = options.SkipEndianSwap ? decompressed : TextureUtilities.SwapEndian16(decompressed);
        }
        else if (options.ForceMorton)
        {
            // Diagnostic: try Morton/Z-order deswizzle instead of macro-block untiling
            var mainSize = (int)TextureUtilities.CalculateMipSize(width, (uint)height, texture.ActualFormat);
            var mainData = decompressed.Length > mainSize ? decompressed[..mainSize] : decompressed;
            textureData = TextureUtilities.UnswizzleMortonDxt(mainData, width, height, texture.ActualFormat,
                !options.SkipEndianSwap);
        }
        else
        {
            var blockSize = TextureUtilities.GetBlockSize(texture.ActualFormat);
            var swapEndian = !options.SkipEndianSwap;
            textureData = TextureUtilities.UntileMacroBlocks(decompressed, width, height, blockSize, swapEndian,
                texture.ActualFormat);
        }

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
        _headerWriter.Diagnostics = options.Diagnostics;

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

        if (verbose) Console.WriteLine($"Dimensions from D3D texture header: {width}x{height}");

        // For 3XDO files, the texture data starts immediately after the header at offset 0x44
        var currentPos = reader.BaseStream.Position;
        var fileSize = reader.BaseStream.Length;
        var remainingBytes = (uint)(fileSize - currentPos);

        // Read all texture data
        var mainData = reader.ReadBytes((int)remainingBytes);

        // Calculate total expected size: atlas (2x resolution) + linear mips
        var atlasSize = (uint)TextureUtilities.CalculateMipSize(width, height, texture.ActualFormat);

        // The DDX stores the GPU footprint, whose block dimensions are each rounded up to a
        // multiple of 32, so the decompression buffer must be sized from the TILE-ALIGNED extent.
        // Sizing it from the logical mip-0 size truncates the decode, and LzxDecompressor reports
        // success regardless — it simply stops writing once the buffer fills and discards the rest
        // of the input.
        //
        // This used to be a special case guarded by `blocksWide < 32 && blocksHigh < 32`. The AND
        // meant a surface with exactly ONE sub-tile axis (512x16, 32x128, 1024x64 …) missed the
        // bump entirely and lost every block column past the first macro tile. Alignment is a
        // property of each axis independently, so there is no special case to make — always ask
        // for the aligned extent.
        //
        // The sequential mip chain is part of the hint too: chunk 1 of the two-chunk format IS
        // the mip chain (whose true size is the sequential tiled total, not the mip-0 extent),
        // and single-chunk files store [tiled mip0][sequential mips] in ONE chunk. A hint that
        // stops at mip 0 collapses those chains silently — e.g. a 64x64 ATI1 needs
        // 8192 (mip0) + 16384 (mips), and the old mip0-only hint cut 7 mips down to 1.
        var decompressHint = (uint)Math.Max(
            atlasSize,
            TextureUtilities.CalculateTiledMipSize(width, height, texture.ActualFormat)
            + TextureUtilities.ComputeSequentialTiledMipTotal(width, height, texture.ActualFormat));

        // Decompress all chunks in sequence
        var compressedData = mainData;
        var decompressedChunks = new List<byte[]>();
        var totalConsumed = 0;

        // Try to decompress first chunk
        var firstChunk = DecompressXMemCompress(compressedData, decompressHint, out var firstChunkCompressedSize);
        if (verbose)
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

            if (verbose)
                Console.WriteLine(
                    $"Attempting to decompress chunk {decompressedChunks.Count + 1} at offset {offset} ({remainingSize} bytes remaining)");

            try
            {
                var remainingCompressed = new byte[remainingSize];
                Array.Copy(compressedData, offset, remainingCompressed, 0, remainingSize);

                // Same hint as chunk 1 (tiled mip0 + sequential mip chain) — a later chunk is
                // either the main surface or a mip chain, and the combined hint covers both.
                var chunk = DecompressXMemCompress(remainingCompressed, decompressHint, out var chunkCompressedSize);
                if (verbose)
                    Console.WriteLine(
                        $"Chunk {decompressedChunks.Count + 1}: consumed {chunkCompressedSize} compressed bytes, got {chunk.Length} decompressed bytes");

                decompressedChunks.Add(chunk);
                totalConsumed += chunkCompressedSize;

                if (chunkCompressedSize == 0) break;
            }
            catch (Exception ex)
            {
                if (verbose)
                    Console.WriteLine($"Failed to decompress chunk {decompressedChunks.Count + 1}: {ex.Message}");

                break;
            }
        }

        // Combine all decompressed chunks
        var totalDecompressed = decompressedChunks.Sum(c => c.Length);

        if (totalDecompressed == 0)
            throw new InvalidOperationException(
                $"LZX decompression produced 0 bytes from {compressedData.Length} bytes of compressed data " +
                $"(expected ~{atlasSize} bytes for {width}x{height} texture)");

        mainData = new byte[totalDecompressed];
        var writeOffset = 0;
        foreach (var chunk in decompressedChunks)
        {
            Array.Copy(chunk, 0, mainData, writeOffset, chunk.Length);
            writeOffset += chunk.Length;
        }

        if (verbose)
            Console.WriteLine(
                $"Combined {decompressedChunks.Count} chunks = {mainData.Length} bytes total (consumed {totalConsumed}/{compressedData.Length} compressed bytes)");

        // Save raw combined data for analysis (optional)
        if (options.SaveRaw && outputPath != null)
        {
            var rawPath = outputPath.Replace(".dds", "_raw.bin");
            File.WriteAllBytes(rawPath, mainData);
            if (verbose) Console.WriteLine($"Saved raw combined data to {rawPath}");
        }

        // Calculate expected main surface size with detected dimensions
        var mainSurfaceSize = (uint)TextureUtilities.CalculateMipSize(width, height, texture.ActualFormat);

        // Wire up the mip atlas unpacker's WriteDdsFile callback
        _mipAtlasUnpacker.WriteDdsFileCallback = _headerWriter.WriteAuxDdsFile;

        // Create chunk processor with delegates
        var chunkProcessor = new DdxChunkProcessor(verbose)
        {
            UnswizzleDxtTexture = UnswizzleDxtTexture,
            UnswizzleDxtTextureHeuristic = UnswizzleDxtTextureHeuristic,
            UnpackMipAtlas = _mipAtlasUnpacker.UnpackMipAtlas,
            WriteDdsFile = _headerWriter.WriteAuxDdsFile
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
            if (verbose)
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
        {
            // When carving DDX from memory dumps, trailing garbage after the real LZX data
            // can cause decompression to fail mid-stream. If we already decompressed some
            // valid chunks before hitting garbage, return the partial result — the output
            // only contains data from fully-decompressed chunks.
            if (decompressedLen > 0)
            {
                if (verbose)
                    Console.WriteLine(
                        $"LZX partial decompression: got {decompressedLen} bytes from {compressedLen} consumed before failure");

                bytesConsumed = compressedLen;
                Array.Resize(ref decompressedData, decompressedLen);
                return decompressedData;
            }

            throw new InvalidOperationException($"LzxDecompressor decompression failed: {result}");
        }

        if (verbose) Console.WriteLine($"Decompressed {compressedLen} -> {decompressedLen} bytes");

        // The output buffer is a hard ceiling: LzxDecompressor stops writing once it fills and
        // still reports success, silently discarding the rest of the input. A saturated buffer
        // with unread input therefore means "we truncated the texture".
        //
        // Both signals are required. A full buffer with zero unread input is an exact fit, not
        // loss; and consuming a prefix of the input is normal on its own — ConvertDdx decompresses
        // a multi-chunk stream by handing the whole remainder to each call — so either signal
        // alone fires on healthy files.
        var leftover = compressedData.Length - compressedLen;
        if (decompressedLen >= decompressedData.Length && leftover > 0)
        {
            _currentOptions?.Diagnostics?.RecordTruncatedRead(
                $"decompress buffer saturated at {decompressedData.Length} bytes, {leftover} compressed bytes left unread");
        }

        bytesConsumed = compressedLen;
        if (decompressedLen < decompressedData.Length) Array.Resize(ref decompressedData, decompressedLen);
        return decompressedData;
    }

    private byte[] UnswizzleDxtTexture(byte[] src, int width, int height, uint format)
    {
        var swapEndian = _currentOptions == null || !_currentOptions.SkipEndianSwap;
        return TextureUtilities.UnswizzleMortonDxtAligned(src, width, height, format, swapEndian,
            _currentOptions?.Diagnostics);
    }

    /// <summary>
    ///     Plain logical-dims untile for heuristic call sites (memory-dump atlas dims, legacy
    ///     linear mip walks) — dimensions there are guesses, not real GPU surfaces, so the
    ///     aligned-extent semantics of <see cref="UnswizzleDxtTexture" /> do not apply.
    /// </summary>
    private byte[] UnswizzleDxtTextureHeuristic(byte[] src, int width, int height, uint format)
    {
        var swapEndian = _currentOptions == null || !_currentOptions.SkipEndianSwap;
        return TextureUtilities.UnswizzleMortonDxt(src, width, height, format, swapEndian,
            _currentOptions?.Diagnostics);
    }
}
