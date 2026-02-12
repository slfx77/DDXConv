// Bitstream reader for LZX decompression.
// Encapsulates all bit-level I/O: reading, consuming, refilling, and Huffman symbol decoding.
// Extracted from LzxDecompressor — originally C: InitBitstream (0x1019a9b0),
// RemoveBits (0x1019aa20), ReadBits (0x1019aaf0).

using System.Runtime.CompilerServices;

namespace DDXConv.Compression;

/// <summary>
///     Manages the LZX bitstream: a 32-bit buffer fed by 16-bit little-endian words.
/// </summary>
internal sealed class BitstreamReader
{
    private const int BlockTypeUncompressed = 3;
    private int _bitsRemaining; // OFF_BITS_REMAINING  0x2EB4

    private byte[] _inputBytes = [];
    private int _inputEnd; // OFF_INPUT_END       0x2B08

    /// <summary>
    ///     Set when the bitstream underflows (not enough input data).
    /// </summary>
    public bool HasError { get; set; }

    /// <summary>
    ///     Current bit buffer value. Exposed for peek operations in Huffman decode
    ///     (callers peek the top N bits to index into the decode table).
    /// </summary>
    public uint BitBuffer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get;
        private set;
    }

    /// <summary>
    ///     Current input position in the byte array.
    /// </summary>
    public int InputPosition { get; private set; }

    /// <summary>
    ///     Configures the input buffer for a new chunk.
    /// </summary>
    public void SetInput(byte[] input, int length)
    {
        _inputBytes = input;
        InputPosition = 0;
        _inputEnd = length;
    }

    /// <summary>
    ///     Resets bitstream state (called during LzxDecompressor.Reset).
    /// </summary>
    public void Reset()
    {
        BitBuffer = 0;
        _bitsRemaining = 0;
        HasError = false;
    }

    /// <summary>
    ///     Loads the initial 4-byte seed into the bit buffer.
    ///     Reads two 16-bit LE words and packs them into bitBuffer.
    ///     Skipped for uncompressed blocks.
    /// </summary>
    /// <remarks>C: InitBitstream (0x1019a9b0, line 24)</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Init(int blockType)
    {
        if (blockType != BlockTypeUncompressed && InputPosition + 3 < _inputEnd)
        {
            var word0 = (uint)(_inputBytes[InputPosition] | (_inputBytes[InputPosition + 1] << 8));
            var word1 = (uint)(_inputBytes[InputPosition + 2] | (_inputBytes[InputPosition + 3] << 8));
            InputPosition += 4;
            BitBuffer = (word0 << 16) | word1;
            _bitsRemaining = 16;
        }
    }

    /// <summary>
    ///     Consumes <paramref name="count" /> bits from the buffer and refills from input if needed.
    /// </summary>
    /// <remarks>C: RemoveBits (0x1019aa20, line 48)</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ConsumeBits(int count)
    {
        BitBuffer <<= count & 0x1f;
        _bitsRemaining -= count;

        if (_bitsRemaining < 1)
        {
            if (InputPosition + 1 >= _inputEnd)
            {
                HasError = true;
                return;
            }

            var word = (uint)(_inputBytes[InputPosition] | (_inputBytes[InputPosition + 1] << 8));
            InputPosition += 2;
            var newBitsRemaining = _bitsRemaining + 16;
            BitBuffer |= word << (-_bitsRemaining & 0x1f);
            _bitsRemaining = newBitsRemaining;

            // Double refill (C line 69)
            if (newBitsRemaining < 1)
            {
                if (InputPosition + 1 >= _inputEnd)
                {
                    HasError = true;
                    return;
                }

                word = (uint)(_inputBytes[InputPosition] | (_inputBytes[InputPosition + 1] << 8));
                InputPosition += 2;
                BitBuffer |= word << (-newBitsRemaining & 0x1f);
                _bitsRemaining = newBitsRemaining + 16;
            }
        }
    }

    /// <summary>
    ///     Reads <paramref name="count" /> bits from the buffer (peek + consume).
    /// </summary>
    /// <remarks>C: ReadBits (0x1019aaf0, line 87)</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadBits(int count)
    {
        var val = BitBuffer >> ((32 - count) & 0x1f);
        ConsumeBits(count);
        return val;
    }

    /// <summary>
    ///     Decodes a Huffman symbol from a decode table, walking the overflow tree if needed.
    ///     Consolidates 6 repeated overflow-walk patterns from the original code.
    /// </summary>
    /// <param name="table">The Huffman decode table (direct entries + overflow nodes).</param>
    /// <param name="tableBits">Number of bits for direct table lookup.</param>
    /// <returns>The decoded symbol, or -1 if the table is corrupt.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int DecodeSymbol(short[] table, int tableBits)
    {
        int symbol = table[BitBuffer >> ((32 - tableBits) & 0x1f)];

        if (symbol < 0)
        {
            // Overflow tree walk: navigate binary tree using bits below the table lookup
            var mask = 1u << ((31 - tableBits) & 0x1f);
            while (symbol < 0)
            {
                var idx = (BitBuffer & mask) == 0
                    ? -symbol * 2
                    : -symbol * 2 + 1;
                if (idx >= table.Length)
                {
                    HasError = true;
                    return -1;
                }

                symbol = table[idx];
                mask >>= 1;
            }
        }

        return symbol;
    }

    /// <summary>
    ///     Checks whether the input has at least <paramref name="count" /> bytes available.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasAvailable(int count)
    {
        return InputPosition + count <= _inputEnd;
    }

    /// <summary>
    ///     Checks whether the input has enough data for a refill (used as an end-of-stream guard).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasInputForRefill()
    {
        return InputPosition + 1 < _inputEnd;
    }

    /// <summary>
    ///     Backs up the input position by <paramref name="bytes" /> bytes.
    ///     Used when transitioning to an uncompressed block.
    /// </summary>
    public void BackUp(int bytes)
    {
        InputPosition -= bytes;
        BitBuffer = 0;
        _bitsRemaining = 0;
    }

    /// <summary>
    ///     Reads a single byte from raw input (bypassing the bitstream).
    ///     Used for uncompressed block data.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadByte()
    {
        return _inputBytes[InputPosition++];
    }

    /// <summary>
    ///     Reads a 32-bit little-endian integer from raw input (bypassing the bitstream).
    ///     Used for uncompressed block headers.
    /// </summary>
    public int ReadInt32LE()
    {
        var value = _inputBytes[InputPosition] | (_inputBytes[InputPosition + 1] << 8) |
                    (_inputBytes[InputPosition + 2] << 16) | (_inputBytes[InputPosition + 3] << 24);
        InputPosition += 4;
        return value;
    }
}
