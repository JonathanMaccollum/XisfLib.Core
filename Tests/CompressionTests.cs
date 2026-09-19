// Wave-1 expectation ledger for CompressionTests (contract items 11, 13, 14; assessment §A.6, §B).
//
// EXPECTED-RED
//   Emit_NonShuffledCodec_CompressionAttributeOmitsItemSize      - grammar gap: FormatCompression emits
//                                                                  ":itemSize" for every codec; §10.6.3
//                                                                  grammar is "zlib:uncompressed-size".
//   Parse_SubblocksAttribute_PopulatesCompressionSubblocks       - contract 11 / assessment §A.6: the
//                                                                  subblocks attribute is never parsed.
//   Read_SubblockedShuffledBlock_ConcatenatesBeforeUnshuffling   - contract 11: decompress each subblock,
//                                                                  concatenate, THEN unshuffle (§10.6).
//
// EXPECTED-GREEN (must not regress)
//   RoundTrip_EveryCodec_PreservesPixelData                      - all eight codecs already wired.
//   RoundTrip_ShuffledCodecs_EveryItemSize_PreservesPixelData
//   Write_ShuffledCodec_StoredBytesMatchSpecShuffleLayout        - §10.6.2 shuffle layout, known pattern.
//   Read_ShuffledBlock_LengthMultipleOfItemSize_RecoversPayload
//   Read_ShuffledBlock_WithTail_LeavesTrailingBytesUnshuffled    - contract 14 is verify-only; the
//                                                                  tail branch already looks correct.
//   Emit_ShuffledCodec_CompressionAttributeCarriesItemSize
//   Parse_CompressionAttribute_WithoutItemSize_Reads
//   Write_SingleSubblock_EmitsNoSubblocksAttribute
//   Zstd_StoredBytes_AreZstandardFrame                           - §10.6.9 frame format.
//   Lz4_StoredBytes_AreBlockFormat_NotFrameFormat                - §10.6.5 block format, no frame magic.

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Xunit;

namespace XisfLib.Core.Tests;

public sealed class CompressionTests
{
    private const int BlockBase = 4096;

    [Theory]
    [InlineData(XisfCompressionCodec.Zlib)]
    [InlineData(XisfCompressionCodec.ZlibSh)]
    [InlineData(XisfCompressionCodec.LZ4)]
    [InlineData(XisfCompressionCodec.LZ4Sh)]
    [InlineData(XisfCompressionCodec.LZ4HC)]
    [InlineData(XisfCompressionCodec.LZ4HCSh)]
    [InlineData(XisfCompressionCodec.Zstd)]
    [InlineData(XisfCompressionCodec.ZstdSh)]
    public async Task RoundTrip_EveryCodec_PreservesPixelData(XisfCompressionCodec codec)
    {
        var pixels = Pattern(512);
        var unit = Unit(GrayImage(XisfSampleFormat.UInt16, pixels));

        var read = await TestKit.RoundTrip(unit, new XisfWriterOptions(DefaultCompression: codec));

        Assert.Equal(pixels, Pixels(read.Images[0]));
    }

    [Theory]
    [InlineData(XisfCompressionCodec.ZlibSh, XisfSampleFormat.UInt16)]
    [InlineData(XisfCompressionCodec.ZlibSh, XisfSampleFormat.Float32)]
    [InlineData(XisfCompressionCodec.ZlibSh, XisfSampleFormat.Float64)]
    [InlineData(XisfCompressionCodec.LZ4Sh, XisfSampleFormat.UInt16)]
    [InlineData(XisfCompressionCodec.LZ4Sh, XisfSampleFormat.Float32)]
    [InlineData(XisfCompressionCodec.LZ4Sh, XisfSampleFormat.Float64)]
    [InlineData(XisfCompressionCodec.LZ4HCSh, XisfSampleFormat.UInt16)]
    [InlineData(XisfCompressionCodec.LZ4HCSh, XisfSampleFormat.Float32)]
    [InlineData(XisfCompressionCodec.LZ4HCSh, XisfSampleFormat.Float64)]
    [InlineData(XisfCompressionCodec.ZstdSh, XisfSampleFormat.UInt16)]
    [InlineData(XisfCompressionCodec.ZstdSh, XisfSampleFormat.Float32)]
    [InlineData(XisfCompressionCodec.ZstdSh, XisfSampleFormat.Float64)]
    public async Task RoundTrip_ShuffledCodecs_EveryItemSize_PreservesPixelData(
        XisfCompressionCodec codec, XisfSampleFormat format)
    {
        var pixels = Pattern(ItemSize(format) * 64);
        var unit = Unit(GrayImage(format, pixels));

        var read = await TestKit.RoundTrip(unit, new XisfWriterOptions(DefaultCompression: codec));

        Assert.Equal(pixels, Pixels(read.Images[0]));
    }

    [Theory]
    [InlineData(XisfSampleFormat.UInt16, 2)]
    [InlineData(XisfSampleFormat.Float32, 4)]
    [InlineData(XisfSampleFormat.Float64, 8)]
    public async Task Write_ShuffledCodec_StoredBytesMatchSpecShuffleLayout(
        XisfSampleFormat format, int itemSize)
    {
        var pixels = Sequential(itemSize * 8);
        var file = await WriteBytes(Unit(GrayImage(format, pixels)),
            new XisfWriterOptions(DefaultCompression: XisfCompressionCodec.ZlibSh));

        var stored = Attachment(file);

        Assert.Equal(Shuffle(pixels, itemSize), InflateZlib(stored, pixels.Length));
    }

    [Theory]
    [InlineData(16, 2)]
    [InlineData(32, 4)]
    [InlineData(64, 8)]
    public async Task Read_ShuffledBlock_LengthMultipleOfItemSize_RecoversPayload(int length, int itemSize)
    {
        var payload = Sequential(length);
        var file = ShuffledZlibFile(payload, itemSize);

        var unit = await TestKit.ReadBytes(file);

        Assert.Equal(payload, Pixels(unit.Images[0]));
    }

    [Theory]
    [InlineData(18, 4)]
    [InlineData(34, 4)]
    [InlineData(36, 8)]
    [InlineData(50, 8)]
    public async Task Read_ShuffledBlock_WithTail_LeavesTrailingBytesUnshuffled(int length, int itemSize)
    {
        var payload = Sequential(length);
        var shuffled = Shuffle(payload, itemSize);
        var tail = length % itemSize;

        // §10.6.2: bytes not forming a complete item are stored unaltered AFTER the shuffled run.
        Assert.NotEqual(0, tail);
        Assert.Equal(payload.Skip(length - tail).ToArray(), shuffled.Skip(length - tail).ToArray());

        var unit = await TestKit.ReadBytes(ShuffledZlibFile(payload, itemSize));

        Assert.Equal(payload, Pixels(unit.Images[0]));
    }

    [Theory]
    [InlineData(XisfCompressionCodec.ZlibSh, "zlib+sh")]
    [InlineData(XisfCompressionCodec.LZ4Sh, "lz4+sh")]
    [InlineData(XisfCompressionCodec.LZ4HCSh, "lz4hc+sh")]
    [InlineData(XisfCompressionCodec.ZstdSh, "zstd+sh")]
    public async Task Emit_ShuffledCodec_CompressionAttributeCarriesItemSize(
        XisfCompressionCodec codec, string codecName)
    {
        var pixels = Pattern(128);
        var file = await WriteBytes(Unit(GrayImage(XisfSampleFormat.UInt16, pixels)),
            new XisfWriterOptions(DefaultCompression: codec));

        var parts = CompressionAttribute(file).Split(':');

        Assert.Equal(3, parts.Length);
        Assert.Equal(codecName, parts[0]);
        Assert.Equal(pixels.Length.ToString(), parts[1]);
        Assert.Equal("2", parts[2]);
    }

    [Theory]
    [InlineData(XisfCompressionCodec.Zlib, "zlib")]
    [InlineData(XisfCompressionCodec.LZ4, "lz4")]
    [InlineData(XisfCompressionCodec.LZ4HC, "lz4hc")]
    [InlineData(XisfCompressionCodec.Zstd, "zstd")]
    public async Task Emit_NonShuffledCodec_CompressionAttributeOmitsItemSize(
        XisfCompressionCodec codec, string codecName)
    {
        var pixels = Pattern(128);
        var file = await WriteBytes(Unit(GrayImage(XisfSampleFormat.UInt16, pixels)),
            new XisfWriterOptions(DefaultCompression: codec));

        var parts = CompressionAttribute(file).Split(':');

        // §10.6.3/10.6.5/10.6.7/10.6.9: the item-size component exists only for the "+sh" codecs.
        Assert.Equal(2, parts.Length);
        Assert.Equal(codecName, parts[0]);
        Assert.Equal(pixels.Length.ToString(), parts[1]);
    }

    [Fact]
    public async Task Parse_CompressionAttribute_WithoutItemSize_Reads()
    {
        var payload = Pattern(48);
        var compressed = DeflateZlib(payload);
        var file = TestKit.BuildMonolithic(
            Document(ImageElementXml(payload.Length, compressed.Length, $"compression=\"zlib:{payload.Length}\"")),
            compressed);

        var unit = await TestKit.ReadBytes(file);

        Assert.Equal(payload, Pixels(unit.Images[0]));
    }

    [Fact]
    public async Task Write_SingleSubblock_EmitsNoSubblocksAttribute()
    {
        var file = await WriteBytes(Unit(GrayImage(XisfSampleFormat.UInt16, Pattern(128))),
            new XisfWriterOptions(DefaultCompression: XisfCompressionCodec.Zstd));

        Assert.Null(ImageElement(file).Attribute("subblocks"));
    }

    [Fact]
    public void Parse_SubblocksAttribute_PopulatesCompressionSubblocks()
    {
        var payload = Sequential(32);
        var (block, first, second) = SubblockedZlibShuffled(payload, itemSize: 4);
        var file = TestKit.BuildMonolithic(
            Document(ImageElementXml(payload.Length, block.Length,
                $"compression=\"zlib+sh:{payload.Length}:4\"",
                $"subblocks=\"{first.Compressed},{first.Uncompressed}:{second.Compressed},{second.Uncompressed}\"")),
            block);

        using var reader = new XisfReader();
        using var stream = new MemoryStream(file);
        var subblocks = reader.ReadMetadataFromStream(stream).Images[0].DataBlockInfo.Compression!.Subblocks;

        Assert.NotNull(subblocks);
        Assert.Equal(2, subblocks!.Count);
        Assert.Equal((ulong)first.Compressed, subblocks[0].CompressedLength);
        Assert.Equal((ulong)first.Uncompressed, subblocks[0].UncompressedLength);
        Assert.Equal((ulong)second.Compressed, subblocks[1].CompressedLength);
        Assert.Equal((ulong)second.Uncompressed, subblocks[1].UncompressedLength);
    }

    [Fact]
    public async Task Read_SubblockedShuffledBlock_ConcatenatesBeforeUnshuffling()
    {
        var payload = Sequential(32);
        const int itemSize = 4;

        // §10.6: shuffling is applied to the WHOLE block before division into subblocks. Shuffling each
        // half independently produces different bytes, so a decoder that unshuffles per subblock (or
        // unshuffles before concatenating) cannot recover the payload from this fixture.
        var wholeThenSplit = Shuffle(payload, itemSize);
        var splitThenShuffle = Shuffle(payload.Take(16).ToArray(), itemSize)
            .Concat(Shuffle(payload.Skip(16).ToArray(), itemSize)).ToArray();
        Assert.NotEqual(wholeThenSplit, splitThenShuffle);

        var (block, first, second) = SubblockedZlibShuffled(payload, itemSize);
        var file = TestKit.BuildMonolithic(
            Document(ImageElementXml(payload.Length, block.Length,
                $"compression=\"zlib+sh:{payload.Length}:{itemSize}\"",
                $"subblocks=\"{first.Compressed},{first.Uncompressed}:{second.Compressed},{second.Uncompressed}\"")),
            block);

        var unit = await TestKit.ReadBytes(file);

        Assert.Equal(payload, Pixels(unit.Images[0]));
    }

    [Fact]
    public async Task Zstd_StoredBytes_AreZstandardFrame()
    {
        var pixels = Pattern(256);
        var file = await WriteBytes(Unit(GrayImage(XisfSampleFormat.UInt16, pixels)),
            new XisfWriterOptions(DefaultCompression: XisfCompressionCodec.Zstd));

        var stored = Attachment(file);

        // §10.6.9: Zstandard frame format. Magic number 0xFD2FB528, little-endian on the wire.
        Assert.Equal(new byte[] { 0x28, 0xB5, 0x2F, 0xFD }, stored.Take(4).ToArray());

        var decoded = new byte[pixels.Length];
        using var decompressor = new ZstdSharp.Decompressor();
        Assert.Equal(pixels.Length, decompressor.Unwrap(stored, decoded, 0));
        Assert.Equal(pixels, decoded);
    }

    [Theory]
    [InlineData(XisfCompressionCodec.LZ4)]
    [InlineData(XisfCompressionCodec.LZ4HC)]
    public async Task Lz4_StoredBytes_AreBlockFormat_NotFrameFormat(XisfCompressionCodec codec)
    {
        var pixels = Pattern(256);
        var file = await WriteBytes(Unit(GrayImage(XisfSampleFormat.UInt16, pixels)),
            new XisfWriterOptions(DefaultCompression: codec));

        var stored = Attachment(file);

        // §10.6.5: LZ4 block format, without the LZ4 frame headers (frame magic 0x184D2204).
        Assert.NotEqual(new byte[] { 0x04, 0x22, 0x4D, 0x18 }, stored.Take(4).ToArray());

        var decoded = new byte[pixels.Length];
        var length = K4os.Compression.LZ4.LZ4Codec.Decode(stored, 0, stored.Length, decoded, 0, decoded.Length);
        Assert.Equal(pixels.Length, length);
        Assert.Equal(pixels, decoded);
    }

    private static byte[] ShuffledZlibFile(byte[] payload, int itemSize)
    {
        var compressed = DeflateZlib(Shuffle(payload, itemSize));
        return TestKit.BuildMonolithic(
            Document(ImageElementXml(payload.Length, compressed.Length,
                $"compression=\"zlib+sh:{payload.Length}:{itemSize}\"")),
            compressed);
    }

    private static (byte[] Block, (int Compressed, int Uncompressed) First, (int Compressed, int Uncompressed) Second)
        SubblockedZlibShuffled(byte[] payload, int itemSize)
    {
        var shuffled = Shuffle(payload, itemSize);
        var half = shuffled.Length / 2;
        var firstCompressed = DeflateZlib(shuffled.Take(half).ToArray());
        var secondCompressed = DeflateZlib(shuffled.Skip(half).ToArray());

        return (firstCompressed.Concat(secondCompressed).ToArray(),
            (firstCompressed.Length, half),
            (secondCompressed.Length, shuffled.Length - half));
    }

    // A UInt16 image: the declared compression item size is a property of the block, not of the sample
    // format, but keeping the payload 16-bit avoids declaring a shuffle over an 8-bit image, which
    // §10.6.2 calls a no-op.
    private static string ImageElementXml(int byteLength, int storedSize, params string[] extraAttributes)
        => $"<Image geometry=\"{byteLength / 2}:1:1\" sampleFormat=\"UInt16\" colorSpace=\"Gray\" " +
           $"location=\"attachment:{BlockBase}:{storedSize}\" {string.Join(" ", extraAttributes)} />";

    private static string Document(string body)
        => "<xisf version=\"1.0\">" +
           "<Metadata>" +
           "<Property id=\"XISF:CreationTime\" type=\"TimePoint\" value=\"2026-09-19T00:00:00.000+00:00\"/>" +
           "<Property id=\"XISF:CreatorApplication\" type=\"String\" value=\"XisfLib.Core.Tests\"/>" +
           "</Metadata>" + body + "</xisf>";

    private static XisfUnit Unit(XisfImage image)
        => XisfFactory.CreateMonolithic(XisfFactory.CreateMinimalMetadata("XisfLib.Core.Tests"), image);

    private static XisfImage GrayImage(XisfSampleFormat format, byte[] pixels)
        => new(new XisfImageGeometry(new[] { (uint)(pixels.Length / ItemSize(format)), 1u }, 1),
               format,
               XisfColorSpace.Gray,
               new InlineDataBlock(pixels, XisfEncoding.Base64),
               format is XisfSampleFormat.Float32 or XisfSampleFormat.Float64 ? new XisfImageBounds(0, 1) : null);

    private static int ItemSize(XisfSampleFormat format) => format switch
    {
        XisfSampleFormat.UInt8 => 1,
        XisfSampleFormat.UInt16 => 2,
        XisfSampleFormat.UInt32 or XisfSampleFormat.Float32 => 4,
        XisfSampleFormat.UInt64 or XisfSampleFormat.Float64 or XisfSampleFormat.Complex32 => 8,
        _ => 16
    };

    private static byte[] Pixels(XisfImage image) => ((InlineDataBlock)image.PixelData).Data.ToArray();

    private static async Task<byte[]> WriteBytes(XisfUnit unit, XisfWriterOptions options)
    {
        using var writer = new XisfWriter(options);
        using var stream = new MemoryStream();
        await writer.WriteAsync(unit, stream);
        return stream.ToArray();
    }

    private static byte[] Attachment(byte[] file)
    {
        using var reader = new XisfReader();
        using var stream = new MemoryStream(file);
        var info = reader.ReadMetadataFromStream(stream).Images[0].DataBlockInfo;
        return file.Skip((int)info.Position).Take((int)info.Size).ToArray();
    }

    private static XElement ImageElement(byte[] file)
    {
        var headerLength = (int)BitConverter.ToUInt32(file, 8);
        var xml = Encoding.UTF8.GetString(file, 16, headerLength);
        return XDocument.Parse(xml).Root!.Elements().First(e => e.Name.LocalName == "Image");
    }

    private static string CompressionAttribute(byte[] file) => ImageElement(file).Attribute("compression")!.Value;

    private static byte[] Shuffle(byte[] input, int itemSize)
    {
        var output = new byte[input.Length];
        var items = input.Length / itemSize;
        var target = 0;
        for (var j = 0; j < itemSize; j++)
            for (var i = 0; i < items; i++)
                output[target++] = input[i * itemSize + j];
        Array.Copy(input, items * itemSize, output, target, input.Length % itemSize);
        return output;
    }

    private static byte[] DeflateZlib(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data, 0, data.Length);
        }
        return output.ToArray();
    }

    private static byte[] InflateZlib(byte[] compressed, int uncompressedLength)
    {
        using var input = new MemoryStream(compressed);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        var output = new byte[uncompressedLength];
        var total = 0;
        int read;
        while (total < uncompressedLength && (read = zlib.Read(output, total, uncompressedLength - total)) > 0)
            total += read;
        Assert.Equal(uncompressedLength, total);
        return output;
    }

    private static byte[] Sequential(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
            bytes[i] = (byte)i;
        return bytes;
    }

    private static byte[] Pattern(int length)
    {
        var bytes = new byte[length];
        var state = 0x12345678u;
        for (var i = 0; i < length; i++)
        {
            state = (state * 1664525u) + 1013904223u;
            bytes[i] = (byte)(state >> 24);
        }
        return bytes;
    }
}
