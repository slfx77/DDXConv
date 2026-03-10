# DDXConv
### Tool to convert New Vegas Xbox 360 .ddx textures to standard .dds files usable in the PC game

---

Current status (numbers from July 2010 proto): Converts *most* textures correctly (22148 out of 26123). Generally, if you want a texture out of the game, you can probably get it with this tool. Everything I have wanted has been supported.

Known Issues:
- Some textures appear to have issues with their mipmaps, I have made an effort to extract them properly, but an option to regenerate mips is included (`-g` or `--regen-mips`) so you can reliably get good data.
- 13 3XDO files fail to convert for one reason or another. This seems to be related to them having a low resolution in at least one axis (8px or lower)

Pull requests and issues are welcome to address these issues or any other bugs you may find.

---

### Usage
You are expected to extract ddx files from bsa archives yourself. Support will not be added for this.
```            
Single File: DDXConv <input_file> [output_file] [options]
      Batch: DDXConv <input_directory> <output_directory> [options]

Standard Options:
  --pc-friendly, -pc   Produce PC-ready normal maps (batch conversion only!)
  --regen-mips, -g     Regenerate mip levels from top level

Memory Dump Options (for textures carved from memory dumps):
  --memory, -m         Use memory texture parser (handles memory dump layouts)
  --atlas, -a          Save full untiled atlas as separate DDS file

GUI Integration Options:
  --progress, -p       Output machine-parseable progress (batch mode only)
                       Format: [PROGRESS] STATUS <path> [error]

Developer Options:
  --raw, -r            Save raw combined decompressed data as binary file
  --save-mips          Save extracted mip levels from atlas
  --save-mip-images    Save converted DDS mip chain as PNG files
  --no-untile-atlas    Do not untile/unswizzle the atlas (leave tiled)
  --no-untile          Do not untile/unswizzle ANY data (for debugging)
  --no-swap            Do not perform endian swap on data
  --verbose, -v        Enable verbose output
```

Requires [XNA Framework Redistributable](https://www.microsoft.com/en-us/download/details.aspx?id=20914) and [.NET 10.0 Runtime (x86)](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) to run.

---

### Credits
Untiling code based on [xenia](https://github.com/xenia-project/xenia)
<br>Decompression handled via [XCompression](https://github.com/gibbed/XCompression) (thanks MrPinball64 for the pointer!)
<br>Special thanks to everyone in the TriangleCity Discord for their accompaniment while I worked on this.
<br>Thanks to [slfx77](https://github.com/slfx77) for their work cleaning up some of my code and fixing 3XDR conversion

---

### Other Tools for New Vegas Prototypes
- [Memory Dump Scanner & Nif Converter](https://github.com/slfx77/xbox-360-minidump-extractor)
- [Save Image Extractor](https://github.com/GamesPastOrg/Fallout_Save_Image_Extractor)
- [Xenia Patches](https://github.com/GamesPastOrg/FalloutForXenia)
