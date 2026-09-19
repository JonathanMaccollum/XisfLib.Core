// EXPECTED-RED (contract item 8 — writer emits every element in no-namespace today,
//                XmlSerializer.cs:385-390 / 415 / 450 / 504 / 591):
//   Writer_RootElement_IsInXisfNamespace
//   Writer_MetadataElement_IsNamespaceQualified
//   Writer_ImageElement_IsNamespaceQualified
//   Writer_GlobalPropertyElement_IsNamespaceQualified
//   Writer_ImageChildElements_AreNamespaceQualified
//   Writer_EmitsNoElementOutsideTheXisfNamespace
//   RoundTrip_NamespacedFile_StaysNamespaced
//
// EXPECTED-GREEN (must-not-regress — the dual bare/namespaced lookup already works,
//                 XmlSerializer.cs:42-43 / Storage.cs:58,85; version+schemaLocation already emitted):
//   Writer_RootElement_DeclaresVersion10
//   Writer_RootElement_KeepsXsiSchemaLocation
//   Reader_AcceptsHeader (Theory: namespaced == PixInsight-style, bare == legacy v1.2 output)
//   Reader_AcceptsBareHeader_ForeverRegression

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Xunit;

namespace XisfLib.Core.Tests;

public sealed class NamespaceTests
{
    private static readonly byte[] Gray2x2UInt16 = { 1, 0, 2, 0, 3, 0, 4, 0 };

    [Fact]
    public async Task Writer_RootElement_IsInXisfNamespace()
    {
        var root = await HeaderProbe.WriteAndParse(MinimalUnit());

        Assert.Equal("xisf", root.Name.LocalName);
        Assert.Equal(HeaderProbe.Xisf, root.Name.Namespace);
    }

    [Fact]
    public async Task Writer_RootElement_DeclaresVersion10()
    {
        var root = await HeaderProbe.WriteAndParse(MinimalUnit());

        Assert.Equal("1.0", root.Attribute("version")?.Value);
    }

    [Fact]
    public async Task Writer_RootElement_KeepsXsiSchemaLocation()
    {
        var root = await HeaderProbe.WriteAndParse(MinimalUnit());

        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
        Assert.Equal(
            "http://www.pixinsight.com/xisf http://pixinsight.com/xisf/xisf-1.0.xsd",
            root.Attribute(xsi + "schemaLocation")?.Value);
    }

    [Fact]
    public async Task Writer_MetadataElement_IsNamespaceQualified()
    {
        var root = await HeaderProbe.WriteAndParse(MinimalUnit());

        var metadata = Assert.Single(root.Elements(), e => e.Name.LocalName == "Metadata");
        Assert.Equal(HeaderProbe.Xisf, metadata.Name.Namespace);
        Assert.All(
            metadata.Elements(),
            child => Assert.Equal(HeaderProbe.Xisf, child.Name.Namespace));
    }

    [Fact]
    public async Task Writer_ImageElement_IsNamespaceQualified()
    {
        var root = await HeaderProbe.WriteAndParse(MinimalUnit());

        var image = Assert.Single(root.Elements(), e => e.Name.LocalName == "Image");
        Assert.Equal(HeaderProbe.Xisf, image.Name.Namespace);
    }

    [Fact]
    public async Task Writer_GlobalPropertyElement_IsNamespaceQualified()
    {
        var unit = MinimalUnit() with
        {
            GlobalProperties = new XisfProperty[] { new XisfScalarProperty<int>("Test:Answer", 42) }
        };

        var root = await HeaderProbe.WriteAndParse(unit);

        var property = Assert.Single(root.Elements(), e => e.Name.LocalName == "Property");
        Assert.Equal(HeaderProbe.Xisf, property.Name.Namespace);
    }

    [Fact]
    public async Task Writer_ImageChildElements_AreNamespaceQualified()
    {
        var image = TestKit.Gray8(2, 2, 7) with
        {
            Properties = new XisfProperty[] { new XisfStringProperty("Instrument:Filter", "Ha") },
            AssociatedElements = new XisfCoreElement[]
            {
                new XisfFitsKeyword("FILTER", "'Ha'", "filter name"),
                new XisfResolution(72, 72)
            }
        };

        var root = await HeaderProbe.WriteAndParse(
            XisfFactory.CreateMonolithic(XisfFactory.CreateMinimalMetadata("T3"), image));

        var imageElement = Assert.Single(root.Elements(), e => e.Name.LocalName == "Image");
        Assert.NotEmpty(imageElement.Elements());
        Assert.All(
            imageElement.Elements(),
            child => Assert.Equal(HeaderProbe.Xisf, child.Name.Namespace));
    }

    [Fact]
    public async Task Writer_EmitsNoElementOutsideTheXisfNamespace()
    {
        var root = await HeaderProbe.WriteAndParse(MinimalUnit());

        var strays = root.DescendantsAndSelf()
            .Where(e => e.Name.Namespace != HeaderProbe.Xisf)
            .Select(e => e.Name.ToString())
            .ToList();

        Assert.Empty(strays);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reader_AcceptsHeader(bool namespaced)
    {
        var file = HeaderProbe.BuildAttached(pos => Header(pos, namespaced), Gray2x2UInt16);

        var unit = await TestKit.ReadBytes(file);

        Assert.Equal("LegacyProducer", unit.Header.Metadata.CreatorApplication);
        var image = Assert.Single(unit.Images);
        Assert.Equal(XisfSampleFormat.UInt16, image.SampleFormat);
        Assert.Equal(XisfColorSpace.Gray, image.ColorSpace);
        Assert.Equal(2u, image.Geometry.Width);
        Assert.Equal(2u, image.Geometry.Height);
        Assert.Equal(1u, image.Geometry.ChannelCount);
        Assert.Equal(Gray2x2UInt16, PixelBytes(image));
    }

    [Fact]
    public async Task Reader_AcceptsBareHeader_ForeverRegression()
    {
        var file = HeaderProbe.BuildAttached(pos => Header(pos, namespaced: false), Gray2x2UInt16);

        var unit = await TestKit.ReadBytes(file);

        Assert.Single(unit.Images);
    }

    [Fact]
    public async Task RoundTrip_NamespacedFile_StaysNamespaced()
    {
        var file = HeaderProbe.BuildAttached(pos => Header(pos, namespaced: true), Gray2x2UInt16);
        var unit = await TestKit.ReadBytes(file);

        var root = await HeaderProbe.WriteAndParse(unit);

        Assert.Equal(HeaderProbe.Xisf, root.Name.Namespace);
        Assert.All(
            root.DescendantsAndSelf(),
            e => Assert.Equal(HeaderProbe.Xisf, e.Name.Namespace));
    }

    private static XisfUnit MinimalUnit() =>
        XisfFactory.CreateMonolithic(XisfFactory.CreateMinimalMetadata("T3"), TestKit.Gray8(2, 2, 7));

    private static byte[] PixelBytes(XisfImage image) =>
        ((InlineDataBlock)image.PixelData).Data.ToArray();

    private static string Header(ulong pos, bool namespaced) =>
        $"""
         <?xml version="1.0" encoding="UTF-8"?>
         <xisf version="1.0"{(namespaced ? HeaderProbe.NamespaceDeclarations : string.Empty)}>
           <Metadata>
             <Property id="XISF:CreationTime" type="TimePoint" value="2026-09-19T00:00:00.000Z"/>
             <Property id="XISF:CreatorApplication" type="String">LegacyProducer</Property>
           </Metadata>
           <Image geometry="2:2:1" sampleFormat="UInt16" colorSpace="Gray" location="attachment:{pos}:8"/>
         </xisf>
         """;
}

internal static class HeaderProbe
{
    public static readonly XNamespace Xisf = "http://www.pixinsight.com/xisf";

    public const string NamespaceDeclarations =
        " xmlns=\"http://www.pixinsight.com/xisf\"" +
        " xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"" +
        " xsi:schemaLocation=\"http://www.pixinsight.com/xisf http://pixinsight.com/xisf/xisf-1.0.xsd\"";

    public static async Task<byte[]> WriteBytes(XisfUnit unit, XisfWriterOptions? options = null)
    {
        using var writer = new XisfWriter(options ?? new XisfWriterOptions());
        using var stream = new MemoryStream();
        await writer.WriteAsync(unit, stream);
        return stream.ToArray();
    }

    public static XElement Root(byte[] file)
    {
        var headerLength = BitConverter.ToUInt32(file, 8);
        var xml = Encoding.UTF8.GetString(file, 16, (int)headerLength);
        return XDocument.Parse(xml).Root!;
    }

    public static async Task<XElement> WriteAndParse(XisfUnit unit, XisfWriterOptions? options = null) =>
        Root(await WriteBytes(unit, options));

    /// The attachment position depends on the header byte length, which in turn depends on the
    /// digits of the position: iterate to the fixed point instead of assuming TestKit's padding.
    public static byte[] BuildAttached(Func<ulong, string> headerFor, byte[] blob)
    {
        var position = 4096UL;
        for (var attempt = 0; ; attempt++)
        {
            var file = TestKit.BuildMonolithic(headerFor(position), blob);
            var actual = (ulong)(file.Length - blob.Length);
            if (actual == position)
                return file;
            if (attempt == 7)
                throw new InvalidOperationException("Attachment position did not converge.");
            position = actual;
        }
    }

    public static XElement Child(XElement parent, string localName) =>
        parent.Elements().Single(e => e.Name.LocalName == localName);

    public static XElement? FindDescendant(XElement root, XName name) =>
        root.Descendants().FirstOrDefault(e => e.Name == name);

    /// Compares an element re-emitted by the writer against the crafted original: name, the
    /// attributes that carry data (namespace declarations are re-serialized freely by XDocument
    /// and are not part of "verbatim"), and the character content.
    public static void AssertSameShape(XElement expected, XElement actual)
    {
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(DataAttributes(expected), DataAttributes(actual));
        Assert.Equal(expected.Value, actual.Value);
        Assert.Equal(expected.Elements().Count(), actual.Elements().Count());

        var expectedChildren = expected.Elements().ToList();
        var actualChildren = actual.Elements().ToList();
        for (var i = 0; i < expectedChildren.Count; i++)
            AssertSameShape(expectedChildren[i], actualChildren[i]);
    }

    private static string[] DataAttributes(XElement element) =>
        element.Attributes()
            .Where(a => !a.IsNamespaceDeclaration)
            .Select(a => $"{a.Name}={a.Value}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
}
