// Synthetic legacy fixtures. Everything here is built in-process (positions, zlib bytes and SHA-1
// digests all depend on each other, so a checked-in file could not stay self-consistent); no user
// image ever enters the repo.
//
// EXPECTED-GREEN (must-not-regress — these are the two header dialects the reader has to accept
//                 forever: our own v1.2 output with bare element names and value-attribute strings,
//                 and N.I.N.A./PixInsight output with the XISF namespace and hyphenated checksum
//                 names (XmlSerializer.cs:741-749, Journal/20260624-Checksum-Sha-Hyphenated-Names.md)):
//   V12Style_ReadsFully
//   V12Style_PixelBytesSurvive
//   V12Style_FitsKeywordsSurvive
//   NinaStyle_ReadsFully
//   NinaStyle_ZlibBlockDecompresses
//   NinaStyle_Sha1ChecksumVerifies
//   NinaStyle_ExposesEveryFitsKeyword
//   NinaStyle_TamperedBlockFailsChecksum
//   MetadataWithRev1AndUnknownProperties_ReadsWithoutError
//   MetadataWithRev1AndUnknownProperties_KeepsMandatoryFields
//   VersionAttribute_MustBe10 (Theory)
//
// EXPECTED-RED: none. This file exists solely to fail if the Rev-1 work breaks backward
// compatibility. A red here in wave 2 or 3 is a regression, never a pinned gap.

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Xunit;

namespace XisfLib.Core.Tests;

public sealed class LegacyCompatTests
{
    private static readonly byte[] Gray2x2UInt16 = { 1, 0, 2, 0, 3, 0, 4, 0 };

    private static readonly byte[] Gray4x4UInt16 = BuildRamp(32);

    [Fact]
    public async Task V12Style_ReadsFully()
    {
        var unit = await TestKit.ReadBytes(BuildV12File());

        Assert.Equal("XisfLib.Core", unit.Header.Metadata.CreatorApplication);
        var image = Assert.Single(unit.Images);
        Assert.Equal(XisfSampleFormat.UInt16, image.SampleFormat);
        Assert.Equal(XisfColorSpace.Gray, image.ColorSpace);
        Assert.Equal(XisfPixelStorage.Planar, image.PixelStorage);
        Assert.Equal(1u, image.Geometry.ChannelCount);
    }

    [Fact]
    public async Task V12Style_PixelBytesSurvive()
    {
        var unit = await TestKit.ReadBytes(BuildV12File());

        Assert.Equal(Gray2x2UInt16, PixelBytes(unit.Images[0]));
    }

    [Fact]
    public async Task V12Style_FitsKeywordsSurvive()
    {
        var unit = await TestKit.ReadBytes(BuildV12File());

        var keywords = FitsKeywords(unit.Images[0]);
        Assert.Equal(2, keywords.Count);
        Assert.Contains(keywords, k => k.Name == "IMAGETYP" && k.Value == "'LIGHT'");
        Assert.Contains(keywords, k => k.Name == "EXPTIME" && k.Comment == "seconds");
    }

    [Fact]
    public async Task NinaStyle_ReadsFully()
    {
        var unit = await TestKit.ReadBytes(BuildNinaFile());

        Assert.Equal("N.I.N.A. 3.1", unit.Header.Metadata.CreatorApplication);
        var image = Assert.Single(unit.Images);
        Assert.Equal(XisfSampleFormat.UInt16, image.SampleFormat);
        Assert.Equal(4u, image.Geometry.Width);
        Assert.Equal(4u, image.Geometry.Height);
    }

    [Fact]
    public async Task NinaStyle_ZlibBlockDecompresses()
    {
        var unit = await TestKit.ReadBytes(BuildNinaFile());

        Assert.Equal(Gray4x4UInt16, PixelBytes(unit.Images[0]));
    }

    [Fact]
    public async Task NinaStyle_Sha1ChecksumVerifies()
    {
        var unit = await TestKit.ReadBytes(BuildNinaFile(), new XisfReaderOptions(ValidateChecksums: true));

        Assert.Equal(Gray4x4UInt16, PixelBytes(unit.Images[0]));
    }

    [Fact]
    public async Task NinaStyle_TamperedBlockFailsChecksum()
    {
        var file = BuildNinaFile();
        file[^1] ^= 0xFF;

        await Assert.ThrowsAsync<InvalidDataException>(
            () => TestKit.ReadBytes(file, new XisfReaderOptions(ValidateChecksums: true)));
    }

    [Fact]
    public async Task NinaStyle_ExposesEveryFitsKeyword()
    {
        var unit = await TestKit.ReadBytes(BuildNinaFile());

        var keywords = FitsKeywords(unit.Images[0]);
        Assert.Equal(NinaKeywords.Length, keywords.Count);
        foreach (var (name, value, comment) in NinaKeywords)
            Assert.Contains(keywords, k => k.Name == name && k.Value == value && k.Comment == comment);
    }

    [Fact]
    public async Task MetadataWithRev1AndUnknownProperties_ReadsWithoutError()
    {
        var unit = await TestKit.ReadBytes(BuildRev1MetadataFile());

        Assert.Single(unit.Images);
    }

    [Fact]
    public async Task MetadataWithRev1AndUnknownProperties_KeepsMandatoryFields()
    {
        var unit = await TestKit.ReadBytes(BuildRev1MetadataFile());

        Assert.Equal("Rev1Producer", unit.Header.Metadata.CreatorApplication);
        Assert.Equal(
            new DateTimeOffset(2026, 9, 19, 0, 0, 0, TimeSpan.Zero),
            unit.Header.Metadata.CreationTime.ToUniversalTime());
    }

    [Theory]
    [InlineData("1.0", true)]
    [InlineData("1.1", false)]
    [InlineData("2.0", false)]
    [InlineData("", false)]
    public async Task VersionAttribute_MustBe10(string version, bool accepted)
    {
        var file = HeaderProbe.BuildAttached(pos => V12Header(pos, version), Gray2x2UInt16);

        if (accepted)
            Assert.Single((await TestKit.ReadBytes(file)).Images);
        else
            await Assert.ThrowsAsync<FormatException>(() => TestKit.ReadBytes(file));
    }

    private static byte[] BuildV12File() =>
        HeaderProbe.BuildAttached(pos => V12Header(pos, "1.0"), Gray2x2UInt16);

    private static byte[] BuildRev1MetadataFile() =>
        HeaderProbe.BuildAttached(Rev1MetadataHeader, Gray2x2UInt16);

    private static byte[] BuildNinaFile()
    {
        var stored = Zlib(Gray4x4UInt16);
        var digest = Convert.ToHexString(SHA1.HashData(stored)).ToLowerInvariant();
        return HeaderProbe.BuildAttached(pos => NinaHeader(pos, stored.Length, digest), stored);
    }

    private static byte[] PixelBytes(XisfImage image) =>
        ((InlineDataBlock)image.PixelData).Data.ToArray();

    private static System.Collections.Generic.List<XisfFitsKeyword> FitsKeywords(XisfImage image) =>
        (image.AssociatedElements ?? Array.Empty<XisfCoreElement>())
            .OfType<XisfFitsKeyword>()
            .ToList();

    private static byte[] BuildRamp(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
            bytes[i] = (byte)(i * 7 % 251);
        return bytes;
    }

    private static byte[] Zlib(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(data, 0, data.Length);
        return output.ToArray();
    }

    private static readonly (string Name, string Value, string Comment)[] NinaKeywords =
    {
        ("SIMPLE", "T", "C# FITS"),
        ("BITPIX", "16", "bits per pixel"),
        ("IMAGETYP", "'LIGHT'", "Type of exposure"),
        ("EXPOSURE", "120.0", "[s] Exposure duration"),
        ("FILTER", "'Ha'", "Active filter name"),
        ("XBINNING", "1", "X axis binning factor"),
        ("YBINNING", "1", "Y axis binning factor"),
        ("CCD-TEMP", "-10.0", "[degC] CCD temperature"),
        ("OBJECT", "'NGC 7000'", "Name of the object"),
        ("DATE-OBS", "'2026-09-18T22:14:03.121'", "Time of observation (UTC)")
    };

    /// The header this library emitted at v1.2: bare element names (no xmlns), String properties
    /// carried in a value attribute, no checksums, no compression, planar UInt16 gray at attachment.
    private static string V12Header(ulong pos, string version) =>
        $"""
         <?xml version="1.0" encoding="UTF-8"?>
         <xisf version="{version}" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:schemaLocation="http://www.pixinsight.com/xisf http://pixinsight.com/xisf/xisf-1.0.xsd">
           <Metadata>
             <Property id="XISF:CreationTime" type="TimePoint" value="2026-01-04T11:22:33.000+00:00"/>
             <Property id="XISF:CreatorApplication" type="String" value="XisfLib.Core"/>
             <Property id="XISF:CreatorOS" type="String" value="Windows"/>
           </Metadata>
           <Image geometry="2:2:1" sampleFormat="UInt16" colorSpace="Gray" location="attachment:{pos}:8">
             <FITSKeyword name="IMAGETYP" value="'LIGHT'" comment="Type of exposure"/>
             <FITSKeyword name="EXPTIME" value="300.0" comment="seconds"/>
           </Image>
         </xisf>
         """;

    /// N.I.N.A.-style: fully namespaced, hyphenated sha-1 checksum name, zlib block, long FITS header.
    private static string NinaHeader(ulong pos, int storedLength, string sha1Hex)
    {
        var keywordLines = string.Join(
            "\n",
            NinaKeywords.Select(k =>
                "    <FITSKeyword name=" + Quote(k.Name) +
                " value=" + Quote(k.Value) +
                " comment=" + Quote(k.Comment) + "/>"));

        return $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <xisf version="1.0"{HeaderProbe.NamespaceDeclarations}>
                  <Metadata>
                    <Property id="XISF:CreationTime" type="TimePoint" value="2026-09-18T22:16:11.000Z"/>
                    <Property id="XISF:CreatorApplication" type="String">N.I.N.A. 3.1</Property>
                  </Metadata>
                  <Image geometry="4:4:1" sampleFormat="UInt16" colorSpace="Gray" pixelStorage="Planar" compression="zlib:{Gray4x4UInt16.Length}" checksum="sha-1:{sha1Hex}" location="attachment:{pos}:{storedLength}">
                {keywordLines}
                  </Image>
                </xisf>
                """;
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "&quot;") + "\"";

    /// Rev-1 Metadata: the four new properties plus XISF:* names this build does not know.
    private static string Rev1MetadataHeader(ulong pos) =>
        $"""
         <?xml version="1.0" encoding="UTF-8"?>
         <xisf version="1.0"{HeaderProbe.NamespaceDeclarations}>
           <Metadata>
             <Property id="XISF:CreationTime" type="TimePoint" value="2026-09-19T00:00:00.000Z"/>
             <Property id="XISF:CreatorApplication" type="String">Rev1Producer</Property>
             <Property id="XISF:BlockAlignmentSize" type="UInt16" value="4096"/>
             <Property id="XISF:MaxInlineBlockSize" type="UInt16" value="3072"/>
             <Property id="XISF:ChecksumAlgorithms" type="String">sha-1,sha-256,sha-512</Property>
             <Property id="XISF:OutputHints" type="String">compression-codec zstd+sh</Property>
             <Property id="XISF:SomethingFromTheFuture" type="String">ignore me</Property>
             <Property id="XISF:AnotherFutureThing" type="Int32" value="17"/>
           </Metadata>
           <Image geometry="2:2:1" sampleFormat="UInt16" colorSpace="Gray" location="attachment:{pos}:8"/>
         </xisf>
         """;
}
