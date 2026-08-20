using System.Globalization;
using BCnEncoder.Decoder;
using BCnEncoder.ImageSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DDXConv;

/// <summary>
///     Per-file result of comparing a converted DDS against its PC reference: mean absolute
///     error of every mip that has a same-dimensions counterpart in the reference chain.
///     <para>
///         This is the harness's change-judgment oracle. Write-coverage counters are
///         destination-side only — a decode that reads the <em>wrong but in-range</em> source
///         blocks is counter-clean while producing garbage — so byte changes are judged by
///         comparing decoded pixels against the era-matched PC reference tree, per mip, RGB and
///         alpha separately. Comparing by identical dimensions also handles the Xbox's half-res
///         normal maps (Xbox mip 0 pairs with PC mip 1) and covers the mip chain, which a
///         mip0-only comparison is blind to.
///     </para>
/// </summary>
internal sealed class MaeComparison
{
    /// <summary>RGB MAE of the converted mip 0 (or the first measurable mip), mean per channel.</summary>
    internal double Mip0Rgb { get; set; } = -1;

    /// <summary>Worst RGB MAE across all compared mips.</summary>
    internal double WorstRgb { get; set; } = -1;

    /// <summary>Worst alpha-channel MAE across all compared mips.</summary>
    internal double WorstAlpha { get; set; } = -1;

    /// <summary>Number of mips that had a same-dimensions reference counterpart.</summary>
    internal int ComparedMips { get; set; }

    /// <summary>
    ///     Per-compared-mip RGB / alpha MAE, in converted-mip order. Baselines carry these so a
    ///     later verify can judge level-by-level over the common prefix: a worst-over-all-mips
    ///     aggregate is NOT comparable across runs when the number of decoded mips changed
    ///     (recovering a truncated chain can only raise the max), which mislabels chain
    ///     recoveries as regressions.
    /// </summary>
    internal List<double> RgbPerMip { get; } = [];

    internal List<double> AlphaPerMip { get; } = [];

    internal bool IsMeasurable => ComparedMips > 0;

    /// <summary>
    ///     Serialized manifest field, e.g. <c>m0=0.12;mw=0.31;mm=3;aw=0.05;r=0.12/0.2/0.31;a=0/0/0.05</c>.
    /// </summary>
    internal string ToManifestField()
    {
        if (!IsMeasurable)
        {
            return "mae=-";
        }

        var head = string.Create(CultureInfo.InvariantCulture,
            $"m0={Mip0Rgb:0.###};mw={WorstRgb:0.###};mm={ComparedMips};aw={WorstAlpha:0.###}");
        if (RgbPerMip.Count == 0)
        {
            return head;
        }

        var r = string.Join('/', RgbPerMip.Select(static v => v.ToString("0.###", CultureInfo.InvariantCulture)));
        var a = string.Join('/', AlphaPerMip.Select(static v => v.ToString("0.###", CultureInfo.InvariantCulture)));
        return $"{head};r={r};a={a}";
    }

    /// <summary>Parses a serialized field back; null for absent or unmeasurable entries.</summary>
    internal static MaeComparison? Parse(string? field)
    {
        if (string.IsNullOrEmpty(field) || field == "mae=-")
        {
            return null;
        }

        var result = new MaeComparison();
        foreach (var part in field.Split(';'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                return null;
            }

            var value = part[(eq + 1)..];
            switch (part[..eq])
            {
                case "m0" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var m0):
                    result.Mip0Rgb = m0;
                    break;
                case "mw" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mw):
                    result.WorstRgb = mw;
                    break;
                case "mm" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mm):
                    result.ComparedMips = mm;
                    break;
                case "aw" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var aw):
                    result.WorstAlpha = aw;
                    break;
                case "r":
                    if (!TryParseSeries(value, result.RgbPerMip))
                    {
                        return null;
                    }

                    break;
                case "a":
                    if (!TryParseSeries(value, result.AlphaPerMip))
                    {
                        return null;
                    }

                    break;
                default:
                    return null;
            }
        }

        return result.IsMeasurable ? result : null;
    }

    private static bool TryParseSeries(string value, List<double> target)
    {
        foreach (var item in value.Split('/'))
        {
            if (!double.TryParse(item, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                return false;
            }

            target.Add(v);
        }

        return true;
    }
}

internal static class MaeOracle
{
    /// <summary>
    ///     Resolves the PC reference DDS for a corpus-relative DDX path. Probes the direct
    ///     combine first, then retries without a leading <c>textures/</c> segment so both
    ///     "…\Data" and "…\Data\textures" work as reference roots.
    /// </summary>
    internal static string? ResolvePcReference(string pcRefRoot, string relativeDdxPath)
    {
        var relDds = Path.ChangeExtension(relativeDdxPath.Replace('/', Path.DirectorySeparatorChar), ".dds");

        var direct = Path.Combine(pcRefRoot, relDds);
        if (File.Exists(direct))
        {
            return direct;
        }

        const string texturesPrefix = "textures";
        if (relDds.StartsWith(texturesPrefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            var stripped = Path.Combine(pcRefRoot, relDds[(texturesPrefix.Length + 1)..]);
            if (File.Exists(stripped))
            {
                return stripped;
            }
        }

        return null;
    }

    /// <summary>
    ///     Compares a converted DDS (in memory) against a PC reference DDS on disk.
    ///     Returns null when the reference cannot be decoded at all; a result with
    ///     <see cref="MaeComparison.ComparedMips" /> == 0 when decodable but no mip pairs share
    ///     dimensions.
    /// </summary>
    internal static MaeComparison? Compare(byte[] convertedDds, string pcReferencePath)
    {
        Image<Rgba32>[] converted;
        Image<Rgba32>[] reference;
        try
        {
            converted = DecodeDds(convertedDds);
        }
        catch
        {
            return null;
        }

        try
        {
            reference = DecodeDds(File.ReadAllBytes(pcReferencePath));
        }
        catch
        {
            DisposeAll(converted);
            return null;
        }

        try
        {
            var result = new MaeComparison();
            for (var i = 0; i < converted.Length; i++)
            {
                var pcMatch = Array.Find(reference,
                    r => r.Width == converted[i].Width && r.Height == converted[i].Height);
                if (pcMatch is null)
                {
                    continue;
                }

                var (rgb, alpha) = ComputeMae(converted[i], pcMatch);
                if (result.ComparedMips == 0)
                {
                    result.Mip0Rgb = rgb;
                }

                result.WorstRgb = Math.Max(result.WorstRgb, rgb);
                result.WorstAlpha = Math.Max(result.WorstAlpha, alpha);
                result.RgbPerMip.Add(rgb);
                result.AlphaPerMip.Add(alpha);
                result.ComparedMips++;
            }

            return result;
        }
        finally
        {
            DisposeAll(converted);
            DisposeAll(reference);
        }
    }

    /// <summary>Mean absolute error per RGB channel and for alpha, over all pixels.</summary>
    internal static (double Rgb, double Alpha) ComputeMae(Image<Rgba32> a, Image<Rgba32> b)
    {
        var pixels = a.Width * a.Height;
        var bufA = new Rgba32[pixels];
        var bufB = new Rgba32[pixels];
        a.CopyPixelDataTo(bufA);
        b.CopyPixelDataTo(bufB);

        long rgbTotal = 0;
        long alphaTotal = 0;
        for (var i = 0; i < pixels; i++)
        {
            rgbTotal += Math.Abs(bufA[i].R - bufB[i].R)
                        + Math.Abs(bufA[i].G - bufB[i].G)
                        + Math.Abs(bufA[i].B - bufB[i].B);
            alphaTotal += Math.Abs(bufA[i].A - bufB[i].A);
        }

        return (rgbTotal / (pixels * 3.0), alphaTotal / (double)pixels);
    }

    /// <summary>
    ///     Decodes a DDS byte stream to its mip chain. Two real-world quirks are handled:
    ///     vanilla PC interface DDS files declare <c>dwMipMapCount = 0</c>, which BCnEncoder
    ///     rejects (patched to 1 with the mip-count flag set); and a few references are
    ///     uncompressed (no FourCC), decoded by a raw pixel-mask reader.
    /// </summary>
    internal static Image<Rgba32>[] DecodeDds(byte[] ddsBytes)
    {
        try
        {
            var images = DecodeWithBcn(ddsBytes);
            if (images.Length > 0)
            {
                return images;
            }
        }
        catch
        {
            // fall through to the quirk paths
        }

        // Quirk 1: dwMipMapCount == 0 (offset 28). Patch to 1 and set DDSD_MIPMAPCOUNT.
        if (ddsBytes.Length >= 128 && BitConverter.ToInt32(ddsBytes, 28) == 0)
        {
            var patched = (byte[])ddsBytes.Clone();
            BitConverter.GetBytes(1).CopyTo(patched, 28);
            BitConverter.GetBytes(BitConverter.ToInt32(patched, 8) | 0x20000).CopyTo(patched, 8);
            try
            {
                var images = DecodeWithBcn(patched);
                if (images.Length > 0)
                {
                    return images;
                }
            }
            catch
            {
                // fall through
            }

            ddsBytes = patched;
        }

        // Quirk 2: uncompressed DDS (DDPF_FOURCC not set) — BCnEncoder does not always take
        // these; read mip 0 straight through the pixel masks.
        var raw = DecodeRawUncompressed(ddsBytes);
        return raw is null ? [] : [raw];
    }

    private static Image<Rgba32>[] DecodeWithBcn(byte[] ddsBytes)
    {
        using var ms = new MemoryStream(ddsBytes);
        return new BcDecoder().DecodeAllMipMapsToImageRgba32(ms);
    }

    private static Image<Rgba32>? DecodeRawUncompressed(byte[] dds)
    {
        if (dds.Length < 128 || dds[0] != 'D' || dds[1] != 'D' || dds[2] != 'S')
        {
            return null;
        }

        var height = BitConverter.ToInt32(dds, 12);
        var width = BitConverter.ToInt32(dds, 16);
        var pfFlags = BitConverter.ToInt32(dds, 80);
        if ((pfFlags & 0x4) != 0 || width <= 0 || height <= 0)
        {
            return null; // DDPF_FOURCC — compressed, not ours
        }

        var bitCount = BitConverter.ToInt32(dds, 88);
        var rMask = BitConverter.ToUInt32(dds, 92);
        var gMask = BitConverter.ToUInt32(dds, 96);
        var bMask = BitConverter.ToUInt32(dds, 100);
        var aMask = BitConverter.ToUInt32(dds, 104);
        if (bitCount is not (16 or 24 or 32))
        {
            return null;
        }

        var bytesPerPixel = bitCount / 8;
        if (128 + (long)width * height * bytesPerPixel > dds.Length)
        {
            return null;
        }

        var image = new Image<Rgba32>(width, height);
        var offset = 128;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++, offset += bytesPerPixel)
            {
                uint pixel = 0;
                for (var b = 0; b < bytesPerPixel; b++)
                {
                    pixel |= (uint)dds[offset + b] << (8 * b);
                }

                image[x, y] = new Rgba32(
                    ExtractChannel(pixel, rMask),
                    ExtractChannel(pixel, gMask),
                    ExtractChannel(pixel, bMask),
                    aMask == 0 ? (byte)255 : ExtractChannel(pixel, aMask));
            }
        }

        return image;
    }

    private static byte ExtractChannel(uint pixel, uint mask)
    {
        if (mask == 0)
        {
            return 0;
        }

        while ((mask & 1) == 0)
        {
            mask >>= 1;
            pixel >>= 1;
        }

        var value = pixel & mask;

        // Scale from the mask's bit width to 8 bits (e.g. 5-bit 31 -> 255).
        return mask >= 255 ? (byte)(value & 0xFF) : (byte)(value * 255 / mask);
    }

    private static void DisposeAll(Image<Rgba32>[] images)
    {
        foreach (var image in images)
        {
            image.Dispose();
        }
    }
}
