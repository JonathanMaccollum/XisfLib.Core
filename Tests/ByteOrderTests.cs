// Wave-1 expectation ledger for ByteOrderTests (contract item 4; assessment §A.5).
//
// EXPECTED-RED
//   Read_BigEndianUInt16_ConvertsToLittleEndian          - byteOrder is never parsed and
//   Read_BigEndianFloat32_ConvertsToLittleEndian           ConvertByteOrder is dead code, so a
//   Read_BigEndianFloat64_ConvertsToLittleEndian           big-endian block decodes as little-endian.
//   Read_BigEndian_MetadataBlockInfoReportsBigEndian     - pins the attribute parse on its own.
//   Read_BigEndianCompressedBlock_AppliesToUncompressedData - §10.4: byteOrder describes the
//                                                            uncompressed data.
//   Read_BigEndianShuffledBlock_ConvertsAfterUnshuffle   - contract 4 ordering: unshuffle, then convert.
//
// EXPECTED-GREEN (must not regress)
//   Read_AbsentByteOrderAttribute_DefaultsToLittleEndian
//   Read_ExplicitLittleByteOrder_IsNoOp
//   Read_BigEndianEightBitImage_IsNoOp                   - §10.4: endianness is immaterial for 8-bit.
//   Write_NeverEmitsByteOrderAttribute
//   Write_EightBitImage_NeverEmitsByteOrderAttribute

using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Xunit;

namespace XisfLib.Core.Tests;

public sealed class ByteOrderTests
{
    private const int BlockBase = 4096;

    private static readonly ushort[] Words = { 0x0102, 0xF0E1, 0x0000, 0xFFFF, 0x1234, 0x89AB };
    private static readonly float[] Singles = { 1.0f, -2.5f, 3.75f, 1e-8f };
    private static readonly double[] Doubles = { 1.0, -2.5, 3.75, 1e-8 };

    [Fact]
    public async Task Read_BigEndianUInt16_ConvertsToLittleEndian()
    {
        var little = LittleEndian(Words);
        var file = Crafted("UInt16", Words.Length, SwapItems(little, 2), "byteOrder=\"big\"");

        var unit = await TestKit.ReadBytes(file);

        Assert.Equal(little, Pixels(unit.Images[0]));
    }

    [Fact]
    public async Task Read_BigEndianFloat32_ConvertsToLittleEndian()
    {
        var little = LittleEndian(Singles);
        var file = Crafted("Float32", Singles.Length, SwapItems(little, 4), "byteOrder=\"big\"", "bounds=\"0:1\"");

        var pixels = Pixels((await TestKit.ReadBytes(file)).Images[0]);

        Assert.Equal(little, pixels);
        Assert.Equal(Singles, Enumerable.Range(0, Singles.Length)
            .Select(i => BinaryPrimitives.ReadSingleLittleEndian(pixels.AsSpan(i * 4, 4))).ToArray());
    }

    [Fact]
    public async Task Read_BigEndianFloat64_ConvertsToLittleEndian()
    {
        var little = LittleEndian(Doubles);
        var file = Crafted("Float64", Doubles.Length, SwapItems(little, 8), "byteOrder=\"big\"", "bounds=\"0:1\"");

        var unit = await TestKit.ReadBytes(file);

        Assert.Equal(little, Pixels(unit.Images[0]));
    }

    [Fact]
    public void Read_BigEndian_MetadataBlockInfoReportsBigEndian()
    {
        var file = Crafted("UInt16", Words.Length, SwapItems(LittleEndian(Words), 2), "byteOrder=\"big\"");

        using var reader = new XisfReader();
        using var stream = new MemoryStream(file);

        Assert.Equal(XisfByteOrder.BigEndian,
            reader.ReadMetadataFromStream(stream).Images[0].DataBlockInfo.ByteOrder);
    }

    [Fact]
    public async Task Read_BigEndianCompressedBlock_AppliesToUncompressedData()
    {
        var little = LittleEndian(Words);
        var big = SwapItems(little, 2);
        var stored = DeflateZlib(big);
        var file = Crafted("UInt16", Words.Length, stored,
            "byteOrder=\"big\"", $"compression=\"zlib:{big.Length}\"");

        var unit = await TestKit.ReadBytes(file);

        Assert.Equal(little, Pixels(unit.Images[0]));
    }

    [Fact]
    public async Task Read_BigEndianShuffledBlock_ConvertsAfterUnshuffle()
    {
        var little = LittleEndian(Singles);
        var big = SwapItems(little, 4);
        var shuffled = Shuffle(big, 4);

        // Converting byte order before unshuffling permutes different bytes and cannot recover the
        // payload, so this fixture discriminates the two orderings.
        Assert.NotEqual(little, Unshuffle(SwapItems(shuffled, 4), 4));

        var file = Crafted("Float32", Singles.Length, DeflateZlib(shuffled),
            "byteOrder=\"big\"", $"compression=\"zlib+sh:{big.Length}:4\"", "bounds=\"0:1\"");

        var unit = await TestKit.ReadBytes(file);

        Assert.Equal(little, Pixels(unit.Images[0]));
    }

    [Fact]
    public async Task Read_AbsentByteOrderAttribute_DefaultsToLittleEndian()
    {
        var little = LittleEndian(Words);
        var file = Crafted("UInt16", Words.Length, little);

        var unit = await TestKit.ReadBytes(file);

        Assert.Equal(little, Pixels(unit.Images[0]));
    }

    [Fact]
    public async Task Read_ExplicitLittleByteOrder_IsNoOp()
    {
        var little = LittleEndian(Words);
        var file = Crafted("UInt16", Words.Length, little, "byteOrder=\"little\"");

        var unit = await TestKit.ReadBytes(file);

        Assert.Equal(little, Pixels(unit.Images[0]));
    }

    [Fact]
    public async Task Read_BigEndianEightBitImage_IsNoOp()
    {
        var payload = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };
        var file = Crafted("UInt8", payload.Length, payload, "byteOrder=\"big\"");

        var unit = await TestKit.ReadBytes(file);

        Assert.Equal(payload, Pixels(unit.Images[0]));
    }

    [Theory]
    [InlineData(XisfSampleFormat.UInt16, 2)]
    [InlineData(XisfSampleFormat.Float32, 4)]
    [InlineData(XisfSampleFormat.Float64, 8)]
    public async Task Write_NeverEmitsByteOrderAttribute(XisfSampleFormat format, int itemSize)
    {
        var file = await WriteBytes(Unit(GrayImage(format, new byte[itemSize * 8], itemSize)));

        Assert.Null(ImageElement(file).Attribute("byteOrder"));
    }

    [Fact]
    public async Task Write_EightBitImage_NeverEmitsByteOrderAttribute()
    {
        var file = await WriteBytes(Unit(GrayImage(XisfSampleFormat.UInt8, new byte[8], 1)));

        Assert.Null(ImageElement(file).Attribute("byteOrder"));
    }

    private static byte[] Crafted(string sampleFormat, int sampleCount, byte[] stored, params string[] extraAttributes)
        => TestKit.BuildMonolithic(
            "<xisf version=\"1.0\">" +
            "<Metadata>" +
            "<Property id=\"XISF:CreationTime\" type=\"TimePoint\" value=\"2026-09-19T00:00:00.000+00:00\"/>" +
            "<Property id=\"XISF:CreatorApplication\" type=\"String\" value=\"XisfLib.Core.Tests\"/>" +
            "</Metadata>" +
            $"<Image geometry=\"{sampleCount}:1:1\" sampleFormat=\"{sampleFormat}\" colorSpace=\"Gray\" " +
            $"location=\"attachment:{BlockBase}:{stored.Length}\" {string.Join(" ", extraAttributes)} />" +
            "</xisf>",
            stored);

    private static XisfUnit Unit(XisfImage image)
        => XisfFactory.CreateMonolithic(XisfFactory.CreateMinimalMetadata("XisfLib.Core.Tests"), image);

    private static XisfImage GrayImage(XisfSampleFormat format, byte[] pixels, int itemSize)
        => new(new XisfImageGeometry(new[] { (uint)(pixels.Length / itemSize), 1u }, 1),
               format,
               XisfColorSpace.Gray,
               new InlineDataBlock(pixels, XisfEncoding.Base64),
               format is XisfSampleFormat.Float32 or XisfSampleFormat.Float64 ? new XisfImageBounds(0, 1) : null);

    private static byte[] Pixels(XisfImage image) => ((InlineDataBlock)image.PixelData).Data.ToArray();

    private static byte[] LittleEndian(ushort[] values)
    {
        var bytes = new byte[values.Length * 2];
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2, 2), values[i]);
        return bytes;
    }

    private static byte[] LittleEndian(float[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4, 4), values[i]);
        return bytes;
    }

    private static byte[] LittleEndian(double[] values)
    {
        var bytes = new byte[values.Length * 8];
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(i * 8, 8), values[i]);
        return bytes;
    }

    private static byte[] SwapItems(byte[] data, int itemSize)
    {
        var output = data.ToArray();
        for (var offset = 0; offset < output.Length; offset += itemSize)
            Array.Reverse(output, offset, itemSize);
        return output;
    }

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

    private static byte[] Unshuffle(byte[] input, int itemSize)
    {
        var output = new byte[input.Length];
        var items = input.Length / itemSize;
        var source = 0;
        for (var j = 0; j < itemSize; j++)
            for (var i = 0; i < items; i++)
                output[(i * itemSize) + j] = input[source++];
        Array.Copy(input, source, output, items * itemSize, input.Length % itemSize);
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

    private static async Task<byte[]> WriteBytes(XisfUnit unit)
    {
        using var writer = new XisfWriter(new XisfWriterOptions());
        using var stream = new MemoryStream();
        await writer.WriteAsync(unit, stream);
        return stream.ToArray();
    }

    private static XElement ImageElement(byte[] file)
    {
        var headerLength = (int)BitConverter.ToUInt32(file, 8);
        var xml = Encoding.UTF8.GetString(file, 16, headerLength);
        return XDocument.Parse(xml).Root!.Elements().First(e => e.Name.LocalName == "Image");
    }
}
