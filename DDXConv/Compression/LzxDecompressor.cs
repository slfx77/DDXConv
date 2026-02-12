// LZX decompression — pure managed C# replacement for XnaNative.dll
// Mechanically translated from tools/LzxVerify/lzx_decompress.c which was
// verified byte-for-byte against XnaNative.dll v4.0.30901.0 on 3,870 DDX files.
//
// Implements the XMemCompress chunked LZX decompression used by Xbox 360.
// Each function below maps 1:1 to a verified C function; the C function address
// is noted in comments for traceability.

using System.Buffers;
using System.Runtime.CompilerServices;

namespace DDXConv.Compression;

public sealed class LzxDecompressor : IDisposable
{
    // ================================================================
    // LZX CONSTANTS
    // ================================================================

    private const int NumChars = 256;
    private const int MainTreeTableBits = 10;
    private const int LengthTreeTableBits = 8;
    private const int AlignedTreeTableBits = 7;
    private const int AlignedTreeNumElements = 8;
    private const int LengthTreeNumElements = 249;
    private const int PreTreeNumElements = 20;

    private const int BlockTypeVerbatim = 1;
    private const int BlockTypeAligned = 2;
    private const int BlockTypeUncompressed = 3;

    // Chunk framing constants
    private const byte StreamTerminator = 0xFF;
    private const int DefaultUncompressedChunkSize = 0x8000; // 32 KB
    private const int MaxTotalChunkSize = 0x980a; // ~38 KB safety limit
    private const int WindowOverflowPadding = 0x106; // 262 extra bytes for match overshoot
    private const int WindowMirrorThreshold = 0x101; // Window positions below this are mirrored
    private const int MaxChunkCountForE8 = 0x7fff; // E8 translation cutoff

    // Decompression state constants
    private const int StateNeedHeader = 1;
    private const int StateDecompressing = 2;

    // ================================================================
    // STATIC TABLES
    // ================================================================

    // Position base and extra bits tables (standard LZX, pre-adjusted by -2)
    private static readonly int[] PositionBase; // OFF_POSITION_BASE 0x2F18
    private static readonly byte[] ExtraBits; // OFF_EXTRA_BITS    0x2EE4
    private readonly byte[] _alignedTreeLengths; // OFF_ALIGNED_TREE_LENGTHS 0xE34
    private readonly byte[] _alignedTreeTable; // OFF_ALIGNED_TREE_TABLE  0xDB4

    // Bitstream (extracted to dedicated class)
    private readonly BitstreamReader _bitstream = new();
    private readonly byte[] _lengthTreeLenBackup; // OFF_LENGTH_TREE_BACKUP 0x2DB4
    private readonly byte[] _lengthTreeLengths; // OFF_LENGTH_TREE_LENGTHS 0xCB8
    private readonly short[] _lengthTreeTable; // OFF_LENGTH_TREE_TABLE   0x818
    private readonly int _mainTreeElements;

    // Tree length backups (C copies current → backup before reading new trees)
    private readonly byte[] _mainTreeLenBackup; // OFF_MAIN_TREE_BACKUP  0x2B14

    // Huffman code lengths
    private readonly byte[] _mainTreeLengths; // OFF_MAIN_TREE_LENGTHS   0xA18

    // Huffman decode tables
    private readonly short[] _mainTreeTable; // OFF_MAIN_TREE_TABLE     0x018

    // Position slots
    private readonly int _numPositionSlots; // OFF_NUM_POS_SLOTS 0x2EB5

    // ================================================================
    // INSTANCE FIELDS
    // ================================================================

    // Sliding window
    private readonly byte[] _window; // OFF_WINDOW_BASE  0x000
    private readonly int _windowMask; // OFF_WINDOW_MASK  0x008
    private readonly int _windowSize; // OFF_WINDOW_SIZE  0x004
    private int _blockRemaining; // OFF_BLOCK_REMAINING  0x2ECC
    private int _blockType; // OFF_BLOCK_TYPE       0x2ED0
    private int _chunkCount; // OFF_CHUNK_COUNT         0x2EC4

    // Decompression state machine
    private int _decompressState; // OFF_STATE            0x2ED4  (1=need header, 2=decompressing)
    private bool _headerRead; // OFF_HEADER_READ      0x2EB6
    private int _intelCurrentPosition; // OFF_INTEL_CURRENT_POS   0x2EC0

    // Intel E8 translation state
    private int _intelFileSize; // OFF_INTEL_FILE_SIZE     0x2EBC

    // Match distance history (LZX repeat offsets)
    private int _repeatOffset0, _repeatOffset1, _repeatOffset2; // OFF_R0/R1/R2 0x00C-0x014
    private int _windowPosition; // OFF_WINDOW_POSITION  0x2EB8

    // ================================================================
    // STATIC CONSTRUCTOR
    // ================================================================

    // C: InitExtraBitsAndPositionBase (0x10198ae0)
    static LzxDecompressor()
    {
        // Position base table pre-adjusted by -2 (matches C posBase[] at line 1825)
        PositionBase =
        [
            -2, -1, 0, 1, 2, 4, 6, 10, 14, 22, 30, 46, 62, 94, 126, 190,
            254, 382, 510, 766, 1022, 1534, 2046, 3070, 4094, 6142, 8190,
            12286, 16382, 24574, 32766, 49150, 65534, 98302, 131070, 196606,
            262142, 393214, 524286, 655358, 786430, 917502, 1048574, 1179646,
            1310718, 1441790, 1572862, 1703934, 1835006, 1966078, 2097150
        ];

        // Extra bits table (matches C hardcoded bytes at line 1810-1822)
        ExtraBits =
        [
            0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6,
            7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13, 14, 14,
            15, 15, 16, 16, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17,
            17, 17, 17
        ];
    }

    // ================================================================
    // CONSTRUCTOR / DISPOSE / RESET
    // ================================================================

    // C: LZX_CreateContext (line 1898)
    public LzxDecompressor(int windowSize = 131072, int chunkSize = 524288)
    {
        _windowSize = windowSize;
        _windowMask = windowSize - 1;
        _window = ArrayPool<byte>.Shared.Rent(windowSize + WindowOverflowPadding);
        Array.Clear(_window, 0, _window.Length);

        // Compute numPositionSlots iteratively (matches C lines 1940-1947)
        _numPositionSlots = 4;
        var posSlots = 4;
        while (posSlots < windowSize)
        {
            posSlots += 1 << ExtraBits[_numPositionSlots];
            _numPositionSlots++;
        }

        _mainTreeElements = NumChars + _numPositionSlots * 8;
        _mainTreeLengths = new byte[_mainTreeElements];
        _lengthTreeLengths = new byte[LengthTreeNumElements];
        _alignedTreeLengths = new byte[AlignedTreeNumElements];

        // Backup arrays for delta encoding across blocks
        _mainTreeLenBackup = new byte[_mainTreeElements];
        _lengthTreeLenBackup = new byte[LengthTreeNumElements];

        // Decode tables: direct entries + overflow space
        var mainTableSize = 1 << MainTreeTableBits;
        _mainTreeTable = new short[mainTableSize + _mainTreeElements * 4];
        var lengthTableSize = 1 << LengthTreeTableBits;
        _lengthTreeTable = new short[lengthTableSize + LengthTreeNumElements * 4];
        _alignedTreeTable = new byte[1 << AlignedTreeTableBits];

        Reset();
    }

    public void Dispose()
    {
        if (_window != null)
        {
            ArrayPool<byte>.Shared.Return(_window);
        }
    }

    // C: ResetLzxState + ResetState + ClearTreeLengths + ResetIntelPos (lines 1838-1887)
    private void Reset()
    {
        _repeatOffset0 = 1;
        _repeatOffset1 = 1;
        _repeatOffset2 = 1;
        _windowPosition = 0;
        _decompressState = StateNeedHeader;
        _blockRemaining = 0;
        _blockType = 0;
        _headerRead = true; // first block needs E8 header read
        _intelFileSize = 0;
        _intelCurrentPosition = 0;
        _chunkCount = 0;
        _bitstream.Reset();
        Array.Clear(_mainTreeLengths);
        Array.Clear(_lengthTreeLengths);
        Array.Clear(_mainTreeLenBackup);
        Array.Clear(_lengthTreeLenBackup);
    }

    // ================================================================
    // PUBLIC API — C: LZX_Decompress + Decompress_ChunkFraming_Simple
    // ================================================================

    // C: Decompress_ChunkFraming_Simple (line 1718) + LZX_Decompress (line 1977)
    public int Decompress(
        byte[] inputBytes, int inputOffset, ref int inputCount,
        byte[] outputBytes, int outputOffset, ref int outputCount)
    {
        var input = inputBytes.AsSpan(inputOffset, inputCount);
        var output = outputBytes.AsSpan(outputOffset, outputCount);

        var totalInput = 0;
        var totalOutput = 0;
        var inputPos = 0;

        while (inputPos < input.Length)
        {
            int chunkCompressedSize;
            int chunkUncompressedSize;
            int headerSize;
            int totalChunkSize;

            var firstByte = input[inputPos];
            if (firstByte == StreamTerminator)
            {
                if (inputPos + 5 > input.Length) break;
                chunkUncompressedSize = (input[inputPos + 1] << 8) | input[inputPos + 2];
                chunkCompressedSize = (input[inputPos + 3] << 8) | input[inputPos + 4];
                headerSize = 5;
                totalChunkSize = chunkCompressedSize + 10;
            }
            else
            {
                if (inputPos + 2 > input.Length) break;
                chunkCompressedSize = (input[inputPos] << 8) | input[inputPos + 1];
                chunkUncompressedSize = DefaultUncompressedChunkSize;
                headerSize = 2;
                totalChunkSize = chunkCompressedSize + 2;
            }

            // C line 1749: totalChunkSize > MaxTotalChunkSize is an error
            if (totalChunkSize > MaxTotalChunkSize) break;
            if (inputPos + totalChunkSize > input.Length) break;

            if (totalOutput + chunkUncompressedSize > output.Length)
            {
                chunkUncompressedSize = output.Length - totalOutput;
                if (chunkUncompressedSize <= 0)
                {
                    // Output buffer full — skip decompression but still consume input.
                    totalInput += totalChunkSize;
                    inputPos += totalChunkSize;
                    if (firstByte == StreamTerminator) break;
                    continue;
                }
            }

            // C: DecompressChunk_Setup sets inputEnd = dataStart + 4 + compressedLen
            // The +4 accounts for the 4 bytes consumed by InitBitstream's seed
            var dataToPass = chunkCompressedSize + 4;
            var available = input.Length - (inputPos + headerSize);
            if (dataToPass > available) dataToPass = available;

            var chunkInput = input.Slice(inputPos + headerSize, dataToPass);
            var chunkOutput = output.Slice(totalOutput, chunkUncompressedSize);

            var decompressed = DecompressChunk(chunkInput, chunkOutput);
            if (decompressed < 0)
            {
                inputCount = totalInput;
                outputCount = totalOutput;
                return -1;
            }

            totalOutput += decompressed;
            totalInput += totalChunkSize;
            inputPos += totalChunkSize;

            // C line 1786: 0xFF chunks terminate this Decompress call
            if (firstByte == StreamTerminator) break;
        }

        inputCount = totalInput;
        outputCount = totalOutput;
        return 0;
    }

    // ================================================================
    // CHUNK SETUP — C: DecompressChunk_Setup (line 1682)
    // ================================================================

    private int DecompressChunk(ReadOnlySpan<byte> compressedData, Span<byte> output)
    {
        // Set up input buffer for this chunk
        _bitstream.SetInput(compressedData.ToArray(), compressedData.Length);

        // C line 1691: InitBitstream at start of chunk
        _bitstream.Init(_blockType);

        // C line 1693: main block-level decompression
        var result = DecompressBlocks(output.Length, output);

        // C line 1695: increment chunk count
        _chunkCount++;

        return result;
    }

    // ================================================================
    // BLOCK-LEVEL STATE MACHINE — C: DecompressBlocks (line 1549)
    // ================================================================

    private int DecompressBlocks(int bytesRemaining, Span<byte> output)
    {
        var totalDecompressed = 0;
        var initialWindowPos = _windowPosition;

        while (bytesRemaining > 0)
        {
            // State 1: need block header (C line 1563)
            if (_decompressState == StateNeedHeader)
            {
                // First block: read Intel E8 header (C line 1566)
                if (_headerRead)
                {
                    _headerRead = false;
                    var intelBit = (int)_bitstream.ReadBits(1);
                    if (intelBit != 0)
                    {
                        var hi = (int)_bitstream.ReadBits(16);
                        var lo = _bitstream.ReadBits(16);
                        _intelFileSize = (int)(lo | ((uint)hi << 16));
                    }
                    else
                    {
                        _intelFileSize = 0;
                    }
                }

                // After uncompressed block, reinit bitstream (C line 1579)
                if (_blockType == BlockTypeUncompressed)
                {
                    _blockType = 0;
                    _bitstream.Init(_blockType);
                }

                // Read block type and size (C line 1585)
                var blockType = (int)_bitstream.ReadBits(3);
                _blockType = blockType;

                var sizeHi = (int)_bitstream.ReadBits(8);
                var sizeMid = (int)_bitstream.ReadBits(8);
                var sizeLo = (int)_bitstream.ReadBits(8);
                var blockSize = sizeLo + (sizeHi * 0x100 + sizeMid) * 0x100;
                _blockRemaining = blockSize;

                if (blockType == BlockTypeAligned)
                {
                    ReadAlignedTree();
                }

                if (blockType == BlockTypeVerbatim || blockType == BlockTypeAligned)
                {
                    // C line 1600: copy tree lengths to backup BEFORE reading new trees
                    Array.Copy(_mainTreeLengths, _mainTreeLenBackup, _mainTreeElements);
                    Array.Copy(_lengthTreeLengths, _lengthTreeLenBackup, LengthTreeNumElements);

                    if (!ReadMainAndLengthTrees()) return -1;
                }
                else if (blockType == BlockTypeUncompressed)
                {
                    if (!ReadUncompressedHeader()) return -1;
                }
                else
                {
                    return -1;
                }

                _decompressState = StateDecompressing;
            }

            // State 2: decompress within current block (C line 1626)
            if (_blockRemaining > 0)
            {
                while (_blockRemaining > 0 && bytesRemaining > 0)
                {
                    var bytesToDecompress = _blockRemaining;
                    if (bytesRemaining <= bytesToDecompress) bytesToDecompress = bytesRemaining;
                    if (bytesToDecompress == 0) return -1;

                    int errorCode;
                    if (_blockType == BlockTypeAligned)
                        errorCode = DecompressAlignedBlock(bytesToDecompress);
                    else if (_blockType == BlockTypeVerbatim)
                        errorCode = DecompressVerbatimBlock(bytesToDecompress);
                    else if (_blockType == BlockTypeUncompressed)
                        errorCode = DecompressUncompressedBlock(bytesToDecompress);
                    else
                        return -1;

                    if (errorCode != 0) return -1;

                    totalDecompressed += bytesToDecompress;
                    bytesRemaining -= bytesToDecompress;
                    _blockRemaining -= bytesToDecompress;
                }
            }

            // When block is done, need new header (C line 1658)
            if (_blockRemaining == 0)
            {
                _decompressState = StateNeedHeader;
            }
        }

        // C line 1664: re-init bitstream at END of chunk
        _bitstream.Init(_blockType);

        // C line 1667-1670: copy from window to output and apply E8
        for (var i = 0; i < totalDecompressed; i++)
        {
            output[i] = _window[(initialWindowPos + i) & _windowMask];
        }

        // C: CopyOutputAndE8Translate — apply E8 if needed
        if (_intelFileSize != 0 && _chunkCount <= MaxChunkCountForE8)
        {
            E8Translate(output, totalDecompressed);
        }

        return totalDecompressed;
    }

    // ================================================================
    // TREE READING — C: ReadCodeLengthsWithPreTree, ReadMainAndLengthTrees
    // ================================================================

    // C: ReadAlignedTree (0x1019d560, line 669)
    private void ReadAlignedTree()
    {
        for (var i = 0; i < AlignedTreeNumElements; i++)
        {
            _alignedTreeLengths[i] = (byte)_bitstream.ReadBits(3);
        }

        HuffmanTableBuilder.BuildAlignedDecodeTable(
            _alignedTreeLengths, _alignedTreeTable, AlignedTreeTableBits, AlignedTreeNumElements);
    }

    // C: ReadMainAndLengthTrees (0x1019d4a0, line 634)
    private bool ReadMainAndLengthTrees()
    {
        // Read main tree part 1: literals (0-255)
        ReadCodeLengthsWithPreTree(NumChars, _mainTreeLenBackup, _mainTreeLengths, 0);
        if (_bitstream.HasError) return false;

        // Read main tree part 2: match symbols (256+)
        ReadCodeLengthsWithPreTree(
            _numPositionSlots * 8,
            _mainTreeLenBackup, _mainTreeLengths, NumChars);
        if (_bitstream.HasError) return false;

        if (!HuffmanTableBuilder.BuildDecodeTable(
                _mainTreeElements, _mainTreeLengths, MainTreeTableBits, _mainTreeTable))
            return false;

        // Read length tree
        ReadCodeLengthsWithPreTree(LengthTreeNumElements, _lengthTreeLenBackup, _lengthTreeLengths, 0);
        if (_bitstream.HasError) return false;

        if (!HuffmanTableBuilder.BuildDecodeTable(
                LengthTreeNumElements, _lengthTreeLengths, LengthTreeTableBits, _lengthTreeTable))
            return false;

        return true;
    }

    // C: ReadCodeLengthsWithPreTree (0x1019cf30, line 335)
    private void ReadCodeLengthsWithPreTree(int numElements,
        byte[] oldLengths, byte[] newLengths, int offset)
    {
        // Read 20 pretree code lengths, 4 bits each (C line 356)
        Span<byte> preLengths = stackalloc byte[24];
        preLengths.Clear();
        for (var i = 0; i < PreTreeNumElements; i++)
        {
            var byteVal = (byte)_bitstream.ReadBits(4);
            preLengths[i] = byteVal;
            if (byteVal > 16)
            {
                _bitstream.HasError = true;
                return;
            }
        }

        if (_bitstream.HasError) return;

        // Build pretree decode table (C line 370)
        // Note: return value NOT checked — matches C behavior
        var preTreeTable = new short[256 + PreTreeNumElements * 4];
        HuffmanTableBuilder.BuildDecodeTable(PreTreeNumElements, preLengths, 8, preTreeTable);

        var outPos = 0;

        while (outPos < numElements)
        {
            // Decode pretree symbol (C line 391)
            var preTreeSymbol = _bitstream.DecodeSymbol(preTreeTable, 8);
            if (_bitstream.HasError) return;

            if (preTreeSymbol < 0 || preTreeSymbol > 23)
            {
                _bitstream.HasError = true;
                return;
            }

            // Consume pretree symbol bits (C line 411)
            _bitstream.ConsumeBits(preLengths[preTreeSymbol]);
            if (_bitstream.HasError) return;

            // Process pretree symbol (C line 434)
            if (preTreeSymbol == 0x11)
            {
                // RepeatZeroShort (17): read 4 bits, count = value + 4 (C line 436)
                var count = (int)_bitstream.ReadBits(4) + 4;
                if (outPos + count > numElements)
                    count = numElements - outPos;
                for (var i = 0; i < count; i++)
                    newLengths[offset + outPos + i] = 0;
                outPos += count;
            }
            else if (preTreeSymbol == 0x12)
            {
                // RepeatZeroLong (18): read 5 bits, count = value + 20 (C line 477)
                var count = (int)_bitstream.ReadBits(5) + 20;
                if (outPos + count > numElements)
                    count = numElements - outPos;
                if (count < 1)
                {
                    outPos++;
                    continue;
                }

                for (var i = 0; i < count; i++)
                    newLengths[offset + outPos + i] = 0;
                outPos += count;
            }
            else if (preTreeSymbol == 0x13)
            {
                // RepeatSame (19): count = 4 or 5 based on sign bit (C line 515)
                var count = 4 - ((int)_bitstream.BitBuffer >> 31);
                _bitstream.ConsumeBits(1);
                if (_bitstream.HasError) return;

                if (outPos + count > numElements)
                    count = numElements - outPos;

                // Decode another pretree symbol for the delta value (C line 541)
                var nextSymbol = _bitstream.DecodeSymbol(preTreeTable, 8);
                if (_bitstream.HasError) return;

                if (nextSymbol < 0 || nextSymbol > 23)
                {
                    _bitstream.HasError = true;
                    return;
                }

                _bitstream.ConsumeBits(preLengths[nextSymbol]);
                if (_bitstream.HasError) return;

                // Delta decode (C line 577)
                var delta = oldLengths[offset + outPos] - nextSymbol;
                var newLen = delta + 17;
                if (newLen > 16) newLen = delta;
                if ((byte)newLen > 16)
                {
                    _bitstream.HasError = true;
                    return;
                }

                for (var i = 0; i < count; i++)
                    newLengths[offset + outPos + i] = (byte)newLen;
                outPos += count;
            }
            else
            {
                // Symbol 0-16: single delta decode (C line 604)
                var delta = oldLengths[offset + outPos] - preTreeSymbol;
                var newLen = delta + 17;
                if (newLen > 16) newLen = delta;
                if ((byte)newLen > 16)
                {
                    _bitstream.HasError = true;
                    return;
                }

                newLengths[offset + outPos] = (byte)newLen;
                outPos++;
            }
        }
    }

    // C: ReadUncompressedHeader (0x1019c110, line 694)
    private bool ReadUncompressedHeader()
    {
        // Back up 2 bytes (the pre-loaded reserve word)
        _bitstream.BackUp(2);

        if (!_bitstream.HasAvailable(12)) return false;

        _repeatOffset0 = _bitstream.ReadInt32LE();
        _repeatOffset1 = _bitstream.ReadInt32LE();
        _repeatOffset2 = _bitstream.ReadInt32LE();
        return true;
    }

    // ================================================================
    // WINDOW HELPER
    // ================================================================

    /// <summary>
    ///     Writes a byte to the sliding window at the given position,
    ///     mirroring it to the overflow region if near the start.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteToWindow(int position, byte value)
    {
        _window[position] = value;
        if (position < WindowMirrorThreshold)
        {
            _window[position + _windowSize] = value;
        }
    }

    // ================================================================
    // VERBATIM BLOCK — C: DecompressVerbatim_Inner/FastPath (lines 769-1086)
    // ================================================================

    private int DecompressVerbatimBlock(int count)
    {
        var windowPos = _windowPosition;
        var endPos = windowPos + count;

        while (windowPos < endPos)
        {
            // Decode main tree symbol — peek top bits (C line 793)
            var mainSymbol = _bitstream.DecodeSymbol(_mainTreeTable, MainTreeTableBits);

            // Guard: ensure input available for refill (C line 809)
            if (!_bitstream.HasInputForRefill())
            {
                _windowPosition = windowPos;
                return -1;
            }

            // Consume symbol bits (C line 809)
            _bitstream.ConsumeBits(_mainTreeLengths[mainSymbol]);

            if (mainSymbol < NumChars)
            {
                // Literal byte (C line 822)
                var maskedPos = windowPos & _windowMask;
                WriteToWindow(maskedPos, (byte)mainSymbol);
                windowPos++;
            }
            else
            {
                // Match (C line 830)
                var matchCode = mainSymbol - NumChars;
                var lengthSlot = matchCode & 7;
                var positionSlot = matchCode >> 3;

                if (lengthSlot == 7)
                {
                    // Decode length tree (C line 833)
                    var lengthSymbol = _bitstream.DecodeSymbol(_lengthTreeTable, LengthTreeTableBits);
                    _bitstream.ConsumeBits(_lengthTreeLengths[lengthSymbol]);
                    lengthSlot = lengthSymbol + 7;
                }

                var matchLength = lengthSlot + 2;

                // Decode match offset (C line 857)
                int matchOffset;
                if (positionSlot < 3)
                {
                    matchOffset = positionSlot switch
                    {
                        0 => _repeatOffset0,
                        1 => _repeatOffset1,
                        _ => _repeatOffset2
                    };
                    if (positionSlot != 0)
                    {
                        if (positionSlot == 1)
                        {
                            _repeatOffset1 = _repeatOffset0;
                        }
                        else
                        {
                            _repeatOffset2 = _repeatOffset0;
                        }

                        _repeatOffset0 = matchOffset;
                    }
                }
                else
                {
                    int extraBitsCount = ExtraBits[positionSlot];
                    var extraVal = _bitstream.ReadBits(extraBitsCount);

                    matchOffset = positionSlot == 3
                        ? 1
                        : (int)extraVal + PositionBase[positionSlot];

                    _repeatOffset2 = _repeatOffset1;
                    _repeatOffset1 = _repeatOffset0;
                    _repeatOffset0 = matchOffset;
                }

                // Copy match (C line 902)
                var srcPos = windowPos - matchOffset;
                for (var i = 0; i < matchLength; i++)
                {
                    var maskedPos = windowPos & _windowMask;
                    var byteVal = _window[(srcPos + i) & _windowMask];
                    WriteToWindow(maskedPos, byteVal);
                    windowPos++;
                }
            }
        }

        _windowPosition = windowPos;
        return 0;
    }

    // ================================================================
    // ALIGNED BLOCK — C: DecompressAligned_Inner/FastPath (lines 1097-1448)
    // ================================================================

    private int DecompressAlignedBlock(int count)
    {
        var windowPos = _windowPosition;
        var endPos = windowPos + count;

        while (windowPos < endPos)
        {
            // Decode main tree symbol
            var mainSymbol = _bitstream.DecodeSymbol(_mainTreeTable, MainTreeTableBits);

            if (!_bitstream.HasInputForRefill())
            {
                _windowPosition = windowPos;
                return -1;
            }

            _bitstream.ConsumeBits(_mainTreeLengths[mainSymbol]);

            if (mainSymbol < NumChars)
            {
                var maskedPos = windowPos & _windowMask;
                WriteToWindow(maskedPos, (byte)mainSymbol);
                windowPos++;
            }
            else
            {
                var matchCode = mainSymbol - NumChars;
                var lengthSlot = matchCode & 7;
                var positionSlot = matchCode >> 3;

                if (lengthSlot == 7)
                {
                    var lengthSymbol = _bitstream.DecodeSymbol(_lengthTreeTable, LengthTreeTableBits);
                    _bitstream.ConsumeBits(_lengthTreeLengths[lengthSymbol]);
                    lengthSlot = lengthSymbol + 7;
                }

                var matchLength = lengthSlot + 2;

                int matchOffset;
                if (positionSlot < 3)
                {
                    matchOffset = positionSlot switch { 0 => _repeatOffset0, 1 => _repeatOffset1, _ => _repeatOffset2 };
                    if (positionSlot != 0)
                    {
                        if (positionSlot == 1)
                        {
                            _repeatOffset1 = _repeatOffset0;
                        }
                        else
                        {
                            _repeatOffset2 = _repeatOffset0;
                        }

                        _repeatOffset0 = matchOffset;
                    }
                }
                else
                {
                    int extraBitsCount = ExtraBits[positionSlot];

                    if (extraBitsCount >= 3)
                    {
                        // Read (extra - 3) bits from bitstream, then 3 from aligned tree
                        var topBits = extraBitsCount - 3;
                        uint verbatimBits = 0;
                        if (topBits > 0)
                        {
                            verbatimBits = _bitstream.ReadBits(topBits);
                        }

                        // Aligned tree decode (C line 1157)
                        int alignedSymbol = _alignedTreeTable[_bitstream.BitBuffer >> 25];
                        int alignedCodeLength = _alignedTreeLengths[alignedSymbol];
                        _bitstream.ConsumeBits(alignedCodeLength);

                        matchOffset = (int)(verbatimBits << 3) + alignedSymbol + PositionBase[positionSlot];
                    }
                    else if (extraBitsCount > 0)
                    {
                        var extraVal = _bitstream.ReadBits(extraBitsCount);
                        matchOffset = (int)extraVal + PositionBase[positionSlot];
                    }
                    else
                    {
                        matchOffset = PositionBase[positionSlot];
                    }

                    _repeatOffset2 = _repeatOffset1;
                    _repeatOffset1 = _repeatOffset0;
                    _repeatOffset0 = matchOffset;
                }

                var srcPos = windowPos - matchOffset;
                for (var i = 0; i < matchLength; i++)
                {
                    var maskedPos = windowPos & _windowMask;
                    var byteVal = _window[(srcPos + i) & _windowMask];
                    WriteToWindow(maskedPos, byteVal);
                    windowPos++;
                }
            }
        }

        _windowPosition = windowPos;
        return 0;
    }

    // ================================================================
    // UNCOMPRESSED BLOCK — C: DecompressUncompressed (line 720)
    // ================================================================

    private int DecompressUncompressedBlock(int count)
    {
        if (!_bitstream.HasAvailable(count)) return -1;

        var windowPos = _windowPosition;

        for (var i = 0; i < count; i++)
        {
            var maskedPos = windowPos & _windowMask;
            WriteToWindow(maskedPos, _bitstream.ReadByte());
            windowPos++;
        }

        // C line 755: mask window position
        _windowPosition = windowPos & _windowMask;
        return 0;
    }

    // ================================================================
    // E8 TRANSLATION — C: E8Translate (0x1019a660, line 1458)
    // Sentinel-based approach matching C exactly.
    // ================================================================

    private void E8Translate(Span<byte> data, int dataLength)
    {
        if (dataLength <= 10) return;

        var curPos = _intelCurrentPosition;
        var fileSize = _intelFileSize;

        var endPos = dataLength - 10;

        // Sentinel approach: save byte at endPos, replace with 0xE8
        var savedByte = data[endPos];
        data[endPos] = 0xE8;

        var i = 0;
        while (i < endPos)
        {
            // Scan for 0xE8
            while (data[i] != 0xE8)
            {
                i++;
                curPos++;
            }

            if (i >= endPos)
            {
                // Hit sentinel, not a real E8
                break;
            }

            // Read 32-bit LE value after E8
            var absValue = data[i + 1] | (data[i + 2] << 8) |
                           (data[i + 3] << 16) | (data[i + 4] << 24);

            if (absValue >= -curPos && absValue < fileSize)
            {
                int relValue;
                if (absValue >= 0)
                    relValue = absValue - curPos;
                else
                    relValue = absValue + fileSize;

                data[i + 1] = (byte)(relValue & 0xFF);
                data[i + 2] = (byte)((relValue >> 8) & 0xFF);
                data[i + 3] = (byte)((relValue >> 16) & 0xFF);
                data[i + 4] = (byte)((relValue >> 24) & 0xFF);
            }

            i += 5;
            curPos += 5;
        }

        // Restore sentinel byte
        data[endPos] = savedByte;

        _intelCurrentPosition += dataLength;
    }
}
