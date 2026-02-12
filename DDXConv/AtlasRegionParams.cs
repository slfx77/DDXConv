namespace DDXConv;

/// <summary>
///     Parameters for extracting a region from an atlas.
/// </summary>
internal readonly record struct AtlasRegionParams(
    int AtlasWidth,
    int AtlasHeight,
    int RegionX,
    int RegionY,
    int RegionWidth,
    int RegionHeight,
    uint Format);
