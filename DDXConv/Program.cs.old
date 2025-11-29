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

            string inputPath = args[0];
            if (Directory.Exists(inputPath))
            {
                // Batch convert all .ddx files in the directory
                string outputDir = args[1];
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
                        parser.ConvertDdxToDds(ddxFile, outputBatchPath);
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
                parser.ConvertDdxToDds(inputPath, outputPath);
                Console.WriteLine($"Successfully converted {inputPath} to {outputPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }
    }

    public class DdxParser
    {
        private const uint MAGIC_3XDO = 0x4F445833;
        private const uint MAGIC_3XDR = 0x52445833;

        public void ConvertDdxToDds(string inputPath, string outputPath)
        {
            using (var reader = new BinaryReader(File.OpenRead(inputPath)))
            {
                uint magic = reader.ReadUInt32();
                
                if (magic == MAGIC_3XDR)
                {
                    throw new InvalidDataException("3XDR files are not yet supported.");
                }
                if (magic != MAGIC_3XDO)
                {
                    throw new InvalidDataException($"Unknown DDX magic: 0x{magic:X8}.");
                }

                ConvertDdxToDds(reader, outputPath);
            }
        }

        private void ConvertDdxToDds(BinaryReader reader, string outputPath)
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
            // Height is at offset 0x0E, width is at offset 0x3C
            
            // Skip to 0x0E (5 bytes: 0x09 to 0x0E)
            reader.ReadBytes(5);
            
            // Read height at 0x0E
            ushort height = 256; // Hardcoded for now - resolution detection is broken
            reader.ReadUInt16(); // Skip the broken height value
            
            // Now at 0x10, read 52 bytes of texture header (to 0x44)
            byte[] textureHeader = reader.ReadBytes(52);
            
            // Width is at absolute offset 0x3C = 0x10 + 0x2C (44 bytes into header)
            ushort width = 256; // Hardcoded for now - resolution detection is broken
            
            Console.WriteLine($"Dimensions from header (hardcoded): {width}x{height}");
            
            var texture = ParseD3DTextureHeader(textureHeader, width, height);

            // For 3XDO files, the texture data starts immediately after the header
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
            
            //// Check if data is XCompress compressed (starts with 0xFF or 0x0F)
            //if (mainData.Length > 0 && (mainData[0] == 0xFF || mainData[0] == 0x0F))
            //{
            //    Console.WriteLine($"Detected XCompress compression, decompressing...");
                
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
                
                // Save raw combined data for analysis
                string rawPath = outputPath.Replace(".dds", "_raw.bin");
                File.WriteAllBytes(rawPath, mainData);
                Console.WriteLine($"Saved raw combined data to {rawPath}");
            //}
            //else
            //{
            //    // Not compressed
            //    string rawPath = outputPath.Replace(".dds", "_raw.bin");
            //    File.WriteAllBytes(rawPath, mainData);
            //    Console.WriteLine($"Saved raw data to {rawPath}");
            //}
            
            // Calculate expected main surface size
            uint mainSurfaceSize = (uint)CalculateMipSize(width, height, texture.ActualFormat);
            
            byte[] linearData;
            
            // Check if we have two chunks or one chunk
            if (mainData.Length >= atlasSize * 2)
            {
                // Two-chunk format: chunk1 = mip atlas, chunk2 = main surface
                Console.WriteLine($"Two-chunk format detected ({mainData.Length} bytes)");
                
                byte[] chunk1 = new byte[atlasSize];
                byte[] chunk2 = new byte[atlasSize];
                Array.Copy(mainData, 0, chunk1, 0, (int)atlasSize);
                Array.Copy(mainData, (int)atlasSize, chunk2, 0, (int)atlasSize);
                
                // Untile both chunks
                byte[] untiledChunk1 = UnswizzleDXTTexture(chunk1, width, height, texture.ActualFormat);
                byte[] untiledChunk2 = UnswizzleDXTTexture(chunk2, width, height, texture.ActualFormat);
                
                Console.WriteLine($"Untiled both chunks to {untiledChunk1.Length} and {untiledChunk2.Length} bytes");
                
                // Chunk 2 is the main surface, chunk 1 contains mip atlas
                byte[] mips = UnpackMipAtlas(untiledChunk1, width, height, texture.ActualFormat);
                Console.WriteLine($"Extracted {mips.Length} bytes of mips from chunk 1");
                
                linearData = new byte[untiledChunk2.Length + mips.Length];
                Array.Copy(untiledChunk2, 0, linearData, 0, untiledChunk2.Length);
                Array.Copy(mips, 0, linearData, untiledChunk2.Length, mips.Length);
                
                Console.WriteLine($"Combined {untiledChunk2.Length} bytes main surface + {mips.Length} bytes mips = {linearData.Length} total");
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
                    // Has partial mips - but don't split yet, the data might be interleaved
                    // Try untiling the entire data as a single texture first
                    Console.WriteLine($"Detected extra data: {mainData.Length} > {mainSurfaceSize}");
                    Console.WriteLine($"Attempting to untile entire {mainData.Length} bytes as single texture");
                    
                    // Calculate what dimensions would fit this data
                    uint totalSize = (uint)mainData.Length;
                    int blockSize = (texture.ActualFormat == 0x82 || texture.ActualFormat == 0x52 || texture.ActualFormat == 0x7B) ? 8 : 16;
                    uint totalBlocks = totalSize / (uint)blockSize;
                    
                    // For 81920 bytes of DXT5: 81920/16 = 5120 blocks
                    // Chunk 1 (49152 bytes) = 3072 blocks = 192x256
                    // Chunk 2 (32768 bytes) = 2048 blocks = 64x256
                    // Together: 256x256!
                    
                    // The chunks might be split horizontally - try to determine split point
                    // For a 256x256 texture split into 192x256 + 64x256:
                    int chunk1Width = 192;
                    int chunk2Width = 64;
                    int chunkHeight = 256;
                    
                    int chunk1Size = CalculateMipSize(chunk1Width, chunkHeight, texture.ActualFormat);
                    int chunk2Size = mainData.Length - chunk1Size;
                    
                    Console.WriteLine($"Trying horizontal split: {chunk1Width}x{chunkHeight} ({chunk1Size} bytes) + {chunk2Width}x{chunkHeight} ({chunk2Size} bytes)");
                    
                    if (chunk1Size + chunk2Size == mainData.Length && chunk2Size > 0)
                    {
                        byte[] chunk1Tiled = new byte[chunk1Size];
                        byte[] chunk2Tiled = new byte[chunk2Size];
                        Array.Copy(mainData, 0, chunk1Tiled, 0, chunk1Size);
                        Array.Copy(mainData, chunk1Size, chunk2Tiled, 0, chunk2Size);
                        
                        byte[] chunk1Untiled = UnswizzleDXTTexture(chunk1Tiled, chunk1Width, chunkHeight, texture.ActualFormat);
                        byte[] chunk2Untiled = UnswizzleDXTTexture(chunk2Tiled, chunk2Width, chunkHeight, texture.ActualFormat);
                        
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
                    
                        byte[] mainSurfaceUntiled = UnswizzleDXTTexture(mainSurfaceTiled, width, height, texture.ActualFormat);
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
                        
                        byte[] mipUntiled = UnswizzleDXTTexture(mipTiled, width / 2, height / 2, texture.ActualFormat);
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
                else if (mainData.Length < mainSurfaceSize)
                {
                    // Data is smaller than expected - might be split differently
                    // Check if it's exactly 2x a smaller dimension
                    if (mainData.Length == mainSurfaceSize / 2 && height == width / 2)
                    {
                        // Data suggests square texture, not the rectangular one from header
                        Console.WriteLine($"WARNING: Data size suggests {width/2}x{width/2} instead of {width}x{height}");
                        width = (ushort)(width / 2);
                        height = (ushort)(width);
                        texture.Width = width;
                        texture.Height = height;
                        mainSurfaceSize = (uint)CalculateMipSize(width, height, texture.ActualFormat);
                    }
                    
                    // Data is smaller than expected - try to determine actual dimensions
                    uint actualSize = (uint)mainData.Length;
                    int blockSize = texture.ActualFormat == 0x82 || texture.ActualFormat == 0x52 ? 8 : 16;
                    uint totalBlocks = actualSize / (uint)blockSize;
                    
                    // Check if it's square
                    uint blocksPerSide = (uint)Math.Sqrt(totalBlocks);
                    if (blocksPerSide * blocksPerSide == totalBlocks)
                    {
                        // It's square - adjust both dimensions
                        ushort actualDimension = (ushort)(blocksPerSide * 4);
                        Console.WriteLine($"WARNING: Data size mismatch! Header says {width}x{height}, but data is {actualDimension}x{actualDimension}");
                        Console.WriteLine($"Using actual data dimensions for untiling and DDS output");
                        width = actualDimension;
                        height = actualDimension;
                        texture.Width = width;
                        texture.Height = height;
                    }
                    
                    // Untile as a single texture
                    byte[] untiled = UnswizzleDXTTexture(mainData, width, height, texture.ActualFormat);
                    Console.WriteLine($"Untiled to {untiled.Length} bytes");
                    
                    linearData = untiled;
                    texture.MipLevels = 1;
                    Console.WriteLine($"Set MipLevels to {texture.MipLevels}");
                }
                else if (mainData.Length == mainSurfaceSize * 2)
                {
                    // Exactly 2x the expected size - might be two separate surfaces
                    Console.WriteLine($"Data is exactly 2x expected size - might be two {width}x{height} chunks");
                    
                    // Try untiling as two separate chunks  
                    byte[] chunk1TiledAlt = new byte[mainData.Length / 2];
                    byte[] chunk2TiledAlt = new byte[mainData.Length / 2];
                    Array.Copy(mainData, 0, chunk1TiledAlt, 0, mainData.Length / 2);
                    Array.Copy(mainData, mainData.Length / 2, chunk2TiledAlt, 0, mainData.Length / 2);
                    
                    // If header says dimensions, try untiling each chunk at those dimensions
                    int chunkSquareSize = (int)Math.Sqrt(mainData.Length / 2 / 16) * 4;
                    if (chunkSquareSize * chunkSquareSize / 16 * 16 == mainData.Length / 2)
                    {
                        Console.WriteLine($"Each chunk appears to be {chunkSquareSize}x{chunkSquareSize}");
                        byte[] chunk1UntiledAlt = UnswizzleDXTTexture(chunk1TiledAlt, chunkSquareSize, chunkSquareSize, texture.ActualFormat);
                        byte[] chunk2UntiledAlt = UnswizzleDXTTexture(chunk2TiledAlt, chunkSquareSize, chunkSquareSize, texture.ActualFormat);
                        Console.WriteLine($"Untiled chunks to {chunk1UntiledAlt.Length} + {chunk2UntiledAlt.Length} bytes");
                        
                        // Chunk 1 might have mips packed
                        byte[] mipsAlt = UnpackMipAtlas(chunk1UntiledAlt, chunkSquareSize, chunkSquareSize, texture.ActualFormat);
                        Console.WriteLine($"Extracted {mipsAlt.Length} bytes of mips from chunk 1");
                        
                        linearData = new byte[chunk2UntiledAlt.Length + mipsAlt.Length];
                        Array.Copy(chunk2UntiledAlt, 0, linearData, 0, chunk2UntiledAlt.Length);
                        Array.Copy(mipsAlt, 0, linearData, chunk2UntiledAlt.Length, mipsAlt.Length);
                        
                        texture.Width = (ushort)chunkSquareSize;
                        texture.Height = (ushort)chunkSquareSize;
                        Console.WriteLine($"Corrected dimensions to {chunkSquareSize}x{chunkSquareSize}");
                        Console.WriteLine($"Combined {chunk2UntiledAlt.Length} bytes main + {mipsAlt.Length} bytes mips = {linearData.Length} total");
                    }
                    else
                    {
                        // Fallback: just use untiled data
                        byte[] untiled = UnswizzleDXTTexture(mainData, width, height, texture.ActualFormat);
                        Console.WriteLine($"Untiled to {untiled.Length} bytes");
                        linearData = untiled;
                        texture.MipLevels = 1;
                    }
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
                    
                    byte[] chunk1Untiled = UnswizzleDXTTexture(chunk1Tiled, squareSize, squareSize, texture.ActualFormat);
                    byte[] chunk2Untiled = UnswizzleDXTTexture(chunk2Tiled, squareSize, squareSize, texture.ActualFormat);
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
                    if (mainData.Length == atlasSize128 + mainSize128)
                    {
                        Console.WriteLine($"Detected 128x128 texture with mip atlas (24576 + 8192 bytes)");
                        
                        byte[] chunk1 = new byte[atlasSize128];
                        byte[] chunk2 = new byte[mainSize128];
                        Array.Copy(mainData, 0, chunk1, 0, atlasSize128);
                        Array.Copy(mainData, atlasSize128, chunk2, 0, mainSize128);
                        
                        // Untile: atlas is 256x192, main is 128x128
                        byte[] untiledAtlas = UnswizzleDXTTexture(chunk1, 256, 192, texture.ActualFormat);
                        byte[] untiledMain = UnswizzleDXTTexture(chunk2, 128, 128, texture.ActualFormat);
                        
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

        private D3DTextureInfo ParseD3DTextureHeader(byte[] header, ushort width, ushort height)
        {
            // Xbox 360 D3D texture header structure
            // Dimensions are passed separately as they're at fixed file offsets
            
            var info = new D3DTextureInfo();

            // Set dimensions from parameters
            info.Width = width;
            info.Height = height;
            
            // Based on Common and MipFlush being first 8 bytes,
            // GPUTEXTURE_FETCH_CONSTANT should be at offset 8
            uint[] formatDwords = new uint[6];
            for (int i = 0; i < 6; i++)
            {
                formatDwords[i] = BitConverter.ToUInt32(header, 8 + i * 4);
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
            int actualWidth = width / 2;
            int actualHeight = width / 2; // Use width/2 to get square dimension
            
            // Handle special case of 256x192 atlas for 128x128 texture
            if (width == 256 && height == 192)
            {
                actualWidth = 128;
                actualHeight = 128;
            }
            
            // Calculate total size needed for all mips linearly packed
            uint totalSize = CalculateMainDataSize((uint)actualWidth, (uint)actualHeight, format, CalculateMipLevels((uint)actualWidth, (uint)actualHeight));
            byte[] output = new byte[totalSize];
            int outputOffset = 0;
            
            // Mip positions in blocks (each block is 4x4 pixels)
            // For 256x256 atlas (64x64 blocks) containing 128x128 texture (32x32 blocks):
            // For 256x192 atlas (64x48 blocks) containing 128x128 texture (32x32 blocks):
            // When atlas is 256x192 for a 128x128 texture, positions stay the same but mip sizes are halved
            Console.WriteLine($"UnpackMipAtlas: width={width}, height={height}, using {(width == 256 && height == 192 ? "256x192" : "default")} mip layout");
            var mipPositions = width == 256 && height == 192 ? new (int x, int y, int w, int h)[]
            {
                (0, 0, 16, 16),      // Mip 0: 64x64 at (0,0)
                (32, 0, 8, 8),       // Mip 1: 32x32 at (128,0)
                (4, 32, 4, 4),       // Mip 2: 16x16 at (16,128)
                (2, 32, 2, 2),       // Mip 3: 8x8 at (8,128)
                (1, 32, 1, 1),       // Mip 4: 4x4 at (4,128)
                (0, 34, 1, 1),       // Mip 5: 2x2 at (0,136) - sub-block
                (0, 33, 1, 1),       // Mip 6: 1x1 at (0,132) - sub-block
            } : new (int x, int y, int w, int h)[]
            {
                (0, 0, 32, 32),      // Mip 0: 128x128 at (0,0)
                (32, 0, 16, 16),     // Mip 1: 64x64 at (128,0)
                (0, 32, 8, 8),       // Mip 2: 32x32 at (0,128)
                (36, 32, 4, 4),      // Mip 3: 16x16 at (144,128)
                (34, 32, 2, 2),      // Mip 4: 8x8 at (136,128)
                (33, 32, 1, 1),      // Mip 5: 4x4 at (132,128)
                (32, 34, 1, 1),      // Mip 6: 2x2 at (128,136) - sub-block
                (32, 33, 1, 1),      // Mip 7: 1x1 at (128,132) - sub-block
            };

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
                        }
                        
                        outputOffset += blockSize;
                    }
                }
            }
            
            return output;
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
