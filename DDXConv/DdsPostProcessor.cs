using BCnEncoder.Decoder;
using BCnEncoder.Encoder;
using BCnEncoder.ImageSharp;
using BCnEncoder.Shared;
using BCnEncoder.Shared.ImageFiles;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DDXConv;

public static class DdsPostProcessor
{
    // Load BC5 normal map, process to convert from 2-channel to 3-channel normal map, load BC4 specular map, use as alpha
    // Save DXT5 format DDS to output path
    // Delete specular map after conversion
    public static void MergeNormalSpecularMaps(string bc5Path, string? bc4Path)
    {
        // Decode inputs to Image<Rgba32> using BCnEncoder's decoder helpers.
        var decoder = new BcDecoder();

        using var bc5Fs = File.OpenRead(bc5Path);
        using var normalImage = decoder.DecodeToImageRgba32(bc5Fs);

        var specImage = new Image<Rgba32>(normalImage.Width, normalImage.Height);

        if (bc4Path != null)
        {
            using var bc4Fs = File.OpenRead(bc4Path);
            specImage = decoder.DecodeToImageRgba32(bc4Fs);
            bc4Fs.Close();
        }
        else
        {
            for (var y = 0; y < normalImage.Height; y++)
            {
                for (var x = 0; x < normalImage.Width; x++)
                {
                    specImage[x, y] = new Rgba32(255, 255, 255, 255);
                }
            }
        }

        if (normalImage.Width != specImage.Width || normalImage.Height != specImage.Height)
            throw new InvalidOperationException("Input images must have same dimensions.");

        // Create combined image
        var combined = new Image<Rgba32>(normalImage.Width, normalImage.Height);

        // Iterate pixels.
        // Assumes normalImage R,G channels are the signed XY encoded as 0..255 -> -1..1
        // We'll reconstruct Z = sqrt(1 - x^2 - y^2) and map to 0..255.
        for (var y = 0; y < normalImage.Height; y++)
        {
            for (var x = 0; x < normalImage.Width; x++)
            {
                var npx = normalImage.Frames[0].PixelBuffer[x, y];
                var spx = specImage.Frames[0].PixelBuffer[x, y];

                // Convert from [0..255] to [-1..1]
                var nx = npx.R / 255f * 2f - 1f;
                var ny = npx.G / 255f * 2f - 1f;

                // Compute z (clamp small negative to 0)
                var nz2 = 1f - nx * nx - ny * ny;
                var nz = nz2 > 0f ? (float)Math.Sqrt(nz2) : 0f;

                // Remap to [0..255]
                var outR = (byte)MathF.Round((nx * 0.5f + 0.5f) * 255f);
                var outG = (byte)MathF.Round((ny * 0.5f + 0.5f) * 255f);
                var outB = (byte)MathF.Round((nz * 0.5f + 0.5f) * 255f);

                // Spec map: use red channel (or luminance). We use red here.
                var outA = spx.R;

                combined[x, y] = new Rgba32(outR, outG, outB, outA);
            }
        }

        // Encode to DXT5 / BC3 using BCnEncoder
        var encoder = new BcEncoder
        {
            OutputOptions =
            {
                GenerateMipMaps = true, // generate full mip chain
                Format = CompressionFormat.Bc3,
                FileFormat = OutputFileFormat.Dds,
                Quality = CompressionQuality.Balanced
            }
        };

        bc5Fs.Close();
        using var outFs = File.OpenWrite(bc5Path);
        encoder.EncodeToStream(combined, outFs);
        outFs.Seek(0x44, SeekOrigin.Begin);
        outFs.Write("KRAN"u8);
        outFs.Close();

        // Delete specular map
        if (bc4Path != null) File.Delete(bc4Path);
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
}
