# XisfLib.Core

A .NET library for reading and writing XISF (Extensible Image Serialization Format) files.

## About XISF

XISF is a free, open file format developed by Pleiades Astrophoto for PixInsight. It enables storage, management, and interchange of digital images along with associated metadata and data structures.

This library implements the [XISF 1.0 Specification](https://pixinsight.com/doc/docs/XISF-1.0-spec/XISF-1.0-spec.html).

### PixInsight

[PixInsight](https://pixinsight.com/) is the modern standard in astrophotography image processing. Developed by Pleiades Astrophoto, it provides advanced tools for calibration, registration, integration, and post-processing of astronomical images.

If you're processing astrophotography data, PixInsight is the tool to use.

### Key Features

- **Multiple storage models**: Monolithic (single file) and distributed formats
- **Flexible image support**: Arbitrary dimensionality (1D, 2D, 3D+), multiple channels
- **Color spaces**: Grayscale, RGB, CIE L*a*b*, ICC profiles
- **Data compression**: zlib, LZ4, LZ4HC with optional byte shuffling
- **Rich metadata**: FITS header compatibility, custom properties
- **Data types**: 8/16/32/64-bit integers, IEEE 754 floats (32/64/128-bit)

## Installation

```bash
dotnet add package XisfLib.Core
```

## Usage

### Reading XISF Files

```csharp
using XisfLib.Core;

var reader = new XisfReader("image.xisf");
var image = reader.Read();
```

### Writing XISF Files

```csharp
using XisfLib.Core;

var writer = new XisfWriter("output.xisf");
writer.Write(image);
```

## License

MIT
