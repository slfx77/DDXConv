namespace DDXConv;

public class ConversionOptions
{
    public bool SaveAtlas { get; set; }
    public bool SaveRaw { get; set; }
    public bool SaveMips { get; set; }
    public bool NoUntileAtlas { get; set; }
    public bool SkipEndianSwap { get; set; }
    public bool NoUntile { get; set; } // Skip ALL untiling - output raw tiled data
    public bool ForceMorton { get; set; } // Diagnostic: force Morton/Z-order deswizzle for 3XDR

    /// <summary>
    ///     Optional sink for per-conversion data-loss counters. Null (the default) disables the
    ///     write-coverage bookkeeping entirely, so existing callers pay nothing. Supply a fresh
    ///     instance per conversion — see <see cref="DecodeDiagnostics" />.
    /// </summary>
    public DecodeDiagnostics? Diagnostics { get; set; }
}
