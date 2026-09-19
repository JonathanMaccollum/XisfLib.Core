// EXPECTED-RED (contract item 7 — the reader drops unrecognized root children unless they carry a
//                uid, in which case DeserializeCoreElement mangles them into XisfReference("")
//                (XmlSerializer.cs:51-63, 337-340); unknown Image children take the same path
//                (XmlSerializer.cs:226-229) and are then skipped on write (XmlSerializer.cs:576-578);
//                an unrecognized Property type silently misparses as String (XmlSerializer.cs:294-296)):
//   ExtensionElement_LandsInHeaderUnknownElements
//   ExtensionElement_ReEmittedVerbatim
//   UnsupportedCoreElement_LandsInHeaderUnknownElements (Theory: ICCProfile/RGBWorkingSpace/DisplayFunction)
//   UnsupportedCoreElement_ReEmittedVerbatim (Theory: same three)
//   UnsupportedCoreElement_NotMangledIntoEmptyReference
//   UnknownImageChild_LandsInImageUnknownElements
//   UnknownImageChild_ReEmittedVerbatim
//   UnrecognizedPropertyType_BecomesRawProperty
//   UnrecognizedPropertyType_ReEmittedWithAttributesAndContent
//   UnrecognizedPropertyType_IsNotAStringProperty
//
// EXPECTED-GREEN (Rev-1 §7 ignore rule — unrecognized content must never be fatal; today it is
//                 silently dropped, which is lossy but does not throw):
//   UnrecognizedContent_DoesNotThrowOnRead
//   UnrecognizedContent_DoesNotThrowOnWrite
//   RecognizedContent_StillSurvivesAlongsideTheUnknowns

using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Xunit;

namespace XisfLib.Core.Tests;

public sealed class PreservationTests
{
    private static readonly byte[] Gray2x2UInt16 = { 9, 0, 8, 0, 7, 0, 6, 0 };

    private static readonly XNamespace Ext = "http://example.org/eigenframe-ext/1";

    [Fact]
    public async Task ExtensionElement_LandsInHeaderUnknownElements()
    {
        var unit = await ReadFixture();

        var extension = Assert.Single(
            unit.Header.UnknownElements, e => e.Name == Ext + "Provenance");

        Assert.Equal("42", extension.Attribute("run")?.Value);
        Assert.Single(extension.Elements());
    }

    [Fact]
    public async Task ExtensionElement_ReEmittedVerbatim()
    {
        var unit = await ReadFixture();
        var written = await HeaderProbe.WriteAndParse(unit);

        var actual = HeaderProbe.FindDescendant(written, Ext + "Provenance");
        Assert.NotNull(actual);
        HeaderProbe.AssertSameShape(Crafted(Ext + "Provenance"), actual!);
    }

    [Theory]
    [InlineData("ICCProfile")]
    [InlineData("RGBWorkingSpace")]
    [InlineData("DisplayFunction")]
    public async Task UnsupportedCoreElement_LandsInHeaderUnknownElements(string localName)
    {
        var unit = await ReadFixture();

        Assert.Single(unit.Header.UnknownElements, e => e.Name.LocalName == localName);
    }

    [Theory]
    [InlineData("ICCProfile")]
    [InlineData("RGBWorkingSpace")]
    [InlineData("DisplayFunction")]
    public async Task UnsupportedCoreElement_ReEmittedVerbatim(string localName)
    {
        var unit = await ReadFixture();
        var written = await HeaderProbe.WriteAndParse(unit);

        var actual = written.Elements().SingleOrDefault(e => e.Name.LocalName == localName);
        Assert.NotNull(actual);
        HeaderProbe.AssertSameShape(Crafted(HeaderProbe.Xisf + localName), actual!);
    }

    [Fact]
    public async Task UnsupportedCoreElement_NotMangledIntoEmptyReference()
    {
        var unit = await ReadFixture();

        Assert.DoesNotContain(
            unit.Header.CoreElements.Values,
            e => e is XisfReference reference && reference.RefId.Length == 0);

        var written = await HeaderProbe.WriteAndParse(unit);
        Assert.DoesNotContain(written.Descendants(), e => e.Name.LocalName == "Reference");
    }

    [Fact]
    public async Task UnknownImageChild_LandsInImageUnknownElements()
    {
        var unit = await ReadFixture();
        var image = Assert.Single(unit.Images);

        Assert.Contains(image.UnknownElements, e => e.Name.LocalName == "Thumbnail");
        Assert.Contains(image.UnknownElements, e => e.Name == Ext + "ImageNote");
    }

    [Fact]
    public async Task UnknownImageChild_ReEmittedVerbatim()
    {
        var unit = await ReadFixture();
        var written = await HeaderProbe.WriteAndParse(unit);

        var image = HeaderProbe.Child(written, "Image");

        var thumbnail = image.Elements().SingleOrDefault(e => e.Name.LocalName == "Thumbnail");
        Assert.NotNull(thumbnail);
        HeaderProbe.AssertSameShape(CraftedImageChild(HeaderProbe.Xisf + "Thumbnail"), thumbnail!);

        var note = image.Elements().SingleOrDefault(e => e.Name == Ext + "ImageNote");
        Assert.NotNull(note);
        HeaderProbe.AssertSameShape(CraftedImageChild(Ext + "ImageNote"), note!);
    }

    [Fact]
    public async Task UnrecognizedPropertyType_BecomesRawProperty()
    {
        var unit = await ReadFixture();

        var property = Assert.Single(unit.GlobalProperties, p => p.Id == "Future:Blob");
        Assert.IsType<XisfRawProperty>(property);
    }

    [Fact]
    public async Task UnrecognizedPropertyType_IsNotAStringProperty()
    {
        var unit = await ReadFixture();

        var property = Assert.Single(unit.GlobalProperties, p => p.Id == "Future:Blob");
        Assert.IsNotType<XisfStringProperty>(property);
    }

    [Fact]
    public async Task UnrecognizedPropertyType_ReEmittedWithAttributesAndContent()
    {
        var unit = await ReadFixture();
        var written = await HeaderProbe.WriteAndParse(unit);

        var actual = written.Elements()
            .SingleOrDefault(e => e.Name.LocalName == "Property"
                                  && e.Attribute("id")?.Value == "Future:Blob");
        Assert.NotNull(actual);

        var expected = CraftedRoot().Elements()
            .Single(e => e.Name.LocalName == "Property" && e.Attribute("id")?.Value == "Future:Blob");
        HeaderProbe.AssertSameShape(expected, actual!);
    }

    [Fact]
    public async Task UnrecognizedContent_DoesNotThrowOnRead()
    {
        var unit = await ReadFixture();

        Assert.NotNull(unit);
        Assert.Single(unit.Images);
    }

    [Fact]
    public async Task UnrecognizedContent_DoesNotThrowOnWrite()
    {
        var unit = await ReadFixture();

        var reread = await TestKit.RoundTrip(unit);

        Assert.Single(reread.Images);
    }

    [Fact]
    public async Task RecognizedContent_StillSurvivesAlongsideTheUnknowns()
    {
        var unit = await ReadFixture();
        var image = Assert.Single(unit.Images);

        Assert.Equal("ExtensionProducer", unit.Header.Metadata.CreatorApplication);
        Assert.Equal(XisfSampleFormat.UInt16, image.SampleFormat);
        Assert.Equal(Gray2x2UInt16, ((InlineDataBlock)image.PixelData).Data.ToArray());
        Assert.Contains(
            image.AssociatedElements ?? System.Array.Empty<XisfCoreElement>(),
            e => e is XisfFitsKeyword keyword && keyword.Name == "IMAGETYP");
    }

    private static async Task<XisfUnit> ReadFixture() =>
        await TestKit.ReadBytes(HeaderProbe.BuildAttached(Header, Gray2x2UInt16));

    private static XElement CraftedRoot() => XDocument.Parse(Header(4096)).Root!;

    private static XElement Crafted(XName name) =>
        CraftedRoot().Elements().Single(e => e.Name == name);

    private static XElement CraftedImageChild(XName name) =>
        HeaderProbe.Child(CraftedRoot(), "Image").Elements().Single(e => e.Name == name);

    private static string Header(ulong pos) =>
        $"""
         <?xml version="1.0" encoding="UTF-8"?>
         <xisf version="1.0"{HeaderProbe.NamespaceDeclarations} xmlns:ext="http://example.org/eigenframe-ext/1">
           <Metadata>
             <Property id="XISF:CreationTime" type="TimePoint" value="2026-09-19T00:00:00.000Z"/>
             <Property id="XISF:CreatorApplication" type="String">ExtensionProducer</Property>
           </Metadata>
           <ICCProfile uid="SampleICC" location="inline:base64">AAECAwQF</ICCProfile>
           <RGBWorkingSpace x="0.648431:0.321152:0.155886" y="0.330856:0.597871:0.066044" Y="0.222491:0.716888:0.060621" gamma="sRGB" name="sRGB IEC61966-2.1"/>
           <DisplayFunction uid="AggressiveDF" m="0.000735:0.000735:0.000735:0.5" s="0.003758:0.003758:0.003758:0" h="1:1:1:1" l="0:0:0:0" r="1:1:1:1"/>
           <ext:Provenance run="42"><ext:Step name="calibrate" order="1"/></ext:Provenance>
           <Property id="Future:Blob" type="FutureType" length="6" location="inline:base64">AAECAwQF</Property>
           <Image geometry="2:2:1" sampleFormat="UInt16" colorSpace="Gray" location="attachment:{pos}:8">
             <FITSKeyword name="IMAGETYP" value="'LIGHT'" comment="frame type"/>
             <Thumbnail geometry="1:1:1" sampleFormat="UInt8" colorSpace="Gray" location="inline:base64">AA==</Thumbnail>
             <ext:ImageNote text="hello" seq="3"/>
           </Image>
         </xisf>
         """;
}
