using System.IO.Compression;
using K4os.Compression.LZ4;
using Xunit;

namespace XisfLib.Core.Tests;

/// <summary>
/// Independent codec implementations and payload generators for the Revision 2 subblock tests.
/// </summary>
public static class CodecKit
{
    public static readonly XisfCompressionCodec[] AllCodecs =
    {
        XisfCompressionCodec.Zlib, XisfCompressionCodec.ZlibSh,
        XisfCompressionCodec.LZ4, XisfCompressionCodec.LZ4Sh,
        XisfCompressionCodec.LZ4HC, XisfCompressionCodec.LZ4HCSh,
        XisfCompressionCodec.Zstd, XisfCompressionCodec.ZstdSh,
    };

    public static TheoryData<XisfCompressionCodec> Codecs()
    {
        var data = new TheoryData<XisfCompressionCodec>();
        foreach (var codec in AllCodecs)
            data.Add(codec);
        return data;
    }

    public static bool IsShuffled(XisfCompressionCodec codec)
        => codec is XisfCompressionCodec.ZlibSh or XisfCompressionCodec.LZ4Sh
            or XisfCompressionCodec.LZ4HCSh or XisfCompressionCodec.ZstdSh;

    public static string Name(XisfCompressionCodec codec) => codec switch
    {
        XisfCompressionCodec.Zlib => "zlib",
        XisfCompressionCodec.ZlibSh => "zlib+sh",
        XisfCompressionCodec.LZ4 => "lz4",
        XisfCompressionCodec.LZ4Sh => "lz4+sh",
        XisfCompressionCodec.LZ4HC => "lz4hc",
        XisfCompressionCodec.LZ4HCSh => "lz4hc+sh",
        XisfCompressionCodec.Zstd => "zstd",
        XisfCompressionCodec.ZstdSh => "zstd+sh",
        _ => throw new NotSupportedException(codec.ToString())
    };

    /// <summary>The compression attribute for a UInt16 block (item size 2 on the shuffled codecs).</summary>
    public static string Attribute(XisfCompressionCodec codec, int uncompressedSize)
        => IsShuffled(codec) ? $"{Name(codec)}:{uncompressedSize}:2" : $"{Name(codec)}:{uncompressedSize}";

    public static byte[] Compress(XisfCompressionCodec codec, byte[] data) => codec switch
    {
        XisfCompressionCodec.Zlib or XisfCompressionCodec.ZlibSh => Zlib(data),
        XisfCompressionCodec.LZ4 or XisfCompressionCodec.LZ4Sh => Lz4(data, LZ4Level.L00_FAST),
        XisfCompressionCodec.LZ4HC or XisfCompressionCodec.LZ4HCSh => Lz4(data, LZ4Level.L09_HC),
        XisfCompressionCodec.Zstd or XisfCompressionCodec.ZstdSh => Zstd(data),
        _ => throw new NotSupportedException(codec.ToString())
    };

    private static byte[] Zlib(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(data, 0, data.Length);
        return output.ToArray();
    }

    private static byte[] Lz4(byte[] data, LZ4Level level)
    {
        var target = new byte[LZ4Codec.MaximumOutputSize(data.Length)];
        var length = LZ4Codec.Encode(data, 0, data.Length, target, 0, target.Length, level);
        return target[..length];
    }

    private static byte[] Zstd(byte[] data)
    {
        using var compressor = new ZstdSharp.Compressor();
        return compressor.Wrap(data).ToArray();
    }

    /// <summary>Uniform random bytes: no codec reduces them, shuffled or not.</summary>
    public static byte[] Noise(int length, int seed = 1)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>Runs of 16 equal bytes: every codec reduces them, shuffled or not.</summary>
    public static byte[] Ramp(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
            bytes[i] = (byte)(i / 16);
        return bytes;
    }

    /// <summary>The block as stored by a codec: shuffled for the "+sh" codecs (item size 2).</summary>
    public static byte[] Stream(XisfCompressionCodec codec, byte[] pixels)
        => IsShuffled(codec) ? Shuffle(pixels, 2) : pixels;

    /// <summary>The pixels a stored (possibly shuffled) stream decodes to.</summary>
    public static byte[] Pixels(XisfCompressionCodec codec, byte[] stream)
        => IsShuffled(codec) ? Unshuffle(stream, 2) : stream;

    public static byte[] Shuffle(byte[] input, int itemSize)
    {
        var output = new byte[input.Length];
        var items = input.Length / itemSize;
        var target = 0;
        for (var j = 0; j < itemSize; j++)
            for (var i = 0; i < items; i++)
                output[target++] = input[i * itemSize + j];
        Array.Copy(input, items * itemSize, output, target, input.Length % itemSize);
        return output;
    }

    public static byte[] Unshuffle(byte[] input, int itemSize)
    {
        var output = new byte[input.Length];
        var items = input.Length / itemSize;
        var source = 0;
        for (var j = 0; j < itemSize; j++)
            for (var i = 0; i < items; i++)
                output[i * itemSize + j] = input[source++];
        Array.Copy(input, source, output, items * itemSize, input.Length % itemSize);
        return output;
    }

    /// <summary>
    /// A stream whose compressed form under <paramref name="codec"/> is exactly as long as the
    /// stream itself: the shape encoders predating Revision 2 could write as compressed data with
    /// equal lengths. Found by growing a zero prefix, which costs the codec almost nothing, in
    /// front of noise, which every codec expands slightly; the stream length stays even (UInt16).
    /// </summary>
    public static (byte[] Stream, byte[] Compressed) EqualLengthCompressed(XisfCompressionCodec codec)
    {
        foreach (var noiseLength in new[] { 1000, 1001 })
        {
            var noise = Noise(noiseLength);
            for (var prefix = 0; prefix <= 600; prefix++)
            {
                if ((prefix + noiseLength) % 2 != 0)
                    continue;

                var stream = new byte[prefix].Concat(noise).ToArray();
                var compressed = codec is XisfCompressionCodec.Zstd or XisfCompressionCodec.ZstdSh
                    ? ZstdFlushedBetween(new byte[prefix], noise)
                    : Compress(codec, stream);
                if (compressed.Length == stream.Length)
                    return (stream, compressed);
            }
        }

        throw new InvalidOperationException($"No equal-length {Name(codec)} stream found");
    }

    /// <summary>
    /// One-shot zstd keeps a block raw until compressing it saves several bytes, so its output
    /// length jumps past equality. Flushing between the parts gives the zeros an RLE block of their
    /// own, and the frame then grows one byte slower than the stream per added zero.
    /// </summary>
    private static byte[] ZstdFlushedBetween(byte[] first, byte[] second)
    {
        using var output = new MemoryStream();
        using (var zstd = new ZstdSharp.CompressionStream(output))
        {
            zstd.Write(first);
            zstd.Flush();
            zstd.Write(second);
        }
        return output.ToArray();
    }

    /// <summary>A UInt16 gray image element over one attached block.</summary>
    public static string ImageElement(int byteLength, params string[] attributes)
        => $"<Image geometry=\"{byteLength / 2}:1:1\" sampleFormat=\"UInt16\" colorSpace=\"Gray\" " +
           $"location=\"attachment:{{POS0}}:{{SIZE0}}\" {string.Join(" ", attributes)}/>";

    public static byte[] File(int byteLength, byte[] stored, params string[] attributes)
        => TestKit.BuildMonolithic(TestKit.HeaderXml(ImageElement(byteLength, attributes)), stored);

    public static string Subblocks(params (int Compressed, int Uncompressed)[] subblocks)
        => $"subblocks=\"{string.Join(":", subblocks.Select(s => $"{s.Compressed},{s.Uncompressed}"))}\"";

    public static async Task<byte[]> ReadPixels(byte[] file, XisfReaderOptions? options = null)
        => TestKit.Pixels((await TestKit.ReadBytes(file, options)).Images[0]).ToArray();

    public static System.Xml.Linq.XElement WrittenImage(byte[] file)
        => TestKit.ParseHeader(file).Root!.Elements().First(e => e.Name.LocalName == "Image");

    public static byte[] Attachment(byte[] file)
    {
        using var reader = new XisfReader();
        using var stream = new MemoryStream(file, writable: false);
        var info = reader.ReadMetadataFromStream(stream).Images[0].DataBlockInfo;
        return file.Skip((int)info.Position).Take((int)info.Size).ToArray();
    }

    public static XisfImage Gray16(byte[] pixels, XisfCompression? compression = null)
        => new(new XisfImageGeometry(new[] { (uint)(pixels.Length / 2), 1u }, 1),
               XisfSampleFormat.UInt16,
               XisfColorSpace.Gray,
               new InlineDataBlock(pixels, XisfEncoding.Base64, Compression: compression));
}
