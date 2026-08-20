namespace DDXConv;

/// <summary>
///     The facts about how a texture's mip-0 surface is stored on the Xbox 360 GPU, computed
///     once per file and used to route decoding. Alignment is a per-axis property (the GPU
///     rounds each block dimension up to a 32-block macro tile) and tail-base-0 textures
///     (min texel dim ≤ 16) share one surface for the whole mip chain with level 0 at a
///     non-origin offset — so "does mip 0 need the aligned+crop treatment" is a single
///     predicate, not a family of special cases keyed on shape.
/// </summary>
internal readonly record struct SurfaceLayout(
    int BlocksW,
    int BlocksH,
    int TiledBlocksW,
    int TiledBlocksH,
    int LinearMip0Bytes,
    int TiledMip0Bytes,
    int TailBaseLevel,
    int OriginXBlocks,
    int OriginYBlocks,
    bool IsBlockFormat)
{
    /// <summary>
    ///     True when decoding mip 0 straight at the logical dimensions would read the wrong
    ///     bytes: the stored surface is bigger than the logical one (either axis sub-tile, or
    ///     over-tile but not 32-block-aligned) or level 0 sits at the packed-tail origin.
    /// </summary>
    internal bool RequiresAlignedMip0 =>
        IsBlockFormat && (TiledMip0Bytes != LinearMip0Bytes || OriginXBlocks != 0 || OriginYBlocks != 0);

    internal static SurfaceLayout For(int width, int height, uint format)
    {
        var isBlock = format is not (0x06 or 0x04);
        var blocksW = Math.Max(1, (width + 3) / 4);
        var blocksH = Math.Max(1, (height + 3) / 4);
        var tiledBlocksW = (blocksW + 31) & ~31;
        var tiledBlocksH = (blocksH + 31) & ~31;
        var linear = TextureUtilities.CalculateMipSize(width, height, format);
        var tiled = TextureUtilities.CalculateTiledMipSize(width, height, format);

        var tailBase = isBlock ? TextureUtilities.ComputeXgTailBaseLevel(width, height) : int.MaxValue;
        var (originX, originY) = isBlock && tailBase == 0
            ? TextureUtilities.ComputeTailLevelOffset(0, width, height)
            : (0, 0);

        return new SurfaceLayout(blocksW, blocksH, tiledBlocksW, tiledBlocksH,
            linear, tiled, tailBase, originX, originY, isBlock);
    }
}
