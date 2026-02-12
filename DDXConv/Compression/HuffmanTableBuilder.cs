// Huffman decode table construction for LZX decompression.
// Extracted from LzxDecompressor — originally C: MakeDecodeTable (0x1019d5c0, line 104)
// and MakeAlignedDecodeTable (0x1019d8f0, line 275).

namespace DDXConv.Compression;

/// <summary>
///     Builds Huffman decode tables used by the LZX decompressor.
/// </summary>
internal static class HuffmanTableBuilder
{
    private const int HuffmanCompletenessCheck = 0x10000; // 2^16 code completeness

    /// <summary>
    ///     Builds a canonical Huffman decode table with overflow tree support.
    ///     Uses a cumulative-sum algorithm verified against XnaNative on 3,870 files.
    /// </summary>
    /// <remarks>C: MakeDecodeTable (0x1019d5c0, line 104)</remarks>
    public static bool BuildDecodeTable(int numSymbols, ReadOnlySpan<byte> lengths,
        int tableBits, short[] table)
    {
        var tableSize = 1 << tableBits;

        // Count code lengths (C: local_90[2..17])
        Span<int> work = stackalloc int[36];
        work.Clear();

        for (var i = 0; i < numSymbols; i++)
        {
            work[lengths[i] + 1]++;
        }

        // Cumulative code-space sum in 16-bit fixed point (C line 134)
        work[0x13] = 0;
        for (var i = 1; i <= 16; i++)
        {
            work[i + 0x13] = work[i + 1] * (1 << (16 - i)) + work[i + 0x12];
        }

        // Verify completeness (C line 150)
        var total = work[0x23]; // = work[16 + 0x13]
        if (total != HuffmanCompletenessCheck)
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
        var bitsToShift = 16 - tableBits;

        var fillIdx = 1;
        var remaining = tableBits;
        if (tableBits > 0)
        {
            while (fillIdx <= tableBits)
            {
                work[fillIdx + 0x12] >>= bitsToShift & 0x1f;
                fillIdx++;
                work[fillIdx] = 1 << ((remaining - 1) & 0x1f);
                remaining--;
            }
        }

        // For lengths tableBits+1..16: compute fill counts
        if (fillIdx <= 16)
        {
            var shift = 16 - fillIdx;
            while (fillIdx <= 16)
            {
                work[fillIdx + 1] = 1 << (shift & 0x1f);
                fillIdx++;
                shift--;
            }
        }

        // Clear unused portion of direct table (C line 190)
        var firstUnused = work[tableBits + 0x13] >> (bitsToShift & 0x1f);
        if (firstUnused < tableSize)
        {
            for (var i = firstUnused; i < tableSize; i++)
            {
                table[i] = 0;
            }
        }

        // Populate tables symbol by symbol (C line 204)
        // nextOverflow must be >= tableSize/2 so that overflow nodes (at nextOverflow*2)
        // don't overlap with the direct table (indices 0..tableSize-1).
        var nextOverflow = Math.Max(numSymbols, (tableSize + 1) / 2);

        for (var symbol = 0; symbol < numSymbols; symbol++)
        {
            int codeLength = lengths[symbol];
            if (codeLength == 0) continue;

            var code = work[codeLength + 0x12]; // current table position
            var next = code + work[codeLength + 1]; // next position

            if (codeLength > tableBits)
            {
                // Overflow tree (C line 218-238)
                work[codeLength + 0x12] = next;
                var overflowBits = codeLength - tableBits;
                var directIdx = code >> (bitsToShift & 0x1f);
                var codeAccumulator = code << (tableBits & 0x1f);

                var nodeRef = directIdx;

                for (var b = 0; b < overflowBits; b++)
                {
                    // If empty slot, allocate a new overflow node pair
                    if (table[nodeRef] == 0)
                    {
                        if (nextOverflow * 2 + 1 >= table.Length) return false;
                        table[nextOverflow * 2] = 0;
                        table[nextOverflow * 2 + 1] = 0;
                        table[nodeRef] = (short)-nextOverflow;
                        nextOverflow++;
                    }

                    // Navigate: go to overflow node and select child by bit
                    var nodeIdx = -table[nodeRef];
                    var bit = (short)codeAccumulator < 0 ? 1 : 0;
                    nodeRef = nodeIdx * 2 + bit;
                    codeAccumulator <<= 1;
                }

                // Store symbol at leaf (C line 238)
                table[nodeRef] = (short)symbol;
            }
            else
            {
                // Direct table: fill entries (C line 240)
                if (next > tableSize) return false;
                for (var i = code; i < next; i++)
                {
                    table[i] = (short)symbol;
                }

                work[codeLength + 0x12] = next;
            }
        }

        return true;
    }

    /// <summary>
    ///     Builds the aligned offset decode table (simpler canonical Huffman, max 7 bits).
    /// </summary>
    /// <remarks>C: MakeAlignedDecodeTable (0x1019d8f0, line 275)</remarks>
    public static void BuildAlignedDecodeTable(ReadOnlySpan<byte> lengths,
        byte[] table, int tableBits, int numElements)
    {
        var tableSize = 1 << tableBits;
        Array.Clear(table, 0, tableSize);

        Span<int> counts = stackalloc int[8];
        counts.Clear();
        for (var i = 0; i < numElements; i++)
        {
            if (lengths[i] <= 7) counts[lengths[i]]++;
        }

        Span<int> nextCode = stackalloc int[8];
        var code = 0;
        counts[0] = 0;
        for (var bits = 1; bits <= 7; bits++)
        {
            code = (code + counts[bits - 1]) << 1;
            nextCode[bits] = code;
        }

        for (var symbol = 0; symbol < numElements; symbol++)
        {
            int codeLength = lengths[symbol];
            if (codeLength == 0) continue;
            var currentCode = nextCode[codeLength]++;
            var baseIdx = currentCode << (tableBits - codeLength);
            var fill = 1 << (tableBits - codeLength);
            for (var i = 0; i < fill; i++)
            {
                table[baseIdx + i] = (byte)symbol;
            }
        }
    }
}
