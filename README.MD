# DDXConv
### Tool to convert New Vegas Xbox 360 .ddx textures to standard .dds files usable in the PC game

---

Current status (numbers from July 2010 proto): Converts *most* textures correctly (22148 out of 26123). Generally, if you want a texture out of the game, you can probably get it with this tool. Everything I have wanted has been supported.

Known Issues:
- Some textures appear to have issues with their mipmaps, I have made an effort to extract them properly, but an option to regenerate mips is included (`-g` or `--regen-mips`) so you can reliably get good data.
- 13 3XDO files fail to convert for one reason or another. This seems to be related to them having a low resolution in at least one axis (8px or lower)
- 3XDR format textures are not supported. This affects 3961 textures.

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
Developer Options:
  --atlas, -a          Save untiled mip atlas as separate DDS file
  --raw, -r            Save raw combined decompressed data as binary file
  --save-mips          Save extracted mip levels from atlas
  --no-untile-atlas    Do not untile/unswizzle the atlas (leave tiled)
  --no-swap            Do not perform endian swap on data
  --verbose, -v        Enable verbose output
```

Requires [XNA Framework Redistributable](https://www.microsoft.com/en-us/download/details.aspx?id=20914) and [.NET 9.0 Runtime (x86)](https://dotnet.microsoft.com/en-us/download/dotnet/9.0) to run.

---

### Credits
Untiling code based on [xenia](https://github.com/xenia-project/xenia)
<br>Decompression handled via [XCompression](https://github.com/gibbed/XCompression) (thanks MrPinball64 for the pointer!)
<br>Special thanks to everyone in the TriangleCity Discord for their accompaniment while I worked on this.

---

### Other Useful Tools for New Vegas Prototype files
- [Save Image Extractor](https://gist.github.com/kran27/291349139c769173a0f02e1eb2462975)