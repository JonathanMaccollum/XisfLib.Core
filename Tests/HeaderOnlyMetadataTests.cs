using System.Text;
using K4os.Compression.LZ4;
using Xunit;

namespace XisfLib.Core.Tests;

/// <summary>
/// WBPP 3.1 / PixInsight 1.9.5 masters store PixInsight:ProcessingHistory as a compressed String
/// property in an attached block near EOF. A metadata read over only the header bytes must leave
/// such properties unresolved instead of throwing.
/// </summary>
public sealed class HeaderOnlyMetadataTests
{
    private const string HistoryId = "PixInsight:ProcessingHistory";
    private const string SignatureId = "PCL:Signature:Integration";
    private const string SignatureText = "ImageIntegration 2026-10-24T01:02:03Z";

    private static readonly string History = string.Concat(Enumerable.Range(0, 200)
        .Select(i => $"<instance class=\"ImageIntegration\" id=\"step{i}\"><parameter id=\"rejection\" value=\"Rejection_ESD\"/></instance>\n"));

    private static readonly double[] VectorValues = { 1.5, -2.25, 3.125 };
    private static readonly double[] MatrixValues = { 1, 2, 3, 4 };

    private static byte[] Lz4Hc(byte[] data)
    {
        var target = new byte[LZ4Codec.MaximumOutputSize(data.Length)];
        var length = LZ4Codec.Encode(data, 0, data.Length, target, 0, target.Length, LZ4Level.L09_HC);
        return target[..length];
    }

    private static byte[] Shuffle(byte[] data, int itemSize)
    {
        var count = data.Length / itemSize;
        var shuffled = new byte[data.Length];
        for (var item = 0; item < count; item++)
            for (var b = 0; b < itemSize; b++)
                shuffled[b * count + item] = data[item * itemSize + b];
        return shuffled;
    }

    private static byte[] Doubles(double[] values) => values.SelectMany(BitConverter.GetBytes).ToArray();

    private static byte[] Pixels() => new byte[4 * 4 * sizeof(ushort)];

    private static MonolithicFile WbppMaster()
    {
        var history = Encoding.UTF8.GetBytes(History);
        var matrix = Doubles(MatrixValues);
        return TestKit.BuildMonolithicFile(
            TestKit.HeaderXml(
                "<Image geometry=\"4:4:1\" sampleFormat=\"UInt16\" colorSpace=\"Gray\" location=\"attachment:{POS0}:{SIZE0}\">",
                $"<Property id=\"{SignatureId}\" type=\"String\">{SignatureText}</Property>",
                $"<Property id=\"{HistoryId}\" type=\"String\" compression=\"lz4hc:{history.Length}\" location=\"attachment:{{POS1}}:{{SIZE1}}\"/>",
                $"<Property id=\"Test:Vector\" type=\"F64Vector\" length=\"{VectorValues.Length}\" location=\"attachment:{{POS2}}:{{SIZE2}}\"/>",
                $"<Property id=\"Test:Matrix\" type=\"F64Matrix\" rows=\"2\" columns=\"2\" compression=\"lz4hc+sh:{matrix.Length}:8\" location=\"attachment:{{POS3}}:{{SIZE3}}\"/>",
                "<FITSKeyword name=\"IMAGETYP\" value=\"'Master Light'\" comment=\"Type of image\"/>",
                "</Image>"),
            Pixels(),
            Lz4Hc(history),
            Doubles(VectorValues),
            Lz4Hc(Shuffle(matrix, 8)));
    }

    private static MonolithicFile HistoryLast()
    {
        var history = Encoding.UTF8.GetBytes(History);
        return TestKit.BuildMonolithicFile(
            TestKit.HeaderXml(
                "<Image geometry=\"4:4:1\" sampleFormat=\"UInt16\" colorSpace=\"Gray\" location=\"attachment:{POS0}:{SIZE0}\">",
                $"<Property id=\"{HistoryId}\" type=\"String\" compression=\"lz4hc:{history.Length}\" location=\"attachment:{{POS1}}:{{SIZE1}}\"/>",
                "</Image>"),
            Pixels(),
            Lz4Hc(history));
    }

    private static byte[] HeaderOnly(byte[] file)
        => file[..(TestKit.HeaderOffset + (int)BitConverter.ToUInt32(file, 8))];

    private static XisfImageMetadata ReadImage(Stream stream)
    {
        using var reader = new XisfReader();
        return Assert.Single(reader.ReadMetadataFromStream(stream).Images);
    }

    private static async Task<XisfImageMetadata> ReadImageAsync(Stream stream)
    {
        using var reader = new XisfReader();
        return Assert.Single((await reader.ReadMetadataFromStreamAsync(stream)).Images);
    }

    private static XisfProperty Property(XisfImageMetadata image, string id)
        => Assert.Single(image.Properties!, p => p.Id == id);

    private static void AssertUnresolvedHistory(XisfImageMetadata image, MonolithicFile file)
    {
        var history = Assert.IsType<XisfStringProperty>(Property(image, HistoryId));
        Assert.Equal("", history.Value);
        var unresolved = Assert.IsType<AttachedDataBlock>(history.UnresolvedBlock);
        Assert.Equal(file.Positions[1], unresolved.Position);
        Assert.Equal(file.Sizes[1], unresolved.Size);
        var compression = Assert.IsType<XisfCompression>(unresolved.Compression);
        Assert.Equal(XisfCompressionCodec.LZ4HC, compression.Codec);
        Assert.Equal((ulong)Encoding.UTF8.GetByteCount(History), compression.UncompressedSize);
        Assert.Null(compression.ItemSize);
    }

    [Fact]
    public void HeaderOnlyRead_LeavesAttachedPropertiesUnresolved()
    {
        var file = WbppMaster();
        using var stream = new MemoryStream(HeaderOnly(file.Bytes), writable: false);

        var image = ReadImage(stream);

        AssertUnresolvedHistory(image, file);

        var signature = Assert.IsType<XisfStringProperty>(Property(image, SignatureId));
        Assert.Equal(SignatureText, signature.Value);
        Assert.Null(signature.UnresolvedBlock);

        var fits = Assert.IsType<XisfFitsKeyword>(Assert.Single(image.AssociatedElements!));
        Assert.Equal("IMAGETYP", fits.Name);
        Assert.Equal("'Master Light'", fits.Value);
    }

    [Fact]
    public async Task HeaderOnlyReadAsync_LeavesAttachedPropertiesUnresolved()
    {
        var file = WbppMaster();
        using var stream = new MemoryStream(HeaderOnly(file.Bytes), writable: false);

        AssertUnresolvedHistory(await ReadImageAsync(stream), file);
    }

    [Fact]
    public void HeaderOnlyRead_VectorAndMatrixPlaceholdersCarryTheirBlocks()
    {
        var file = WbppMaster();
        using var stream = new MemoryStream(HeaderOnly(file.Bytes), writable: false);

        var image = ReadImage(stream);

        var vector = Assert.IsType<XisfVectorProperty<double>>(Property(image, "Test:Vector"));
        Assert.Empty(vector.Values);
        Assert.Equal(new AttachedDataBlock(file.Positions[2], file.Sizes[2]), vector.UnresolvedBlock);

        var matrix = Assert.IsType<XisfMatrixProperty<double>>(Property(image, "Test:Matrix"));
        Assert.Empty(matrix.Values);
        Assert.Equal(2u, matrix.Rows);
        Assert.Equal(2u, matrix.Columns);
        var unresolved = Assert.IsType<AttachedDataBlock>(matrix.UnresolvedBlock);
        Assert.Equal(file.Positions[3], unresolved.Position);
        Assert.Equal(file.Sizes[3], unresolved.Size);
        Assert.Equal(XisfCompressionCodec.LZ4HCSh, unresolved.Compression!.Codec);
        Assert.Equal((ulong)(MatrixValues.Length * sizeof(double)), unresolved.Compression.UncompressedSize);
        Assert.Equal(8u, unresolved.Compression.ItemSize);
    }

    [Fact]
    public void FullStreamMetadataRead_ResolvesAttachedProperties()
    {
        var file = WbppMaster();
        using var stream = new MemoryStream(file.Bytes, writable: false);

        var image = ReadImage(stream);

        var history = Assert.IsType<XisfStringProperty>(Property(image, HistoryId));
        Assert.Equal(History, history.Value);
        Assert.Null(history.UnresolvedBlock);

        var vector = Assert.IsType<XisfVectorProperty<double>>(Property(image, "Test:Vector"));
        Assert.Equal(VectorValues, vector.Values);
        Assert.Null(vector.UnresolvedBlock);

        var matrix = Assert.IsType<XisfMatrixProperty<double>>(Property(image, "Test:Matrix"));
        Assert.Equal(MatrixValues, matrix.Values);
        Assert.Null(matrix.UnresolvedBlock);
    }

    [Fact]
    public async Task FullRead_ResolvesAttachedProperties()
    {
        var unit = await TestKit.ReadBytes(WbppMaster().Bytes);

        var history = Assert.IsType<XisfStringProperty>(Assert.Single(unit.Images).Properties!.Single(p => p.Id == HistoryId));
        Assert.Equal(History, history.Value);
        Assert.Null(history.UnresolvedBlock);
    }

    [Fact]
    public void BlockEndingExactlyAtStreamEnd_Resolves()
    {
        var file = HistoryLast();
        Assert.Equal(file.Positions[1] + file.Sizes[1], (ulong)file.Bytes.Length);
        using var stream = new MemoryStream(file.Bytes, writable: false);

        var history = Assert.IsType<XisfStringProperty>(Property(ReadImage(stream), HistoryId));

        Assert.Equal(History, history.Value);
        Assert.Null(history.UnresolvedBlock);
    }

    [Fact]
    public void BlockEndingOneBytePastStreamEnd_StaysUnresolved()
    {
        var file = HistoryLast();
        using var stream = new MemoryStream(file.Bytes[..^1], writable: false);

        AssertUnresolvedHistory(ReadImage(stream), file);
    }

    [Fact]
    public void NonSeekableStream_LeavesAttachedPropertyUnresolved()
    {
        var file = HistoryLast();
        using var stream = new NonSeekableStream(HeaderOnly(file.Bytes));

        AssertUnresolvedHistory(ReadImage(stream), file);
    }

    [Fact]
    public async Task WritingHeaderOnlyProperties_ThrowsNamingTheProperty()
    {
        using var stream = new MemoryStream(HeaderOnly(WbppMaster().Bytes), writable: false);
        var metadata = ReadImage(stream);
        var unresolvedIds = metadata.Properties!.Where(p => p.UnresolvedBlock != null).Select(p => p.Id).ToList();
        var image = TestKit.Gray8(4, 4, 0) with { Properties = metadata.Properties };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => TestKit.WriteBytes(TestKit.Unit(image)));

        Assert.Contains(unresolvedIds, id => error.Message.Contains($"'{id}'"));
    }

    [Fact]
    public void HeaderOnlyRead_LeavesGlobalChecksummedPropertyUnresolvedWithoutValidating()
    {
        const string bogusDigest = "0000000000000000000000000000000000000000";
        var file = TestKit.BuildMonolithicFile(
            TestKit.HeaderXml(
                "<Image geometry=\"4:4:1\" sampleFormat=\"UInt16\" colorSpace=\"Gray\" location=\"attachment:{POS0}:{SIZE0}\"/>",
                $"<Property id=\"Test:Global\" type=\"String\" checksum=\"sha-1:{bogusDigest}\" location=\"attachment:{{POS1}}:{{SIZE1}}\"/>"),
            Pixels(),
            Encoding.UTF8.GetBytes("global value"));
        using var reader = new XisfReader(new XisfReaderOptions(ValidateChecksums: true));

        using (var full = new MemoryStream(file.Bytes, writable: false))
            Assert.Throws<InvalidDataException>(() => reader.ReadMetadataFromStream(full));

        using var headerOnly = new MemoryStream(HeaderOnly(file.Bytes), writable: false);
        var unit = reader.ReadMetadataFromStream(headerOnly);

        var global = Assert.IsType<XisfStringProperty>(Assert.Single(unit.GlobalProperties, p => p.Id == "Test:Global"));
        Assert.Equal("", global.Value);
        var unresolved = Assert.IsType<AttachedDataBlock>(global.UnresolvedBlock);
        Assert.Equal(file.Positions[1], unresolved.Position);
        Assert.Equal(file.Sizes[1], unresolved.Size);
        Assert.Equal(XisfHashAlgorithm.SHA1, unresolved.Checksum!.Algorithm);
    }

    private sealed class NonSeekableStream : Stream
    {
        private readonly MemoryStream _inner;

        public NonSeekableStream(byte[] data) => _inner = new MemoryStream(data, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
