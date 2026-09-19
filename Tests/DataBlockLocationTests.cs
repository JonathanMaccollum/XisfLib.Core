// Wave-1 expectation ledger for DataBlockLocationTests (contract items 5, 10; assessment §A.4, §D).
//
// EXPECTED-RED
//   Read_InlineBase64Property_HasRealContent            - inline blocks deserialize as empty data
//   Read_InlineHexProperty_HasRealContent                 (XmlSerializer.DeserializeDataBlockLocation
//   Read_InlineBlock_WhitespaceInContentIgnored           returns ReadOnlyMemory.Empty and the element's
//   Read_ZeroLengthInlineVector_IsEmptyVector             character data is never read).
//   Read_EmbeddedImage_HasRealContent                   - location="embedded" never consults the child
//   Read_EmbeddedImage_HexEncoding_HasRealContent         Data element; the Data path is only reached
//   Read_EmbeddedImage_CompressionOnDataElement           when no location attribute exists, and it
//                                                         always decodes as base64 (§10.3, §10.6.1).
//   Read_UrlLocation_ExtendsToLastParenthesis           - Rev 1 correction D.7; today the parse stops
//                                                         at the FIRST ')'.
//   Read_UnknownCodec_HeaderParseSucceedsAndSiblingBlockReadable - contract 10: an unknown codec throws
//                                                         FormatException during header deserialization
//                                                         today, killing the whole unit.
//   Read_UnknownCodec_MaterializingBlockThrowsUnsupportedObject
//
// EXPECTED-GREEN (must not regress)
//   Read_AttachmentBlock_Materializes
//   Read_UrlLocation_WithoutParentheses_ParsesUri

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace XisfLib.Core.Tests;

public sealed class DataBlockLocationTests
{
    private const int BlockBase = 4096;

    // §10.3 worked example: the Base64 content decodes to this 34-byte ByteArray value.
    private const string SpecInlineBase64 = "VGhpcyBpcyBhIHRlc3QgLSBURVNUIC0gMTIzNDU2Nzg5MA==";
    private const string SpecInlineText = "This is a test - TEST - 1234567890";

    [Fact]
    public async Task Read_InlineBase64Property_HasRealContent()
    {
        var file = TestKit.BuildMonolithic(Document(
            $"<Property id=\"TestProperty\" type=\"ByteArray\" length=\"34\" location=\"inline:base64\">" +
            $"{SpecInlineBase64}</Property>"));

        var unit = await TestKit.ReadBytes(file);

        Assert.Equal(Encoding.ASCII.GetBytes(SpecInlineText), Vector(unit, "TestProperty"));
    }

    [Fact]
    public async Task Read_InlineHexProperty_HasRealContent()
    {
        var payload = Encoding.ASCII.GetBytes(SpecInlineText);
        var hex = Convert.ToHexString(payload).ToLowerInvariant();
        var file = TestKit.BuildMonolithic(Document(
            $"<Property id=\"TestProperty\" type=\"ByteArray\" length=\"{payload.Length}\" " +
            $"location=\"inline:hex\">{hex}</Property>"));

        var unit = await TestKit.ReadBytes(file);

        Assert.Equal(payload, Vector(unit, "TestProperty"));
    }

    [Fact]
    public async Task Read_InlineBlock_WhitespaceInContentIgnored()
    {
        var spaced = "\n   " + SpecInlineBase64.Insert(16, "\n  ").Insert(8, " ") + "\n  ";
        var file = TestKit.BuildMonolithic(Document(
            $"<Property id=\"TestProperty\" type=\"ByteArray\" length=\"34\" location=\"inline:base64\">" +
            $"{spaced}</Property>"));

        var unit = await TestKit.ReadBytes(file);

        Assert.Equal(Encoding.ASCII.GetBytes(SpecInlineText), Vector(unit, "TestProperty"));
    }

    [Fact]
    public async Task Read_ZeroLengthInlineVector_IsEmptyVector()
    {
        var file = TestKit.BuildMonolithic(Document(
            "<Property id=\"EmptyVector\" type=\"ByteArray\" length=\"0\" location=\"inline:base64\"></Property>"));

        var unit = await TestKit.ReadBytes(file);

        Assert.Empty(Vector(unit, "EmptyVector"));
    }

    [Fact]
    public async Task Read_EmbeddedImage_HasRealContent()
    {
        var payload = Pattern(36);
        var file = TestKit.BuildMonolithic(Document(
            $"<Image geometry=\"{payload.Length}:1:1\" sampleFormat=\"UInt8\" colorSpace=\"Gray\" location=\"embedded\">" +
            $"<Data encoding=\"base64\">\n   {Convert.ToBase64String(payload)}\n   </Data></Image>"));

        var unit = await TestKit.ReadBytes(file);

        Assert.Equal(payload, Pixels(unit.Images[0]));
    }

    [Fact]
    public async Task Read_EmbeddedImage_HexEncoding_HasRealContent()
    {
        var payload = Pattern(36);
        var file = TestKit.BuildMonolithic(Document(
            $"<Image geometry=\"{payload.Length}:1:1\" sampleFormat=\"UInt8\" colorSpace=\"Gray\" location=\"embedded\">" +
            $"<Data encoding=\"hex\">{Convert.ToHexString(payload).ToLowerInvariant()}</Data></Image>"));

        var unit = await TestKit.ReadBytes(file);

        Assert.Equal(payload, Pixels(unit.Images[0]));
    }

    [Fact]
    public async Task Read_EmbeddedImage_CompressionOnDataElement()
    {
        var payload = Pattern(64);
        var compressed = Convert.ToBase64String(DeflateZlib(payload));
        // §10.6.1: for an embedded block the compression attribute belongs to the child Data element.
        var file = TestKit.BuildMonolithic(Document(
            $"<Image geometry=\"{payload.Length}:1:1\" sampleFormat=\"UInt8\" colorSpace=\"Gray\" location=\"embedded\">" +
            $"<Data compression=\"zlib:{payload.Length}\" encoding=\"base64\">{compressed}</Data></Image>"));

        var unit = await TestKit.ReadBytes(file);

        Assert.Equal(payload, Pixels(unit.Images[0]));
    }

    [Fact]
    public async Task Read_AttachmentBlock_Materializes()
    {
        var payload = Pattern(40);
        var file = TestKit.BuildMonolithic(Document(Gray8Element(payload.Length, BlockBase, payload.Length)), payload);

        var unit = await TestKit.ReadBytes(file);

        Assert.Equal(payload, Pixels(unit.Images[0]));
    }

    [Fact]
    public async Task Read_UrlLocation_ExtendsToLastParenthesis()
    {
        const string url = "ftp://ftp.example.com/public/example(2016).dat";
        var unit = await ReadDistributed(Document(
            $"<Image geometry=\"16:1:1\" sampleFormat=\"UInt8\" colorSpace=\"Gray\" location=\"url({url})\" />"));

        var external = Assert.IsType<ExternalDataBlock>(unit.Images[0].PixelData);

        Assert.Equal(url, external.Location.ToString());
    }

    [Fact]
    public async Task Read_UrlLocation_WithoutParentheses_ParsesUri()
    {
        const string url = "http://mysite.example.com/myfile.bin";
        var unit = await ReadDistributed(Document(
            $"<Image geometry=\"16:1:1\" sampleFormat=\"UInt8\" colorSpace=\"Gray\" location=\"url({url})\" />"));

        var external = Assert.IsType<ExternalDataBlock>(unit.Images[0].PixelData);

        Assert.Equal(url, external.Location.ToString());
    }

    [Fact]
    public void Read_UnknownCodec_HeaderParseSucceedsAndSiblingBlockReadable()
    {
        var file = UnknownCodecFile(out var readable, out _);

        using var reader = new XisfReader();
        using var stream = new MemoryStream(file);
        var metadata = reader.ReadMetadataFromStream(stream);

        Assert.Equal(2, metadata.Images.Count);
        Assert.Equal((ulong)BlockBase, metadata.Images[0].DataBlockInfo.Position);
        Assert.Equal((ulong)readable.Length, metadata.Images[0].DataBlockInfo.Size);
    }

    [Fact]
    public async Task Read_UnknownCodec_MaterializingBlockThrowsUnsupportedObject()
    {
        var file = UnknownCodecFile(out _, out _);

        await Assert.ThrowsAsync<XisfUnsupportedObjectException>(() => TestKit.ReadBytes(file));
    }

    private static byte[] UnknownCodecFile(out byte[] readable, out byte[] unsupported)
    {
        readable = Pattern(32);
        unsupported = Pattern(24);

        return TestKit.BuildMonolithic(
            Document(
                Gray8Element(readable.Length, BlockBase, readable.Length) +
                Gray8Element(unsupported.Length, BlockBase + readable.Length, unsupported.Length,
                    $"compression=\"brotli:{unsupported.Length}\"")),
            readable, unsupported);
    }

    private static async Task<XisfUnit> ReadDistributed(string headerXml)
    {
        using var reader = new XisfReader(new XisfReaderOptions(LoadExternalReferences: false));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(headerXml));
        return await reader.ReadAsync(stream, ".xish");
    }

    private static string Gray8Element(int pixelCount, int position, int storedSize, params string[] extraAttributes)
        => $"<Image geometry=\"{pixelCount}:1:1\" sampleFormat=\"UInt8\" colorSpace=\"Gray\" " +
           $"location=\"attachment:{position}:{storedSize}\" {string.Join(" ", extraAttributes)} />";

    private static string Document(string body)
        => "<xisf version=\"1.0\">" +
           "<Metadata>" +
           "<Property id=\"XISF:CreationTime\" type=\"TimePoint\" value=\"2026-09-19T00:00:00.000+00:00\"/>" +
           "<Property id=\"XISF:CreatorApplication\" type=\"String\" value=\"XisfLib.Core.Tests\"/>" +
           "</Metadata>" + body + "</xisf>";

    private static byte[] Pixels(XisfImage image) => ((InlineDataBlock)image.PixelData).Data.ToArray();

    private static byte[] Vector(XisfUnit unit, string id)
        => Assert.IsType<XisfVectorProperty<byte>>(unit.GlobalProperties.Single(p => p.Id == id)).Values.ToArray();

    private static byte[] DeflateZlib(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data, 0, data.Length);
        }
        return output.ToArray();
    }

    private static byte[] Pattern(int length)
    {
        var bytes = new byte[length];
        var state = 0x0BADF00Du;
        for (var i = 0; i < length; i++)
        {
            state = (state * 1664525u) + 1013904223u;
            bytes[i] = (byte)(state >> 24);
        }
        return bytes;
    }
}
