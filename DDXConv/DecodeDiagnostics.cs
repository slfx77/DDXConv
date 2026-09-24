namespace DDXConv;

/// <summary>
///     Per-conversion tally of everything the decoder silently dropped.
///     <para>
///         Untiling is a permutation: every source block in the surface must land in exactly one
///         destination block, and every destination block must be written exactly once. When that
///         invariant breaks the decoder produces a texture anyway — out-of-range source offsets
///         leave the destination zero-filled (a pure-black DXT block), and a mis-scaled row stride
///         makes two source blocks alias onto the same destination. Neither raised so much as a
///         log line, so ~4,700 of 22,616 converted textures shipped with black rectangles in them
///         and the golden-hash harness recorded every one as stable.
///     </para>
///     <para>
///         Instances are per-conversion by design. Both <see cref="DdxVerifyHarness" /> and the
///         batch CLI decode under <c>Parallel.ForEach</c>, so a static or thread-static counter in
///         a data-loss detector would itself lose data.
///     </para>
/// </summary>
public sealed class DecodeDiagnostics
{
    /// <summary>Block copies skipped because the source offset fell outside the decoded buffer.</summary>
    public int SkippedBlockCopies { get; private set; }

    /// <summary>Destination block slots no source block ever wrote — these decode to black.</summary>
    public int UnwrittenDestinationBlocks { get; private set; }

    /// <summary>
    ///     Destination block slots written more than once. Signals an aliasing bug (typically a
    ///     truncated row stride), which produces plausible-looking but wrong pixels rather than
    ///     black ones — invisible to any zero-block check.
    /// </summary>
    public int DuplicateDestinationWrites { get; private set; }

    /// <summary>Total destination block slots across every untiled surface. The denominator.</summary>
    public long SurfaceBlockTotal { get; private set; }

    /// <summary>Bytes of zero padding appended to a short surface to reach its declared size.</summary>
    public long PaddedBytes { get; private set; }

    /// <summary>Decode paths that stopped early (short decompress, exhausted chunk, atlas overrun).</summary>
    public int TruncatedReads { get; private set; }

    /// <summary>Reasons recorded alongside <see cref="TruncatedReads" />, for attribution.</summary>
    public List<string> TruncationReasons { get; } = [];

    /// <summary>
    ///     Decompressed 3XDR bytes after mip 0 that the conversion never untiles, zero padding included.
    /// </summary>
    public long DroppedTrailingBytes { get; private set; }

    /// <summary>
    ///     Blocks among <see cref="DroppedTrailingBytes" /> holding any non-zero byte: stored data the
    ///     conversion drops, typically the levels after mip 0. Zero when the tail is only padding.
    /// </summary>
    public int DroppedTrailingDataBlocks { get; private set; }

    /// <summary>
    ///     Mip atlases that could not be split into levels and were emitted whole, at the atlas's own
    ///     dimensions instead of the header's, as a single level.
    /// </summary>
    public int FullAtlasFallbacks { get; private set; }

    /// <summary>
    ///     Blocks copied into a short mip tail from atlas blocks no level claimed. Their placement is
    ///     inferred to reach the declared chain length, not read from the atlas layout.
    /// </summary>
    public int FilledMipTailBlocks { get; private set; }

    /// <summary>True when this conversion satisfies the untile-is-a-permutation invariant.</summary>
    /// <remarks>
    ///     Deliberately limited to that invariant. <see cref="DroppedTrailingDataBlocks" />,
    ///     <see cref="FullAtlasFallbacks" /> and <see cref="FilledMipTailBlocks" /> describe losses or
    ///     inferences outside it and are read separately by a caller's own acceptance gate.
    /// </remarks>
    public bool IsLossless =>
        SkippedBlockCopies == 0 && UnwrittenDestinationBlocks == 0 && DuplicateDestinationWrites == 0;

    public void RecordSkippedBlockCopy()
    {
        SkippedBlockCopies++;
    }

    public void RecordPadding(long bytes)
    {
        if (bytes > 0)
        {
            PaddedBytes += bytes;
        }
    }

    /// <summary>
    ///     Records the bytes of <paramref name="decoded" /> after its first <paramref name="keptBytes" />, and
    ///     how many <paramref name="blockSize" />-byte blocks among them are not all zero.
    /// </summary>
    public void RecordDroppedTrailing(ReadOnlySpan<byte> decoded, int keptBytes, int blockSize)
    {
        if (keptBytes < 0 || keptBytes >= decoded.Length || blockSize <= 0)
        {
            return;
        }

        var tail = decoded[keptBytes..];
        DroppedTrailingBytes += tail.Length;
        for (var offset = 0; offset < tail.Length; offset += blockSize)
        {
            if (tail.Slice(offset, Math.Min(blockSize, tail.Length - offset)).ContainsAnyExcept((byte)0))
            {
                DroppedTrailingDataBlocks++;
            }
        }
    }

    public void RecordFullAtlasFallback()
    {
        FullAtlasFallbacks++;
    }

    public void RecordFilledMipTailBlock()
    {
        FilledMipTailBlocks++;
    }

    public void RecordTruncatedRead(string reason)
    {
        TruncatedReads++;
        if (TruncationReasons.Count < 16)
        {
            TruncationReasons.Add(reason);
        }
    }

    /// <summary>
    ///     Folds one untiled surface's write-coverage map into the running totals.
    ///     <paramref name="writeCounts" /> holds, per destination block slot, how many source
    ///     blocks were copied into it.
    /// </summary>
    public void RecordSurfaceCoverage(ReadOnlySpan<byte> writeCounts)
    {
        SurfaceBlockTotal += writeCounts.Length;
        foreach (var count in writeCounts)
        {
            if (count == 0)
            {
                UnwrittenDestinationBlocks++;
            }
            else if (count > 1)
            {
                DuplicateDestinationWrites += count - 1;
            }
        }
    }

    /// <summary>Compact single-line form for the verify manifest's third field.</summary>
    public string ToManifestField()
    {
        return $"s={SkippedBlockCopies},u={UnwrittenDestinationBlocks},d={DuplicateDestinationWrites}," +
               $"p={PaddedBytes},t={TruncatedReads},n={SurfaceBlockTotal}," +
               $"r={DroppedTrailingBytes},k={DroppedTrailingDataBlocks},a={FullAtlasFallbacks},f={FilledMipTailBlocks}";
    }
}
