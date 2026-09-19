using Xunit;

namespace XisfLib.Core.Tests;

// EXPECTED-RED (assessment §D "SerializeMetadata drops 15 of the 19 already-modeled fields",
// contract item 12):
//   DescriptiveStringFields_RoundTrip
//   ListFields_RoundTrip
//   CompressionFields_RoundTrip
//   CommaSeparatedStringFields_RoundTrip
//   OriginalCreationTime_RoundTrips
//   AllModeledFields_SurviveRoundTrip
//   NewlineSeparatedListFields_EmitNewlineSeparatedValue   — §11.4.2 (Authors/BibliographicReferences/
//                                                            Contributors/RelatedResources)
//   NewlineSeparatedListFields_ParseFromCraftedHeader
//   CommaSeparatedFields_AreNotSplitIntoLists              — §11.4.2 (Keywords/Languages/
//                                                            CompressionCodecs/ChecksumAlgorithms)
//   BlockAlignmentSize_RoundTripsAsUInt16                  — Rev-1 new property
//   ChecksumAlgorithms_RoundTripsAsString                  — Rev-1 new property
//   MaxInlineBlockSize_RoundTripsAsUInt16                  — Rev-1 new property
//   OutputHints_RoundTripsAsString                         — Rev-1 new property
//   NewMetadataProperties_ParseFromCraftedHeader
//
// EXPECTED-GREEN (must not regress):
//   RequiredMetadata_RoundTrips
//   CreatorModuleAndCreatorOS_RoundTrip
//   RequiredMetadata_EmitsMandatoryProperties
public sealed class MetadataTests
{
    private static readonly DateTimeOffset OriginalCreation =
        new(2024, 6, 1, 3, 4, 5, 60, TimeSpan.FromHours(2));

    private static XisfMetadata Full() => new(
        TestKit.FixedCreationTime,
        "XisfLib.Core.Tests 1.3",
        CreatorModule: "XISF module version 01.00.00.0024",
        CreatorOS: "Windows",
        Authors: new[] { "James T. Kirk", "Spock" },
        Title: "The Pleiades",
        Description: "A long description of the resources in this unit.",
        Abstract: "A summary of the resources in this unit.",
        AccessRights: "Public domain",
        BibliographicReferences: new[] { "2019A&A...123..456X", "2020ApJ...789..012Y" },
        BriefDescription: "Messier 45, The Pleiades Cluster",
        CompressionLevel: 90,
        CompressionCodecs: "zstd+sh,lz4hc",
        Contributors: new[] { "Leonard McCoy", "Nyota Uhura" },
        Copyright: "Copyright (c) 2026 Extragalactic Imaging Corporation.",
        Keywords: "IC 342, spiral galaxy, deep-sky object",
        Languages: "en,fr",
        License: "CC BY-SA 4.0",
        OriginalCreationTime: OriginalCreation,
        RelatedResources: new[] { "https://example.com/a", "https://example.com/b" },
        BlockAlignmentSize: (ushort)4096,
        ChecksumAlgorithms: "sha-1,sha-256",
        MaxInlineBlockSize: (ushort)3072,
        OutputHints: "compression-codec zstd+sh compression-level 90");

    private static async Task<XisfMetadata> RoundTripFull()
        => (await TestKit.RoundTrip(TestKit.UnitWithMetadata(Full()))).Header.Metadata;

    // ==================== Mandatory properties (§11.4.1) ====================

    [Fact]
    public async Task RequiredMetadata_RoundTrips()
    {
        var metadata = TestKit.Metadata("XisfLib.Core.Tests 1.3");
        var back = (await TestKit.RoundTrip(TestKit.UnitWithMetadata(metadata))).Header.Metadata;

        Assert.Equal(TestKit.FixedCreationTime, back.CreationTime);
        Assert.Equal("XisfLib.Core.Tests 1.3", back.CreatorApplication);
    }

    [Fact]
    public async Task RequiredMetadata_EmitsMandatoryProperties()
    {
        var document = await TestKit.WrittenHeader(TestKit.UnitWithMetadata(TestKit.Metadata()));

        var creationTime = TestKit.FindProperty(document, "XISF:CreationTime")!;
        Assert.Equal("TimePoint", (string?)creationTime.Attribute("type"));

        var creatorApplication = TestKit.FindProperty(document, "XISF:CreatorApplication")!;
        Assert.Equal("String", (string?)creatorApplication.Attribute("type"));
        Assert.Equal("XisfLib.Core.Tests", TestKit.PropertyValue(creatorApplication));
    }

    [Fact]
    public async Task CreatorModuleAndCreatorOS_RoundTrip()
    {
        var back = await RoundTripFull();

        Assert.Equal("XISF module version 01.00.00.0024", back.CreatorModule);
        Assert.Equal("Windows", back.CreatorOS);
    }

    // ==================== Optional properties (§11.4.2) ====================

    [Fact]
    public async Task DescriptiveStringFields_RoundTrip()
    {
        var back = await RoundTripFull();

        Assert.Equal("The Pleiades", back.Title);
        Assert.Equal("A long description of the resources in this unit.", back.Description);
        Assert.Equal("A summary of the resources in this unit.", back.Abstract);
        Assert.Equal("Public domain", back.AccessRights);
        Assert.Equal("Messier 45, The Pleiades Cluster", back.BriefDescription);
        Assert.Equal("Copyright (c) 2026 Extragalactic Imaging Corporation.", back.Copyright);
        Assert.Equal("CC BY-SA 4.0", back.License);
    }

    [Fact]
    public async Task ListFields_RoundTrip()
    {
        var back = await RoundTripFull();

        Assert.Equal(new[] { "James T. Kirk", "Spock" }, back.Authors!);
        Assert.Equal(new[] { "2019A&A...123..456X", "2020ApJ...789..012Y" }, back.BibliographicReferences!);
        Assert.Equal(new[] { "Leonard McCoy", "Nyota Uhura" }, back.Contributors!);
        Assert.Equal(new[] { "https://example.com/a", "https://example.com/b" }, back.RelatedResources!);
    }

    [Fact]
    public async Task CompressionFields_RoundTrip()
    {
        var back = await RoundTripFull();

        Assert.Equal(90, back.CompressionLevel);
        Assert.Equal("zstd+sh,lz4hc", back.CompressionCodecs);
    }

    [Fact]
    public async Task CommaSeparatedStringFields_RoundTrip()
    {
        var back = await RoundTripFull();

        Assert.Equal("IC 342, spiral galaxy, deep-sky object", back.Keywords);
        Assert.Equal("en,fr", back.Languages);
    }

    [Fact]
    public async Task OriginalCreationTime_RoundTrips()
        => Assert.Equal(OriginalCreation, (await RoundTripFull()).OriginalCreationTime);

    [Fact]
    public async Task AllModeledFields_SurviveRoundTrip()
    {
        var expected = Full();
        var back = await RoundTripFull();

        Assert.Equal(expected.CreationTime, back.CreationTime);
        Assert.Equal(expected.CreatorApplication, back.CreatorApplication);
        Assert.Equal(expected.CreatorModule, back.CreatorModule);
        Assert.Equal(expected.CreatorOS, back.CreatorOS);
        Assert.Equal(expected.Authors!, back.Authors!);
        Assert.Equal(expected.Title, back.Title);
        Assert.Equal(expected.Description, back.Description);
        Assert.Equal(expected.Abstract, back.Abstract);
        Assert.Equal(expected.AccessRights, back.AccessRights);
        Assert.Equal(expected.BibliographicReferences!, back.BibliographicReferences!);
        Assert.Equal(expected.BriefDescription, back.BriefDescription);
        Assert.Equal(expected.CompressionLevel, back.CompressionLevel);
        Assert.Equal(expected.CompressionCodecs, back.CompressionCodecs);
        Assert.Equal(expected.Contributors!, back.Contributors!);
        Assert.Equal(expected.Copyright, back.Copyright);
        Assert.Equal(expected.Keywords, back.Keywords);
        Assert.Equal(expected.Languages, back.Languages);
        Assert.Equal(expected.License, back.License);
        Assert.Equal(expected.OriginalCreationTime, back.OriginalCreationTime);
        Assert.Equal(expected.RelatedResources!, back.RelatedResources!);
        Assert.Equal(expected.BlockAlignmentSize, back.BlockAlignmentSize);
        Assert.Equal(expected.ChecksumAlgorithms, back.ChecksumAlgorithms);
        Assert.Equal(expected.MaxInlineBlockSize, back.MaxInlineBlockSize);
        Assert.Equal(expected.OutputHints, back.OutputHints);
    }

    // ==================== List separators (§11.4.2) ====================

    [Theory]
    [InlineData("XISF:Authors")]
    [InlineData("XISF:BibliographicReferences")]
    [InlineData("XISF:Contributors")]
    [InlineData("XISF:RelatedResources")]
    public async Task NewlineSeparatedListFields_EmitNewlineSeparatedValue(string propertyId)
    {
        var document = await TestKit.WrittenHeader(TestKit.UnitWithMetadata(Full()));
        var element = TestKit.FindProperty(document, propertyId)!;

        Assert.Equal("String", (string?)element.Attribute("type"));

        var value = TestKit.PropertyValue(element)!;
        Assert.DoesNotContain(",", value);
        Assert.Equal(2, value.Split('\n').Length);
    }

    [Fact]
    public async Task NewlineSeparatedListFields_ParseFromCraftedHeader()
    {
        var unit = await ReadMetadata(
            "<Property id=\"XISF:Authors\" type=\"String\">James T. Kirk\nSpock\nLeonard McCoy</Property>",
            "<Property id=\"XISF:BibliographicReferences\" type=\"String\">first\nsecond</Property>",
            "<Property id=\"XISF:Contributors\" type=\"String\">alpha\nbeta</Property>",
            "<Property id=\"XISF:RelatedResources\" type=\"String\">one\ntwo</Property>");

        Assert.Equal(new[] { "James T. Kirk", "Spock", "Leonard McCoy" }, unit.Authors!);
        Assert.Equal(new[] { "first", "second" }, unit.BibliographicReferences!);
        Assert.Equal(new[] { "alpha", "beta" }, unit.Contributors!);
        Assert.Equal(new[] { "one", "two" }, unit.RelatedResources!);
    }

    [Fact]
    public async Task CommaSeparatedFields_AreNotSplitIntoLists()
    {
        var unit = await ReadMetadata(
            "<Property id=\"XISF:Keywords\" type=\"String\">IC 342, spiral galaxy</Property>",
            "<Property id=\"XISF:Languages\" type=\"String\">en,fr</Property>",
            "<Property id=\"XISF:CompressionCodecs\" type=\"String\">zstd+sh,lz4hc</Property>",
            "<Property id=\"XISF:ChecksumAlgorithms\" type=\"String\">sha-1,sha-256</Property>");

        Assert.Equal("IC 342, spiral galaxy", unit.Keywords);
        Assert.Equal("en,fr", unit.Languages);
        Assert.Equal("zstd+sh,lz4hc", unit.CompressionCodecs);
        Assert.Equal("sha-1,sha-256", unit.ChecksumAlgorithms);
    }

    // ==================== Rev-1 additions ====================

    [Fact]
    public async Task BlockAlignmentSize_RoundTripsAsUInt16()
    {
        var document = await TestKit.WrittenHeader(TestKit.UnitWithMetadata(Full()));
        var element = TestKit.FindProperty(document, "XISF:BlockAlignmentSize")!;

        Assert.Equal("UInt16", (string?)element.Attribute("type"));
        Assert.Equal("4096", TestKit.PropertyValue(element));
        Assert.Equal((ushort?)4096, (await RoundTripFull()).BlockAlignmentSize);
    }

    [Fact]
    public async Task MaxInlineBlockSize_RoundTripsAsUInt16()
    {
        var document = await TestKit.WrittenHeader(TestKit.UnitWithMetadata(Full()));
        var element = TestKit.FindProperty(document, "XISF:MaxInlineBlockSize")!;

        Assert.Equal("UInt16", (string?)element.Attribute("type"));
        Assert.Equal("3072", TestKit.PropertyValue(element));
        Assert.Equal((ushort?)3072, (await RoundTripFull()).MaxInlineBlockSize);
    }

    [Fact]
    public async Task ChecksumAlgorithms_RoundTripsAsString()
    {
        var document = await TestKit.WrittenHeader(TestKit.UnitWithMetadata(Full()));
        var element = TestKit.FindProperty(document, "XISF:ChecksumAlgorithms")!;

        Assert.Equal("String", (string?)element.Attribute("type"));
        Assert.Equal("sha-1,sha-256", TestKit.PropertyValue(element));
        Assert.Equal("sha-1,sha-256", (await RoundTripFull()).ChecksumAlgorithms);
    }

    [Fact]
    public async Task OutputHints_RoundTripsAsString()
    {
        var document = await TestKit.WrittenHeader(TestKit.UnitWithMetadata(Full()));
        var element = TestKit.FindProperty(document, "XISF:OutputHints")!;

        Assert.Equal("String", (string?)element.Attribute("type"));
        Assert.Equal("compression-codec zstd+sh compression-level 90", TestKit.PropertyValue(element));
        Assert.Equal("compression-codec zstd+sh compression-level 90", (await RoundTripFull()).OutputHints);
    }

    [Fact]
    public async Task NewMetadataProperties_ParseFromCraftedHeader()
    {
        var unit = await ReadMetadata(
            "<Property id=\"XISF:BlockAlignmentSize\" type=\"UInt16\" value=\"4096\"/>",
            "<Property id=\"XISF:MaxInlineBlockSize\" type=\"UInt16\" value=\"3072\"/>",
            "<Property id=\"XISF:ChecksumAlgorithms\" type=\"String\">sha256</Property>",
            "<Property id=\"XISF:OutputHints\" type=\"String\">no-compress-data</Property>");

        Assert.Equal((ushort?)4096, unit.BlockAlignmentSize);
        Assert.Equal((ushort?)3072, unit.MaxInlineBlockSize);
        Assert.Equal("sha256", unit.ChecksumAlgorithms);
        Assert.Equal("no-compress-data", unit.OutputHints);
    }

    // ==================== Helpers ====================

    /// <summary>Reads a unit whose Metadata element carries the mandatory pair plus the given properties.</summary>
    private static async Task<XisfMetadata> ReadMetadata(params string[] metadataProperties)
    {
        var headerXml = TestKit.HeaderXml()
            .Replace("</Metadata>", string.Concat(metadataProperties) + "</Metadata>");
        var unit = await TestKit.ReadBytes(TestKit.BuildMonolithic(headerXml));
        return unit.Header.Metadata;
    }
}
