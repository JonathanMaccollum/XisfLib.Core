using Xunit;

namespace XisfLib.Core.Tests;

// EXPECTED-RED:
//   DuplicateImageIds_AreRejected          — contract item 13 / assessment §D "Image id uniqueness is
//                                            now a MUST"; Validation.cs has no such check
//   DuplicateImageIds_WriteThrows          — same gap, through XisfWriter.WriteAsync
//   ShuffledCodecWithoutItemSize_IsRejected — contract item 13 / assessment §B "+sh with no ItemSize
//                                            silently skips shuffling"
//   InvalidPropertyId_IsRejected           — §8.4.1; two problems behind this one: ValidateUnit never
//                                            reaches ValidateImage (Validation.cs:74-97 ignores
//                                            unit.Images), and PropertyIdRegex uses '::' where the
//                                            spec's namespace separator is a single ':'
//
// EXPECTED-GREEN (must not regress):
//   DistinctImageIds_AreAccepted
//   ImagesWithoutIds_AreAccepted
//   ShuffledCodecWithItemSize_IsAccepted
//   NamespacedPropertyId_IsAccepted        — green today only because images are never validated;
//                                            once ValidateImage is reached it holds ONLY with the
//                                            corrected single-colon regex, which is the point of it
//   SimplePropertyId_IsAccepted
//   MissingCreatorApplication_IsRejected
//   InvalidUniqueElementId_IsRejected
//   ValidUniqueElementId_IsAccepted
public sealed class ValidationTests
{
    // ==================== Image id uniqueness (Rev-1 MUST) ====================

    [Fact]
    public void DuplicateImageIds_AreRejected()
    {
        var unit = TestKit.Unit(
            TestKit.Gray8(4, 4, 1) with { ImageId = "main" },
            TestKit.Gray8(4, 4, 2) with { ImageId = "main" });

        Assert.False(Validate(unit));
    }

    [Fact]
    public async Task DuplicateImageIds_WriteThrows()
    {
        var unit = TestKit.Unit(
            TestKit.Gray8(4, 4, 1) with { ImageId = "main" },
            TestKit.Gray8(4, 4, 2) with { ImageId = "main" });

        using var stream = new MemoryStream();
        using var writer = new XisfWriter();
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteAsync(unit, stream));
    }

    [Fact]
    public void DistinctImageIds_AreAccepted()
    {
        var unit = TestKit.Unit(
            TestKit.Gray8(4, 4, 1) with { ImageId = "main" },
            TestKit.Gray8(4, 4, 2) with { ImageId = "mask" });

        Assert.True(Validate(unit));
    }

    [Fact]
    public void ImagesWithoutIds_AreAccepted()
        => Assert.True(Validate(TestKit.Unit(TestKit.Gray8(4, 4, 1), TestKit.Gray8(4, 4, 2))));

    // ==================== Byte-shuffling codecs need an item size (§10.6.2) ====================

    [Theory]
    [InlineData(XisfCompressionCodec.ZlibSh)]
    [InlineData(XisfCompressionCodec.LZ4Sh)]
    [InlineData(XisfCompressionCodec.LZ4HCSh)]
    [InlineData(XisfCompressionCodec.ZstdSh)]
    public void ShuffledCodecWithoutItemSize_IsRejected(XisfCompressionCodec codec)
        => Assert.False(Validate(TestKit.Unit(CompressedImage(codec, itemSize: null))));

    [Theory]
    [InlineData(XisfCompressionCodec.ZlibSh)]
    [InlineData(XisfCompressionCodec.LZ4Sh)]
    [InlineData(XisfCompressionCodec.LZ4HCSh)]
    [InlineData(XisfCompressionCodec.ZstdSh)]
    public void ShuffledCodecWithItemSize_IsAccepted(XisfCompressionCodec codec)
        => Assert.True(Validate(TestKit.Unit(CompressedImage(codec, itemSize: 4))));

    // ==================== Property identifiers (§8.4.1) ====================

    [Theory]
    [InlineData("Namespace:Property")]
    [InlineData("foo:bar:Foo2_Bar3")]
    [InlineData("Observation:Time:Start")]
    public void NamespacedPropertyId_IsAccepted(string id)
        => Assert.True(Validate(TestKit.Unit(ImageWithProperty(id))));

    [Theory]
    [InlineData("MyFirstProperty")]
    [InlineData("mySecondOne234")]
    [InlineData("_this_1_is_a_test")]
    public void SimplePropertyId_IsAccepted(string id)
        => Assert.True(Validate(TestKit.Unit(ImageWithProperty(id))));

    [Theory]
    [InlineData("9Bad")]
    [InlineData("bad-id")]
    [InlineData("Foo::Bar")]
    [InlineData("Foo:")]
    [InlineData(":Foo")]
    [InlineData("")]
    public void InvalidPropertyId_IsRejected(string id)
        => Assert.False(Validate(TestKit.Unit(ImageWithProperty(id))));

    // ==================== Existing validation regressions ====================

    [Fact]
    public void MissingCreatorApplication_IsRejected()
    {
        var metadata = new XisfMetadata(TestKit.FixedCreationTime, string.Empty);
        Assert.False(Validate(TestKit.UnitWithMetadata(metadata, TestKit.Gray8(4, 4, 1))));
    }

    [Fact]
    public void InvalidUniqueElementId_IsRejected()
        => Assert.False(Validate(UnitWithCoreElement("9bad")));

    [Fact]
    public void ValidUniqueElementId_IsAccepted()
        => Assert.True(Validate(UnitWithCoreElement("Ref_1")));

    // ==================== Helpers ====================

    private static bool Validate(XisfUnit unit)
    {
        using var writer = new XisfWriter();
        return writer.Validate(unit);
    }

    private static XisfImage CompressedImage(XisfCompressionCodec codec, uint? itemSize)
    {
        var image = TestKit.GrayF32(4, 4, 0.5f);
        var pixels = TestKit.Pixels(image);
        var compression = new XisfCompression(codec, (ulong)pixels.Length, itemSize);
        return image with { PixelData = new InlineDataBlock(pixels, XisfEncoding.Base64, Compression: compression) };
    }

    private static XisfImage ImageWithProperty(string propertyId)
        => TestKit.Gray8(4, 4, 1) with
        {
            Properties = new XisfProperty[] { new XisfStringProperty(propertyId, "value") }
        };

    private static XisfUnit UnitWithCoreElement(string uid)
        => new(new MonolithicStorage(),
               new XisfHeader(
                   TestKit.Metadata(),
                   new Dictionary<string, XisfCoreElement> { [uid] = new XisfResolution(72, 72, XisfResolutionUnit.Inch, uid) }),
               Array.Empty<XisfImage>(),
               Array.Empty<XisfProperty>());
}
