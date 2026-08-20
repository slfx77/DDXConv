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

    /// <summary>True when this conversion satisfies the untile-is-a-permutation invariant.</summary>
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
               $"p={PaddedBytes},t={TruncatedReads},n={SurfaceBlockTotal}";
    }
}
