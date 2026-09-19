// Carriage only — no projection, spherical-rotation or spline math is exercised anywhere here.
// Property shapes follow the worked example in spec §11.5.3.7.9 and the definitions in
// §11.5.3.7.2 / §11.5.3.7.4.4; identifiers use the single-colon namespace separator of §8.4.1.
//
// EXPECTED-RED (contract item 6 — F64Vector/F64Matrix/I32Vector properties can neither be written
//                (XmlSerializer.cs:495-496 throws NotSupportedException) nor read (XmlSerializer.cs:294-296
//                misparses them as String, and DeserializeProperty never looks at `location`);
//                contract item 7 for the unrecognized-type property; §11.1.6 for the newline):
//   Solution_RoundTripsEveryPropertyId
//   Solution_ScalarAndStringProperties_RoundTrip
//   Solution_F64Vectors_RoundTripBitExact
//   Solution_F64Matrix_KeepsRowsAndColumns
//   Solution_I32Vector_RoundTrips
//   Solution_PropertiesStayOnTheImage
//   TermsString_PreservesEmbeddedNewline
//   TermsString_SerializedAsCharacterContent
//   BlockBackedSolutionProperties_ReadFromAttachment (Theory)
//   UnrecognizedSolutionPropertyType_BecomesRawProperty
//   UnrecognizedSolutionPropertyType_ReEmittedVerbatim
//   UnrecognizedSolutionPropertyId_RidesAlong — an unknown identifier with a KNOWN type is an
//     ordinary property and must keep round-tripping (§11.5.3.7.7 ignore-but-preserve rule). Wave 1
//     filed this as EXPECTED-GREEN; wave 2 measured it RED and reclassified: the assertion is about
//     a String property, but the test reaches it through RoundTripSolutionImage(), which writes the
//     whole solution set — so it is gated on contract item 6 exactly like its siblings above.
//
// EXPECTED-GREEN (must-not-regress):
//   SingleColonPropertyIdentifiers_SurviveRoundTrip  — §8.4.1 allows a single ':' separator, which
//     every AstrometricSolution identifier uses; XisfValidator's PropertyIdRegex only admits '::'
//     (Validation.cs:13-15), so this goes red the moment image validation is wired in with that regex.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace XisfLib.Core.Tests;

public sealed class AstrometricCarriageTests
{
    private const string Solution = "AstrometricSolution:";
    private const string Distortion = "AstrometricSolution:DistortionModel:ImageToProjection:";

    private static readonly double[] ReferenceCelestial = { 299.86819467540001, 40.733932451900003 };
    private static readonly double[] ReferenceImage = { 2048.5, 1536.5 };
    private static readonly double[] LinearTransformation =
        { -3.8194444444444445e-4, 1.2345678901234567e-7, 1.2345678901234567e-7, 3.8194444444444445e-4 };
    private static readonly int[] NodeOffsets = { 0, 901, 1802, 2703 };
    private static readonly double[] Nodes =
        { -1.0, -1.0, 0.0, 0.25, 1.0, 0.9999999999999998, -0.5, 0.5, 0.125, -0.125, 0.75, -0.75 };
    private static readonly double[] Coefficients =
        { 1.4210854715202004e-14, -2.2204460492503131e-16, 3.0517578125e-5 };
    private const double FallbackThreshold = 0.1417233906;
    private const string Terms = "Local\nFallback";

    [Fact]
    public async Task Solution_RoundTripsEveryPropertyId()
    {
        var image = await RoundTripSolutionImage();

        Assert.Equal(
            SolutionProperties().Select(p => p.Id).OrderBy(id => id, StringComparer.Ordinal),
            image.Properties!.Select(p => p.Id).OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Solution_ScalarAndStringProperties_RoundTrip()
    {
        var image = await RoundTripSolutionImage();

        Assert.Equal("1.0", Property<XisfStringProperty>(image, Solution + "Version").Value);
        Assert.Equal("Gnomonic", Property<XisfStringProperty>(image, Solution + "ProjectionSystem").Value);
        Assert.Equal("ICRS", Property<XisfStringProperty>(image, Solution + "CelestialReferenceSystem").Value);
        Assert.Equal("VariableOrder", Property<XisfStringProperty>(image, Distortion + "BasisFunction").Value);
        Assert.Equal(4, Property<XisfScalarProperty<int>>(image, Distortion + "Order").Value);
        AssertBitExact(
            FallbackThreshold,
            Property<XisfScalarProperty<double>>(image, Distortion + "Fallback:Threshold").Value);
    }

    [Fact]
    public async Task Solution_F64Vectors_RoundTripBitExact()
    {
        var image = await RoundTripSolutionImage();

        AssertBitExact(
            ReferenceCelestial,
            Property<XisfVectorProperty<double>>(image, Solution + "ReferenceCelestialCoordinates").Values);
        AssertBitExact(
            ReferenceImage,
            Property<XisfVectorProperty<double>>(image, Solution + "ReferenceImageCoordinates").Values);
        AssertBitExact(
            Coefficients,
            Property<XisfVectorProperty<double>>(image, Distortion + "Local:X:Coefficients").Values);
    }

    [Fact]
    public async Task Solution_F64Matrix_KeepsRowsAndColumns()
    {
        var image = await RoundTripSolutionImage();

        var linear = Property<XisfMatrixProperty<double>>(image, Solution + "LinearTransformationMatrix");
        Assert.Equal(2u, linear.Rows);
        Assert.Equal(2u, linear.Columns);
        AssertBitExact(LinearTransformation, linear.Values);

        var nodes = Property<XisfMatrixProperty<double>>(image, Distortion + "Local:X:Nodes");
        Assert.Equal(6u, nodes.Rows);
        Assert.Equal(2u, nodes.Columns);
        AssertBitExact(Nodes, nodes.Values);
    }

    [Fact]
    public async Task Solution_I32Vector_RoundTrips()
    {
        var image = await RoundTripSolutionImage();

        var offsets = Property<XisfVectorProperty<int>>(image, Distortion + "Local:X:NodeOffsets");
        Assert.Equal(NodeOffsets, offsets.Values);
    }

    [Fact]
    public async Task Solution_PropertiesStayOnTheImage()
    {
        var unit = await TestKit.RoundTrip(SolutionUnit());

        Assert.Empty(unit.GlobalProperties);
        Assert.Equal(SolutionProperties().Count, Assert.Single(unit.Images).Properties!.Count);
    }

    [Fact]
    public async Task TermsString_PreservesEmbeddedNewline()
    {
        var image = await RoundTripSolutionImage();

        // §11.1.6: white space inside the character contents of a String property is significant.
        Assert.Equal(Terms, Property<XisfStringProperty>(image, Distortion + "Terms").Value);
    }

    [Fact]
    public async Task TermsString_SerializedAsCharacterContent()
    {
        var root = await HeaderProbe.WriteAndParse(SolutionUnit());

        var terms = root.Descendants()
            .Single(e => e.Name.LocalName == "Property"
                         && e.Attribute("id")?.Value == Distortion + "Terms");

        // §11.1.6: a String Property shall not have a value attribute.
        Assert.Null(terms.Attribute("value"));
        Assert.Equal(Terms, terms.Value);
    }

    [Fact]
    public async Task UnrecognizedSolutionPropertyId_RidesAlong()
    {
        var image = await RoundTripSolutionImage();

        Assert.Equal(
            "solver-private-token",
            Property<XisfStringProperty>(image, Solution + "FutureProp").Value);
    }

    [Fact]
    public async Task SingleColonPropertyIdentifiers_SurviveRoundTrip()
    {
        var image = TestKit.GrayF32(2, 2, 0.25f) with
        {
            Properties = new XisfProperty[]
            {
                new XisfStringProperty(Solution + "Version", "1.0"),
                new XisfStringProperty(Solution + "ProjectionSystem", "Gnomonic"),
                new XisfScalarProperty<int>(Distortion + "Order", 4),
                new XisfScalarProperty<double>(Distortion + "Fallback:Threshold", FallbackThreshold)
            }
        };

        var unit = await TestKit.RoundTrip(
            XisfFactory.CreateMonolithic(XisfFactory.CreateMinimalMetadata("T3"), image));

        var readBack = Assert.Single(unit.Images);
        Assert.Equal(4, readBack.Properties!.Count);
        Assert.Contains(readBack.Properties, p => p.Id == Distortion + "Fallback:Threshold");
    }

    [Theory]
    [InlineData(Solution + "ReferenceCelestialCoordinates")]
    [InlineData(Distortion + "Local:X:NodeOffsets")]
    public async Task BlockBackedSolutionProperties_ReadFromAttachment(string id)
    {
        var unit = await TestKit.ReadBytes(HeaderProbe.BuildAttached(AttachedSolutionHeader, AttachedBlob()));
        var image = Assert.Single(unit.Images);

        var property = image.Properties!.Single(p => p.Id == id);
        if (id.EndsWith("NodeOffsets", StringComparison.Ordinal))
            Assert.Equal(NodeOffsets, Assert.IsType<XisfVectorProperty<int>>(property).Values);
        else
            AssertBitExact(ReferenceCelestial, Assert.IsType<XisfVectorProperty<double>>(property).Values);
    }

    [Fact]
    public async Task UnrecognizedSolutionPropertyType_BecomesRawProperty()
    {
        var unit = await TestKit.ReadBytes(HeaderProbe.BuildAttached(AttachedSolutionHeader, AttachedBlob()));
        var image = Assert.Single(unit.Images);

        var property = image.Properties!.Single(p => p.Id == Solution + "FutureBlob");
        Assert.IsType<XisfRawProperty>(property);
    }

    [Fact]
    public async Task UnrecognizedSolutionPropertyType_ReEmittedVerbatim()
    {
        var unit = await TestKit.ReadBytes(HeaderProbe.BuildAttached(AttachedSolutionHeader, AttachedBlob()));

        var root = await HeaderProbe.WriteAndParse(unit);

        var written = root.Descendants()
            .SingleOrDefault(e => e.Name.LocalName == "Property"
                                  && e.Attribute("id")?.Value == Solution + "FutureBlob");
        Assert.NotNull(written);
        Assert.Equal("FutureType", written!.Attribute("type")?.Value);
        Assert.Equal("7", written.Attribute("rank")?.Value);
        Assert.Equal("AAECAwQF", written.Value.Trim());
    }

    private static async Task<XisfImage> RoundTripSolutionImage() =>
        Assert.Single((await TestKit.RoundTrip(SolutionUnit())).Images);

    private static XisfUnit SolutionUnit()
    {
        var image = TestKit.GrayF32(4, 4, 0.5f) with
        {
            ImageId = "main",
            Properties = SolutionProperties()
        };
        return XisfFactory.CreateMonolithic(XisfFactory.CreateMinimalMetadata("T3"), image);
    }

    private static IReadOnlyList<XisfProperty> SolutionProperties() => new XisfProperty[]
    {
        new XisfStringProperty(Solution + "Version", "1.0"),
        new XisfStringProperty(Solution + "ProjectionSystem", "Gnomonic"),
        new XisfStringProperty(Solution + "CelestialReferenceSystem", "ICRS"),
        new XisfVectorProperty<double>(Solution + "ReferenceCelestialCoordinates", ReferenceCelestial),
        new XisfVectorProperty<double>(Solution + "ReferenceImageCoordinates", ReferenceImage),
        new XisfMatrixProperty<double>(Solution + "LinearTransformationMatrix", 2, 2, LinearTransformation),
        new XisfStringProperty(Distortion + "BasisFunction", "VariableOrder"),
        new XisfScalarProperty<int>(Distortion + "Order", 4),
        new XisfStringProperty(Distortion + "Terms", Terms),
        new XisfVectorProperty<int>(Distortion + "Local:X:NodeOffsets", NodeOffsets),
        new XisfMatrixProperty<double>(Distortion + "Local:X:Nodes", 6, 2, Nodes),
        new XisfVectorProperty<double>(Distortion + "Local:X:Coefficients", Coefficients),
        new XisfScalarProperty<double>(Distortion + "Fallback:Threshold", FallbackThreshold),
        new XisfStringProperty(Solution + "FutureProp", "solver-private-token")
    };

    private static byte[] AttachedBlob()
    {
        var blob = new byte[PixelBytes + 16 + 16];
        for (var i = 0; i < PixelBytes; i++)
            blob[i] = (byte)(i + 1);

        BinaryPrimitives.WriteDoubleLittleEndian(blob.AsSpan(PixelBytes, 8), ReferenceCelestial[0]);
        BinaryPrimitives.WriteDoubleLittleEndian(blob.AsSpan(PixelBytes + 8, 8), ReferenceCelestial[1]);
        for (var i = 0; i < NodeOffsets.Length; i++)
            BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(PixelBytes + 16 + i * 4, 4), NodeOffsets[i]);

        return blob;
    }

    private const int PixelBytes = 8;

    private static string AttachedSolutionHeader(ulong pos) =>
        $"""
         <?xml version="1.0" encoding="UTF-8"?>
         <xisf version="1.0"{HeaderProbe.NamespaceDeclarations}>
           <Metadata>
             <Property id="XISF:CreationTime" type="TimePoint" value="2026-09-19T00:00:00.000Z"/>
             <Property id="XISF:CreatorApplication" type="String">PixInsight 1.9.5</Property>
           </Metadata>
           <Image geometry="2:2:1" sampleFormat="UInt16" colorSpace="Gray" location="attachment:{pos}:{PixelBytes}">
             <Property id="AstrometricSolution:Version" type="String">1.0</Property>
             <Property id="AstrometricSolution:ProjectionSystem" type="String">Gnomonic</Property>
             <Property id="AstrometricSolution:ReferenceCelestialCoordinates" type="F64Vector" length="2" location="attachment:{pos + PixelBytes}:16"/>
             <Property id="AstrometricSolution:DistortionModel:ImageToProjection:Local:X:NodeOffsets" type="I32Vector" length="4" location="attachment:{pos + PixelBytes + 16}:16"/>
             <Property id="AstrometricSolution:FutureBlob" type="FutureType" rank="7">AAECAwQF</Property>
           </Image>
         </xisf>
         """;

    private static T Property<T>(XisfImage image, string id) where T : XisfProperty =>
        Assert.IsType<T>(image.Properties!.Single(p => p.Id == id));

    private static void AssertBitExact(double expected, double actual) =>
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(actual));

    private static void AssertBitExact(IReadOnlyList<double> expected, IReadOnlyList<double> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
            AssertBitExact(expected[i], actual[i]);
    }
}
