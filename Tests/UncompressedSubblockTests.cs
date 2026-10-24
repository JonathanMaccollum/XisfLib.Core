using Xunit;
using static XisfLib.Core.Tests.CodecKit;

namespace XisfLib.Core.Tests;

/// <summary>
/// XISF 1.0 Revision 2 §10.6.1: subblocks with equal compressed and uncompressed lengths are stored
/// uncompressed, every other subblock is decompressed, and an encoder never expands a subblock.
/// </summary>
public sealed class UncompressedSubblockTests
{
    private const int Half = 1024;

    private static readonly XisfReaderOptions ProbeOff = new(TryDecompressEqualLengthSubblocks: false);

    public static TheoryData<XisfCompressionCodec, bool> CodecsAndProbe()
    {
        var data = new TheoryData<XisfCompressionCodec, bool>();
        foreach (var codec in AllCodecs)
        {
            data.Add(codec, true);
            data.Add(codec, false);
        }
        return data;
    }

    // ==================== Reader ====================

    [Theory]
    [MemberData(nameof(CodecsAndProbe))]
    public async Task Read_EqualLengthBlockWithoutSubblocks_IsCopiedRaw(XisfCompressionCodec codec, bool probe)
    {
        var pixels = Noise(2 * Half);
        var stored = Stream(codec, pixels);
        var file = File(pixels.Length, stored, $"compression=\"{Attribute(codec, pixels.Length)}\"");

        Assert.Equal(pixels, await ReadPixels(file, probe ? null : ProbeOff));
    }

    [Theory]
    [MemberData(nameof(CodecsAndProbe))]
    public async Task Read_EqualLengthExplicitSubblocks_AreCopiedRaw(XisfCompressionCodec codec, bool probe)
    {
        var pixels = Noise(2 * Half);
        var stored = Stream(codec, pixels);
        var file = File(pixels.Length, stored,
            $"compression=\"{Attribute(codec, pixels.Length)}\"",
            Subblocks((Half, Half), (Half, Half)));

        Assert.Equal(pixels, await ReadPixels(file, probe ? null : ProbeOff));
    }

    [Theory]
    [MemberData(nameof(CodecsAndProbe))]
    public async Task Read_MixedCompressedAndRawSubblocks_Concatenate(XisfCompressionCodec codec, bool probe)
    {
        var compressible = Ramp(Half);
        var raw = Noise(Half);
        var compressed = Compress(codec, compressible);
        Assert.True(compressed.Length < Half);

        var stream = compressible.Concat(raw).ToArray();
        var file = File(stream.Length, compressed.Concat(raw).ToArray(),
            $"compression=\"{Attribute(codec, stream.Length)}\"",
            Subblocks((compressed.Length, Half), (Half, Half)));

        Assert.Equal(Pixels(codec, stream), await ReadPixels(file, probe ? null : ProbeOff));
    }

    [Theory]
    [InlineData(XisfCompressionCodec.ZlibSh)]
    [InlineData(XisfCompressionCodec.LZ4Sh)]
    [InlineData(XisfCompressionCodec.LZ4HCSh)]
    [InlineData(XisfCompressionCodec.ZstdSh)]
    public async Task Read_ShuffledRawSubblocks_AreUnshuffledAfterConcatenation(XisfCompressionCodec codec)
    {
        var stream = Noise(2 * Half);
        var perSubblock = Unshuffle(stream[..Half], 2).Concat(Unshuffle(stream[Half..], 2)).ToArray();
        Assert.NotEqual(Unshuffle(stream, 2), perSubblock);

        var file = File(stream.Length, stream,
            $"compression=\"{Attribute(codec, stream.Length)}\"",
            Subblocks((Half, Half), (Half, Half)));

        Assert.Equal(Unshuffle(stream, 2), await ReadPixels(file));
    }

    [Theory]
    [MemberData(nameof(Codecs), MemberType = typeof(CodecKit))]
    public async Task Read_LegacyEqualLengthCompressedBlock_ProbeDecompresses(XisfCompressionCodec codec)
    {
        var (stream, compressed) = EqualLengthCompressed(codec);
        var file = File(stream.Length, compressed, $"compression=\"{Attribute(codec, stream.Length)}\"");

        Assert.Equal(Pixels(codec, stream), await ReadPixels(file));
    }

    [Theory]
    [MemberData(nameof(Codecs), MemberType = typeof(CodecKit))]
    public async Task Read_LegacyEqualLengthCompressedBlock_ProbeOff_ReadsRaw(XisfCompressionCodec codec)
    {
        var (stream, compressed) = EqualLengthCompressed(codec);
        var file = File(stream.Length, compressed, $"compression=\"{Attribute(codec, stream.Length)}\"");

        Assert.Equal(Pixels(codec, compressed), await ReadPixels(file, ProbeOff));
    }

    [Theory]
    [MemberData(nameof(Codecs), MemberType = typeof(CodecKit))]
    public async Task Read_LegacyEqualLengthCompressedSubblock_BesideRawSubblock_ProbeDecompresses(
        XisfCompressionCodec codec)
    {
        var (legacy, compressed) = EqualLengthCompressed(codec);
        var raw = Noise(Half, seed: 99);
        var stream = legacy.Concat(raw).ToArray();
        var file = File(stream.Length, compressed.Concat(raw).ToArray(),
            $"compression=\"{Attribute(codec, stream.Length)}\"",
            Subblocks((compressed.Length, legacy.Length), (Half, Half)));

        Assert.Equal(Pixels(codec, stream), await ReadPixels(file));
    }

    [Fact]
    public async Task Read_SmallRawNoiseBlock_Lz4_ProbeRejectsAndReadsRaw()
    {
        var pixels = Noise(64, seed: 7);
        var file = File(pixels.Length, pixels, $"compression=\"lz4:{pixels.Length}\"");

        Assert.Equal(pixels, await ReadPixels(file));
    }

    [Theory]
    [MemberData(nameof(Codecs), MemberType = typeof(CodecKit))]
    public async Task Read_CompressedLongerThanUncompressed_IsDecompressed(XisfCompressionCodec codec)
    {
        var pixels = Noise(2 * Half);
        var compressed = Compress(codec, Stream(codec, pixels));
        Assert.True(compressed.Length > pixels.Length);

        var file = File(pixels.Length, compressed, $"compression=\"{Attribute(codec, pixels.Length)}\"");

        Assert.Equal(pixels, await ReadPixels(file));
    }

    [Theory]
    [MemberData(nameof(Codecs), MemberType = typeof(CodecKit))]
    public async Task Read_LongerSubblockBesideRawSubblock_IsDecompressed(XisfCompressionCodec codec)
    {
        var stream = Noise(2 * Half);
        var expanded = Compress(codec, stream[..Half]);
        Assert.True(expanded.Length > Half);

        var file = File(stream.Length, expanded.Concat(stream[Half..]).ToArray(),
            $"compression=\"{Attribute(codec, stream.Length)}\"",
            Subblocks((expanded.Length, Half), (Half, Half)));

        Assert.Equal(Pixels(codec, stream), await ReadPixels(file));
    }

    // ==================== Writer ====================

    [Theory]
    [MemberData(nameof(Codecs), MemberType = typeof(CodecKit))]
    public async Task Write_IncompressibleBlock_DropsCompressionAndStoresOriginalBytes(XisfCompressionCodec codec)
    {
        var pixels = Noise(4 * Half);
        var file = await TestKit.WriteBytes(TestKit.Unit(Gray16(pixels)), new XisfWriterOptions(DefaultCompression: codec));

        var image = WrittenImage(file);
        Assert.Null(image.Attribute("compression"));
        Assert.Null(image.Attribute("subblocks"));
        Assert.Equal(pixels, Attachment(file));
        Assert.Equal(pixels, await ReadPixels(file));
    }

    [Theory]
    [MemberData(nameof(Codecs), MemberType = typeof(CodecKit))]
    public async Task Write_ExplicitSplit_StoresIncompressibleSubblockRaw(XisfCompressionCodec codec)
    {
        var stream = Ramp(Half).Concat(Noise(Half)).ToArray();
        var pixels = Pixels(codec, stream);
        var requested = new XisfCompression(codec, 0, IsShuffled(codec) ? 2u : null,
            new[] { new XisfCompressionSubblock(0, Half), new XisfCompressionSubblock(0, Half) });

        var file = await TestKit.WriteBytes(TestKit.Unit(Gray16(pixels, requested)));

        var image = WrittenImage(file);
        Assert.Equal(Attribute(codec, pixels.Length), image.Attribute("compression")?.Value);

        var subblocks = image.Attribute("subblocks")!.Value.Split(':')
            .Select(s => s.Split(',').Select(int.Parse).ToArray()).ToArray();
        Assert.Equal(2, subblocks.Length);
        Assert.True(subblocks[0][0] < subblocks[0][1]);
        Assert.Equal(new[] { Half, Half }, subblocks[1]);

        var stored = Attachment(file);
        Assert.Equal(subblocks[0][0] + Half, stored.Length);
        Assert.Equal(stream[Half..], stored[subblocks[0][0]..]);
        Assert.Equal(pixels, await ReadPixels(file));
    }

    [Theory]
    [MemberData(nameof(Codecs), MemberType = typeof(CodecKit))]
    public async Task Write_ExplicitSplit_NothingShrinks_DropsCompressionAndStoresOriginalBytes(
        XisfCompressionCodec codec)
    {
        var pixels = Noise(2 * Half);
        var requested = new XisfCompression(codec, 0, IsShuffled(codec) ? 2u : null,
            new[] { new XisfCompressionSubblock(0, Half), new XisfCompressionSubblock(0, Half) });

        var file = await TestKit.WriteBytes(TestKit.Unit(Gray16(pixels, requested)));

        var image = WrittenImage(file);
        Assert.Null(image.Attribute("compression"));
        Assert.Null(image.Attribute("subblocks"));
        Assert.Equal(pixels, Attachment(file));
        Assert.Equal(pixels, await ReadPixels(file));
    }

    [Theory]
    [MemberData(nameof(Codecs), MemberType = typeof(CodecKit))]
    public async Task Write_ExplicitSplit_AllCompressible_EverySubblockShorter(XisfCompressionCodec codec)
    {
        var pixels = Ramp(2 * Half);
        var requested = new XisfCompression(codec, 0, IsShuffled(codec) ? 2u : null,
            new[] { new XisfCompressionSubblock(0, Half), new XisfCompressionSubblock(0, Half) });

        var file = await TestKit.WriteBytes(TestKit.Unit(Gray16(pixels, requested)));

        var subblocks = WrittenImage(file).Attribute("subblocks")!.Value.Split(':')
            .Select(s => s.Split(',').Select(int.Parse).ToArray());
        Assert.All(subblocks, s => Assert.True(s[0] < s[1]));
        Assert.Equal(pixels, await ReadPixels(file));
    }
}
