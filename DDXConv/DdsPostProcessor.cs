using BCnEncoder.Decoder;
using BCnEncoder.Encoder;
using BCnEncoder.ImageSharp;
using BCnEncoder.Shared;
using BCnEncoder.Shared.ImageFiles;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace DDXConv;

public static class DdsPostProcessor
{
    // Load BC5 normal map, process to convert from 2-channel to 3-channel normal map, load BC4 specular map, use as alpha
    // Save DXT5 format DDS to output path (DXT1 when no usable specular companion exists)
    // Delete specular map after conversion
    public static void MergeNormalSpecularMaps(string bc5Path, string? bc4Path)
    {
        var bc5Bytes = File.ReadAllBytes(bc5Path);
        var bc4Bytes = bc4Path != null ? File.ReadAllBytes(bc4Path) : null;

        var merged = MergeNormalSpecularMapsFromMemory(bc5Bytes, bc4Bytes);
        File.WriteAllBytes(bc5Path, merged);

        // Delete specular map
        if (bc4Path != null) File.Delete(bc4Path);
    }

    /// <summary>
    ///     Re-encodes a BC5/ATI2 normal map into the form vanilla Fallout NV loads — the engine
    ///     does not accept BC5 normal maps and renders whatever stale memory occupies the slot.
    ///     RG (signed XY) pass through unchanged and Blue is SATURATED (255), matching the
    ///     vanilla corpus (B ≈ 255 regardless of slope; a sqrt(1-x²-y²) reconstruction sags
    ///     toward 128 on steep texels and renders as dark quads along crevices).
    ///     <para>
    ///         With a usable BC4 specular companion, the output is DXT5/BC3 with the companion's
    ///         red channel in alpha — FNV reads the per-texel specular mask from normal-map alpha.
    ///         With NO usable companion the output is DXT1/BC1 with no alpha channel at all: that
    ///         is vanilla's encoding for "this material has no specular" (397/400 of the same
    ///         no-companion textures ship as DXT1 on PC), and the engine treats an alpha-less
    ///         normal map as specular-off even when the shape's SF_Specular flag is set. The old
    ///         neutral-gray-128 alpha fallback turned "no specular" into "50% gloss everywhere"
    ///         (the Doc Mitchell shiny-outfit bug).
    ///     </para>
    /// </summary>
    public static byte[] MergeNormalSpecularMapsFromMemory(byte[] bc5Bytes, byte[]? bc4Bytes)
    {
        ArgumentNullException.ThrowIfNull(bc5Bytes);

        var decoder = new BcDecoder();

        using var bc5Stream = new MemoryStream(bc5Bytes);
        using var normalImage = decoder.DecodeToImageRgba32(bc5Stream);

        Image<Rgba32>? specImage = null;
        if (bc4Bytes is not null)
        {
            using var bc4Stream = new MemoryStream(bc4Bytes);
            specImage = decoder.DecodeToImageRgba32(bc4Stream);

            // A specular companion of a different resolution than the normal map can't be
            // sampled per-texel, so it's unusable. This happens when the companion lookup
            // lands on a sibling that isn't the real `_s` map (e.g. the diffuse). Discard it
            // and fall through to the no-specular DXT1 encoding rather than failing the whole
            // conversion — a failed merge would drop the normal map to its un-loadable
            // BC5/ATI2 original, which is exactly the texture-swap bug this merge prevents.
            if (specImage.Width != normalImage.Width || specImage.Height != normalImage.Height)
            {
                specImage.Dispose();
                specImage = null;
            }
        }

        try
        {
            using var combined = new Image<Rgba32>(normalImage.Width, normalImage.Height);
            for (var y = 0; y < normalImage.Height; y++)
            {
                for (var x = 0; x < normalImage.Width; x++)
                {
                    var npx = normalImage.Frames[0].PixelBuffer[x, y];
                    var alpha = specImage is null ? (byte)255 : specImage.Frames[0].PixelBuffer[x, y].R;

                    combined[x, y] = new Rgba32(npx.R, npx.G, 255, alpha);
                }
            }

            var encoder = new BcEncoder
            {
                OutputOptions =
                {
                    GenerateMipMaps = true,
                    // No usable specular companion => DXT1 (no alpha channel), vanilla's
                    // "no specular" spelling. With a companion => DXT5, spec mask in alpha.
                    Format = specImage is null ? CompressionFormat.Bc1 : CompressionFormat.Bc3,
                    FileFormat = OutputFileFormat.Dds,
                    Quality = CompressionQuality.Balanced
                }
            };

            using var outStream = new MemoryStream();
            encoder.EncodeToStream(combined, outStream);

            var bytes = outStream.ToArray();
            // KRAN marker at 0x44 — downstream code that sniffs for the marker
            // (DdsTextureDecoder, etc.) identifies packer-converted textures by it.
            if (bytes.Length >= 0x48)
            {
                bytes[0x44] = (byte)'K';
                bytes[0x45] = (byte)'R';
                bytes[0x46] = (byte)'A';
                bytes[0x47] = (byte)'N';
            }

            return bytes;
        }
        finally
        {
            specImage?.Dispose();
        }
    }

    private static CompressionFormat GetCompressionFromPixelFormat(uint pf)
    {
        if (pf == DdsPixelFormat.Dxt1) return CompressionFormat.Bc1;

        if (pf == DdsPixelFormat.Dxt3) return CompressionFormat.Bc2;

        if (pf == DdsPixelFormat.Dxt5) return CompressionFormat.Bc3;

        if (pf == DdsPixelFormat.Ati1) return CompressionFormat.Bc4;

        if (pf == DdsPixelFormat.Ati2) return CompressionFormat.Bc5;

        throw new NotSupportedException("Unsupported pixel format: " + pf);
    }

    public static void RegenerateMips(string ddsPath)
    {
        var decoder = new BcDecoder();
        using var fs = File.OpenRead(ddsPath);
        var dds = DdsFile.Load(fs);
        using var image = decoder.DecodeToImageRgba32(dds);
        fs.Close();

        var encoder = new BcEncoder
        {
            OutputOptions =
            {
                GenerateMipMaps = dds.header.dwMipMapCount > 1,
                Format = GetCompressionFromPixelFormat(dds.header.ddsPixelFormat.dwFourCc),
                FileFormat = OutputFileFormat.Dds,
                Quality = CompressionQuality.BestQuality
            }
        };

        // Encode to a temporary file then replace the original to avoid corrupting the file on error.
        var tmpPath = ddsPath + ".regen.tmp";
        using (var outFs = File.Create(tmpPath))
        {
            encoder.EncodeToStream(image, outFs);
            outFs.Seek(0x44, SeekOrigin.Begin);
            outFs.Write("KRAN"u8);
        }

        File.Delete(ddsPath);
        File.Move(tmpPath, ddsPath);
    }

    public static string[] ExportMipImages(string ddsPath)
    {
        var decoder = new BcDecoder();
        using var fs = File.OpenRead(ddsPath);
        var mipImages = decoder.DecodeAllMipMapsToImageRgba32(fs);
        var outputPaths = new string[mipImages.Length];
        var directory = Path.GetDirectoryName(ddsPath) ?? Directory.GetCurrentDirectory();
        var baseName = Path.GetFileNameWithoutExtension(ddsPath);

        try
        {
            for (var i = 0; i < mipImages.Length; i++)
            {
                var outputPath = Path.Combine(directory, $"{baseName}_mip{i}.png");
                mipImages[i].SaveAsPng(outputPath, new PngEncoder());
                outputPaths[i] = outputPath;
            }
        }
        finally
        {
            foreach (var mipImage in mipImages)
            {
                mipImage.Dispose();
            }
        }

        return outputPaths;
    }
}
