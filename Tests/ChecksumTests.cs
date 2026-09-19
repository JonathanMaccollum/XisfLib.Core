// Wave-1 expectation ledger for ChecksumTests (contract items 9 and 10; assessment §B, §D).
//
// EXPECTED-RED
//   Write_WithCalculateChecksums_EmitsChecksumAttribute            - contract 9: the monolithic writer
//                                                                    leaves Checksum null, so the
//                                                                    CalculateChecksums option is dead.
//   Write_Checksum_IsLowercaseHexDigestOfStoredBytes               - contract 9 / §10.5 digest form.
//   Write_Checksum_OfCompressedBlock_CoversStoredBytesNotPixels    - contract 9 / §10.6.1.
//   Write_ChecksumAlgorithmNames_AreHyphenated                     - contract 9 / §10.5 Table 9.
//   Write_ThenReadMetadata_BlockInfoCarriesChecksum                - contract 9, read-back of the above.
//   Read_Sha3Checksum_MaterializingBlockThrowsUnsupportedObject    - contract 10: today the uncomputable
//                                                                    algorithm surfaces as
//                                                                    NotSupportedException.
//
// EXPECTED-GREEN (must not regress)
//   Write_WithoutCalculateChecksums_EmitsNoChecksumAttribute
//   Read_ValidChecksum_MaterializesBlock                           - digest over the STORED bytes.
//   Read_CorruptedStoredBytes_ThrowsInvalidDataException
//   Read_CompressedBlock_BadChecksum_FailsBeforeDecompression      - §10.6.1 security rule; already
//                                                                    verify-then-decompress.
//   Read_CompressedBlock_GoodChecksumOverInvalidZlib_FailsInDecompression - control proving the
//                                                                    previous test discriminates.
//   Read_LegacyNonHyphenatedAlgorithmName_Parses
//   Read_ValidationDisabled_SkipsVerification
//   Read_Sha3ChecksumOnSecondImage_HeaderParseAndSiblingBlockRemainReadable - contract 10's "rest of the
//                                                                    unit stays accessible" half.

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Xunit;

namespace XisfLib.Core.Tests;

public sealed class ChecksumTests
{
    private const int BlockBase = 4096;
    private const string Sha3Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task Write_WithoutCalculateChecksums_EmitsNoChecksumAttribute()
    {
        var file = await WriteBytes(Unit(Gray16(Pattern(64))), new XisfWriterOptions());

        Assert.Null(ImageElement(file).Attribute("checksum"));
    }

    [Fact]
    public async Task Write_WithCalculateChecksums_EmitsChecksumAttribute()
    {
        var file = await WriteBytes(Unit(Gray16(Pattern(64))), new XisfWriterOptions(CalculateChecksums: true));

        var checksum = ImageElement(file).Attribute("checksum");

        Assert.NotNull(checksum);
        Assert.StartsWith("sha-1:", checksum!.Value);
    }

    [Fact]
    public async Task Write_Checksum_IsLowercaseHexDigestOfStoredBytes()
    {
        var pixels = Pattern(64);
        var file = await WriteBytes(Unit(Gray16(pixels)), new XisfWriterOptions(CalculateChecksums: true));

        var checksum = ImageElement(file).Attribute("checksum");
        Assert.NotNull(checksum);

        var parts = checksum!.Value.Split(':');
        Assert.Equal(2, parts.Length);
        Assert.Equal(parts[1].ToLowerInvariant(), parts[1]);
        Assert.Equal(Hex(SHA1.HashData(Attachment(file))), parts[1]);
    }

    [Fact]
    public async Task Write_Checksum_OfCompressedBlock_CoversStoredBytesNotPixels()
    {
        var pixels = Enumerable.Repeat((byte)0xA5, 256).ToArray();
        var file = await WriteBytes(Unit(Gray16(pixels)),
            new XisfWriterOptions(DefaultCompression: XisfCompressionCodec.Zlib, CalculateChecksums: true));

        var checksum = ImageElement(file).Attribute("checksum");
        Assert.NotNull(checksum);

        var stored = Attachment(file);
        // §10.6.1: for a compressed block the digest is computed for the compressed data, never the original.
        Assert.NotEqual(pixels.Length, stored.Length);
        Assert.Equal(Hex(SHA1.HashData(stored)), checksum!.Value.Split(':')[1]);
        Assert.NotEqual(Hex(SHA1.HashData(pixels)), checksum.Value.Split(':')[1]);
    }

    [Theory]
    [InlineData(XisfHashAlgorithm.SHA1, "sha-1", 20)]
    [InlineData(XisfHashAlgorithm.SHA256, "sha-256", 32)]
    [InlineData(XisfHashAlgorithm.SHA512, "sha-512", 64)]
    public async Task Write_ChecksumAlgorithmNames_AreHyphenated(
        XisfHashAlgorithm algorithm, string name, int digestLength)
    {
        var file = await WriteBytes(Unit(Gray16(Pattern(64))),
            new XisfWriterOptions(CalculateChecksums: true, ChecksumAlgorithm: algorithm));

        var checksum = ImageElement(file).Attribute("checksum");
        Assert.NotNull(checksum);

        var parts = checksum!.Value.Split(':');
        Assert.Equal(name, parts[0]);
        Assert.Equal(digestLength * 2, parts[1].Length);
        Assert.Equal(Hex(Digest(Attachment(file), algorithm)), parts[1]);
    }

    [Fact]
    public async Task Write_ThenReadMetadata_BlockInfoCarriesChecksum()
    {
        var file = await WriteBytes(Unit(Gray16(Pattern(64))),
            new XisfWriterOptions(CalculateChecksums: true, ChecksumAlgorithm: XisfHashAlgorithm.SHA256));

        var info = MetadataBlockInfo(file, 0);

        Assert.NotNull(info.Checksum);
        Assert.Equal(XisfHashAlgorithm.SHA256, info.Checksum!.Algorithm);
        Assert.Equal(Digest(Attachment(file), XisfHashAlgorithm.SHA256), info.Checksum.Digest.ToArray());
    }

    [Theory]
    [InlineData(XisfHashAlgorithm.SHA1, "sha-1")]
    [InlineData(XisfHashAlgorithm.SHA256, "sha-256")]
    [InlineData(XisfHashAlgorithm.SHA512, "sha-512")]
    public async Task Read_ValidChecksum_MaterializesBlock(XisfHashAlgorithm algorithm, string name)
    {
        var payload = Pattern(32);
        var file = TestKit.BuildMonolithic(
            Document(Gray8Element(payload.Length, payload.Length,
                $"checksum=\"{name}:{Hex(Digest(payload, algorithm))}\"")),
            payload);

        var unit = await TestKit.ReadBytes(file);

        Assert.Equal(payload, Pixels(unit.Images[0]));
    }

    [Fact]
    public async Task Read_LegacyNonHyphenatedAlgorithmName_Parses()
    {
        var payload = Pattern(32);
        var file = TestKit.BuildMonolithic(
            Document(Gray8Element(payload.Length, payload.Length,
                $"checksum=\"sha1:{Hex(SHA1.HashData(payload))}\"")),
            payload);

        var unit = await TestKit.ReadBytes(file);

        Assert.Equal(payload, Pixels(unit.Images[0]));
    }

    [Fact]
    public async Task Read_CorruptedStoredBytes_ThrowsInvalidDataException()
    {
        var payload = Pattern(32);
        var corrupted = payload.ToArray();
        corrupted[7] ^= 0xFF;

        var file = TestKit.BuildMonolithic(
            Document(Gray8Element(payload.Length, payload.Length,
                $"checksum=\"sha-1:{Hex(SHA1.HashData(payload))}\"")),
            corrupted);

        await Assert.ThrowsAsync<InvalidDataException>(() => TestKit.ReadBytes(file));
    }

    [Fact]
    public async Task Read_ValidationDisabled_SkipsVerification()
    {
        var payload = Pattern(32);
        var file = TestKit.BuildMonolithic(
            Document(Gray8Element(payload.Length, payload.Length,
                $"checksum=\"sha-1:{Hex(SHA1.HashData(Pattern(16)))}\"")),
            payload);

        var unit = await TestKit.ReadBytes(file, new XisfReaderOptions(ValidateChecksums: false));

        Assert.Equal(payload, Pixels(unit.Images[0]));
    }

    [Fact]
    public async Task Read_CompressedBlock_BadChecksum_FailsBeforeDecompression()
    {
        // Stored bytes are not a valid zlib stream, so any attempt to decompress raises a decompression
        // failure. §10.6.1 forbids decompressing a block whose checksum failed, so the surfaced failure
        // must be the checksum failure instead.
        var stored = Enumerable.Repeat((byte)0xFF, 16).ToArray();
        var file = TestKit.BuildMonolithic(
            Document(Gray8Element(16, stored.Length,
                "compression=\"zlib:16\"",
                $"checksum=\"sha-1:{Hex(SHA1.HashData(Pattern(8)))}\"")),
            stored);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => TestKit.ReadBytes(file));

        Assert.Contains("checksum", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Read_CompressedBlock_GoodChecksumOverInvalidZlib_FailsInDecompression()
    {
        var stored = Enumerable.Repeat((byte)0xFF, 16).ToArray();
        var file = TestKit.BuildMonolithic(
            Document(Gray8Element(16, stored.Length,
                "compression=\"zlib:16\"",
                $"checksum=\"sha-1:{Hex(SHA1.HashData(stored))}\"")),
            stored);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => TestKit.ReadBytes(file));

        Assert.DoesNotContain("checksum", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Read_Sha3Checksum_MaterializingBlockThrowsUnsupportedObject()
    {
        var payload = Pattern(32);
        var file = TestKit.BuildMonolithic(
            Document(Gray8Element(payload.Length, payload.Length, $"checksum=\"sha3-256:{Sha3Digest}\"")),
            payload);

        await Assert.ThrowsAsync<XisfUnsupportedObjectException>(() => TestKit.ReadBytes(file));
    }

    [Fact]
    public async Task Read_Sha3ChecksumOnSecondImage_HeaderParseAndSiblingBlockRemainReadable()
    {
        var first = Pattern(32);
        var second = Pattern(48);
        var file = TestKit.BuildMonolithic(
            Document(
                Gray8Element(first.Length, first.Length) +
                $"<Image geometry=\"{second.Length}:1:1\" sampleFormat=\"UInt8\" colorSpace=\"Gray\" " +
                $"location=\"attachment:{BlockBase + first.Length}:{second.Length}\" " +
                $"checksum=\"sha3-256:{Sha3Digest}\" />"),
            first, second);

        using var reader = new XisfReader();
        using var stream = new MemoryStream(file);
        var metadata = reader.ReadMetadataFromStream(stream);

        Assert.Equal(2, metadata.Images.Count);
        Assert.Equal((ulong)BlockBase, metadata.Images[0].DataBlockInfo.Position);
        Assert.Equal((ulong)first.Length, metadata.Images[0].DataBlockInfo.Size);
        Assert.Equal(XisfHashAlgorithm.SHA3_256, metadata.Images[1].DataBlockInfo.Checksum!.Algorithm);

        var unit = await TestKit.ReadBytes(file, new XisfReaderOptions(ValidateChecksums: false));
        Assert.Equal(first, Pixels(unit.Images[0]));
    }

    private static string Gray8Element(int pixelCount, int storedSize, params string[] extraAttributes)
        => $"<Image geometry=\"{pixelCount}:1:1\" sampleFormat=\"UInt8\" colorSpace=\"Gray\" " +
           $"location=\"attachment:{BlockBase}:{storedSize}\" {string.Join(" ", extraAttributes)} />";

    private static string Document(string body)
        => "<xisf version=\"1.0\">" +
           "<Metadata>" +
           "<Property id=\"XISF:CreationTime\" type=\"TimePoint\" value=\"2026-09-19T00:00:00.000+00:00\"/>" +
           "<Property id=\"XISF:CreatorApplication\" type=\"String\" value=\"XisfLib.Core.Tests\"/>" +
           "</Metadata>" + body + "</xisf>";

    private static XisfUnit Unit(XisfImage image)
        => XisfFactory.CreateMonolithic(XisfFactory.CreateMinimalMetadata("XisfLib.Core.Tests"), image);

    private static XisfImage Gray16(byte[] pixels)
        => new(new XisfImageGeometry(new[] { (uint)(pixels.Length / 2), 1u }, 1),
               XisfSampleFormat.UInt16,
               XisfColorSpace.Gray,
               new InlineDataBlock(pixels, XisfEncoding.Base64));

    private static byte[] Pixels(XisfImage image) => ((InlineDataBlock)image.PixelData).Data.ToArray();

    private static byte[] Digest(byte[] data, XisfHashAlgorithm algorithm) => algorithm switch
    {
        XisfHashAlgorithm.SHA1 => SHA1.HashData(data),
        XisfHashAlgorithm.SHA256 => SHA256.HashData(data),
        XisfHashAlgorithm.SHA512 => SHA512.HashData(data),
        _ => throw new NotSupportedException(algorithm.ToString())
    };

    private static string Hex(byte[] digest) => Convert.ToHexString(digest).ToLowerInvariant();

    private static async Task<byte[]> WriteBytes(XisfUnit unit, XisfWriterOptions options)
    {
        using var writer = new XisfWriter(options);
        using var stream = new MemoryStream();
        await writer.WriteAsync(unit, stream);
        return stream.ToArray();
    }

    private static XisfDataBlockInfo MetadataBlockInfo(byte[] file, int imageIndex)
    {
        using var reader = new XisfReader();
        using var stream = new MemoryStream(file);
        return reader.ReadMetadataFromStream(stream).Images[imageIndex].DataBlockInfo;
    }

    private static byte[] Attachment(byte[] file)
    {
        var info = MetadataBlockInfo(file, 0);
        return file.Skip((int)info.Position).Take((int)info.Size).ToArray();
    }

    private static XElement ImageElement(byte[] file)
    {
        var headerLength = (int)BitConverter.ToUInt32(file, 8);
        var xml = Encoding.UTF8.GetString(file, 16, headerLength);
        return XDocument.Parse(xml).Root!.Elements().First(e => e.Name.LocalName == "Image");
    }

    private static byte[] Pattern(int length)
    {
        var bytes = new byte[length];
        var state = 0x2468ACE0u;
        for (var i = 0; i < length; i++)
        {
            state = (state * 1664525u) + 1013904223u;
            bytes[i] = (byte)(state >> 24);
        }
        return bytes;
    }
}
