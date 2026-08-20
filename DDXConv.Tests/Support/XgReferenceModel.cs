namespace DDXConv.Tests.Support;

/// <summary>
///     Independent reference model of the Xbox 360 XGraphics tiled-surface addressing,
///     transcribed from the decompiled Xenon runtime in
///     <c>tools/GhidraProject/texture_upload_decompiled_xenon.txt</c> — NOT from
///     <c>TextureUtilities</c>. The production code is tested against this model so a shared
///     bug cannot self-certify; two independent spellings of the offset function are kept and
///     cross-checked against each other for the same reason.
///     <para>Decompile provenance: <c>XGAddress2DTiledOffset</c> lines 11475–11489,
///     <c>XGRAPHICS::Get2DTiledExtents</c> per-point body lines 11592–11598,
///     <c>XGGetMipTailBaseLevel</c> lines 11341–11358 (ceil-log2),
///     <c>D3D__GetMipTailLevelOffsetCoords</c> disassembly lines 10240–10323,
///     <c>D3D__AlignTextureDimensions</c> lines 10189–10228.</para>
/// </summary>
internal static class XgReferenceModel
{
    internal static int CeilLog2(int v)
    {
        return v <= 1 ? 0 : 32 - int.LeadingZeroCount(v - 1);
    }

    private static int FloorLog2(int v)
    {
        return 31 - int.LeadingZeroCount(v);
    }

    /// <summary>
    ///     <c>XGAddress2DTiledOffset(x, y, pitch, texelPitch)</c>, decompile 11475–11489.
    ///     Returns the ELEMENT offset (multiply by the element size for bytes). x/y are element
    ///     coordinates (compressed-block coordinates for BC formats), pitch is in elements.
    /// </summary>
    internal static int TiledOffset(int x, int y, int pitch, int elementBytes)
    {
        var k = FloorLog2(elementBytes);
        var micro = (((y & 6) * 4) + (x & 7)) << k;
        var off = ((((pitch + 0x1F) >> 5) * (y >> 5) + (x >> 5)) << (k + 7))
                  + (((y & 1) * 8 + (micro & 0xFFFFFF0)) * 2)
                  + ((y & 8) << (k + 3))
                  + (micro & 0xF);
        var res = ((((y & 0x10) * 0x10 + (off & 0x7FFFE00)) * 2 + (off & 0x1C0)) * 4)
                  + ((((y & 0x7FFFFF8) * 2 + x) & 0x18) * 8)
                  + (off & 0x3F);
        return res >> k;
    }

    /// <summary>
    ///     The per-point body of <c>XGRAPHICS::Get2DTiledExtents</c>, decompile 11592–11598 —
    ///     an independent spelling of <see cref="TiledOffset" /> used to cross-check the
    ///     transcription itself.
    /// </summary>
    internal static int TiledOffsetExtentsForm(int x, int y, int pitch, int elementBytes)
    {
        var k = FloorLog2(elementBytes);
        var u4 = ((x & 7) + (y & 6) * 4) << k;
        u4 = (((x >> 5) + (y >> 5) * ((pitch + 0x1F) >> 5)) << (k + 7))
             + ((u4 & 0x7FFFFF0) * 2) + (u4 & 0xF)
             + ((y & 8) << (k + 3)) + ((y & 1) * 0x10);
        u4 = (((u4 & 0x7FFFE00) * 2 + (u4 & 0x1C0)) * 4)
             + ((((x & 0x7FFFFF8) + (((y >> 2) & 2) * 8)) & 0x18) * 8)
             + (u4 & 0x3F)
             + ((y & 0x10) * 0x80);
        return u4 >> k;
    }

    /// <summary>
    ///     <c>XGGetMipTailBaseLevel</c> (decompile 11341–11358, border = 0): min of the CEIL
    ///     log2 of each dimension, minus 4, clamped at 0. Note ceil — the production bug this
    ///     guards against was floor, which diverges for non-power-of-two dims 17–31.
    /// </summary>
    internal static int MipTailBaseLevel(int width, int height)
    {
        var m = Math.Min(CeilLog2(width), CeilLog2(height));
        return Math.Max(0, m - 4);
    }

    /// <summary>
    ///     <c>D3D__GetMipTailLevelOffsetCoords</c> (disassembly 10240–10323), returned in
    ///     4×4-block coordinates for BC formats. <paramref name="tailWidth" /> /
    ///     <paramref name="tailHeight" /> are the (pow2-rounded) dims of the tail's base level.
    /// </summary>
    internal static (int XBlocks, int YBlocks) TailLevelOffsetBlocks(int level, int tailWidth, int tailHeight)
    {
        var wide = CeilLog2(tailHeight) < CeilLog2(tailWidth);
        int offX = 0, offY = 0;
        if (level < 3)
        {
            var v = 16 >> level;
            if (wide)
            {
                offY = v;
            }
            else
            {
                offX = v;
            }
        }
        else
        {
            var wp = 1 << CeilLog2(tailWidth);
            var hp = 1 << CeilLog2(tailHeight);
            if (wide)
            {
                offX = wp >> (level - 2);
            }
            else
            {
                offY = hp >> (level - 2);
            }
        }

        return (offX / 4, offY / 4);
    }

    /// <summary>
    ///     Where level 0 of a 2D BC texture lives inside its stored surface, per
    ///     <c>XGRAPHICS::GetMipTailLevelOffsetCoords</c> (11760–11799) +
    ///     <c>D3D__AlignTextureDimensions</c> (10189–10228): when the tail base level is 0 the
    ///     whole chain shares one surface whose dims are the POW2-ROUNDED tail dims (then
    ///     tile-aligned), and level 0 sits at the tail offset; otherwise level 0 is at the
    ///     origin of its own aligned surface.
    /// </summary>
    internal static (int OriginX, int OriginY, int PitchBlocks, int HeightBlocks) Level0Surface(
        int width, int height, int blockBytes)
    {
        // W alignment: max(32, 256/bytesPerBlock) blocks = 32 for both BC block sizes.
        const int wAlignBlocks = 32;
        if (MipTailBaseLevel(width, height) == 0)
        {
            var tw = 1 << CeilLog2(width);
            var th = 1 << CeilLog2(height);
            var (ox, oy) = TailLevelOffsetBlocks(0, tw, th);
            var pitch = (Math.Max(1, tw / 4) + wAlignBlocks - 1) / wAlignBlocks * wAlignBlocks;
            var rows = (Math.Max(1, th / 4) + 31) / 32 * 32;
            return (ox, oy, pitch, rows);
        }

        var pitchBlocks = (Math.Max(1, (width + 3) / 4) + wAlignBlocks - 1) / wAlignBlocks * wAlignBlocks;
        var heightBlocks = (Math.Max(1, (height + 3) / 4) + 31) / 32 * 32;
        return (0, 0, pitchBlocks, heightBlocks);
    }

    /// <summary>
    ///     End-to-end reference answer for level-0 decode: the tiled ELEMENT offset that logical
    ///     block (bx, by) of a width×height BC texture must be read from.
    /// </summary>
    internal static int Level0SourceElement(int bx, int by, int width, int height, int blockBytes)
    {
        var (ox, oy, pitch, _) = Level0Surface(width, height, blockBytes);
        return TiledOffset(ox + bx, oy + by, pitch, blockBytes);
    }
}
