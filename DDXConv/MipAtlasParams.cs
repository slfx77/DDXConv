namespace DDXConv;

/// <summary>
///     Parameters for unpacking a mip atlas.
/// </summary>
internal readonly record struct MipAtlasParams(
    int AtlasWidth,
    int AtlasHeight,
    uint Format,
    int MainWidth,
    int MainHeight,
    string? OutputPath = null,
    bool SaveMips = false);
