using Xunit;
using static XisfLib.Core.Tests.CodecKit;

namespace XisfLib.Core.Tests;

/// <summary>
/// XISF 1.0 Revision 2 §10.6.1: every compressed subblock must decompress to exactly its declared
/// length, otherwise the data block is invalid. Every such failure surfaces as
/// <see cref="InvalidDataException"/>, never as a codec or argument exception.
/// </summary>
public sealed class BlockIntegrityTests
{
    private const int Length = 1024;

    public static TheoryData<XisfCompressionCodec> BaseCodecs() => new()
    {
        XisfCompressionCodec.Zlib,
        XisfCompressionCodec.LZ4,
        XisfCompressionCodec.LZ4HC,
        XisfCompressionCodec.Zstd,
    };

    private static async Task AssertInvalid(byte[] file)
        => await Assert.ThrowsAsync<InvalidDataException>(() => TestKit.ReadBytes(file));

    [Theory]
    [MemberData(nameof(BaseCodecs))]
    public async Task Read_OutputLongerThanDeclared_IsInvalid(XisfCompressionCodec codec)
    {
        var compressed = Compress(codec, Ramp(Length));
        var declared = Length / 2;
        Assert.NotEqual(compressed.Length, declared);

        await AssertInvalid(File(declared, compressed, $"compression=\"{Attribute(codec, declared)}\""));
    }

    [Theory]
    [MemberData(nameof(BaseCodecs))]
    public async Task Read_OutputShorterThanDeclared_IsInvalid(XisfCompressionCodec codec)
    {
        var compressed = Compress(codec, Ramp(Length));
        var declared = Length * 2;

        await AssertInvalid(File(declared, compressed, $"compression=\"{Attribute(codec, declared)}\""));
    }

    [Theory]
    [MemberData(nameof(BaseCodecs))]
    public async Task Read_CorruptCompressedData_IsInvalid(XisfCompressionCodec codec)
    {
        var garbage = Enumerable.Repeat((byte)0xFF, 64).ToArray();

        await AssertInvalid(File(Length, garbage, $"compression=\"{Attribute(codec, Length)}\""));
    }

    [Theory]
    [MemberData(nameof(BaseCodecs))]
    public async Task Read_SubblockLengthsMisassigned_IsInvalid(XisfCompressionCodec codec)
    {
        var first = Compress(codec, Ramp(Length));
        var second = Compress(codec, Ramp(Length));

        await AssertInvalid(File(2 * Length, first.Concat(second).ToArray(),
            $"compression=\"{Attribute(codec, 2 * Length)}\"",
            Subblocks((first.Length, Length - 100), (second.Length, Length + 100))));
    }

    [Theory]
    [MemberData(nameof(BaseCodecs))]
    public async Task Read_SubblockUncompressedSumShortOfDeclared_IsInvalid(XisfCompressionCodec codec)
    {
        var first = Compress(codec, Ramp(Length));
        var second = Compress(codec, Ramp(Length));

        await AssertInvalid(File(2 * Length + 200, first.Concat(second).ToArray(),
            $"compression=\"{Attribute(codec, 2 * Length + 200)}\"",
            Subblocks((first.Length, Length), (second.Length, Length))));
    }

    [Theory]
    [MemberData(nameof(BaseCodecs))]
    public async Task Read_SubblockUncompressedSumBeyondDeclared_IsInvalid(XisfCompressionCodec codec)
    {
        var first = Compress(codec, Ramp(Length));
        var second = Compress(codec, Ramp(Length));

        await AssertInvalid(File(Length, first.Concat(second).ToArray(),
            $"compression=\"{Attribute(codec, Length)}\"",
            Subblocks((first.Length, Length), (second.Length, Length))));
    }

    [Theory]
    [MemberData(nameof(BaseCodecs))]
    public async Task Read_SubblockCompressedSumBeyondStoredBlock_IsInvalid(XisfCompressionCodec codec)
    {
        var first = Compress(codec, Ramp(Length));
        var second = Compress(codec, Ramp(Length));

        await AssertInvalid(File(2 * Length, first.Concat(second).ToArray(),
            $"compression=\"{Attribute(codec, 2 * Length)}\"",
            Subblocks((first.Length, Length), (second.Length + 50, Length))));
    }

    [Theory]
    [MemberData(nameof(BaseCodecs))]
    public async Task Read_SubblockCompressedSumShortOfStoredBlock_IsInvalid(XisfCompressionCodec codec)
    {
        var first = Compress(codec, Ramp(Length));
        var second = Compress(codec, Ramp(Length));
        var trailing = new byte[50];

        await AssertInvalid(File(2 * Length, first.Concat(second).Concat(trailing).ToArray(),
            $"compression=\"{Attribute(codec, 2 * Length)}\"",
            Subblocks((first.Length, Length), (second.Length, Length))));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("100,200:300")]
    [InlineData("100,200;300,400")]
    [InlineData("-1,200")]
    public async Task Read_MalformedSubblocksAttribute_HeaderParsesAndBlockIsUnsupported(string subblocks)
    {
        var compressed = Compress(XisfCompressionCodec.Zlib, Ramp(Length));
        var file = File(Length, compressed, $"compression=\"zlib:{Length}\"", $"subblocks=\"{subblocks}\"");

        using (var reader = new XisfReader())
        using (var stream = new MemoryStream(file, writable: false))
        {
            var info = Assert.Single(reader.ReadMetadataFromStream(stream).Images).DataBlockInfo;
            Assert.Null(info.Compression);
        }

        var error = await Assert.ThrowsAsync<XisfUnsupportedObjectException>(() => TestKit.ReadBytes(file));
        Assert.Contains("subblocks", error.Message);
    }

    // Valid zlib headers with FDICT set: inflate reports Z_NEED_DICT, which .NET surfaces as
    // ZLibException rather than InvalidDataException.
    public static TheoryData<XisfCompressionCodec, byte, byte> DictionaryHeaders()
    {
        var data = new TheoryData<XisfCompressionCodec, byte, byte>();
        foreach (var codec in new[] { XisfCompressionCodec.Zlib, XisfCompressionCodec.ZlibSh })
            foreach (var (cmf, flg) in new (byte, byte)[] { (0x78, 0x20), (0x78, 0x7D), (0x78, 0xBB), (0x78, 0xF9) })
                data.Add(codec, cmf, flg);
        return data;
    }

    [Theory]
    [MemberData(nameof(DictionaryHeaders))]
    public async Task Read_RawBlockStartingWithDictionaryZlibHeader_IsCopiedRaw(
        XisfCompressionCodec codec, byte cmf, byte flg)
    {
        var stream = new[] { cmf, flg }.Concat(Noise(Length - 2)).ToArray();
        var file = File(Length, stream, $"compression=\"{Attribute(codec, Length)}\"");

        Assert.Equal(Pixels(codec, stream), await ReadPixels(file));
    }

    [Theory]
    [MemberData(nameof(DictionaryHeaders))]
    public async Task Read_ShorterStreamWithDictionaryZlibHeader_IsInvalid(
        XisfCompressionCodec codec, byte cmf, byte flg)
    {
        var stored = new[] { cmf, flg }.Concat(Noise(62)).ToArray();

        await AssertInvalid(File(Length, stored, $"compression=\"{Attribute(codec, Length)}\""));
    }

    [Fact]
    public async Task Read_UnparsableShuffleItemSize_HeaderParsesAndBlockIsUnsupported()
    {
        var compressed = Compress(XisfCompressionCodec.ZlibSh, Shuffle(Ramp(Length), 2));
        var file = File(Length, compressed, $"compression=\"zlib+sh:{Length}:two\"");

        using (var reader = new XisfReader())
        using (var stream = new MemoryStream(file, writable: false))
        {
            var info = Assert.Single(reader.ReadMetadataFromStream(stream).Images).DataBlockInfo;
            Assert.Null(info.Compression);
        }

        var error = await Assert.ThrowsAsync<XisfUnsupportedObjectException>(() => TestKit.ReadBytes(file));
        Assert.Contains("item size", error.Message);
    }

    [Fact]
    public async Task Read_UnparsableThirdComponentOnNonShuffledCodec_IsIgnored()
    {
        var pixels = Ramp(Length);
        var file = File(Length, Compress(XisfCompressionCodec.Zlib, pixels), $"compression=\"zlib:{Length}:two\"");

        Assert.Equal(pixels, await ReadPixels(file));
    }

    [Fact]
    public async Task Read_UncompressedAttachedBlockBeyondArrayLimit_IsNotSupported()
    {
        var declared = (ulong)Array.MaxLength + 1;
        var file = TestKit.BuildMonolithic(
            TestKit.HeaderXml(
                $"<Image geometry=\"{Length / 2}:1:1\" sampleFormat=\"UInt16\" colorSpace=\"Gray\" " +
                $"location=\"attachment:{TestKit.BlockBase}:{declared}\"/>"),
            Noise(Length));

        await Assert.ThrowsAsync<NotSupportedException>(() => TestKit.ReadBytes(file));
    }

    [Fact]
    public async Task Read_SubblocksAttributeWithoutCompression_IsIgnored()
    {
        var pixels = Noise(Length);
        var file = File(Length, pixels, "subblocks=\"not,valid:at,all\"");

        Assert.Equal(pixels, await ReadPixels(file));
    }

    [Fact]
    public async Task Read_DeclaredSizeBeyondArrayLimit_IsNotSupported()
    {
        var declared = (ulong)Array.MaxLength + 1;
        var file = TestKit.BuildMonolithic(
            TestKit.HeaderXml(ImageElement(Length, $"compression=\"zlib:{declared}\"")),
            Compress(XisfCompressionCodec.Zlib, Ramp(Length)));

        await Assert.ThrowsAsync<NotSupportedException>(() => TestKit.ReadBytes(file));
    }
}
