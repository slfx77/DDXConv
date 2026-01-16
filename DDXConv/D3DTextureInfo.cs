namespace DDXConv;

public class D3DTextureInfo
{
    public uint Width { get; set; }
    public uint Height { get; set; }
    public uint Format { get; set; }
    public uint DataFormat { get; set; }
    public uint ActualFormat { get; set; }
    public uint MipLevels { get; set; }
    public bool Tiled { get; set; }
    public uint Endian { get; set; }
    public uint MainDataSize { get; set; }
}
