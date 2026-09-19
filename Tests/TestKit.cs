using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace XisfLib.Core.Tests;

/// <summary>
/// A monolithic file assembled by <see cref="TestKit.BuildMonolithicFile"/>, together with the
/// absolute byte positions and sizes chosen for its attached blocks.
/// </summary>
public sealed record MonolithicFile(byte[] Bytes, IReadOnlyList<ulong> Positions, IReadOnlyList<ulong> Sizes);

/// <summary>
/// Shared harness for the Rev-1 conformance suite. Signatures are frozen by
/// Journal/20260919.rev1-implementation-contract.md ("Test harness contract").
/// </summary>
public static class TestKit
{
    /// <summary>Bytes preceding the XML header in a monolithic file: signature(8) + length(4) + reserved(4).</summary>
    public const int HeaderOffset = 16;

    public const string XisfNamespace = "http://www.pixinsight.com/xisf";

    public static readonly DateTimeOffset FixedCreationTime =
        new(2026, 9, 19, 12, 34, 56, 789, TimeSpan.Zero);

    // ==================== Round-trip / read harness ====================

    public static async Task<XisfUnit> RoundTrip(XisfUnit unit, XisfWriterOptions? w = null, XisfReaderOptions? r = null)
    {
        var bytes = await WriteBytes(unit, w);
        return await ReadBytes(bytes, r);
    }

    public static async Task<XisfUnit> ReadBytes(byte[] file, XisfReaderOptions? r = null)
    {
        using var stream = new MemoryStream(file, writable: false);
        using var reader = new XisfReader(r ?? new XisfReaderOptions());
        return await reader.ReadAsync(stream, ".xisf");
    }

    public static async Task<byte[]> WriteBytes(XisfUnit unit, XisfWriterOptions? w = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new XisfWriter(w ?? new XisfWriterOptions()))
        {
            await writer.WriteAsync(unit, stream);
        }
        return stream.ToArray();
    }

    /// <summary>Writes the unit and returns its XML header parsed, for assertions about emitted form.</summary>
    public static async Task<XDocument> WrittenHeader(XisfUnit unit, XisfWriterOptions? w = null)
        => ParseHeader(await WriteBytes(unit, w));

    public static XDocument ParseHeader(byte[] file)
    {
        var headerLength = BitConverter.ToUInt32(file, 8);
        return XDocument.Parse(Encoding.UTF8.GetString(file, HeaderOffset, (int)headerLength));
    }

    // ==================== Monolithic file assembly ====================

    private static readonly Regex PlaceholderRegex =
        new(@"\{(POS|POSITION|SIZE)(\d+)\}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The byte position of the first attached block in a file built by
    /// <see cref="BuildMonolithic"/>: the declared header is padded with spaces so callers can
    /// hard-code <c>attachment:4096:size</c>. Later blocks follow contiguously. A header too long
    /// to fit pushes the base to the next multiple of this value.
    /// </summary>
    public const int BlockBase = 4096;

    /// <summary>
    /// Assembles signature + headerLength(uint32 LE) + 4 reserved zero bytes + UTF-8 header +
    /// padding + the attached blocks, in the order given, the first at <see cref="BlockBase"/>.
    /// The header may instead reference the layout with {POS0}/{SIZE0}, {POS1}/… — substituted to
    /// a fixed point, so `attachment:pos:size` always matches. A header with no attached blocks is
    /// emitted verbatim, unpadded.
    /// </summary>
    public static byte[] BuildMonolithic(string headerXml, params byte[][] attachedBlocks)
        => BuildMonolithicFile(headerXml, attachedBlocks).Bytes;

    public static MonolithicFile BuildMonolithicFile(string headerXml, params byte[][] attachedBlocks)
    {
        var sizes = attachedBlocks.Select(b => (ulong)b.Length).ToArray();
        var positions = new ulong[attachedBlocks.Length];

        var xml = Encoding.UTF8.GetBytes(Substitute(headerXml, positions, sizes));
        var headerLength = xml.Length;

        if (attachedBlocks.Length > 0)
        {
            var basePosition = AlignedBase(xml.Length);
            for (var iteration = 0; iteration < 8; iteration++)
            {
                var cursor = basePosition;
                for (var i = 0; i < positions.Length; i++)
                {
                    positions[i] = cursor;
                    cursor += sizes[i];
                }

                xml = Encoding.UTF8.GetBytes(Substitute(headerXml, positions, sizes));
                var nextBase = AlignedBase(xml.Length);
                if (nextBase == basePosition)
                    break;

                basePosition = nextBase;
            }

            headerLength = (int)basePosition - HeaderOffset;
        }

        var headerBytes = new byte[headerLength];
        headerBytes.AsSpan().Fill((byte)' ');
        xml.CopyTo(headerBytes, 0);

        using var buffer = new MemoryStream();
        buffer.Write(Encoding.ASCII.GetBytes("XISF0100"));
        buffer.Write(BitConverter.GetBytes((uint)headerBytes.Length));
        buffer.Write(new byte[4]);
        buffer.Write(headerBytes);
        foreach (var block in attachedBlocks)
            buffer.Write(block);

        return new MonolithicFile(buffer.ToArray(), positions, sizes);
    }

    private static ulong AlignedBase(int xmlLength)
    {
        var needed = HeaderOffset + xmlLength;
        var blocks = (needed + BlockBase - 1) / BlockBase;
        return (ulong)(Math.Max(blocks, 1) * BlockBase);
    }

    private static string Substitute(string template, IReadOnlyList<ulong> positions, IReadOnlyList<ulong> sizes)
        => PlaceholderRegex.Replace(template, match =>
        {
            var index = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            if (index >= positions.Count)
                return match.Value;

            var kind = match.Groups[1].Value.ToUpperInvariant();
            var value = kind == "SIZE" ? sizes[index] : positions[index];
            return value.ToString(CultureInfo.InvariantCulture);
        });

    /// <summary>Wraps body elements in a valid namespaced XISF header with the mandatory metadata.</summary>
    public static string HeaderXml(params string[] bodyElements)
        => Header(XisfNamespace, bodyElements);

    /// <summary>The same, with no namespace declaration — the form this library emitted through v1.2.</summary>
    public static string BareHeaderXml(params string[] bodyElements)
        => Header(null, bodyElements);

    private static string Header(string? ns, IReadOnlyList<string> bodyElements)
    {
        var xmlns = ns is null ? "" : $" xmlns=\"{ns}\"";
        var creationTime = FixedCreationTime.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
        var body = string.Concat(bodyElements);
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
               $"<xisf version=\"1.0\"{xmlns}>" +
               "<Metadata>" +
               $"<Property id=\"XISF:CreationTime\" type=\"TimePoint\" value=\"{creationTime}\"/>" +
               "<Property id=\"XISF:CreatorApplication\" type=\"String\" value=\"TestKit\"/>" +
               "</Metadata>" +
               body +
               "</xisf>";
    }

    // ==================== Minimal valid inputs ====================

    public static XisfMetadata Metadata(string creatorApplication = "XisfLib.Core.Tests")
        => new(FixedCreationTime, creatorApplication);

    public static XisfImage Gray8(int w, int h, byte fill)
    {
        var data = new byte[w * h];
        Array.Fill(data, fill);
        return new XisfImage(
            new XisfImageGeometry(new[] { (uint)w, (uint)h }, 1),
            XisfSampleFormat.UInt8,
            XisfColorSpace.Gray,
            new InlineDataBlock(data, XisfEncoding.Base64));
    }

    public static XisfImage GrayF32(int w, int h, float fill)
    {
        var data = new byte[w * h * sizeof(float)];
        for (var i = 0; i < w * h; i++)
            BitConverter.GetBytes(fill).CopyTo(data, i * sizeof(float));

        return new XisfImage(
            new XisfImageGeometry(new[] { (uint)w, (uint)h }, 1),
            XisfSampleFormat.Float32,
            XisfColorSpace.Gray,
            new InlineDataBlock(data, XisfEncoding.Base64),
            Bounds: new XisfImageBounds(0, 1));
    }

    public static XisfUnit Unit(params XisfImage[] images)
        => new(new MonolithicStorage(),
               new XisfHeader(Metadata(), new Dictionary<string, XisfCoreElement>()),
               images,
               Array.Empty<XisfProperty>());

    public static XisfUnit PropertyUnit(params XisfProperty[] globalProperties)
        => new(new MonolithicStorage(),
               new XisfHeader(Metadata(), new Dictionary<string, XisfCoreElement>()),
               Array.Empty<XisfImage>(),
               globalProperties);

    public static XisfUnit UnitWithMetadata(XisfMetadata metadata, params XisfImage[] images)
        => new(new MonolithicStorage(),
               new XisfHeader(metadata, new Dictionary<string, XisfCoreElement>()),
               images,
               Array.Empty<XisfProperty>());

    // ==================== Small utilities ====================

    public static ReadOnlyMemory<byte> Pixels(XisfImage image) => image.PixelData switch
    {
        InlineDataBlock inline => inline.Data,
        EmbeddedDataBlock embedded => embedded.Data,
        _ => throw new InvalidOperationException($"Pixel data was not materialized: {image.PixelData.GetType().Name}")
    };

    public static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }

    public static string Base64(byte[] data) => Convert.ToBase64String(data);

    public static XElement? FindProperty(XDocument document, string id)
        => document.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Property" && (string?)e.Attribute("id") == id);

    /// <summary>Property value per §11.1.4/§11.1.6: the value attribute, else character content.</summary>
    public static string? PropertyValue(XElement property)
    {
        var attribute = (string?)property.Attribute("value");
        if (!string.IsNullOrEmpty(attribute))
            return attribute;

        var text = property.Value;
        return string.IsNullOrEmpty(text) ? null : text.Trim();
    }
}
