using System;
using System.IO;
using System.Runtime.InteropServices;
using XCompression;

namespace DDXConv
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("Usage: DDXConv <input.ddx> [output.dds] or DDXConv <directory> <output_directory>");
                Console.WriteLine("Converts Xbox 360 DDX texture files to DDS format");
                return;
            }

            // Simple CLI parsing: collect option flags (start with '-') and positional args
            var opts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var positional = new List<string>();
            foreach (var a in args)
            {
                if (a.StartsWith("-")) opts.Add(a);
                else positional.Add(a);
            }

            if (positional.Count < 1)
            {
                Console.WriteLine("Usage: DDXConv <input.ddx> [output.dds] [--atlas|-a] [--raw|-r]");
                return;
            }

            string inputPath = positional[0];
            bool saveAtlas = opts.Contains("--atlas") || opts.Contains("-a");
            bool saveRaw = opts.Contains("--raw") || opts.Contains("-r");

            if (Directory.Exists(inputPath))
            {
                // Batch convert all .ddx files in the directory
                if (positional.Count < 2)
                {
                    Console.WriteLine("Batch mode requires output directory as second positional argument.");
                    return;
                }

                string outputDir = positional[1];
                Directory.CreateDirectory(outputDir);

                var ddxFiles = Directory.GetFiles(inputPath, "*.ddx", SearchOption.AllDirectories);
                foreach (var ddxFile in ddxFiles)
                {
                    string relativePath = Path.GetRelativePath(inputPath, ddxFile);
                    string outputBatchPath = Path.Combine(outputDir, Path.ChangeExtension(relativePath, ".dds"));
                    Directory.CreateDirectory(Path.GetDirectoryName(outputBatchPath)!);

                    try
                    {
                        var parser = new DdxParser();
                        parser.ConvertDdxToDds(ddxFile, outputBatchPath, new ConversionOptions { SaveAtlas = saveAtlas, SaveRaw = saveRaw });
                        Console.WriteLine($"Converted {ddxFile} to {outputBatchPath}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error converting {ddxFile}: {ex.Message}");
                    }
                }

                Console.WriteLine($"Batch conversion completed. Converted {ddxFiles.Length} files.");
                return;
            }

            if (!File.Exists(inputPath))
            {
                Console.WriteLine($"Input file or directory not found: {inputPath}");
                return;
            }
            
            string outputPath = args.Length > 1 ? args[1] : Path.ChangeExtension(inputPath, ".dds");

            try
            {
                var parser = new DdxParser();
                parser.ConvertDdxToDds(inputPath, outputPath, new ConversionOptions { SaveAtlas = saveAtlas, SaveRaw = saveRaw });
                Console.WriteLine($"Successfully converted {inputPath} to {outputPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }
    }

    public class ConversionOptions
    {
        public bool SaveAtlas { get; set; }
        public bool SaveRaw { get; set; }
    }

    public class DdxParser
    {
        private const uint MAGIC_3XDO = 0x4F445833;
        private const uint MAGIC_3XDR = 0x52445833;

        public void ConvertDdxToDds(string inputPath, string outputPath, ConversionOptions options)
        {
            using (var reader = new BinaryReader(File.OpenRead(inputPath)))
            {
                uint magic = reader.ReadUInt32();
                
                bool is3xdr = (magic == MAGIC_3XDR);
                if (!is3xdr && magic != MAGIC_3XDO)
                {
                    throw new InvalidDataException($"Unknown DDX magic: 0x{magic:X8}.");
                }

                ConvertDdxToDds(reader, outputPath, options, is3xdr);
            }
        }

        private void ConvertDdxToDds(BinaryReader reader, string outputPath, ConversionOptions options, bool is3xdr)
        {
            // Read priority bytes (used for degradation)
            byte priorityL = reader.ReadByte();
            byte priorityC = reader.ReadByte();
            byte priorityH = reader.ReadByte();
            
            // Read version
            ushort version = reader.ReadUInt16();

            if (version < 3)
            {
                throw new NotSupportedException($"DDX version {version} is not supported. Need version >= 3");
            }

            // After reading version, we're at offset 0x09
            // D3DTexture header (52 bytes) starts at offset 0x08 (1 byte back)
            // We need to go back 1 byte and read 52 bytes to get to 0x3C
            reader.BaseStream.Seek(-1, SeekOrigin.Current); // Go back 1 byte to 0x08
            
            byte[] textureHeader = reader.ReadBytes(52); // Read 0x08 to 0x3C
            
            // Now we're at 0x3C, skip to 0x44 (8 more bytes)
            reader.ReadBytes(8);
            
            // Parse the D3DTexture header to extract dimensions and format from Format dwords
            var texture = ParseD3DTextureHeaderWithDimensions(textureHeader, out ushort width, out ushort height);
            
            Console.WriteLine($"Dimensions from D3D texture header: {width}x{height}");

            // For 3XDO files, the texture data starts immediately after the header at offset 0x44
            // There are no separate size fields - just read all remaining data
            long currentPos = reader.BaseStream.Position;
            long fileSize = reader.BaseStream.Length;
            uint remainingBytes = (uint)(fileSize - currentPos);
            
            // Read all texture data
            byte[] mainData = reader.ReadBytes((int)remainingBytes);
            
            // Calculate total expected size: atlas (2x resolution) + linear mips
            // Use ActualFormat instead of DataFormat for correct size calculation
            uint atlasSize = (uint)CalculateMipSize(width, height, texture.ActualFormat);
            uint linearDataSize = CalculateMainDataSize(width, height, texture.ActualFormat, CalculateMipLevels(width, height));
            
            // Decompress all chunks in sequence
            byte[] compressedData = mainData;
            List<byte[]> decompressedChunks = new List<byte[]>();
            int totalConsumed = 0;
            
            // Try to decompress first chunk
            byte[] firstChunk = DecompressXMemCompress(compressedData, atlasSize, out int firstChunkCompressedSize);
            Console.WriteLine($"Chunk 1: consumed {firstChunkCompressedSize} compressed bytes, got {firstChunk.Length} decompressed bytes");
            decompressedChunks.Add(firstChunk);
            totalConsumed += firstChunkCompressedSize;
            
            // Try to decompress additional chunks until we run out of data
            while (totalConsumed < compressedData.Length)
            {
                int offset = totalConsumed;
                int remainingSize = compressedData.Length - offset;
                
                if (remainingSize < 10) // Need at least some bytes for a valid XMemCompress chunk
                    break;
                
                Console.WriteLine($"Attempting to decompress chunk {decompressedChunks.Count + 1} at offset {offset} ({remainingSize} bytes remaining)");
                
                try
                {
                    byte[] remainingCompressed = new byte[remainingSize];
                    Array.Copy(compressedData, offset, remainingCompressed, 0, remainingSize);
                    
                    byte[] chunk = DecompressXMemCompress(remainingCompressed, atlasSize, out int chunkCompressedSize);
                    Console.WriteLine($"Chunk {decompressedChunks.Count + 1}: consumed {chunkCompressedSize} compressed bytes, got {chunk.Length} decompressed bytes");
                    decompressedChunks.Add(chunk);
                    totalConsumed += chunkCompressedSize;
                    
                    if (chunkCompressedSize == 0)
                        break; // Avoid infinite loop if nothing was consumed
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to decompress chunk {decompressedChunks.Count + 1}: {ex.Message}");
                    break;
                }
            }
            
            // Combine all decompressed chunks
            int totalDecompressed = decompressedChunks.Sum(c => c.Length);
            mainData = new byte[totalDecompressed];
            int writeOffset = 0;
            for (int i = 0; i < decompressedChunks.Count; i++)
            {
                Array.Copy(decompressedChunks[i], 0, mainData, writeOffset, decompressedChunks[i].Length);
                writeOffset += decompressedChunks[i].Length;
            }
            Console.WriteLine($"Combined {decompressedChunks.Count} chunks = {mainData.Length} bytes total (consumed {totalConsumed}/{compressedData.Length} compressed bytes)");
                
                // Save raw combined data for analysis (optional)
                if (options != null && options.SaveRaw)
                {
                    string rawPath = outputPath.Replace(".dds", "_raw.bin");
                    File.WriteAllBytes(rawPath, mainData);
                    Console.WriteLine($"Saved raw combined data to {rawPath}");
                }
            
            // Calculate expected main surface size with detected dimensions
            uint mainSurfaceSize = (uint)CalculateMipSize(width, height, texture.ActualFormat);
            
            byte[] linearData;
            
            // Check if we have two chunks or one chunk
            // Two chunk format can be:
            // 1. Exactly 2x atlasSize (for small textures like 128x128 or 256x256)
            // 2. Main surface + smaller mip atlas (for large textures like 1024x1024)
            bool isTwoChunkFormat = false;
            uint chunk1Size = 0;
            uint chunk2Size = 0;
            
            if (mainData.Length == atlasSize * 2)
            {
                // Small texture: two equal-sized chunks
                isTwoChunkFormat = true;
                chunk1Size = atlasSize;
                chunk2Size = atlasSize;
            }
            else if (mainData.Length > mainSurfaceSize)
            {
                // ALL DDX files use two-chunk format: chunk1 = mip atlas, chunk2 = main surface
                int remainingSize = mainData.Length - (int)mainSurfaceSize;
                
                isTwoChunkFormat = true;
                chunk1Size = (uint)remainingSize;
                chunk2Size = (uint)mainSurfaceSize;
                Console.WriteLine($"Detected two-chunk format: atlas={chunk1Size} + main={chunk2Size}");
            }
            
            if (isTwoChunkFormat)
            {
                // Two-chunk format
                // chunk1Size and chunk2Size were set by detection above
                // For all textures: chunk1 (smaller) = mip atlas, chunk2 (larger) = main surface
                Console.WriteLine($"Two-chunk format confirmed ({mainData.Length} bytes)");
                
                // Determine block size for atlas dimension calculation
                int blockSize;
                switch (texture.ActualFormat)
                {
                    case 0x52: // DXT1
                    case 0x7B: // ATI1/BC4
                    case 0x82: // DXT1 variant
                    case 0x86: // DXT1 variant
                    case 0x12: // GPUTEXTUREFORMAT_DXT1
                        blockSize = 8;
                        break;
                        
                    case 0x53: // DXT3
                    case 0x54: // DXT5
                    case 0x71: // DXT5 variant (normal maps)
                    case 0x88: // DXT5 variant
                    case 0x13: // GPUTEXTUREFORMAT_DXT2/3
                    case 0x14: // GPUTEXTUREFORMAT_DXT4/5
                        blockSize = 16;
                        break;
                        
                    default:
                        blockSize = 16;
                        break;
                }
                
                byte[] chunk1 = new byte[chunk1Size];
                byte[] chunk2 = new byte[chunk2Size];
                Array.Copy(mainData, 0, chunk1, 0, chunk1Size);
                Array.Copy(mainData, chunk1Size, chunk2, 0, chunk2Size);
                
                // Determine atlas dimensions
                int atlasWidth, atlasHeight;
                
                if (width <= 256 && height <= 256)
                {
                    // Small texture: atlas same size as main
                    atlasWidth = width;
                    atlasHeight = height;
                }
                else
                {
                    // Large texture: calculate atlas dimensions from actual chunk1 size
                    // The atlas contains mips, determine dimensions by reverse-calculating from size
                    int blocksInAtlas = (int)chunk1Size / blockSize;
                    
                    // For non-square textures, atlas width is typically 5/8 of main width
                    // For square textures, atlas is same dimensions as main
                    if (width == height)
                    {
                        // Square: atlas same as main (1024x1024 for 1024x1024 texture)
                        atlasWidth = width;
                        atlasHeight = height;
                    }
                    else if (width > height)
                    {
                        // Wider than tall: e.g., 512x256 -> atlas 320x256
                        atlasWidth = (width * 5) / 8;
                        atlasHeight = height;
                    }
                    else
                    {
                        // Taller than wide: atlas width * 5/8, height same
                        atlasWidth = width;
                        atlasHeight = (height * 5) / 8;
                    }
                }
                
                Console.WriteLine($"Untiling chunk1 ({chunk1Size} bytes) as atlas {atlasWidth}x{atlasHeight} and chunk2 ({chunk2Size} bytes) as main {width}x{height}");
                
                // Untile both chunks
                byte[] untiledAtlas = is3xdr ? chunk1 : UnswizzleDXTTexture(chunk1, atlasWidth, atlasHeight, texture.ActualFormat);
                byte[] untiledMain = is3xdr ? chunk2 : UnswizzleDXTTexture(chunk2, width, height, texture.ActualFormat);
                
                Console.WriteLine($"Untiled both chunks to {untiledAtlas.Length} and {untiledMain.Length} bytes");
                
                // Save the untiled atlas as a DDS file for inspection (optional)
                if (options != null && options.SaveAtlas)
                {
                    string atlasPath = outputPath.Replace(".dds", "_atlas.dds");
                    var atlasTexture = new D3DTextureInfo
                    {
                        Width = (ushort)atlasWidth,
                        Height = (ushort)atlasHeight,
                        Format = texture.Format,
                        ActualFormat = texture.ActualFormat,
                        DataFormat = texture.DataFormat,
                        MipLevels = 1
                    };
                    WriteDdsFile(atlasPath, atlasTexture, untiledAtlas, null);
                    Console.WriteLine($"Saved untiled atlas to {atlasPath}");
                }
                
                // Extract mips from atlas
                byte[] mips = UnpackMipAtlas(untiledAtlas, atlasWidth, atlasHeight, texture.ActualFormat);
                Console.WriteLine($"Extracted {mips.Length} bytes of mips from atlas");
                
                linearData = new byte[untiledMain.Length + mips.Length];
                Array.Copy(untiledMain, 0, linearData, 0, untiledMain.Length);
                Array.Copy(mips, 0, linearData, untiledMain.Length, mips.Length);
                
                Console.WriteLine($"Combined {untiledMain.Length} bytes main surface + {mips.Length} bytes mips = {linearData.Length} total");
            }
            else
            {
                // Single-chunk format: could be main surface only, or main + partial mips
                Console.WriteLine($"Single-chunk format detected ({mainData.Length} bytes, expected {mainSurfaceSize} for {width}x{height})");
                
                // Check if data might be two square chunks instead of one rectangular
                // E.g., header says 512x256 but data is actually 2x 256x256
                // This pattern is only used for format 0x71 (normal maps with mip atlas)
                int halfSize = mainData.Length / 2;
                int squareSize = (int)Math.Sqrt(halfSize / 16) * 4;
                bool couldBeTwoSquares = texture.ActualFormat == 0x71 && 
                                         (squareSize * squareSize / 16 * 16 == halfSize) && 
                                         width == squareSize * 2 && height == squareSize;
                
                // Check if we have more data than just the main surface (partial mips)
                if (mainData.Length > mainSurfaceSize)
                {
                    // Has partial mips
                    Console.WriteLine($"Detected extra data: {mainData.Length} > {mainSurfaceSize}");
                    
                    // Check if this is large texture (>=512) with sequential mips
                    if (width >= 512 && height >= 512)
                    {
                        Console.WriteLine($"Large texture detected - treating extra as sequential mip data");
                        
                        // Untile main surface
                        byte[] mainSurfaceTiled = new byte[mainSurfaceSize];
                        Array.Copy(mainData, 0, mainSurfaceTiled, 0, (int)mainSurfaceSize);
                        byte[] mainSurfaceUntiled = is3xdr ? mainSurfaceTiled : UnswizzleDXTTexture(mainSurfaceTiled, width, height, texture.ActualFormat);
                        
                        // Process remaining mips sequentially
                        int remainingSize = mainData.Length - (int)mainSurfaceSize;
                        byte[] remainingData = new byte[remainingSize];
                        Array.Copy(mainData, (int)mainSurfaceSize, remainingData, 0, remainingSize);
                        
                        // Calculate how many mip levels we have
                        int mipLevels = 1;
                        int processedMipData = 0;
                        int mipWidth = width / 2;
                        int mipHeight = height / 2;
                        
                        while (mipWidth >= 4 && mipHeight >= 4 && processedMipData < remainingSize)
                        {
                            int mipSize = CalculateMipSize(mipWidth, mipHeight, texture.ActualFormat);
                            if (processedMipData + mipSize > remainingSize) break;
                            processedMipData += mipSize;
                            mipLevels++;
                            mipWidth /= 2;
                            mipHeight /= 2;
                        }
                        
                        Console.WriteLine($"Found {mipLevels} mip levels (main + {mipLevels-1} mips)");
                        
                        // Untile each mip sequentially
                        List<byte[]> mipDataList = new List<byte[]>();
                        mipDataList.Add(mainSurfaceUntiled);
                        
                        int mipOffset = 0;
                        mipWidth = width / 2;
                        mipHeight = height / 2;
                        for (int i = 1; i < mipLevels; i++)
                        {
                            int mipSize = CalculateMipSize(mipWidth, mipHeight, texture.ActualFormat);
                            byte[] mipTiled = new byte[mipSize];
                            Array.Copy(remainingData, mipOffset, mipTiled, 0, mipSize);
                            byte[] mipUntiled = is3xdr ? mipTiled : UnswizzleDXTTexture(mipTiled, mipWidth, mipHeight, texture.ActualFormat);
                            mipDataList.Add(mipUntiled);
                            mipOffset += mipSize;
                            mipWidth /= 2;
                            mipHeight /= 2;
                        }
                        
                        // Combine all mips
                        int totalSize = mipDataList.Sum(m => m.Length);
                        linearData = new byte[totalSize];
                        int offset = 0;
                        foreach (var mip in mipDataList)
                        {
                            Array.Copy(mip, 0, linearData, offset, mip.Length);
                            offset += mip.Length;
                        }
                        texture.MipLevels = (byte)mipLevels;
                        Console.WriteLine($"Set MipLevels to {texture.MipLevels}");
                    }
                    else
                    {
                        // Small texture - might use horizontal split for 256x256
                        Console.WriteLine($"Attempting horizontal split for small texture");
                        
                        // The chunks might be split horizontally - try to determine split point
                        // For a 256x256 texture split into 192x256 + 64x256:
                        int chunk1Width = 192;
                        int chunk2Width = 64;
                        int chunkHeight = 256;
                        
                        int horizontalChunk1Size = CalculateMipSize(chunk1Width, chunkHeight, texture.ActualFormat);
                        int horizontalChunk2Size = mainData.Length - horizontalChunk1Size;
                        
                        Console.WriteLine($"Trying horizontal split: {chunk1Width}x{chunkHeight} ({horizontalChunk1Size} bytes) + {chunk2Width}x{chunkHeight} ({horizontalChunk2Size} bytes)");
                        
                        if (horizontalChunk1Size + horizontalChunk2Size == mainData.Length && horizontalChunk2Size > 0)
                        {
                            byte[] chunk1Tiled = new byte[horizontalChunk1Size];
                            byte[] chunk2Tiled = new byte[horizontalChunk2Size];
                            Array.Copy(mainData, 0, chunk1Tiled, 0, horizontalChunk1Size);
                            Array.Copy(mainData, horizontalChunk1Size, chunk2Tiled, 0, horizontalChunk2Size);
                            
                            byte[] chunk1Untiled = is3xdr ? chunk1Tiled : UnswizzleDXTTexture(chunk1Tiled, chunk1Width, chunkHeight, texture.ActualFormat);
                            byte[] chunk2Untiled = is3xdr ? chunk2Tiled : UnswizzleDXTTexture(chunk2Tiled, chunk2Width, chunkHeight, texture.ActualFormat);
                            
                            Console.WriteLine($"Untiled chunks: {chunk1Untiled.Length} + {chunk2Untiled.Length} bytes");
                            
                            // Now we need to interleave these horizontally to form 256x256
                            // chunk1 is left 192 pixels, chunk2 is right 64 pixels
                            linearData = InterleaveHorizontalChunks(chunk1Untiled, chunk2Untiled, chunk1Width, chunk2Width, chunkHeight, texture.ActualFormat);
                            Console.WriteLine($"Interleaved to {linearData.Length} bytes");
                            texture.MipLevels = 1;
                            Console.WriteLine($"Set MipLevels to {texture.MipLevels}");
                        }
                        else
                        {
                            // Fallback: try untiling just the main surface portion
                            Console.WriteLine($"Horizontal split didn't match, trying simple split at mainSurfaceSize");
                            byte[] mainSurfaceTiled = new byte[mainSurfaceSize];
                            Array.Copy(mainData, 0, mainSurfaceTiled, 0, (int)mainSurfaceSize);
                        
                            byte[] mainSurfaceUntiled = is3xdr ? mainSurfaceTiled : UnswizzleDXTTexture(mainSurfaceTiled, width, height, texture.ActualFormat);
                            Console.WriteLine($"Untiled main surface: {mainSurfaceUntiled.Length} bytes");
                            
                            // The remaining data might be packed mips - try to extract them
                            int remainingSize = mainData.Length - (int)mainSurfaceSize;
                            Console.WriteLine($"Remaining data: {remainingSize} bytes (might be packed mips)");
                        
                            // For a 128x128 mip: 16384 bytes
                            // Check if we have exactly one mip's worth
                            int expectedMip1Size = CalculateMipSize(width / 2, height / 2, texture.ActualFormat);
                            if (remainingSize == expectedMip1Size)
                            {
                                Console.WriteLine($"Remaining data matches 128x128 mip size, extracting...");
                                byte[] mipTiled = new byte[remainingSize];
                                Array.Copy(mainData, (int)mainSurfaceSize, mipTiled, 0, remainingSize);
                                
                                byte[] mipUntiled = is3xdr ? mipTiled : UnswizzleDXTTexture(mipTiled, width / 2, height / 2, texture.ActualFormat);
                                Console.WriteLine($"Untiled mip: {mipUntiled.Length} bytes");
                                
                                linearData = new byte[mainSurfaceUntiled.Length + mipUntiled.Length];
                                Array.Copy(mainSurfaceUntiled, 0, linearData, 0, mainSurfaceUntiled.Length);
                                Array.Copy(mipUntiled, 0, linearData, mainSurfaceUntiled.Length, mipUntiled.Length);
                                texture.MipLevels = 2;
                            }
                            else
                            {
                                // Don't know the layout, just use main surface
                                Console.WriteLine($"WARNING: Unknown mip layout, using only main surface");
                                linearData = mainSurfaceUntiled;
                                texture.MipLevels = 1;
                            }
                            
                            Console.WriteLine($"Set MipLevels to {texture.MipLevels}");
                        }
                    }
                }
                else if (mainData.Length < mainSurfaceSize)
                {
                    // Data is smaller than expected - just untile what we have
                    Console.WriteLine($"WARNING: Data size smaller than expected: {mainData.Length} < {mainSurfaceSize}");
                    
                    // Untile as a single texture
                    byte[] untiled = is3xdr ? mainData : UnswizzleDXTTexture(mainData, width, height, texture.ActualFormat);
                    Console.WriteLine($"Untiled to {untiled.Length} bytes");
                    
                    linearData = untiled;
                    texture.MipLevels = 1;
                    Console.WriteLine($"Set MipLevels to {texture.MipLevels}");
                }
                else if (mainData.Length == mainSurfaceSize * 2)
                {
                    // Exactly 2x the expected size - might be two separate surfaces
                    Console.WriteLine($"Data is exactly 2x expected size - treating as two chunks");
                    
                    // Split into two equal chunks
                    byte[] chunk1TiledAlt = new byte[mainData.Length / 2];
                    byte[] chunk2TiledAlt = new byte[mainData.Length / 2];
                    Array.Copy(mainData, 0, chunk1TiledAlt, 0, mainData.Length / 2);
                    Array.Copy(mainData, mainData.Length / 2, chunk2TiledAlt, 0, mainData.Length / 2);
                    
                    byte[] chunk1UntiledAlt = is3xdr ? chunk1TiledAlt : UnswizzleDXTTexture(chunk1TiledAlt, width, height, texture.ActualFormat);
                    byte[] chunk2UntiledAlt = is3xdr ? chunk2TiledAlt : UnswizzleDXTTexture(chunk2TiledAlt, width, height, texture.ActualFormat);
                    Console.WriteLine($"Untiled chunks to {chunk1UntiledAlt.Length} + {chunk2UntiledAlt.Length} bytes");
                    
                    // Chunk 1 might have mips packed
                    byte[] mipsAlt = UnpackMipAtlas(chunk1UntiledAlt, width, height, texture.ActualFormat);
                    Console.WriteLine($"Extracted {mipsAlt.Length} bytes of mips from chunk 1");
                    
                    linearData = new byte[chunk2UntiledAlt.Length + mipsAlt.Length];
                    Array.Copy(chunk2UntiledAlt, 0, linearData, 0, chunk2UntiledAlt.Length);
                    Array.Copy(mipsAlt, 0, linearData, chunk2UntiledAlt.Length, mipsAlt.Length);
                    
                    Console.WriteLine($"Combined {chunk2UntiledAlt.Length} bytes main + {mipsAlt.Length} bytes mips = {linearData.Length} total");
                }
                // Check if data might be two square chunks before assuming exact match
                else if (couldBeTwoSquares)
                {
                    Console.WriteLine($"Exact size match but might be two {squareSize}x{squareSize} chunks instead of {width}x{height}");
                    Console.WriteLine($"Processing as {squareSize}x{squareSize} texture with mips in chunk 1, main surface in chunk 2");
                    
                    byte[] chunk1Tiled = new byte[halfSize];
                    byte[] chunk2Tiled = new byte[halfSize];
                    Array.Copy(mainData, 0, chunk1Tiled, 0, halfSize);
                    Array.Copy(mainData, halfSize, chunk2Tiled, 0, halfSize);
                    
                    byte[] chunk1Untiled = is3xdr ? chunk1Tiled : UnswizzleDXTTexture(chunk1Tiled, squareSize, squareSize, texture.ActualFormat);
                    byte[] chunk2Untiled = is3xdr ? chunk2Tiled : UnswizzleDXTTexture(chunk2Tiled, squareSize, squareSize, texture.ActualFormat);
                    Console.WriteLine($"Untiled chunks to {chunk1Untiled.Length} + {chunk2Untiled.Length} bytes");
                    
                    // Chunk 1 has mip atlas, chunk 2 has main surface
                    byte[] mips = UnpackMipAtlas(chunk1Untiled, squareSize, squareSize, texture.ActualFormat);
                    Console.WriteLine($"Extracted {mips.Length} bytes of mips from chunk 1");
                    
                    linearData = new byte[chunk2Untiled.Length + mips.Length];
                    Array.Copy(chunk2Untiled, 0, linearData, 0, chunk2Untiled.Length);
                    Array.Copy(mips, 0, linearData, chunk2Untiled.Length, mips.Length);
                    
                    texture.Width = (ushort)squareSize;
                    texture.Height = (ushort)squareSize;
                    texture.MipLevels = (uint)(CalculateMipLevels((uint)squareSize, (uint)squareSize));
                    Console.WriteLine($"Final texture: {texture.Width}x{texture.Height} with {texture.MipLevels} mip levels");
                    Console.WriteLine($"Total data: {linearData.Length} bytes ({chunk2Untiled.Length} main + {mips.Length} mips)");
                }
                else
                {
                    // Exact match - check if this is mip atlas + main surface format
                    // For 128x128: 24576 bytes atlas (256x192) + 8192 bytes main (128x128) = 32768 total
                    int blockSize = (texture.ActualFormat == 0x82 || texture.ActualFormat == 0x52 || texture.ActualFormat == 0x7B) ? 8 : 16;
                    
                    // Check for 128x128 texture with mip atlas
                    int atlasSize128 = 24576;
                    int mainSize128 = 8192;
                    if (width == 128 && height == 128 && mainData.Length == atlasSize128 + mainSize128)
                    {
                        Console.WriteLine($"Detected 128x128 texture with mip atlas (24576 + 8192 bytes)");
                        
                        byte[] chunk1 = new byte[atlasSize128];
                        byte[] chunk2 = new byte[mainSize128];
                        Array.Copy(mainData, 0, chunk1, 0, atlasSize128);
                        Array.Copy(mainData, atlasSize128, chunk2, 0, mainSize128);
                        
                        // Untile: atlas is 256x192, main is 128x128
                        byte[] untiledAtlas = is3xdr ? chunk1 : UnswizzleDXTTexture(chunk1, 256, 192, texture.ActualFormat);
                        byte[] untiledMain = is3xdr ? chunk2 : UnswizzleDXTTexture(chunk2, 128, 128, texture.ActualFormat);
                        
                        Console.WriteLine($"Untiled atlas (256x192) to {untiledAtlas.Length} bytes");
                        Console.WriteLine($"Untiled main (128x128) to {untiledMain.Length} bytes");
                        
                        // Save the untiled atlas for inspection
                        string atlasPath = outputPath.Replace(".dds", "_atlas_untiled.bin");
                        File.WriteAllBytes(atlasPath, untiledAtlas);
                        Console.WriteLine($"Saved untiled atlas to {atlasPath}");
                        
                        // Extract mips from atlas
                        byte[] mips = UnpackMipAtlas(untiledAtlas, 256, 192, texture.ActualFormat);
                        Console.WriteLine($"Extracted {mips.Length} bytes of mips from atlas");
                        
                        // Combine main + mips
                        linearData = new byte[untiledMain.Length + mips.Length];
                        Array.Copy(untiledMain, 0, linearData, 0, untiledMain.Length);
                        Array.Copy(mips, 0, linearData, untiledMain.Length, mips.Length);
                        
                        texture.Width = 128;
                        texture.Height = 128;
                        texture.MipLevels = CalculateMipLevels(128, 128);
                        Console.WriteLine($"Final: 128x128 with {texture.MipLevels} mip levels, {linearData.Length} bytes total");
                    }
                    else
                    {
                        // Just untile as-is
                        byte[] untiled = UnswizzleDXTTexture(mainData, width, height, texture.ActualFormat);
                        Console.WriteLine($"Untiled to {untiled.Length} bytes");
                        
                        linearData = untiled;
                        texture.MipLevels = 1;
                        Console.WriteLine($"Set MipLevels to {texture.MipLevels}");
                    }
                }
            }
            
            // No tail data in first tested file
            byte[] tailData = null;

            // MipLevels already set correctly (either from hasMips detection or default)
            // Don't recalculate here as it would override the no-mips detection
            
            // Convert to DDS and write
            WriteDdsFile(outputPath, texture, linearData, tailData);
        }
        
        private uint ReadBigEndianUInt32(byte[] data, int offset)
        {
            return ((uint)data[offset] << 24) |
                   ((uint)data[offset + 1] << 16) |
                   ((uint)data[offset + 2] << 8) |
                   (uint)data[offset + 3];
        }
        
        private byte[] DecompressXMemCompress(byte[] compressedData, uint uncompressedSize, out int bytesConsumed)
        {
            // Xbox 360 DDX files may contain more data than the atlas size suggests
            // Allocate extra space for potential linear mip data
            byte[] decompressedData = new byte[uncompressedSize * 2]; // Double the buffer to be safe
            
            using (var context = new DecompressionContext())
            {
                int compressedLen = compressedData.Length;
                int decompressedLen = decompressedData.Length;
                
                ErrorCode result = context.Decompress(
                    compressedData, 0, ref compressedLen,
                    decompressedData, 0, ref decompressedLen);

                if (result != ErrorCode.None)
                {
                    throw new Exception($"XMemCompress decompression failed: {result}");
                }
                
                Console.WriteLine($"Decompressed {compressedLen} -> {decompressedLen} bytes");
                bytesConsumed = compressedLen;
                
                // Trim to actual decompressed size
                if (decompressedLen < decompressedData.Length)
                {
                    Array.Resize(ref decompressedData, decompressedLen);
                }
            }

            return decompressedData;
        }

        private D3DTextureInfo ParseD3DTextureHeaderWithDimensions(byte[] header, out ushort width, out ushort height)
        {
            // Xbox 360 D3D texture header structure (52 bytes starting at file offset 0x10):
            // The header we receive here is the 52 bytes from 0x10-0x43
            // Offset 0-23 in header: D3DResource (Common, RefCount, Fence, ReadFence, Identifier, BaseFlush)
            // Offset 24-27 in header: MipFlush  
            // Offset 28-51 in header: Format (xe_gpu_texture_fetch_t, 6 dwords = 24 bytes)
            //
            // Format structure (from Xenia xenos.h):
            //   dword_0: pitch, tiling, clamp modes
            //   dword_1: format (bits 0-5), endianness, base_address
            //   dword_2: size_2d { width:13, height:13, stack_depth:6 }
            //   dword_3-5: swizzle, mip info, etc.
            //
            // IMPORTANT: Xbox 360 is big-endian, so Format dwords must be read as big-endian!
            
            // Our header starts at file offset 0x08
            // Dimensions dword is at formatDword[5] position = offset 16+20 = 36, stored as BIG-ENDIAN
            byte[] dword5Bytes = new byte[4];
            Array.Copy(header, 36, dword5Bytes, 0, 4);
            Array.Reverse(dword5Bytes); // Convert from big-endian to little-endian
            uint dword5 = BitConverter.ToUInt32(dword5Bytes, 0);
            
            // Decode size_2d structure (dimensions stored as size-1):
            // Bits 0-12: width - 1
            // Bits 13-25: height - 1  
            // Bits 26-31: stack_depth
            width = (ushort)((dword5 & 0x1FFF) + 1);
            height = (ushort)(((dword5 >> 13) & 0x1FFF) + 1);
            
            Console.WriteLine($"Parsed from Format dword_5: 0x{dword5:X8} -> {width}x{height}");
            
            // Now parse the rest using the existing method
            return ParseD3DTextureHeader(header, width, height);
        }

        private D3DTextureInfo ParseD3DTextureHeader(byte[] header, ushort width, ushort height)
        {
            // Xbox 360 D3D texture header structure
            // The header we receive is 52 bytes starting at file offset 0x08
            // Dimensions are passed separately - they're extracted from Format dword_2
            
            var info = new D3DTextureInfo();

            // Set dimensions from parameters
            info.Width = width;
            info.Height = height;
            
            // Our header starts at file offset 0x08
            // The .old version read header from file 0x10 and read formatDwords from offset 8 within that header = file 0x18
            // So we need to read formatDwords from file 0x18, which is offset 0x18-0x08 = 0x10 = 16 within our header
            // Format dwords are stored as LITTLE-ENDIAN (already in Intel byte order), NO byte reversal needed
            
            uint[] formatDwords = new uint[6];
            for (int i = 0; i < 6; i++)
            {
                formatDwords[i] = BitConverter.ToUInt32(header, 16 + i * 4);
            }
            
            uint dword0 = formatDwords[0];
            uint dword3 = formatDwords[3];
            uint dword4 = formatDwords[4];

            // The format appears to be in DWORD[3] byte 0 (bits 0-7)
            // But for format 0x82, the actual texture format (DXT1/DXT5) is in DWORD[4] byte 3
            info.DataFormat = dword3 & 0xFF;
            
            // For 0x82 textures, check DWORD[4] high byte to distinguish DXT1 from DXT5
            uint actualFormat = (dword4 >> 24) & 0xFF;
            Console.WriteLine($"Format detection: DataFormat=0x{info.DataFormat:X2}, DWORD[4]=0x{dword4:X8}, ActualFormat=0x{actualFormat:X2}");
            
            info.Endian = (dword0 >> 26) & 0x3;
            info.Tiled = ((dword0 >> 19) & 1) != 0;

            // Store the actual format for untiling
            info.ActualFormat = actualFormat != 0 ? actualFormat : info.DataFormat;
            
            // Determine DDS format
            info.Format = GetDxgiFormat(info.ActualFormat);
            
            // Calculate mip levels from dimensions
            info.MipLevels = CalculateMipLevels(info.Width, info.Height);
            
            // Calculate main data size (before mip tail)
            info.MainDataSize = CalculateMainDataSize(info.Width, info.Height, info.ActualFormat, info.MipLevels);

            return info;
        }

        private uint GetDxgiFormat(uint gpuFormat)
        {
            // Map Xbox 360 GPU texture formats to D3D formats
            // For 0x82 base format, the actual format is determined by DWORD[4]
            
            return gpuFormat switch
            {
                0x52 => 0x31545844, // DXT1
                0x53 => 0x33545844, // DXT3  
                0x54 => 0x35545844, // DXT5
                0x71 => 0x32495441, // ATI2 (BC5) - Xbox 360 normal map format
                0x7B => 0x31495441, // ATI1 (BC4) - Single channel format (specular maps)
                0x82 => 0x31545844, // DXT1 (default when DWORD[4] is 0)
                0x86 => 0x31545844, // DXT1 variant
                0x88 => 0x35545844, // DXT5 variant
                0x12 => 0x31545844, // GPUTEXTUREFORMAT_DXT1
                0x13 => 0x33545844, // GPUTEXTUREFORMAT_DXT2/3
                0x14 => 0x35545844, // GPUTEXTUREFORMAT_DXT4/5
                0x06 => 0x18280046, // GPUTEXTUREFORMAT_8_8_8_8 -> A8R8G8B8
                0x04 => 0x28280044, // GPUTEXTUREFORMAT_5_6_5 -> R5G6B5
                _ => 0x31545844 // Default to DXT1
            };
        }

        private uint CalculateMipLevels(uint width, uint height)
        {
            uint levels = 1;
            uint w = width;
            uint h = height;
            
            while (w > 1 || h > 1)
            {
                w = Math.Max(1, w / 2);
                h = Math.Max(1, h / 2);
                levels++;
            }
            
            return levels;
        }

        private uint CalculateMainDataSize(uint width, uint height, uint format, uint mipLevels)
        {
            uint totalSize = 0;
            uint w = width;
            uint h = height;
            
            for (int i = 0; i < mipLevels; i++)
            {
                uint mipSize = CalculateMipSize(w, h, format);
                totalSize += mipSize;
                
                w = Math.Max(1, w / 2);
                h = Math.Max(1, h / 2);
            }
            
            return totalSize;
        }

        private uint CalculateMipSize(uint width, uint height, uint format)
        {
            // Calculate size based on format
            switch (format)
            {
                case 0x52: // DXT1
                case 0x7B: // ATI1/BC4 (single channel, same block size as DXT1)
                case 0x82: // DXT1 variant
                case 0x86: // DXT1 variant
                case 0x12: // GPUTEXTUREFORMAT_DXT1
                    return Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 8;
                
                case 0x53: // DXT3
                case 0x54: // DXT5
                case 0x71: // DXT5 variant (normal maps)
                case 0x88: // DXT5 variant
                case 0x13: // GPUTEXTUREFORMAT_DXT2/3
                case 0x14: // GPUTEXTUREFORMAT_DXT4/5
                    return Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 16;
                
                case 0x06: // A8R8G8B8 - 32 bits per pixel
                    return width * height * 4;
                
                case 0x04: // R5G6B5 - 16 bits per pixel
                    return width * height * 2;
                
                default:
                    return width * height * 4; // Default to 32bpp
            }
        }
        
        private int CalculateMipSize(int width, int height, uint format)
        {
            return (int)CalculateMipSize((uint)width, (uint)height, format);
        }

        private void WriteDdsFile(string outputPath, D3DTextureInfo texture, byte[] mainData, byte[] tailData)
        {
            Console.WriteLine($"Writing DDS: Format=0x{texture.Format:X8}, ActualFormat=0x{texture.ActualFormat:X2}, DataFormat=0x{texture.DataFormat:X2}, MipLevels={texture.MipLevels}");
            using (var writer = new BinaryWriter(File.Create(outputPath)))
            {
                // Write DDS header
                WriteDdsHeader(writer, texture);
                
                // Write texture data
                writer.Write(mainData);
                
                if (tailData != null && tailData.Length > 0)
                {
                    writer.Write(tailData);
                }
            }
        }

        private void WriteDdsHeader(BinaryWriter writer, D3DTextureInfo texture)
        {
            // DDS magic
            writer.Write(0x20534444); // "DDS "

            // DDS_HEADER
            writer.Write(124); // dwSize
            
            uint flags = 0x1 | 0x2 | 0x4 | 0x1000; // DDSD_CAPS | DDSD_HEIGHT | DDSD_WIDTH | DDSD_PIXELFORMAT
            if (texture.MipLevels > 1)
                flags |= 0x20000; // DDSD_MIPMAPCOUNT
            
            writer.Write(flags); // dwFlags
            writer.Write(texture.Height); // dwHeight
            writer.Write(texture.Width); // dwWidth
            
            uint pitch = CalculatePitch(texture.Width, texture.ActualFormat);
            writer.Write(pitch); // dwPitchOrLinearSize
            
            writer.Write(0); // dwDepth
            writer.Write(texture.MipLevels); // dwMipMapCount
            
            // dwReserved1[11]
            for (int i = 0; i < 11; i++)
                writer.Write(0);

            // DDS_PIXELFORMAT
            WriteDdsPixelFormat(writer, texture.Format);

            // dwCaps
            uint caps = 0x1000; // DDSCAPS_TEXTURE
            if (texture.MipLevels > 1)
                caps |= 0x400000 | 0x8; // DDSCAPS_MIPMAP | DDSCAPS_COMPLEX
            
            writer.Write(caps);
            writer.Write(0); // dwCaps2
            writer.Write(0); // dwCaps3
            writer.Write(0); // dwCaps4
            writer.Write(0); // dwReserved2
        }

        private void WriteDdsPixelFormat(BinaryWriter writer, uint fourccCode)
        {
            Console.WriteLine($"WriteDdsPixelFormat: fourccCode=0x{fourccCode:X8}");
            writer.Write(32); // dwSize
            writer.Write(0x4); // dwFlags = DDPF_FOURCC
            writer.Write(fourccCode); // FourCC code (DDS format: 0x31545844=DXT1, 0x35545844=DXT5)
            writer.Write(0); // dwRGBBitCount
            writer.Write(0); // dwRBitMask
            writer.Write(0); // dwGBitMask
            writer.Write(0); // dwBBitMask
            writer.Write(0); // dwABitMask
        }

        private uint CalculatePitch(uint width, uint format)
        {
            switch (format)
            {
                case 0x12: // DXT1
                    return Math.Max(1, (width + 3) / 4) * 8;
                
                case 0x13: // DXT3
                case 0x14: // DXT5
                    return Math.Max(1, (width + 3) / 4) * 16;
                
                case 0x06: // A8R8G8B8
                    return width * 4;
                
                case 0x04: // R5G6B5
                    return width * 2;
                
                default:
                    return width * 4;
            }
        }
        
        private byte[] UnswizzleDXTTexture(byte[] src, int width, int height, uint format)
        {
            // Determine block size based on format
            int blockSize;
            switch (format)
            {
                case 0x52: // DXT1
                case 0x7B: // ATI1/BC4 (single channel, same block size as DXT1)
                case 0x82: // DXT1 variant
                case 0x86: // DXT1 variant
                case 0x12: // GPUTEXTUREFORMAT_DXT1
                    blockSize = 8;
                    break;
                    
                case 0x53: // DXT3
                case 0x54: // DXT5
                case 0x71: // DXT5 variant (normal maps)
                case 0x88: // DXT5 variant
                case 0x13: // GPUTEXTUREFORMAT_DXT2/3
                case 0x14: // GPUTEXTUREFORMAT_DXT4/5
                    blockSize = 16;
                    break;
                    
                default:
                    return src; // Unknown format, return as-is
            }
            
            int blocksWide = width / 4;
            int blocksHigh = height / 4;
            byte[] dst = new byte[src.Length];
            
            // Xbox 360 tiling algorithm from Xenia emulator
            // Bytes per pixel (log2) - for DXT blocks
            uint log2Bpp = (uint)(blockSize / 4 + ((blockSize / 2) >> (blockSize / 4)));
            
            for (int y = 0; y < blocksHigh; y++)
            {
                uint inputRowOffset = TiledOffset2DRow((uint)y, (uint)blocksWide, log2Bpp);
                
                for (int x = 0; x < blocksWide; x++)
                {
                    uint inputOffset = TiledOffset2DColumn((uint)x, (uint)y, log2Bpp, inputRowOffset);
                    inputOffset >>= (int)log2Bpp;
                    
                    int srcOffset = (int)(inputOffset * blockSize);
                    int dstOffset = (y * blocksWide + x) * blockSize;
                    
                    if (srcOffset + blockSize <= src.Length && dstOffset + blockSize <= dst.Length)
                    {
                        // Copy block and fix endianness for each 16-bit word
                        for (int i = 0; i < blockSize; i += 2)
                        {
                            // Xbox 360 is big-endian, swap bytes
                            dst[dstOffset + i] = src[srcOffset + i + 1];
                            dst[dstOffset + i + 1] = src[srcOffset + i];
                        }
                    }
                }
            }
            
            return dst;
        }
        
        // Xbox 360 tiling functions from Xenia emulator
        // https://github.com/xenia-project/xenia/blob/master/src/xenia/gpu/texture_conversion.cc
        private uint TiledOffset2DRow(uint y, uint width, uint log2Bpp)
        {
            uint macro = (y / 32 * (width / 32)) << (int)(log2Bpp + 7);
            uint micro = (y & 6) << 2 << (int)log2Bpp;
            return macro + ((micro & ~0xFu) << 1) + (micro & 0xF) +
                   ((y & 8) << (int)(3 + log2Bpp)) + ((y & 1) << 4);
        }
        
        private uint TiledOffset2DColumn(uint x, uint y, uint log2Bpp, uint baseOffset)
        {
            uint macro = (x / 32) << (int)(log2Bpp + 7);
            uint micro = (x & 7) << (int)log2Bpp;
            uint offset = baseOffset + macro + ((micro & ~0xFu) << 1) + (micro & 0xF);
            return ((offset & ~0x1FFu) << 3) + ((offset & 0x1C0) << 2) + (offset & 0x3F) +
                   ((y & 16) << 7) + (((((y & 8) >> 2) + (x >> 3)) & 3) << 6);
        }
        
        private byte[] InterleaveHorizontalChunks(byte[] leftChunk, byte[] rightChunk, int leftWidth, int rightWidth, int height, uint format)
        {
            // Interleave two chunks horizontally to form a complete texture
            // leftChunk is leftWidth pixels wide, rightChunk is rightWidth pixels wide
            int totalWidth = leftWidth + rightWidth;
            int blockSize = (format == 0x82 || format == 0x52 || format == 0x7B) ? 8 : 16;
            
            int leftBlocksWide = leftWidth / 4;
            int rightBlocksWide = rightWidth / 4;
            int totalBlocksWide = totalWidth / 4;
            int blocksHigh = height / 4;
            
            byte[] result = new byte[totalBlocksWide * blocksHigh * blockSize];
            
            // Copy blocks row by row
            for (int row = 0; row < blocksHigh; row++)
            {
                int dstRowOffset = row * totalBlocksWide * blockSize;
                int leftSrcRowOffset = row * leftBlocksWide * blockSize;
                int rightSrcRowOffset = row * rightBlocksWide * blockSize;
                
                // Copy left chunk blocks for this row
                Array.Copy(leftChunk, leftSrcRowOffset, result, dstRowOffset, leftBlocksWide * blockSize);
                
                // Copy right chunk blocks for this row
                Array.Copy(rightChunk, rightSrcRowOffset, result, dstRowOffset + leftBlocksWide * blockSize, rightBlocksWide * blockSize);
            }
            
            return result;
        }
        
        private bool GetPackedMipOffset(int texWidth, int texHeight, int mipLevel, out int xBlocks, out int yBlocks)
        {
            // Xbox 360 packed mip algorithm from Xenia
            // Mips with smallest dimension <=16 get packed into a single tile (32x32 texels)
            int log2Width = (int)Math.Ceiling(Math.Log(texWidth, 2));
            int log2Height = (int)Math.Ceiling(Math.Log(texHeight, 2));
            int log2Size = Math.Min(log2Width, log2Height);
            
            if (log2Size > 4 + mipLevel)
            {
                // The shortest dimension is bigger than 16, not packed
                xBlocks = 0;
                yBlocks = 0;
                return false;
            }
            
            int packedMipBase = (log2Size > 4) ? (log2Size - 4) : 0;
            int packedMip = mipLevel - packedMipBase;
            
            // Find the block offset of the mip
            if (packedMip < 3)
            {
                if (log2Width > log2Height)
                {
                    // Wider than tall. Laid out vertically.
                    xBlocks = 0;
                    yBlocks = 16 >> packedMip;
                }
                else
                {
                    // Taller than wide. Laid out horizontally.
                    xBlocks = 16 >> packedMip;
                    yBlocks = 0;
                }
            }
            else
            {
                int offset;
                if (log2Width > log2Height)
                {
                    // Wider than tall. Laid out horizontally.
                    offset = (1 << (log2Width - packedMipBase)) >> (packedMip - 2);
                    xBlocks = offset;
                    yBlocks = 0;
                }
                else
                {
                    // Taller than wide. Laid out vertically.
                    xBlocks = 0;
                    offset = (1 << (log2Height - packedMipBase)) >> (packedMip - 2);
                    yBlocks = offset;
                }
            }
            
            return true;
        }
        
        private byte[] UnpackMipAtlas(byte[] atlasData, int width, int height, uint format)
        {
            // Determine block size based on format
            int blockSize;
            switch (format)
            {
                case 0x52: // DXT1
                case 0x7B: // ATI1/BC4
                case 0x82: // DXT1 variant
                case 0x86: // DXT1 variant
                case 0x12: // GPUTEXTUREFORMAT_DXT1
                    blockSize = 8;
                    break;
                    
                case 0x53: // DXT3
                case 0x54: // DXT5
                case 0x71: // DXT5 variant (normal maps)
                case 0x88: // DXT5 variant
                case 0x13: // GPUTEXTUREFORMAT_DXT2/3
                case 0x14: // GPUTEXTUREFORMAT_DXT4/5
                    blockSize = 16;
                    break;
                    
                default:
                    blockSize = 16; // Default to DXT5 block size
                    break;
            }
            
            int atlasWidthInBlocks = width / 4;
            
            // Actual texture is half the atlas width (for square textures)
            // But for 256x192 atlas, actual texture is 128x128
            // And for 512x384 atlas, actual texture is 1024x1024
            int actualWidth = width / 2;
            int actualHeight = width / 2; // Use width/2 to get square dimension
            
            // Handle special case of 256x192 atlas for 128x128 texture
            if (width == 256 && height == 192)
            {
                actualWidth = 128;
                actualHeight = 128;
            }
            // Handle special case of 512x384 atlas for 1024x1024 texture
            else if (width == 512 && height == 384)
            {
                actualWidth = 1024;
                actualHeight = 1024;
            }
            // For non-square atlases, determine the actual texture dimensions
            // 320x256 atlas -> 512x256 texture (width = atlas_width * 8/5, height = atlas_height)
            else if (width == 320 && height == 256)
            {
                actualWidth = 512;
                actualHeight = 256;
            }
            // For wider-than-tall atlases where width = 5/4 of height, actual texture is 8/5 * atlas_width
            else if (width * 4 == height * 5)
            {
                actualWidth = width * 8 / 5;
                actualHeight = height;
            }
            // For taller-than-wide atlases where height = 5/4 of width, actual texture is 8/5 * atlas_height
            else if (height * 4 == width * 5)
            {
                actualWidth = width;
                actualHeight = height * 8 / 5;
            }
            else
            {
                // For other non-square atlases, deduce from the ratio
                // Atlas dimensions for non-square textures seem to be: atlas_width ≈ tex_width * 5/8
                if (width > height)
                {
                    // Wider than tall
                    actualWidth = width * 8 / 5;
                    actualHeight = height;
                }
                else
                {
                    // Taller than wide
                    actualWidth = width;
                    actualHeight = height * 8 / 5;
                }
            }
            
            // Calculate total size needed for all mips linearly packed
            uint mipCount = CalculateMipLevels((uint)actualWidth, (uint)actualHeight);
            uint totalSize = CalculateMainDataSize((uint)actualWidth, (uint)actualHeight, format, mipCount);
            byte[] output = new byte[totalSize];
            int outputOffset = 0;

            // Debug: log expected total size and per-mip sizes
            Console.WriteLine($"UnpackMipAtlas: expected mip count={mipCount}, totalSize={totalSize} bytes");
            int debugW = actualWidth;
            int debugH = actualHeight;
            for (int m = 0; m < mipCount; m++)
            {
                int s = CalculateMipSize(debugW, debugH, format);
                Console.WriteLine($"  mip {m}: {debugW}x{debugH} -> {s} bytes");
                debugW = Math.Max(1, debugW / 2);
                debugH = Math.Max(1, debugH / 2);
            }
            
            // Mip positions in blocks (each block is 4x4 pixels)
            // For 256x256 atlas (64x64 blocks) containing 128x128 texture (32x32 blocks):
            // For 256x192 atlas (64x48 blocks) containing 128x128 texture (32x32 blocks):
            // For 320x256 atlas (80x64 blocks) containing 512x256 texture mips:
            // For 1024x1024 atlas containing 1024x1024 texture - mips are packed within
            // User measurements (in pixels): 0,0 | 512,0 | 0,256 | 256,256 | 512,256 | 640,256 | 768,256 | 912,256 | 904,256 | 900,256 | 896,264 | 896,260
            Console.WriteLine($"UnpackMipAtlas: width={width}, height={height}, actualTexture={actualWidth}x{actualHeight}, using {(width == 256 && height == 192 ? "256x192" : width == 1024 && height == 1024 ? "1024x1024" : "calculated")} mip layout");
            
            // Special handling for 1024x1024 atlas with split mips
            if (width == 1024 && height == 1024)
            {
                // Mip 0 (512x512): split into top 512x256 at (0,0) and bottom 512x256 at (512,0)
                // Extract top half
                Console.WriteLine($"Extracting mip 0 (split): 512x512 - top half at (0,0), bottom half at (512,0)");
                int topHalfBlocks = 128 * 64; // 512/4 * 256/4
                for (int by = 0; by < 64; by++)
                {
                    for (int bx = 0; bx < 128; bx++)
                    {
                        int srcOffset = (by * atlasWidthInBlocks + bx) * blockSize;
                        if (srcOffset + blockSize <= atlasData.Length && outputOffset + blockSize <= output.Length)
                        {
                            Array.Copy(atlasData, srcOffset, output, outputOffset, blockSize);
                        }
                        outputOffset += blockSize;
                    }
                }
                
                // Extract bottom half at (512, 0) = block (128, 0)
                for (int by = 0; by < 64; by++)
                {
                    for (int bx = 0; bx < 128; bx++)
                    {
                        int srcBlockX = 128 + bx;
                        int srcBlockY = by;
                        int srcOffset = (srcBlockY * atlasWidthInBlocks + srcBlockX) * blockSize;
                        if (srcOffset + blockSize <= atlasData.Length && outputOffset + blockSize <= output.Length)
                        {
                            Array.Copy(atlasData, srcOffset, output, outputOffset, blockSize);
                        }
                        outputOffset += blockSize;
                    }
                }
                
                // Remaining mips: positions from user
                // Mip 1 (256x256): split top at (0,256), bottom at (256,256)
                Console.WriteLine($"Extracting mip 1 (split): 256x256 - top half at (0,256), bottom half at (256,256)");
                // Top half: (0, 256) = block (0, 64), size 256x128 = 64x32 blocks
                for (int by = 0; by < 32; by++)
                {
                    for (int bx = 0; bx < 64; bx++)
                    {
                        int srcOffset = ((64 + by) * atlasWidthInBlocks + bx) * blockSize;
                        if (srcOffset + blockSize <= atlasData.Length && outputOffset + blockSize <= output.Length)
                        {
                            Array.Copy(atlasData, srcOffset, output, outputOffset, blockSize);
                        }
                        outputOffset += blockSize;
                    }
                }
                // Bottom half: (256, 256) = block (64, 64)
                for (int by = 0; by < 32; by++)
                {
                    for (int bx = 0; bx < 64; bx++)
                    {
                        int srcOffset = ((64 + by) * atlasWidthInBlocks + (64 + bx)) * blockSize;
                        if (srcOffset + blockSize <= atlasData.Length && outputOffset + blockSize <= output.Length)
                        {
                            Array.Copy(atlasData, srcOffset, output, outputOffset, blockSize);
                        }
                        outputOffset += blockSize;
                    }
                }
                
                // Remaining non-split mips
                var remainingMips = new (int x, int y, int w, int h)[]
                {
                    (512, 256, 128, 128),    // Mip 2: 128x128 at (512,256)
                    (640, 256, 64, 64),      // Mip 3: 64x64 at (640,256)
                    (768, 256, 32, 32),      // Mip 4: 32x32 at (768,256)
                    (912, 256, 16, 16),      // Mip 5: 16x16 at (912,256)
                    (904, 256, 8, 8),        // Mip 6: 8x8 at (904,256)
                    (900, 256, 4, 4),        // Mip 7: 4x4 at (900,256)
                    (896, 264, 4, 4),        // Mip 8: 2x2 at (896,264) - sub-block, extract as 4x4 block
                    (896, 260, 4, 4),        // Mip 9: 1x1 at (896,260) - sub-block, extract as 4x4 block
                };
                
                for (int i = 0; i < remainingMips.Length; i++)
                {
                    var (mipX, mipY, mipW, mipH) = remainingMips[i];
                    int mipXInBlocks = mipX / 4;
                    int mipYInBlocks = mipY / 4;
                    int mipWidthInBlocks = mipW / 4;
                    int mipHeightInBlocks = mipH / 4;
                    
                    Console.WriteLine($"Extracting mip {i + 2}: {mipW}x{mipH} from atlas position ({mipX}, {mipY})");
                    
                    for (int by = 0; by < mipHeightInBlocks; by++)
                    {
                        for (int bx = 0; bx < mipWidthInBlocks; bx++)
                        {
                            int srcBlockX = mipXInBlocks + bx;
                            int srcBlockY = mipYInBlocks + by;
                            int srcOffset = (srcBlockY * atlasWidthInBlocks + srcBlockX) * blockSize;
                            
                            if (srcOffset + blockSize <= atlasData.Length && outputOffset + blockSize <= output.Length)
                            {
                                Array.Copy(atlasData, srcOffset, output, outputOffset, blockSize);
                            }
                            
                            outputOffset += blockSize;
                        }
                    }
                }
                
                // Trim to actual extracted size
                var trimmed = new byte[outputOffset];
                Array.Copy(output, 0, trimmed, 0, outputOffset);
                return trimmed;
            }
            
            var mipPositions = width == 256 && height == 192 ? new (int x, int y, int w, int h)[]
            {
                (0, 0, 16, 16),      // Mip 0: 64x64 at (0,0)
                (32, 0, 8, 8),       // Mip 1: 32x32 at (128,0)
                (4, 32, 4, 4),       // Mip 2: 16x16 at (16,128)
                (2, 32, 2, 2),       // Mip 3: 8x8 at (8,128)
                (1, 32, 1, 1),       // Mip 4: 4x4 at (4,128)
                (0, 34, 1, 1),       // Mip 5: 2x2 at (0,136) - sub-block
                (0, 33, 1, 1),       // Mip 6: 1x1 at (0,132) - sub-block
            } : width == 128 && height == 320 ? new (int x, int y, int w, int h)[]
            {
                // Measured for 128x512 texture with 128x320 atlas
                // User provided pixel measurements:
                //  - 64x256 at (0,0)
                //  - 32x64  at (0,256)
                // Convert to blocks (4x4 pixels per block): x/4, y/4, w/4, h/4
                (0, 0, 16, 64),      // Mip: 64x256 at (0,0)
                (0, 64, 8, 16),      // Mip: 32x64  at (0,256)
            } : width == 320 && height == 256 ? new (int x, int y, int w, int h)[]
            {
                // 512x256 texture mips manually measured
                (0, 0, 64, 32),      // Mip 0: 256x128 at (0,0)
                (0, 32, 32, 16),     // Mip 1: 128x64 at (0,128)
                (32, 32, 16, 8),     // Mip 2: 64x32 at (128,128)
                (64, 36, 8, 4),      // Mip 3: 32x16 at (256,144)
                (64, 34, 4, 2),      // Mip 4: 16x8 at (256,136)
                (64, 33, 2, 1),      // Mip 5: 8x4 at (256,132)
                (68, 32, 1, 1),      // Mip 6: 4x2 at (272,128) - sub-block, store as 1 block (4x4)
                (66, 32, 1, 1),      // Mip 7: 2x1 at (264,128) - sub-block, store as 1 block (4x4)
                (65, 32, 1, 1),      // Mip 8: 1x1 at (260,128) - sub-block, store as 1 block (4x4)
            } : new (int x, int y, int w, int h)[]
            {
                // Default 256x256 atlas for 128x128 texture
                (0, 0, 32, 32),      // Mip 0: 128x128 at (0,0)
                (32, 0, 16, 16),     // Mip 1: 64x64 at (128,0)
                (0, 32, 8, 8),       // Mip 2: 32x32 at (0,128)
                (36, 32, 4, 4),      // Mip 3: 16x16 at (144,128)
                (34, 32, 2, 2),      // Mip 4: 8x8 at (136,128)
                (33, 32, 1, 1),      // Mip 5: 4x4 at (132,128)
                (32, 34, 1, 1),      // Mip 6: 2x2 at (128,136) - sub-block
                (32, 33, 1, 1),      // Mip 7: 1x1 at (128,132) - sub-block
            };

            // Track which atlas blocks we've consumed so we can pick remaining blocks if needed
            bool[,] usedBlocks = new bool[height / 4, width / 4];

            for (int mipLevel = 0; mipLevel < mipPositions.Length; mipLevel++)
            {
                var (mipXInBlocks, mipYInBlocks, mipWidthInBlocks, mipHeightInBlocks) = mipPositions[mipLevel];
                int mipWidth = mipWidthInBlocks * 4;
                int mipHeight = mipHeightInBlocks * 4;

                if (mipWidth < 4 || mipHeight < 4)
                    break; // Can't have mips smaller than DXT block size

                Console.WriteLine($"Extracting mip {mipLevel}: {mipWidth}x{mipHeight} from atlas position ({mipXInBlocks * 4}, {mipYInBlocks * 4})");
                
                // Extract this mip from the atlas
                for (int by = 0; by < mipHeightInBlocks; by++)
                {
                    for (int bx = 0; bx < mipWidthInBlocks; bx++)
                    {
                        int srcBlockX = mipXInBlocks + bx;
                        int srcBlockY = mipYInBlocks + by;
                        int srcOffset = (srcBlockY * atlasWidthInBlocks + srcBlockX) * blockSize;

                        if (srcOffset + blockSize <= atlasData.Length && outputOffset + blockSize <= output.Length)
                        {
                            Array.Copy(atlasData, srcOffset, output, outputOffset, blockSize);
                            usedBlocks[srcBlockY, srcBlockX] = true;
                        }

                        outputOffset += blockSize;
                    }
                }
            }

            // If we didn't extract the full mip tail, try to fill remaining bytes from unused atlas blocks
            int mip0Size = CalculateMipSize(actualWidth, actualHeight, format);
            int desiredTailBytes = (int)totalSize - mip0Size; // bytes excluding main surface

            Console.WriteLine($"UnpackMipAtlas: extracted so far {outputOffset} bytes, desired tail {desiredTailBytes} bytes");

            if (outputOffset < desiredTailBytes)
            {
                Console.WriteLine("UnpackMipAtlas: filling remaining mip tail from unused atlas blocks");
                for (int by = 0; by < height / 4 && outputOffset < desiredTailBytes; by++)
                {
                    for (int bx = 0; bx < atlasWidthInBlocks && outputOffset < desiredTailBytes; bx++)
                    {
                        if (usedBlocks[by, bx]) continue;
                        int srcOffset = (by * atlasWidthInBlocks + bx) * blockSize;
                        if (srcOffset + blockSize <= atlasData.Length && outputOffset + blockSize <= output.Length)
                        {
                            Array.Copy(atlasData, srcOffset, output, outputOffset, blockSize);
                            usedBlocks[by, bx] = true;
                            outputOffset += blockSize;
                        }
                    }
                }
                Console.WriteLine($"UnpackMipAtlas: after filling, extracted {outputOffset} bytes (desired {desiredTailBytes})");
            }

            Console.WriteLine($"UnpackMipAtlas: final extracted {outputOffset} bytes (buffer {output.Length} bytes)");
            
            // Trim to actual extracted size
            var finalTrimmed = new byte[outputOffset];
            Array.Copy(output, 0, finalTrimmed, 0, outputOffset);
            return finalTrimmed;
        }
    }

    public class D3DTextureInfo
    {
        public uint Width { get; set; }
        public uint Height { get; set; }
        public uint Format { get; set; }
        public uint DataFormat { get; set; }
        public uint ActualFormat { get; set; } // The real format (0x54 for DXT5, 0 for DXT1)
        public uint MipLevels { get; set; }
        public uint Pitch { get; set; }
        public bool Tiled { get; set; }
        public uint Endian { get; set; }
        public uint MainDataSize { get; set; }
    }
}
