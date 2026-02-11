// LZX decompression — pure managed C# replacement for XnaNative.dll
// Mechanically translated from tools/LzxVerify/lzx_decompress.c which was
// verified byte-for-byte against XnaNative.dll v4.0.30901.0 on 3,870 DDX files.
//
// Implements the XMemCompress chunked LZX decompression used by Xbox 360.
// Each function below maps 1:1 to a verified C function; the C function address
// is noted in comments for traceability.

using System.Buffers;

namespace DDXConv.Compression;

public sealed class LzxDecompressor : IDisposable
{
    // LZX constants
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

    // Context fields (mapped from C context struct offsets in lzx_context.h)
    private readonly int _windowSize;    // OFF_WINDOW_SIZE  0x004
    private readonly int _windowMask;    // OFF_WINDOW_MASK  0x008
    private readonly byte[] _window;     // OFF_WINDOW_BASE  0x000
    private readonly int _numPositionSlots; // OFF_NUM_POS_SLOTS 0x2EB5

    private int _r0, _r1, _r2;          // OFF_R0/R1/R2     0x00C-0x014

    // Huffman decode tables
    private readonly short[] _mainTreeTable;     // OFF_MAIN_TREE_TABLE     0x018
    private readonly short[] _lengthTreeTable;   // OFF_LENGTH_TREE_TABLE   0x818
    private readonly byte[] _alignedTreeTable;   // OFF_ALIGNED_TREE_TABLE  0xDB4

    // Code lengths
    private readonly byte[] _mainTreeLengths;    // OFF_MAIN_TREE_LENGTHS   0xA18
    private readonly byte[] _lengthTreeLengths;  // OFF_LENGTH_TREE_LENGTHS 0xCB8
    private readonly byte[] _alignedTreeLengths; // OFF_ALIGNED_TREE_LENGTHS 0xE34

    // Tree length backups (C copies current → backup before reading new trees)
    private readonly byte[] _mainTreeLenBackup;    // OFF_MAIN_TREE_BACKUP  0x2B14
    private readonly byte[] _lengthTreeLenBackup;  // OFF_LENGTH_TREE_BACKUP 0x2DB4

    private readonly int _mainTreeElements;

    // Bitstream state
    private byte[] _inputBytes = Array.Empty<byte>();
    private int _inputPosition;          // OFF_INPUT_POSITION  0x2B04
    private int _inputEnd;               // OFF_INPUT_END       0x2B08
    private uint _bitBuffer;             // OFF_BIT_BUFFER      0x2EB0
    private int _bitsRemaining;          // OFF_BITS_REMAINING  0x2EB4 (int8_t in C)

    // Decompression state machine
    private int _windowPosition;         // OFF_WINDOW_POSITION  0x2EB8
    private int _blockType;              // OFF_BLOCK_TYPE       0x2ED0
    private int _blockRemaining;         // OFF_BLOCK_REMAINING  0x2ECC
    private int _state;                  // OFF_STATE            0x2ED4  (1=need header, 2=decompressing)
    private bool _headerRead;            // OFF_HEADER_READ      0x2EB6
    private bool _errorFlag;             // OFF_ERROR_FLAG       0x2EB7

    // Intel E8 translation
    private int _intelFileSize;          // OFF_INTEL_FILE_SIZE     0x2EBC
    private int _intelCurrentPosition;   // OFF_INTEL_CURRENT_POS   0x2EC0
    private int _chunkCount;             // OFF_CHUNK_COUNT         0x2EC4

    // Position base and extra bits tables (standard LZX, pre-adjusted by -2)
    private static readonly int[] PositionBase;  // OFF_POSITION_BASE 0x2F18
    private static readonly byte[] ExtraBits;    // OFF_EXTRA_BITS    0x2EE4

    // C: InitExtraBitsAndPositionBase (0x10198ae0)
    static LzxDecompressor()
    {
        // Position base table pre-adjusted by -2 (matches C posBase[] at line 1825)
        PositionBase = new[]
        {
            -2, -1, 0, 1, 2, 4, 6, 10, 14, 22, 30, 46, 62, 94, 126, 190,
            254, 382, 510, 766, 1022, 1534, 2046, 3070, 4094, 6142, 8190,
            12286, 16382, 24574, 32766, 49150, 65534, 98302, 131070, 196606,
            262142, 393214, 524286, 655358, 786430, 917502, 1048574, 1179646,
            1310718, 1441790, 1572862, 1703934, 1835006, 1966078, 2097150
        };

        // Extra bits table (matches C hardcoded bytes at line 1810-1822)
        ExtraBits = new byte[]
        {
            0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6,
            7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13, 14, 14,
            15, 15, 16, 16, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17,
            17, 17, 17
        };
    }

    // C: LZX_CreateContext (line 1898)
    public LzxDecompressor(int windowSize = 131072, int chunkSize = 524288)
    {
        _windowSize = windowSize;
        _windowMask = windowSize - 1;
        _window = ArrayPool<byte>.Shared.Rent(windowSize + 0x106);
        Array.Clear(_window, 0, _window.Length);

        // Compute numPositionSlots iteratively (matches C lines 1940-1947)
        _numPositionSlots = 4;
        int posSlots = 4;
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
        int mainTableSize = 1 << MainTreeTableBits;
        _mainTreeTable = new short[mainTableSize + _mainTreeElements * 4];
        int lengthTableSize = 1 << LengthTreeTableBits;
        _lengthTreeTable = new short[lengthTableSize + LengthTreeNumElements * 4];
        _alignedTreeTable = new byte[1 << AlignedTreeTableBits];

        Reset();
    }

    // C: ResetLzxState + ResetState + ClearTreeLengths + ResetIntelPos (lines 1838-1887)
    private void Reset()
    {
        _r0 = 1;
        _r1 = 1;
        _r2 = 1;
        _windowPosition = 0;
        _state = 1; // need block header
        _blockRemaining = 0;
        _blockType = 0;
        _headerRead = true; // first block needs E8 header read
        _errorFlag = false;
        _intelFileSize = 0;
        _intelCurrentPosition = 0;
        _chunkCount = 0;
        _bitBuffer = 0;
        _bitsRemaining = 0;
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

        int totalInput = 0;
        int totalOutput = 0;
        int inputPos = 0;

        while (inputPos < input.Length)
        {
            int chunkCompressedSize;
            int chunkUncompressedSize;
            int headerSize;
            int totalChunkSize;

            byte firstByte = input[inputPos];
            if (firstByte == 0xFF)
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
                chunkUncompressedSize = 0x8000;
                headerSize = 2;
                totalChunkSize = chunkCompressedSize + 2;
            }

            // C line 1749: totalChunkSize > 0x980a is an error
            if (totalChunkSize > 0x980a) break;
            if (inputPos + totalChunkSize > input.Length) break;

            if (totalOutput + chunkUncompressedSize > output.Length)
            {
                chunkUncompressedSize = output.Length - totalOutput;
                if (chunkUncompressedSize <= 0)
                {
                    // Output buffer full — skip decompression but still consume input.
                    // XnaNative.dll uses an overflow buffer here; we just advance past
                    // remaining chunks so inputCount reflects the full stream consumption.
                    totalInput += totalChunkSize;
                    inputPos += totalChunkSize;
                    if (firstByte == 0xFF) break; // 0xFF terminates stream
                    continue;
                }
            }

            // C: DecompressChunk_Setup sets inputEnd = dataStart + 4 + compressedLen
            // The +4 accounts for the 4 bytes consumed by InitBitstream's seed
            int dataToPass = chunkCompressedSize + 4;
            int available = input.Length - (inputPos + headerSize);
            if (dataToPass > available) dataToPass = available;

            var chunkInput = input.Slice(inputPos + headerSize, dataToPass);
            var chunkOutput = output.Slice(totalOutput, chunkUncompressedSize);

            int decompressed = DecompressChunk(chunkInput, chunkOutput);
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
            if (firstByte == 0xFF) break;
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
        _inputBytes = compressedData.ToArray();
        _inputPosition = 0;
        _inputEnd = compressedData.Length;

        // C line 1691: InitBitstream at start of chunk
        InitBitstream();

        // C line 1693: main block-level decompression
        int result = DecompressBlocks(output.Length, output);

        // C line 1695: increment chunk count
        _chunkCount++;

        return result;
    }

    // ================================================================
    // BLOCK-LEVEL STATE MACHINE — C: DecompressBlocks (line 1549)
    // ================================================================

    private int DecompressBlocks(int bytesRemaining, Span<byte> output)
    {
        int totalDecompressed = 0;
        int initialWindowPos = _windowPosition;

        while (true)
        {
            if (bytesRemaining < 1) break;

            // State 1: need block header (C line 1563)
            if (_state == 1)
            {
                // First block: read Intel E8 header (C line 1566)
                if (_headerRead)
                {
                    _headerRead = false;
                    int intelBit = (int)ReadBits(1);
                    if (intelBit != 0)
                    {
                        int hi = (int)ReadBits(16);
                        uint lo = ReadBits(16);
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
                    InitBitstream();
                }

                // Read block type and size (C line 1585)
                int blockType = (int)ReadBits(3);
                _blockType = blockType;

                int sizeHi = (int)ReadBits(8);
                int sizeMid = (int)ReadBits(8);
                int sizeLo = (int)ReadBits(8);
                int blockSize = sizeLo + (sizeHi * 0x100 + sizeMid) * 0x100;
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

                _state = 2; // transition to decompressing
            }

            // State 2: decompress within current block (C line 1626)
            if (_blockRemaining > 0)
            {
                while (_blockRemaining > 0 && bytesRemaining > 0)
                {
                    int toDo = _blockRemaining;
                    if (bytesRemaining <= toDo) toDo = bytesRemaining;
                    if (toDo == 0) return -1;

                    int err;
                    if (_blockType == BlockTypeAligned)
                        err = DecompressAlignedBlock(toDo);
                    else if (_blockType == BlockTypeVerbatim)
                        err = DecompressVerbatimBlock(toDo);
                    else if (_blockType == BlockTypeUncompressed)
                        err = DecompressUncompressedBlock(toDo);
                    else
                        return -1;

                    if (err != 0) return -1;

                    totalDecompressed += toDo;
                    bytesRemaining -= toDo;
                    _blockRemaining -= toDo;
                }
            }

            // When block is done, need new header (C line 1658)
            if (_blockRemaining == 0)
            {
                _state = 1;
            }

            if (bytesRemaining == 0) break;
        }

        // C line 1664: re-init bitstream at END of chunk
        InitBitstream();

        // C line 1667-1670: copy from window to output and apply E8
        // Use initialWindowPos (saved before decompression) as the copy start.
        // The decoder can overshoot endPos when the last match straddles the boundary,
        // causing _windowPosition to advance past the target. Using the initial position
        // ensures we copy from where the data actually starts in the window.
        for (int i = 0; i < totalDecompressed; i++)
        {
            output[i] = _window[(initialWindowPos + i) & _windowMask];
        }

        // C: CopyOutputAndE8Translate — apply E8 if needed
        if (_intelFileSize != 0 && _chunkCount <= 0x7fff)
        {
            E8Translate(output, totalDecompressed);
        }

        return totalDecompressed;
    }

    // ================================================================
    // BITSTREAM OPERATIONS — C: InitBitstream, RemoveBits, ReadBits
    // ================================================================

    // C: InitBitstream (0x1019a9b0, line 24)
    // Loads 4 bytes as two LE words. SKIPPED if blockType == 3 (uncompressed).
    private void InitBitstream()
    {
        if (_blockType != BlockTypeUncompressed)
        {
            if (_inputPosition + 3 < _inputEnd)
            {
                uint word0 = (uint)(_inputBytes[_inputPosition] | (_inputBytes[_inputPosition + 1] << 8));
                uint word1 = (uint)(_inputBytes[_inputPosition + 2] | (_inputBytes[_inputPosition + 3] << 8));
                _inputPosition += 4;
                _bitBuffer = (word0 << 16) | word1;
                _bitsRemaining = 16;
            }
        }
    }

    // C: RemoveBits (0x1019aa20, line 48)
    private void RemoveBits(int count)
    {
        _bitBuffer <<= (count & 0x1f);
        _bitsRemaining -= count;

        if (_bitsRemaining < 1)
        {
            if (_inputPosition + 1 >= _inputEnd)
            {
                _errorFlag = true;
                return;
            }
            uint word = (uint)(_inputBytes[_inputPosition] | (_inputBytes[_inputPosition + 1] << 8));
            _inputPosition += 2;
            int newBR = _bitsRemaining + 16;
            _bitBuffer |= word << ((-_bitsRemaining) & 0x1f);
            _bitsRemaining = newBR;

            // Double refill (C line 69)
            if (newBR < 1)
            {
                if (_inputPosition + 1 >= _inputEnd)
                {
                    _errorFlag = true;
                    return;
                }
                word = (uint)(_inputBytes[_inputPosition] | (_inputBytes[_inputPosition + 1] << 8));
                _inputPosition += 2;
                _bitBuffer |= word << ((-newBR) & 0x1f);
                _bitsRemaining = newBR + 16;
            }
        }
    }

    // C: ReadBits (0x1019aaf0, line 87)
    private uint ReadBits(int count)
    {
        uint val = _bitBuffer >> ((32 - count) & 0x1f);
        RemoveBits(count);
        return val;
    }

    // ================================================================
    // HUFFMAN TABLE BUILDERS
    // ================================================================

    // C: MakeDecodeTable (0x1019d5c0, line 104)
    // Cumulative-sum algorithm verified against XnaNative on 3,870 files.
    private static bool MakeDecodeTable(int numSymbols, ReadOnlySpan<byte> lengths,
        int tableBits, short[] table)
    {
        int tableSize = 1 << tableBits;

        // Count code lengths (C: local_90[2..17])
        Span<int> work = stackalloc int[36];
        work.Clear();

        for (int i = 0; i < numSymbols; i++)
        {
            work[lengths[i] + 1]++;
        }

        // Cumulative code-space sum in 16-bit fixed point (C line 134)
        // local_90[0x13] = cumulative sum start
        work[0x13] = 0;
        for (int i = 1; i <= 16; i++)
        {
            // Unrolled 4-at-a-time in C, but sequential is equivalent
            work[i + 0x13] = work[i + 1] * (1 << (16 - i)) + work[i + 0x12];
        }

        // Verify completeness (C line 150)
        int total = work[0x23]; // = work[16 + 0x13]
        if (total != 0x10000)
        {
            if (total == 0)
            {
                // All zero lengths — clear table
                Array.Clear(table, 0, Math.Min(table.Length, tableSize));
                return true;
            }
            return false; // oversubscribed or undersubscribed
        }

        // Compute first canonical code per length (C line 168)
        int bitsToShift = 16 - tableBits;

        // For lengths 1..tableBits: shift cumulative sums right by (16-tableBits)
        // and compute fill counts
        int fillIdx = 1;
        int remaining = tableBits;
        if (tableBits > 0)
        {
            while (fillIdx <= tableBits)
            {
                work[fillIdx + 0x12] >>= (bitsToShift & 0x1f);
                fillIdx++;
                work[fillIdx] = 1 << ((remaining - 1) & 0x1f);
                remaining--;
            }
        }

        // For lengths tableBits+1..16: compute fill counts
        if (fillIdx <= 16)
        {
            int shift = 16 - fillIdx;
            while (fillIdx <= 16)
            {
                work[fillIdx + 1] = 1 << (shift & 0x1f);
                fillIdx++;
                shift--;
            }
        }

        // Clear unused portion of direct table (C line 190)
        int firstUnused = work[tableBits + 0x13] >> (bitsToShift & 0x1f);
        if (firstUnused < tableSize)
        {
            for (int i = firstUnused; i < tableSize; i++)
            {
                table[i] = 0;
            }
        }

        // Populate tables symbol by symbol (C line 204)
        // nextOverflow must be >= tableSize/2 so that overflow nodes (at nextOverflow*2)
        // don't overlap with the direct table (indices 0..tableSize-1).
        int nextOverflow = Math.Max(numSymbols, (tableSize + 1) / 2);

        for (int sym = 0; sym < numSymbols; sym++)
        {
            int len = lengths[sym];
            if (len == 0) continue;

            int code = work[len + 0x12];         // current table position
            int next = code + work[len + 1];     // next position

            if (len > tableBits)
            {
                // Overflow tree (C line 218-238)
                // Matches C's simple do-while loop exactly.
                work[len + 0x12] = next;
                int overflowBits = len - tableBits;
                int directIdx = code >> (bitsToShift & 0x1f);
                int iVar8 = code << (tableBits & 0x1f);

                // nodeRef: index into table[] where the current pointer lives.
                // Starts at the direct table entry, then moves into overflow region.
                int nodeRef = directIdx;

                for (int b = 0; b < overflowBits; b++)
                {
                    // If empty slot, allocate a new overflow node pair
                    if (table[nodeRef] == 0)
                    {
                        if (nextOverflow * 2 + 1 >= table.Length) return false;
                        table[nextOverflow * 2] = 0;
                        table[nextOverflow * 2 + 1] = 0;
                        table[nodeRef] = (short)(-nextOverflow);
                        nextOverflow++;
                    }

                    // Navigate: go to overflow node and select child by bit
                    // C: psVar9 = (int16_t*)(overflowTable + *psVar9 * -4);
                    //    if ((int16_t)iVar8 < 0) psVar9++;
                    int nodeIdx = -table[nodeRef];
                    int bit = ((short)iVar8 < 0) ? 1 : 0;
                    nodeRef = nodeIdx * 2 + bit;
                    iVar8 <<= 1;
                }

                // Store symbol at leaf (C line 238: *psVar9 = sVar3)
                table[nodeRef] = (short)sym;
            }
            else
            {
                // Direct table: fill entries (C line 240)
                if (next > tableSize) return false;
                for (int i = code; i < next; i++)
                {
                    table[i] = (short)sym;
                }
                work[len + 0x12] = next;
            }
        }

        return true;
    }

    // C: MakeAlignedDecodeTable (0x1019d8f0, line 275)
    private void MakeAlignedDecodeTable()
    {
        int tableSize = 1 << AlignedTreeTableBits;
        Array.Clear(_alignedTreeTable, 0, tableSize);

        Span<int> counts = stackalloc int[8];
        counts.Clear();
        for (int i = 0; i < AlignedTreeNumElements; i++)
        {
            if (_alignedTreeLengths[i] <= 7) counts[_alignedTreeLengths[i]]++;
        }

        Span<int> nextCode = stackalloc int[8];
        int code = 0;
        counts[0] = 0;
        for (int bits = 1; bits <= 7; bits++)
        {
            code = (code + counts[bits - 1]) << 1;
            nextCode[bits] = code;
        }

        for (int sym = 0; sym < AlignedTreeNumElements; sym++)
        {
            int len = _alignedTreeLengths[sym];
            if (len == 0) continue;
            int c = nextCode[len]++;
            int baseIdx = c << (AlignedTreeTableBits - len);
            int fill = 1 << (AlignedTreeTableBits - len);
            for (int i = 0; i < fill; i++)
            {
                _alignedTreeTable[baseIdx + i] = (byte)sym;
            }
        }
    }

    // ================================================================
    // TREE READING — C: ReadCodeLengthsWithPreTree, ReadMainAndLengthTrees
    // ================================================================

    // C: ReadAlignedTree (0x1019d560, line 669)
    private void ReadAlignedTree()
    {
        for (int i = 0; i < AlignedTreeNumElements; i++)
        {
            _alignedTreeLengths[i] = (byte)ReadBits(3);
        }
        MakeAlignedDecodeTable();
    }

    // C: ReadMainAndLengthTrees (0x1019d4a0, line 634)
    private bool ReadMainAndLengthTrees()
    {
        // Read main tree part 1: literals (0-255)
        ReadCodeLengthsWithPreTree(NumChars, _mainTreeLenBackup, _mainTreeLengths, 0);
        if (_errorFlag) return false;

        // Read main tree part 2: match symbols (256+)
        ReadCodeLengthsWithPreTree(
            _numPositionSlots * 8,
            _mainTreeLenBackup, _mainTreeLengths, NumChars);
        if (_errorFlag) return false;

        if (!MakeDecodeTable(_mainTreeElements, _mainTreeLengths, MainTreeTableBits, _mainTreeTable))
            return false;

        // Read length tree
        ReadCodeLengthsWithPreTree(LengthTreeNumElements, _lengthTreeLenBackup, _lengthTreeLengths, 0);
        if (_errorFlag) return false;

        if (!MakeDecodeTable(LengthTreeNumElements, _lengthTreeLengths, LengthTreeTableBits, _lengthTreeTable))
            return false;

        return true;
    }

    // C: ReadCodeLengthsWithPreTree (0x1019cf30, line 335)
    // All bitstream operations are INLINE for performance, matching the C exactly.
    private void ReadCodeLengthsWithPreTree(int numElements,
        byte[] oldLengths, byte[] newLengths, int offset)
    {
        // Read 20 pretree code lengths, 4 bits each (C line 356)
        Span<byte> preLengths = stackalloc byte[24];
        preLengths.Clear();
        for (int i = 0; i < PreTreeNumElements; i++)
        {
            byte b = (byte)ReadBits(4);
            preLengths[i] = b;
            if (b > 16) { _errorFlag = true; return; }
        }
        if (_errorFlag) return;

        // Build pretree decode table (C line 370)
        // Note: return value NOT checked — matches C behavior
        short[] preTreeTable = new short[256 + PreTreeNumElements * 4];
        MakeDecodeTable(PreTreeNumElements, preLengths, 8, preTreeTable);

        // Cache bitstream state locally (C line 373)
        int inputPos = _inputPosition;
        int inputEnd = _inputEnd;
        uint bitBuffer = _bitBuffer;
        int bitsRemain = _bitsRemaining;
        bool errorFlag = _errorFlag;
        int outPos = 0;

        if (numElements > 0)
        {
            while (outPos < numElements)
            {
                // Decode pretree symbol (C line 391)
                int preTreeSymbol = preTreeTable[bitBuffer >> 24];

                // Walk overflow if needed (C line 394)
                if (preTreeSymbol < 0)
                {
                    uint mask = 0x800000;
                    while (preTreeSymbol < 0)
                    {
                        int idx;
                        if ((bitBuffer & mask) == 0)
                            idx = (-preTreeSymbol) * 2;
                        else
                            idx = (-preTreeSymbol) * 2 + 1;
                        if (idx >= preTreeTable.Length) { errorFlag = true; goto WRITEBACK; }
                        preTreeSymbol = preTreeTable[idx];
                        mask >>= 1;
                    }
                }

                if (preTreeSymbol > 23) { errorFlag = true; goto WRITEBACK; }

                // Consume pretree symbol bits — inline (C line 411)
                int symLen = preLengths[preTreeSymbol];
                bitBuffer <<= (symLen & 0x1f);
                bitsRemain -= symLen;
                if (bitsRemain < 1)
                {
                    if (inputPos + 1 >= inputEnd) { errorFlag = true; goto WRITEBACK; }
                    uint word = (uint)(_inputBytes[inputPos] | (_inputBytes[inputPos + 1] << 8));
                    bitBuffer |= word << ((-bitsRemain) & 0x1f);
                    int newBR = bitsRemain + 16;
                    inputPos += 2;
                    if (newBR < 1)
                    {
                        if (inputPos + 1 >= inputEnd) { errorFlag = true; goto WRITEBACK; }
                        word = (uint)(_inputBytes[inputPos] | (_inputBytes[inputPos + 1] << 8));
                        bitBuffer |= word << ((-newBR) & 0x1f);
                        bitsRemain = newBR + 16;
                        inputPos += 2;
                    }
                    else
                    {
                        bitsRemain = newBR;
                    }
                }
                if (errorFlag) goto WRITEBACK;

                // Process pretree symbol (C line 434)
                if (preTreeSymbol == 0x11)
                {
                    // RepeatZeroShort (17): read 4 bits, count = value + 4 (C line 436)
                    uint count = bitBuffer >> 28;
                    bitBuffer <<= 4;
                    bitsRemain -= 4;
                    if (bitsRemain < 1)
                    {
                        if (inputPos + 1 < inputEnd)
                        {
                            uint w = (uint)(_inputBytes[inputPos] | (_inputBytes[inputPos + 1] << 8));
                            bitBuffer |= w << ((-bitsRemain) & 0x1f);
                            int nb = bitsRemain + 16;
                            inputPos += 2;
                            if (nb < 1)
                            {
                                if (inputPos + 1 < inputEnd)
                                {
                                    w = (uint)(_inputBytes[inputPos] | (_inputBytes[inputPos + 1] << 8));
                                    bitBuffer |= w << ((-nb) & 0x1f);
                                    bitsRemain = nb + 16;
                                    inputPos += 2;
                                }
                                else { errorFlag = true; }
                            }
                            else { bitsRemain = nb; }
                        }
                        else { errorFlag = true; }
                    }
                    count += 4;
                    if (outPos + (int)count > numElements)
                        count = (uint)(numElements - outPos);
                    for (int i = 0; i < (int)count; i++)
                        newLengths[offset + outPos + i] = 0;
                    outPos += (int)count - 1;
                }
                else if (preTreeSymbol == 0x12)
                {
                    // RepeatZeroLong (18): read 5 bits, count = value + 20 (C line 477)
                    uint count = bitBuffer >> 27;
                    bitBuffer <<= 5;
                    bitsRemain -= 5;
                    if (bitsRemain < 1)
                    {
                        if (inputPos + 1 < inputEnd)
                        {
                            uint w = (uint)(_inputBytes[inputPos] | (_inputBytes[inputPos + 1] << 8));
                            bitBuffer |= w << ((-bitsRemain) & 0x1f);
                            int nb = bitsRemain + 16;
                            inputPos += 2;
                            if (nb < 1)
                            {
                                if (inputPos + 1 < inputEnd)
                                {
                                    w = (uint)(_inputBytes[inputPos] | (_inputBytes[inputPos + 1] << 8));
                                    bitBuffer |= w << ((-nb) & 0x1f);
                                    bitsRemain = nb + 16;
                                    inputPos += 2;
                                }
                                else { errorFlag = true; }
                            }
                            else { bitsRemain = nb; }
                        }
                        else { errorFlag = true; }
                    }
                    count += 20;
                    if (outPos + (int)count > numElements)
                        count = (uint)(numElements - outPos);
                    if ((int)count < 1) { outPos--; goto NEXT; }
                    for (int i = 0; i < (int)count; i++)
                        newLengths[offset + outPos + i] = 0;
                    outPos += (int)count - 1;
                }
                else if (preTreeSymbol == 0x13)
                {
                    // RepeatSame (19): count = 4 - ((int)bitBuffer >> 31) = 4 or 5 (C line 515)
                    uint count = (uint)(4 - ((int)bitBuffer >> 31));
                    uint uVar5 = bitBuffer << 1;
                    bitsRemain -= 1;
                    if (bitsRemain < 1)
                    {
                        if (inputPos + 1 < inputEnd)
                        {
                            uint w = (uint)(_inputBytes[inputPos] | (_inputBytes[inputPos + 1] << 8));
                            uVar5 |= w << ((-bitsRemain) & 0x1f);
                            int nb = bitsRemain + 16;
                            inputPos += 2;
                            if (nb < 1)
                            {
                                if (inputPos + 1 < inputEnd)
                                {
                                    w = (uint)(_inputBytes[inputPos] | (_inputBytes[inputPos + 1] << 8));
                                    uVar5 |= w << ((-nb) & 0x1f);
                                    bitsRemain = nb + 16;
                                    inputPos += 2;
                                }
                                else { errorFlag = true; }
                            }
                            else { bitsRemain = nb; }
                        }
                        else { errorFlag = true; }
                    }

                    if (outPos + (int)count > numElements)
                        count = (uint)(numElements - outPos);

                    // Decode another pretree symbol for the delta value (C line 541)
                    int nextSym = preTreeTable[uVar5 >> 24];
                    if (nextSym < 0)
                    {
                        uint m2 = 0x800000;
                        while (nextSym < 0)
                        {
                            int idx;
                            if ((uVar5 & m2) == 0)
                                idx = (-nextSym) * 2;
                            else
                                idx = (-nextSym) * 2 + 1;
                            if (idx >= preTreeTable.Length) { errorFlag = true; goto WRITEBACK; }
                            nextSym = preTreeTable[idx];
                            m2 >>= 1;
                        }
                    }
                    if (nextSym > 23) { errorFlag = true; goto WRITEBACK; }

                    // Consume next symbol bits (C line 557)
                    int nextLen = preLengths[nextSym];
                    bitBuffer = uVar5 << (nextLen & 0x1f);
                    bitsRemain -= nextLen;
                    if (bitsRemain < 1)
                    {
                        if (inputPos + 1 < inputEnd)
                        {
                            uint w = (uint)(_inputBytes[inputPos] | (_inputBytes[inputPos + 1] << 8));
                            bitBuffer |= w << ((-bitsRemain) & 0x1f);
                            int nb = bitsRemain + 16;
                            inputPos += 2;
                            if (nb < 1)
                            {
                                if (inputPos + 1 < inputEnd)
                                {
                                    w = (uint)(_inputBytes[inputPos] | (_inputBytes[inputPos + 1] << 8));
                                    bitBuffer |= w << ((-nb) & 0x1f);
                                    bitsRemain = nb + 16;
                                    inputPos += 2;
                                }
                                else { errorFlag = true; }
                            }
                            else { bitsRemain = nb; }
                        }
                        else { errorFlag = true; }
                    }

                    // Delta decode (C line 577)
                    int delta = oldLengths[offset + outPos] - nextSym;
                    int newLen = delta + 17;
                    if (newLen > 16) newLen = delta;
                    if ((byte)newLen > 16) { errorFlag = true; goto WRITEBACK; }

                    for (int i = 0; i < (int)count; i++)
                        newLengths[offset + outPos + i] = (byte)newLen;
                    outPos += (int)count - 1;
                    goto NEXT;
                }
                else
                {
                    // Symbol 0-16: single delta decode (C line 604)
                    int delta = oldLengths[offset + outPos] - preTreeSymbol;
                    int newLen = delta + 17;
                    if (newLen > 16) newLen = delta;
                    if ((byte)newLen > 16) { errorFlag = true; goto WRITEBACK; }
                    newLengths[offset + outPos] = (byte)newLen;
                }

            NEXT:
                outPos++;
            }
        }

    WRITEBACK:
        _inputPosition = inputPos;
        _bitBuffer = bitBuffer;
        _bitsRemaining = bitsRemain;
        _errorFlag = errorFlag;
    }

    // C: ReadUncompressedHeader (0x1019c110, line 694)
    private bool ReadUncompressedHeader()
    {
        // Back up 2 bytes (the pre-loaded reserve word)
        _inputPosition -= 2;
        _bitBuffer = 0;
        _bitsRemaining = 0;

        if (_inputPosition + 12 > _inputEnd) return false;

        _r0 = _inputBytes[_inputPosition] | (_inputBytes[_inputPosition + 1] << 8) |
              (_inputBytes[_inputPosition + 2] << 16) | (_inputBytes[_inputPosition + 3] << 24);
        _inputPosition += 4;
        _r1 = _inputBytes[_inputPosition] | (_inputBytes[_inputPosition + 1] << 8) |
              (_inputBytes[_inputPosition + 2] << 16) | (_inputBytes[_inputPosition + 3] << 24);
        _inputPosition += 4;
        _r2 = _inputBytes[_inputPosition] | (_inputBytes[_inputPosition + 1] << 8) |
              (_inputBytes[_inputPosition + 2] << 16) | (_inputBytes[_inputPosition + 3] << 24);
        _inputPosition += 4;
        return true;
    }

    // ================================================================
    // VERBATIM BLOCK — C: DecompressVerbatim_Inner/FastPath (lines 769-1086)
    // Combined into single function with inline mirroring check.
    // ================================================================

    private int DecompressVerbatimBlock(int count)
    {
        int windowPos = _windowPosition;
        int endPos = windowPos + count;
        int windowSize = _windowSize;
        int windowMask = _windowMask;

        // Cache bitstream locally (matches C inner loops)
        uint bitBuffer = _bitBuffer;
        int bitsRemain = _bitsRemaining;
        int inputPos = _inputPosition;
        int inputEnd = _inputEnd;

        while (windowPos < endPos)
        {
            // Decode main tree symbol — peek top 10 bits (C line 793)
            int mainSymbol = _mainTreeTable[bitBuffer >> 22];

            if (mainSymbol < 0)
            {
                // Overflow walk (C line 795)
                uint mask = 0x200000;
                while (mainSymbol < 0)
                {
                    int idx;
                    if ((bitBuffer & mask) == 0)
                        idx = (-mainSymbol) * 2;
                    else
                        idx = (-mainSymbol) * 2 + 1;
                    mainSymbol = _mainTreeTable[idx];
                    mask >>= 1;
                }
            }

            if (inputPos + 1 >= inputEnd) { _windowPosition = windowPos; goto WRITEBACK_ERR; }

            // Consume bits (C line 809)
            int codeLen = _mainTreeLengths[mainSymbol];
            bitBuffer <<= (codeLen & 0x1f);
            bitsRemain -= codeLen;
            if (bitsRemain < 1)
            {
                uint w = (uint)(_inputBytes[inputPos] | (_inputBytes[inputPos + 1] << 8));
                bitBuffer |= w << ((-bitsRemain) & 0x1f);
                bitsRemain += 16;
                inputPos += 2;
            }

            if (mainSymbol < NumChars)
            {
                // Literal byte (C line 822)
                int mpos = windowPos & windowMask;
                _window[mpos] = (byte)(mainSymbol);
                if (mpos < 0x101)
                    _window[mpos + windowSize] = (byte)(mainSymbol);
                windowPos++;
            }
            else
            {
                // Match (C line 830)
                int matchCode = mainSymbol - NumChars;
                int lengthSlot = matchCode & 7;
                int positionSlot = matchCode >> 3;

                if (lengthSlot == 7)
                {
                    // Decode length tree (C line 833)
                    int lengthSym = _lengthTreeTable[bitBuffer >> 24];
                    if (lengthSym < 0)
                    {
                        uint lmask = 0x800000;
                        while (lengthSym < 0)
                        {
                            int idx = ((bitBuffer & lmask) == 0)
                                ? (-lengthSym) * 2
                                : (-lengthSym) * 2 + 1;
                            lengthSym = _lengthTreeTable[idx];
                            lmask >>= 1;
                        }
                    }
                    int lLen = _lengthTreeLengths[lengthSym];
                    bitBuffer <<= (lLen & 0x1f);
                    bitsRemain -= lLen;
                    if (bitsRemain < 1)
                    {
                        uint w = (uint)(_inputBytes[inputPos] | (_inputBytes[inputPos + 1] << 8));
                        bitBuffer |= w << ((-bitsRemain) & 0x1f);
                        bitsRemain += 16;
                        inputPos += 2;
                    }
                    lengthSlot = lengthSym + 7;
                }

                int matchLength = lengthSlot + 2;

                // Decode match offset (C line 857)
                int matchOffset;
                if (positionSlot < 3)
                {
                    matchOffset = positionSlot switch
                    {
                        0 => _r0,
                        1 => _r1,
                        _ => _r2
                    };
                    if (positionSlot != 0)
                    {
                        if (positionSlot == 1) { _r1 = _r0; }
                        else { _r2 = _r0; }
                        _r0 = matchOffset;
                    }
                }
                else
                {
                    int extraBitsCount = ExtraBits[positionSlot];
                    uint extraVal = bitBuffer >> ((32 - extraBitsCount) & 0x1f);
                    bitBuffer <<= (extraBitsCount & 0x1f);
                    bitsRemain -= extraBitsCount;
                    if (bitsRemain < 1)
                    {
                        uint w = (uint)(_inputBytes[inputPos] | (_inputBytes[inputPos + 1] << 8));
                        bitBuffer |= w << ((-bitsRemain) & 0x1f);
                        bitsRemain += 16;
                        inputPos += 2;
                        if (bitsRemain < 1)
                        {
                            w = (uint)(_inputBytes[inputPos] | (_inputBytes[inputPos + 1] << 8));
                            bitBuffer |= w << ((-bitsRemain) & 0x1f);
                            bitsRemain += 16;
                            inputPos += 2;
                        }
                    }

                    if (positionSlot == 3)
                    {
                        matchOffset = 1;
                    }
                    else
                    {
                        matchOffset = (int)extraVal + PositionBase[positionSlot];
                    }
                    _r2 = _r1;
                    _r1 = _r0;
                    _r0 = matchOffset;
                }

                // Copy match (C line 902)
                int srcPos = windowPos - matchOffset;
                for (int i = 0; i < matchLength; i++)
                {
                    int mpos = windowPos & windowMask;
                    byte b = _window[(srcPos + i) & windowMask];
                    _window[mpos] = b;
                    if (mpos < 0x101)
                        _window[mpos + windowSize] = b;
                    windowPos++;
                }
            }
        }

        // Writeback (C line 786)
        _bitBuffer = bitBuffer;
        _bitsRemaining = bitsRemain;
        _inputPosition = inputPos;
        _windowPosition = windowPos;
        return 0;

    WRITEBACK_ERR:
        _bitBuffer = bitBuffer;
        _bitsRemaining = bitsRemain;
        _inputPosition = inputPos;
        return -1;
    }

    // ================================================================
    // ALIGNED BLOCK — C: DecompressAligned_Inner/FastPath (lines 1097-1448)
    // ================================================================

    private int DecompressAlignedBlock(int count)
    {
        int windowPos = _windowPosition;
        int endPos = windowPos + count;
        int windowSize = _windowSize;
        int windowMask = _windowMask;

        uint bitBuffer = _bitBuffer;
        int bitsRemain = _bitsRemaining;
        int inputPos = _inputPosition;
        int inputEnd = _inputEnd;

        while (windowPos < endPos)
        {
            // Decode main tree symbol
            int mainSymbol = _mainTreeTable[bitBuffer >> 22];
            if (mainSymbol < 0)
            {
                uint mask = 0x200000;
                while (mainSymbol < 0)
                {
                    int idx = ((bitBuffer & mask) == 0)
                        ? (-mainSymbol) * 2
                        : (-mainSymbol) * 2 + 1;
                    mainSymbol = _mainTreeTable[idx];
                    mask >>= 1;
                }
            }

            if (inputPos + 1 >= inputEnd) { _windowPosition = windowPos; goto WRITEBACK_ERR; }

            int codeLen = _mainTreeLengths[mainSymbol];
            bitBuffer <<= (codeLen & 0x1f);
            bitsRemain -= codeLen;
            if (bitsRemain < 1)
            {
                uint w = (uint)(_inputBytes[inputPos] | (_inputBytes[inputPos + 1] << 8));
                bitBuffer |= w << ((-bitsRemain) & 0x1f);
                bitsRemain += 16;
                inputPos += 2;
            }

            if (mainSymbol < NumChars)
            {
                int mpos = windowPos & windowMask;
                _window[mpos] = (byte)(mainSymbol);
                if (mpos < 0x101)
                    _window[mpos + windowSize] = (byte)(mainSymbol);
                windowPos++;
            }
            else
            {
                int matchCode = mainSymbol - NumChars;
                int lengthSlot = matchCode & 7;
                int positionSlot = matchCode >> 3;

                if (lengthSlot == 7)
                {
                    int lengthSym = _lengthTreeTable[bitBuffer >> 24];
                    if (lengthSym < 0)
                    {
                        uint lmask = 0x800000;
                        while (lengthSym < 0)
                        {
                            int idx = ((bitBuffer & lmask) == 0)
                                ? (-lengthSym) * 2
                                : (-lengthSym) * 2 + 1;
                            lengthSym = _lengthTreeTable[idx];
                            lmask >>= 1;
                        }
                    }
                    int lLen = _lengthTreeLengths[lengthSym];
                    bitBuffer <<= (lLen & 0x1f);
                    bitsRemain -= lLen;
                    if (bitsRemain < 1)
                    {
                        uint w = (uint)(_inputBytes[inputPos] | (_inputBytes[inputPos + 1] << 8));
                        bitBuffer |= w << ((-bitsRemain) & 0x1f);
                        bitsRemain += 16;
                        inputPos += 2;
                    }
                    lengthSlot = lengthSym + 7;
                }

                int matchLength = lengthSlot + 2;

                int matchOffset;
                if (positionSlot < 3)
                {
                    matchOffset = positionSlot switch { 0 => _r0, 1 => _r1, _ => _r2 };
                    if (positionSlot != 0)
                    {
                        if (positionSlot == 1) { _r1 = _r0; }
                        else { _r2 = _r0; }
                        _r0 = matchOffset;
                    }
                }
                else
                {
                    int extraBitsCount = ExtraBits[positionSlot];

                    if (extraBitsCount >= 3)
                    {
                        // Read (extra - 3) bits from bitstream, then 3 from aligned tree
                        // C: when extraBits == 3, verbatimBits = 0 (no bitstream read)
                        int topBits = extraBitsCount - 3;
                        uint verbatimBits = 0;
                        if (topBits > 0)
                        {
                            verbatimBits = bitBuffer >> ((32 - topBits) & 0x1f);
                            bitBuffer <<= (topBits & 0x1f);
                            bitsRemain -= topBits;
                            if (bitsRemain < 1)
                            {
                                uint w = (uint)(_inputBytes[inputPos] | (_inputBytes[inputPos + 1] << 8));
                                bitBuffer |= w << ((-bitsRemain) & 0x1f);
                                bitsRemain += 16;
                                inputPos += 2;
                                if (bitsRemain < 1)
                                {
                                    w = (uint)(_inputBytes[inputPos] | (_inputBytes[inputPos + 1] << 8));
                                    bitBuffer |= w << ((-bitsRemain) & 0x1f);
                                    bitsRemain += 16;
                                    inputPos += 2;
                                }
                            }
                        }

                        // Aligned tree decode (C line 1157)
                        int alignedSym = _alignedTreeTable[bitBuffer >> 25];
                        int aLen = _alignedTreeLengths[alignedSym];
                        bitBuffer <<= (aLen & 0x1f);
                        bitsRemain -= aLen;
                        if (bitsRemain < 1)
                        {
                            uint w = (uint)(_inputBytes[inputPos] | (_inputBytes[inputPos + 1] << 8));
                            bitBuffer |= w << ((-bitsRemain) & 0x1f);
                            bitsRemain += 16;
                            inputPos += 2;
                        }

                        matchOffset = (int)(verbatimBits << 3) + alignedSym + PositionBase[positionSlot];
                    }
                    else if (extraBitsCount > 0)
                    {
                        uint val = bitBuffer >> ((32 - extraBitsCount) & 0x1f);
                        bitBuffer <<= (extraBitsCount & 0x1f);
                        bitsRemain -= extraBitsCount;
                        if (bitsRemain < 1)
                        {
                            uint w = (uint)(_inputBytes[inputPos] | (_inputBytes[inputPos + 1] << 8));
                            bitBuffer |= w << ((-bitsRemain) & 0x1f);
                            bitsRemain += 16;
                            inputPos += 2;
                        }
                        matchOffset = (int)val + PositionBase[positionSlot];
                    }
                    else
                    {
                        matchOffset = PositionBase[positionSlot];
                    }

                    _r2 = _r1;
                    _r1 = _r0;
                    _r0 = matchOffset;
                }

                int srcPos = windowPos - matchOffset;
                for (int i = 0; i < matchLength; i++)
                {
                    int mpos = windowPos & windowMask;
                    byte b = _window[(srcPos + i) & windowMask];
                    _window[mpos] = b;
                    if (mpos < 0x101)
                        _window[mpos + windowSize] = b;
                    windowPos++;
                }
            }
        }

        _bitBuffer = bitBuffer;
        _bitsRemaining = bitsRemain;
        _inputPosition = inputPos;
        _windowPosition = windowPos;
        return 0;

    WRITEBACK_ERR:
        _bitBuffer = bitBuffer;
        _bitsRemaining = bitsRemain;
        _inputPosition = inputPos;
        return -1;
    }

    // ================================================================
    // UNCOMPRESSED BLOCK — C: DecompressUncompressed (line 720)
    // ================================================================

    private int DecompressUncompressedBlock(int count)
    {
        if (_inputPosition + count > _inputEnd) return -1;

        int windowPos = _windowPosition;
        int windowSize = _windowSize;
        int windowMask = _windowMask;

        for (int i = 0; i < count; i++)
        {
            byte b = _inputBytes[_inputPosition++];
            int mpos = windowPos & windowMask;
            _window[mpos] = b;
            if (mpos < 0x101)
                _window[mpos + windowSize] = b;
            windowPos++;
        }

        // C line 755: mask window position
        _windowPosition = windowPos & windowMask;
        return 0;
    }

    // ================================================================
    // E8 TRANSLATION — C: E8Translate (0x1019a660, line 1458)
    // Sentinel-based approach matching C exactly.
    // ================================================================

    private void E8Translate(Span<byte> data, int dataLength)
    {
        if (dataLength <= 10) return;

        int curPos = _intelCurrentPosition;
        int fileSize = _intelFileSize;

        int endPos = dataLength - 10;

        // Sentinel approach: save byte at endPos, replace with 0xE8
        byte savedByte = data[endPos];
        data[endPos] = 0xE8;

        int i = 0;
        while (i < endPos)
        {
            // Scan for 0xE8
            while (data[i] != 0xE8) { i++; curPos++; }

            if (i >= endPos)
            {
                // Hit sentinel, not a real E8
                break;
            }

            // Read 32-bit LE value after E8
            int absValue = data[i + 1] | (data[i + 2] << 8) |
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

    // ================================================================
    // DISPOSE
    // ================================================================

    public void Dispose()
    {
        if (_window != null)
        {
            ArrayPool<byte>.Shared.Return(_window);
        }
    }
}
