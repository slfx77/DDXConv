using DDXConv.Tests.Support;
using Xunit;

namespace DDXConv.Tests;

/// <summary>
///     A 3XDR conversion untiles mip 0 from the first mip-0-size bytes of its decompressed stream and drops the
///     rest. <see cref="DecodeDiagnostics.DroppedTrailingBytes" /> counts that tail and
///     <see cref="DecodeDiagnostics.DroppedTrailingDataBlocks" /> the blocks in it that carry data, so zero
///     padding and dropped stored levels are told apart (the rule the 2026-09-23 DDX measurement's oracle used:
///     712 Final 3XDR entries with non-zero blocks after mip 0, 154 with none).
/// </summary>
public sealed class DecodeDiagnosticsDroppedTrailingTests
{
    private const int Size = 64;
    private const int Mip0Bytes = Size / 4 * (Size / 4) * 8; // DXT1: 256 blocks of 8 bytes

    /// <summary>Only the tail after mip 0 is counted; zero blocks in it are padding, not dropped data.</summary>
    /// <param name="tailBytes">Bytes appended after mip 0.</param>
    /// <param name="dataBlocks">Leading tail blocks made non-zero.</param>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(768, 0)]
    [InlineData(768, 32)]
    [InlineData(768, 96)]
    public void ThreeXdrTailIsCountedAndItsDataBlocksSeparated(int tailBytes, int dataBlocks)
    {
        var diagnostics = Convert(Build3Xdr(tailBytes, dataBlocks));

        Assert.Equal(tailBytes, diagnostics.DroppedTrailingBytes);
        Assert.Equal(dataBlocks, diagnostics.DroppedTrailingDataBlocks);
        Assert.True(diagnostics.IsLossless);
        Assert.EndsWith($",r={tailBytes},k={dataBlocks},a=0,f=0", diagnostics.ToManifestField(),
            StringComparison.Ordinal);
    }

    /// <summary>The same payload framed as 3XDO decodes through the chunk routes, which never count a 3XDR tail.</summary>
    [Fact]
    public void ThreeXdoNeverCountsATrailingDrop()
    {
        var file = Build3Xdr(768, 32);
        file[3] = (byte)'O';

        var diagnostics = Convert(file);

        Assert.Equal(0, diagnostics.DroppedTrailingBytes);
        Assert.Equal(0, diagnostics.DroppedTrailingDataBlocks);
    }

    /// <summary>A partial last block still counts when it holds a non-zero byte, and a kept prefix covering the
    /// whole buffer records nothing.</summary>
    [Fact]
    public void PartialLastBlockAndFullyKeptBufferAreHandled()
    {
        var decoded = new byte[20];
        decoded[19] = 1;
        var diagnostics = new DecodeDiagnostics();

        diagnostics.RecordDroppedTrailing(decoded, 8, 8);
        diagnostics.RecordDroppedTrailing(decoded, 20, 8);

        Assert.Equal(12, diagnostics.DroppedTrailingBytes);
        Assert.Equal(1, diagnostics.DroppedTrailingDataBlocks);
    }

    private static DecodeDiagnostics Convert(byte[] file)
    {
        var diagnostics = new DecodeDiagnostics();
        var dds = new DdxParser().ConvertDdxToDds(file, new ConversionOptions { Diagnostics = diagnostics });
        Assert.NotEmpty(dds);
        return diagnostics;
    }

    /// <summary>A 64×64 DXT1 3XDR whose stream is a stamped mip 0 followed by a tail of which the first
    /// <paramref name="dataBlocks" /> blocks are non-zero.</summary>
    private static byte[] Build3Xdr(int tailBytes, int dataBlocks)
    {
        var payload = new byte[Mip0Bytes + tailBytes];
        SyntheticDdx.IndexStampedBlocks(Mip0Bytes / 8, 8).CopyTo(payload, 0);
        for (var block = 0; block < dataBlocks; block++)
        {
            payload[Mip0Bytes + block * 8] = (byte)(block + 1);
        }

        var file = SyntheticDdx.Build3Xdo(Size, Size, SyntheticDdx.Dxt1, payload);
        file[3] = (byte)'R';
        return file;
    }
}
