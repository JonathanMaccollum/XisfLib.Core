using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

namespace XisfLib.Core.Implementations
{
    /// <summary>
    /// XML serializer for XISF headers and elements.
    /// Specification Reference: Section 9.5 XISF Header, Section 11 Core Elements
    /// </summary>
    internal sealed class XisfXmlSerializer : IXisfXmlSerializer
    {
        private readonly IXisfValidator _validator;
        private static readonly XNamespace XisfNamespace = "http://www.pixinsight.com/xisf";
        private static readonly XNamespace XsiNamespace = "http://www.w3.org/2001/XMLSchema-instance";
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        private const string TimePointFormat = "yyyy-MM-ddTHH:mm:ss.fffzzz";

        private static readonly HashSet<string> CoreElementNames = new(StringComparer.Ordinal)
        {
            "Reference", "Resolution", "FITSKeyword", "ColorFilterArray"
        };

        public XisfXmlSerializer(IXisfValidator validator)
        {
            _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        }

        #region Property type table

        private enum PropertyKind { Scalar, String, TimePoint, Vector, Matrix }

        private sealed record PropertyTypeInfo(PropertyKind Kind, Type ClrType, int ItemSize);

        /// <summary>
        /// Primary and alternate type identifiers of §8.4.4.1 Table 3, §8.4.4.3 Table 5,
        /// §8.4.4.4 Table 6, §8.4.4.5 Table 7 and §8.4.4.6 Table 8.
        /// </summary>
        private static readonly Dictionary<string, PropertyTypeInfo> PropertyTypes = BuildPropertyTypes();

        private static Dictionary<string, PropertyTypeInfo> BuildPropertyTypes()
        {
            var types = new Dictionary<string, PropertyTypeInfo>(StringComparer.OrdinalIgnoreCase)
            {
                ["Boolean"] = new(PropertyKind.Scalar, typeof(bool), 1),
                ["String"] = new(PropertyKind.String, typeof(string), 1),
                ["TimePoint"] = new(PropertyKind.TimePoint, typeof(DateTimeOffset), 1)
            };

            void Add(string scalar, string? scalarAlternate, string? vectorAlternate, string? matrixAlternate,
                     string vectorPrefix, Type clrType, int itemSize)
            {
                types[scalar] = new PropertyTypeInfo(PropertyKind.Scalar, clrType, itemSize);
                if (scalarAlternate != null)
                    types[scalarAlternate] = types[scalar];

                types[vectorPrefix + "Vector"] = new PropertyTypeInfo(PropertyKind.Vector, clrType, itemSize);
                if (vectorAlternate != null)
                    types[vectorAlternate] = types[vectorPrefix + "Vector"];

                types[vectorPrefix + "Matrix"] = new PropertyTypeInfo(PropertyKind.Matrix, clrType, itemSize);
                if (matrixAlternate != null)
                    types[matrixAlternate] = types[vectorPrefix + "Matrix"];
            }

            Add("Int8", null, null, null, "I8", typeof(sbyte), 1);
            Add("UInt8", "Byte", "ByteArray", "ByteMatrix", "UI8", typeof(byte), 1);
            Add("Int16", "Short", null, null, "I16", typeof(short), 2);
            Add("UInt16", "UShort", null, null, "UI16", typeof(ushort), 2);
            Add("Int32", "Int", "IVector", "IMatrix", "I32", typeof(int), 4);
            Add("UInt32", "UInt", "UIVector", "UIMatrix", "UI32", typeof(uint), 4);
            Add("Int64", null, null, null, "I64", typeof(long), 8);
            Add("UInt64", null, null, null, "UI64", typeof(ulong), 8);
            Add("Float32", "Float", null, null, "F32", typeof(float), 4);
            Add("Float64", "Double", "Vector", "Matrix", "F64", typeof(double), 8);

            return types;
        }

        #endregion

        #region Deserialization

        public XisfHeader DeserializeHeader(XDocument document)
        {
            var root = document.Root;
            if (root == null || root.Name.LocalName != "xisf")
                throw new FormatException("Invalid XISF header: missing or invalid root element");

            var version = root.Attribute("version")?.Value;
            if (version != "1.0")
                throw new FormatException($"Unsupported XISF version: {version}");

            string? initialComment = null;
            var firstNode = document.Nodes().FirstOrDefault();
            if (firstNode is XComment comment)
                initialComment = comment.Value;

            var metadataElement = root.Element(XisfNamespace + "Metadata") ??
                                  root.Element("Metadata");
            if (metadataElement == null)
                throw new FormatException("XISF header must contain a Metadata element");

            var metadata = DeserializeMetadata(metadataElement);

            var coreElements = new Dictionary<string, XisfCoreElement>();
            var unknownElements = new List<XElement>();

            foreach (var element in root.Elements())
            {
                var localName = element.Name.LocalName;
                if (localName == "Metadata" || localName == "Image" || localName == "Property")
                    continue;

                if (!CoreElementNames.Contains(localName))
                {
                    unknownElements.Add(new XElement(element));
                    continue;
                }

                var uid = element.Attribute("uid")?.Value;
                if (!string.IsNullOrEmpty(uid))
                    coreElements[uid] = DeserializeCoreElement(element);
            }

            return new XisfHeader(metadata, coreElements, initialComment)
            {
                UnknownElements = unknownElements
            };
        }

        private XisfMetadata DeserializeMetadata(XElement element)
        {
            var creationTimeStr = MetadataText(element, "XISF:CreationTime");
            var creatorApp = MetadataText(element, "XISF:CreatorApplication");

            if (creationTimeStr == null || creatorApp == null)
                throw new FormatException("Metadata must contain XISF:CreationTime and XISF:CreatorApplication");

            return new XisfMetadata(
                DateTimeOffset.Parse(creationTimeStr, Invariant),
                creatorApp,
                CreatorModule: MetadataText(element, "XISF:CreatorModule"),
                CreatorOS: MetadataText(element, "XISF:CreatorOS"),
                Authors: MetadataList(element, "XISF:Authors"),
                Title: MetadataText(element, "XISF:Title"),
                Description: MetadataText(element, "XISF:Description"),
                Abstract: MetadataText(element, "XISF:Abstract"),
                AccessRights: MetadataText(element, "XISF:AccessRights"),
                BibliographicReferences: MetadataList(element, "XISF:BibliographicReferences"),
                BriefDescription: MetadataText(element, "XISF:BriefDescription"),
                CompressionLevel: MetadataInt32(element, "XISF:CompressionLevel"),
                CompressionCodecs: MetadataText(element, "XISF:CompressionCodecs"),
                Contributors: MetadataList(element, "XISF:Contributors"),
                Copyright: MetadataText(element, "XISF:Copyright"),
                Keywords: MetadataText(element, "XISF:Keywords"),
                Languages: MetadataText(element, "XISF:Languages"),
                License: MetadataText(element, "XISF:License"),
                OriginalCreationTime: MetadataTimePoint(element, "XISF:OriginalCreationTime"),
                RelatedResources: MetadataList(element, "XISF:RelatedResources"),
                BlockAlignmentSize: MetadataUInt16(element, "XISF:BlockAlignmentSize"),
                ChecksumAlgorithms: MetadataText(element, "XISF:ChecksumAlgorithms"),
                MaxInlineBlockSize: MetadataUInt16(element, "XISF:MaxInlineBlockSize"),
                OutputHints: MetadataText(element, "XISF:OutputHints"));
        }

        private static string? MetadataText(XElement metadataElement, string propertyId)
        {
            var property = metadataElement.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "Property" &&
                                     e.Attribute("id")?.Value == propertyId);
            if (property == null)
                return null;

            var attribute = property.Attribute("value")?.Value;
            if (!string.IsNullOrEmpty(attribute))
                return attribute;

            var text = property.Value.Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }

        /// <summary>§11.4.2: Authors, BibliographicReferences, Contributors and RelatedResources are
        /// newline-separated lists; Keywords, Languages, CompressionCodecs and ChecksumAlgorithms are
        /// comma-separated strings that must stay whole.</summary>
        private static IReadOnlyList<string>? MetadataList(XElement metadataElement, string propertyId)
        {
            var text = MetadataText(metadataElement, propertyId);
            if (text == null)
                return null;

            var entries = text.Split('\n')
                .Select(entry => entry.Trim())
                .Where(entry => entry.Length > 0)
                .ToList();

            return entries.Count > 0 ? entries : null;
        }

        private static int? MetadataInt32(XElement metadataElement, string propertyId)
        {
            var text = MetadataText(metadataElement, propertyId);
            return text != null && int.TryParse(text, NumberStyles.Integer, Invariant, out var value) ? value : null;
        }

        private static ushort? MetadataUInt16(XElement metadataElement, string propertyId)
        {
            var text = MetadataText(metadataElement, propertyId);
            return text != null && ushort.TryParse(text, NumberStyles.Integer, Invariant, out var value) ? value : null;
        }

        private static DateTimeOffset? MetadataTimePoint(XElement metadataElement, string propertyId)
        {
            var text = MetadataText(metadataElement, propertyId);
            return text != null ? DateTimeOffset.Parse(text, Invariant) : null;
        }

        public XisfImage DeserializeImage(XElement element, XisfBlockReader? blockReader = null)
        {
            var geometryStr = element.Attribute("geometry")?.Value ??
                             throw new FormatException("Image element must have geometry attribute");
            var geometry = ParseGeometry(geometryStr);

            var sampleFormatStr = element.Attribute("sampleFormat")?.Value ??
                                 throw new FormatException("Image element must have sampleFormat attribute");
            var sampleFormat = Enum.Parse<XisfSampleFormat>(sampleFormatStr, ignoreCase: true);

            var colorSpaceStr = element.Attribute("colorSpace")?.Value ??
                               throw new FormatException("Image element must have colorSpace attribute");
            var colorSpace = Enum.Parse<XisfColorSpace>(colorSpaceStr, ignoreCase: true);

            var pixelData = ParseDataBlock(element) ??
                throw new FormatException("Image element must have either location attribute or Data child element");

            XisfImageBounds? bounds = null;
            var boundsStr = element.Attribute("bounds")?.Value;
            if (!string.IsNullOrEmpty(boundsStr))
            {
                var parts = boundsStr.Split(':');
                if (parts.Length == 2 &&
                    double.TryParse(parts[0], NumberStyles.Float, Invariant, out var lower) &&
                    double.TryParse(parts[1], NumberStyles.Float, Invariant, out var upper))
                {
                    bounds = new XisfImageBounds(lower, upper);
                }
            }

            var pixelStorageStr = element.Attribute("pixelStorage")?.Value;
            var pixelStorage = string.IsNullOrEmpty(pixelStorageStr) ?
                XisfPixelStorage.Planar : Enum.Parse<XisfPixelStorage>(pixelStorageStr, ignoreCase: true);

            var imageTypeStr = element.Attribute("imageType")?.Value;
            XisfImageType? imageType = string.IsNullOrEmpty(imageTypeStr) ?
                null : Enum.Parse<XisfImageType>(imageTypeStr, ignoreCase: true);

            var offsetStr = element.Attribute("offset")?.Value;
            double? offset = string.IsNullOrEmpty(offsetStr) ?
                null : double.Parse(offsetStr, Invariant);

            var imageId = element.Attribute("id")?.Value;

            var uuidStr = element.Attribute("uuid")?.Value;
            Guid? uuid = string.IsNullOrEmpty(uuidStr) ? null : Guid.Parse(uuidStr);

            var properties = new List<XisfProperty>();
            var associatedElements = new List<XisfCoreElement>();
            var unknownElements = new List<XElement>();

            foreach (var child in element.Elements())
            {
                var localName = child.Name.LocalName;
                if (localName == "Property")
                    properties.Add(DeserializeProperty(child, blockReader));
                else if (localName == "Data")
                    continue;
                else if (CoreElementNames.Contains(localName))
                    associatedElements.Add(DeserializeCoreElement(child));
                else
                    unknownElements.Add(new XElement(child));
            }

            return new XisfImage(
                geometry,
                sampleFormat,
                colorSpace,
                pixelData,
                bounds,
                pixelStorage,
                imageType,
                offset,
                null,
                imageId,
                uuid,
                properties.Count > 0 ? properties : null,
                associatedElements.Count > 0 ? associatedElements : null)
            {
                UnknownElements = unknownElements
            };
        }

        public XisfProperty DeserializeProperty(XElement element, XisfBlockReader? blockReader = null)
        {
            var id = element.Attribute("id")?.Value ??
                    throw new FormatException("Property element must have id attribute");

            var typeStr = element.Attribute("type")?.Value ??
                         throw new FormatException("Property element must have type attribute");

            if (!PropertyTypes.TryGetValue(typeStr, out var info))
                return new XisfRawProperty(id, new XElement(element));

            var comment = element.Attribute("comment")?.Value;

            switch (info.Kind)
            {
                case PropertyKind.String:
                    var (text, unresolvedText) = ReadStringValue(element, blockReader);
                    return unresolvedText is null
                        ? new XisfStringProperty(id, text, comment)
                        : new XisfStringProperty(id, text, comment) { UnresolvedBlock = unresolvedText };

                case PropertyKind.TimePoint:
                    var timeText = ScalarText(element);
                    if (string.IsNullOrEmpty(timeText))
                        throw new FormatException("TimePoint property must have value");
                    return new XisfTimePointProperty(id, DateTimeOffset.Parse(timeText, Invariant), comment);

                case PropertyKind.Vector:
                    var (vector, unresolvedVector) = ReadBlockValue(element, info.ItemSize, blockReader);
                    return Mark(CreateVector(id, comment, info.ClrType, vector.Span), unresolvedVector);

                case PropertyKind.Matrix:
                    var rows = ParseDimension(element.Attribute("rows")?.Value);
                    var columns = ParseDimension(element.Attribute("columns")?.Value);
                    var (matrix, unresolvedMatrix) = ReadBlockValue(element, info.ItemSize, blockReader);
                    return Mark(CreateMatrix(id, comment, info.ClrType, rows, columns, matrix.Span), unresolvedMatrix);

                default:
                    return CreateScalar(id, comment, info.ClrType, ScalarText(element));
            }
        }

        public XisfCoreElement DeserializeCoreElement(XElement element)
        {
            var uid = element.Attribute("uid")?.Value;

            switch (element.Name.LocalName)
            {
                case "Reference":
                    var refId = element.Attribute("ref")?.Value ??
                               throw new FormatException("Reference element must have ref attribute");
                    return new XisfReference(refId);

                case "Resolution":
                    var h = double.Parse(element.Attribute("horizontal")?.Value ?? "72", Invariant);
                    var v = double.Parse(element.Attribute("vertical")?.Value ?? "72", Invariant);
                    var unitStr = element.Attribute("unit")?.Value;
                    var unit = string.IsNullOrEmpty(unitStr) ? XisfResolutionUnit.Inch : Enum.Parse<XisfResolutionUnit>(unitStr, ignoreCase: true);
                    return new XisfResolution(h, v, unit, uid);

                case "FITSKeyword":
                    var name = element.Attribute("name")?.Value ?? "";
                    var value = element.Attribute("value")?.Value ?? "";
                    var comment = element.Attribute("comment")?.Value ?? "";
                    return new XisfFitsKeyword(name, value, comment, uid);

                case "ColorFilterArray":
                    var pattern = element.Attribute("pattern")?.Value ??
                                 throw new FormatException("ColorFilterArray element must have pattern attribute");
                    var widthStr = element.Attribute("width")?.Value ??
                                  throw new FormatException("ColorFilterArray element must have width attribute");
                    var heightStr = element.Attribute("height")?.Value ??
                                   throw new FormatException("ColorFilterArray element must have height attribute");
                    var cfaWidth = uint.Parse(widthStr, Invariant);
                    var cfaHeight = uint.Parse(heightStr, Invariant);
                    var cfaName = element.Attribute("name")?.Value;
                    return new XisfColorFilterArray(pattern, cfaWidth, cfaHeight, cfaName, uid);

                default:
                    throw new NotSupportedException(
                        $"Core element {element.Name.LocalName} is not deserialized; it is carried as unrecognized content");
            }
        }

        public XisfDataBlock DeserializeDataBlockLocation(string location)
        {
            if (location.StartsWith("inline:", StringComparison.Ordinal))
            {
                return new InlineDataBlock(ReadOnlyMemory<byte>.Empty, ParseEncoding(location.Substring(7)));
            }

            if (location == "embedded")
            {
                return new EmbeddedDataBlock(ReadOnlyMemory<byte>.Empty, XisfEncoding.Base64);
            }

            if (location.StartsWith("attachment:", StringComparison.Ordinal))
            {
                var parts = location.Substring(11).Split(':');
                if (parts.Length >= 2 &&
                    ulong.TryParse(parts[0], out var position) &&
                    ulong.TryParse(parts[1], out var size))
                {
                    return new AttachedDataBlock(position, size);
                }
            }
            else if (location.StartsWith("url(", StringComparison.Ordinal))
            {
                // §10.3 (Rev 1 correction D.7): the URL extends to the LAST closing parenthesis.
                var urlEnd = location.LastIndexOf(')');
                if (urlEnd > 4)
                {
                    var uri = new Uri(location.Substring(4, urlEnd - 4));
                    var trailer = location.Substring(urlEnd + 1);
                    var indexId = trailer.StartsWith(":", StringComparison.Ordinal) ? trailer.Substring(1) : null;
                    return new ExternalDataBlock(uri, IndexId: indexId);
                }
            }

            throw new FormatException($"Invalid data block location: {location}");
        }

        /// <summary>
        /// Builds the data block an element serializes, taking inline content from the element's own
        /// character data and embedded content from its child Data element, whose encoding and
        /// compression attributes govern (§10.3, §10.6.1).
        /// </summary>
        private XisfDataBlock? ParseDataBlock(XElement element)
        {
            var location = element.Attribute("location")?.Value;
            var dataElement = element.Element(XisfNamespace + "Data") ?? element.Element("Data");

            XisfDataBlock block;
            var compressionOwner = element;

            if (!string.IsNullOrEmpty(location) && location != "embedded")
            {
                block = DeserializeDataBlockLocation(location);
                if (block is InlineDataBlock inline)
                    block = inline with { Data = DecodeContent(element.Value, inline.Encoding) };
            }
            else if (dataElement != null)
            {
                var encoding = ParseEncoding(dataElement.Attribute("encoding")?.Value);
                block = new EmbeddedDataBlock(DecodeContent(dataElement.Value, encoding), encoding);
                compressionOwner = dataElement;
            }
            else if (location == "embedded")
            {
                block = new EmbeddedDataBlock(ReadOnlyMemory<byte>.Empty, XisfEncoding.Base64);
            }
            else
            {
                return null;
            }

            var compressionStr = compressionOwner.Attribute("compression")?.Value
                                 ?? element.Attribute("compression")?.Value;
            if (!string.IsNullOrEmpty(compressionStr))
            {
                var subblocksStr = compressionOwner.Attribute("subblocks")?.Value
                                   ?? element.Attribute("subblocks")?.Value;
                var (compression, unsupported) = ParseCompression(compressionStr, subblocksStr);
                block = compression != null
                    ? block with { Compression = compression }
                    : block with { UnsupportedDeclaration = unsupported };
            }

            var checksumStr = element.Attribute("checksum")?.Value;
            if (!string.IsNullOrEmpty(checksumStr))
            {
                var (checksum, unsupported) = ParseChecksum(checksumStr);
                block = checksum != null
                    ? block with { Checksum = checksum }
                    : block with { UnsupportedDeclaration = unsupported };
            }

            if (string.Equals(element.Attribute("byteOrder")?.Value, "big", StringComparison.OrdinalIgnoreCase))
                block = block with { ByteOrder = XisfByteOrder.BigEndian };

            return block;
        }

        private static ReadOnlyMemory<byte> DecodeContent(string content, XisfEncoding encoding)
        {
            var compact = new StringBuilder(content.Length);
            foreach (var character in content)
            {
                if (!char.IsWhiteSpace(character))
                    compact.Append(character);
            }

            if (compact.Length == 0)
                return ReadOnlyMemory<byte>.Empty;

            return encoding == XisfEncoding.Hex
                ? Convert.FromHexString(compact.ToString())
                : Convert.FromBase64String(compact.ToString());
        }

        private static XisfEncoding ParseEncoding(string? encoding)
            => string.Equals(encoding, "hex", StringComparison.OrdinalIgnoreCase) ? XisfEncoding.Hex : XisfEncoding.Base64;

        private static XisfProperty Mark(XisfProperty property, AttachedDataBlock? unresolved)
            => unresolved is null ? property : property with { UnresolvedBlock = unresolved };

        private (ReadOnlyMemory<byte> Data, AttachedDataBlock? Unresolved) ReadBlockValue(
            XElement element, int itemSize, XisfBlockReader? blockReader)
        {
            var block = ParseDataBlock(element);
            if (block == null)
                return (ReadOnlyMemory<byte>.Empty, null);

            if (blockReader != null)
            {
                return blockReader(block, itemSize) is { } data
                    ? (data, null)
                    : (ReadOnlyMemory<byte>.Empty, (AttachedDataBlock)block);
            }

            return block switch
            {
                InlineDataBlock inline => (inline.Data, null),
                EmbeddedDataBlock embedded => (embedded.Data, null),
                _ => (ReadOnlyMemory<byte>.Empty, null)
            };
        }

        /// <summary>§11.1.6: a String property carries its value as character data, whose white space
        /// is significant; the value attribute form is legacy but still accepted.</summary>
        private (string Value, AttachedDataBlock? Unresolved) ReadStringValue(XElement element, XisfBlockReader? blockReader)
        {
            var attribute = element.Attribute("value");
            if (attribute != null)
                return (attribute.Value, null);

            if (element.Attribute("location") != null)
            {
                var (bytes, unresolved) = ReadBlockValue(element, 1, blockReader);
                return (Encoding.UTF8.GetString(bytes.Span), unresolved);
            }

            return (element.Value, null);
        }

        private static string ScalarText(XElement element)
            => element.Attribute("value")?.Value ?? element.Value.Trim();

        private static uint ParseDimension(string? text)
            => string.IsNullOrEmpty(text) ? 0u : uint.Parse(text, Invariant);

        #endregion

        #region Serialization

        public XDocument SerializeHeader(XisfHeader header, XisfWriterOptions options)
        {
            var root = new XElement(XisfNamespace + "xisf",
                new XAttribute("version", "1.0"),
                new XAttribute(XNamespace.Xmlns + "xsi", XsiNamespace.NamespaceName),
                new XAttribute(XsiNamespace + "schemaLocation",
                    "http://www.pixinsight.com/xisf http://pixinsight.com/xisf/xisf-1.0.xsd")
            );

            var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null), root);

            if (!string.IsNullOrEmpty(header.InitialComment))
            {
                doc.Root!.AddBeforeSelf(new XComment(header.InitialComment));
            }

            root.Add(SerializeMetadata(header.Metadata, options));

            foreach (var kvp in header.CoreElements)
            {
                root.Add(SerializeCoreElement(kvp.Value, options));
            }

            foreach (var unknown in header.UnknownElements)
            {
                root.Add(new XElement(unknown));
            }

            return doc;
        }

        private XElement SerializeMetadata(XisfMetadata metadata, XisfWriterOptions options)
        {
            var element = new XElement(XisfNamespace + "Metadata");

            AddValueProperty(element, "XISF:CreationTime", "TimePoint", FormatTimePoint(metadata.CreationTime));
            AddTextProperty(element, "XISF:CreatorApplication", metadata.CreatorApplication);

            AddTextProperty(element, "XISF:CreatorModule", metadata.CreatorModule);
            AddTextProperty(element, "XISF:CreatorOS", metadata.CreatorOS);
            AddListProperty(element, "XISF:Authors", metadata.Authors);
            AddTextProperty(element, "XISF:Title", metadata.Title);
            AddTextProperty(element, "XISF:Description", metadata.Description);
            AddTextProperty(element, "XISF:Abstract", metadata.Abstract);
            AddTextProperty(element, "XISF:AccessRights", metadata.AccessRights);
            AddListProperty(element, "XISF:BibliographicReferences", metadata.BibliographicReferences);
            AddTextProperty(element, "XISF:BriefDescription", metadata.BriefDescription);

            if (metadata.CompressionLevel.HasValue)
                AddValueProperty(element, "XISF:CompressionLevel", "Int32",
                    metadata.CompressionLevel.Value.ToString(Invariant));

            AddTextProperty(element, "XISF:CompressionCodecs", metadata.CompressionCodecs);
            AddListProperty(element, "XISF:Contributors", metadata.Contributors);
            AddTextProperty(element, "XISF:Copyright", metadata.Copyright);
            AddTextProperty(element, "XISF:Keywords", metadata.Keywords);
            AddTextProperty(element, "XISF:Languages", metadata.Languages);
            AddTextProperty(element, "XISF:License", metadata.License);

            if (metadata.OriginalCreationTime.HasValue)
                AddValueProperty(element, "XISF:OriginalCreationTime", "TimePoint",
                    FormatTimePoint(metadata.OriginalCreationTime.Value));

            AddListProperty(element, "XISF:RelatedResources", metadata.RelatedResources);

            if (metadata.BlockAlignmentSize.HasValue)
                AddValueProperty(element, "XISF:BlockAlignmentSize", "UInt16",
                    metadata.BlockAlignmentSize.Value.ToString(Invariant));

            AddTextProperty(element, "XISF:ChecksumAlgorithms", metadata.ChecksumAlgorithms);

            if (metadata.MaxInlineBlockSize.HasValue)
                AddValueProperty(element, "XISF:MaxInlineBlockSize", "UInt16",
                    metadata.MaxInlineBlockSize.Value.ToString(Invariant));

            AddTextProperty(element, "XISF:OutputHints", metadata.OutputHints);

            return element;
        }

        private static void AddValueProperty(XElement metadata, string id, string type, string value)
            => metadata.Add(new XElement(XisfNamespace + "Property",
                new XAttribute("id", id),
                new XAttribute("type", type),
                new XAttribute("value", value)));

        private static void AddTextProperty(XElement metadata, string id, string? value)
        {
            if (string.IsNullOrEmpty(value))
                return;

            metadata.Add(new XElement(XisfNamespace + "Property",
                new XAttribute("id", id),
                new XAttribute("type", "String"),
                new XText(value)));
        }

        private static void AddListProperty(XElement metadata, string id, IReadOnlyList<string>? values)
        {
            if (values == null || values.Count == 0)
                return;

            AddTextProperty(metadata, id, string.Join("\n", values));
        }

        public XElement SerializeProperty(XisfProperty property, XisfWriterOptions options)
        {
            if (property.UnresolvedBlock is { } unresolved)
                throw new InvalidOperationException(
                    $"Property '{property.Id}' was read metadata-only and its value block " +
                    $"(attachment:{unresolved.Position}:{unresolved.Size}) was never loaded; " +
                    "writing it would replace the value with an empty placeholder. Read the full file before writing, " +
                    "or supply the value and set UnresolvedBlock = null.");

            if (property is XisfRawProperty raw)
                return new XElement(raw.Element);

            var element = new XElement(XisfNamespace + "Property",
                new XAttribute("id", property.Id));

            if (!string.IsNullOrEmpty(property.Comment))
            {
                element.Add(new XAttribute("comment", property.Comment));
            }

            switch (property)
            {
                case XisfScalarProperty<bool> boolean:
                    AddScalar(element, "Boolean", boolean.Value ? "true" : "false");
                    break;

                case XisfScalarProperty<sbyte> int8:
                    AddScalar(element, "Int8", int8.Value.ToString(Invariant));
                    break;

                case XisfScalarProperty<byte> uint8:
                    AddScalar(element, "UInt8", uint8.Value.ToString(Invariant));
                    break;

                case XisfScalarProperty<short> int16:
                    AddScalar(element, "Int16", int16.Value.ToString(Invariant));
                    break;

                case XisfScalarProperty<ushort> uint16:
                    AddScalar(element, "UInt16", uint16.Value.ToString(Invariant));
                    break;

                case XisfScalarProperty<int> int32:
                    AddScalar(element, "Int32", int32.Value.ToString(Invariant));
                    break;

                case XisfScalarProperty<uint> uint32:
                    AddScalar(element, "UInt32", uint32.Value.ToString(Invariant));
                    break;

                case XisfScalarProperty<long> int64:
                    AddScalar(element, "Int64", int64.Value.ToString(Invariant));
                    break;

                case XisfScalarProperty<ulong> uint64:
                    AddScalar(element, "UInt64", uint64.Value.ToString(Invariant));
                    break;

                case XisfScalarProperty<float> float32:
                    AddScalar(element, "Float32", FormatSingle(float32.Value));
                    break;

                case XisfScalarProperty<double> float64:
                    AddScalar(element, "Float64", FormatDouble(float64.Value));
                    break;

                case XisfStringProperty text:
                    element.Add(new XAttribute("type", "String"));
                    if (text.Value.Length > 0)
                        element.Add(new XText(text.Value));
                    break;

                case XisfTimePointProperty timePoint:
                    AddScalar(element, "TimePoint", FormatTimePoint(timePoint.Value));
                    break;

                default:
                    if (!TryAddVector(element, property) && !TryAddMatrix(element, property))
                        throw new NotSupportedException(
                            $"Property type {property.GetType().Name} is not yet supported for serialization");
                    break;
            }

            return element;
        }

        private static void AddScalar(XElement element, string type, string value)
        {
            element.Add(new XAttribute("type", type));
            element.Add(new XAttribute("value", value));
        }

        public XElement SerializeImage(XisfImage image, XisfWriterOptions options)
        {
            var element = new XElement(XisfNamespace + "Image");

            element.Add(new XAttribute("geometry", FormatGeometry(image.Geometry)));
            element.Add(new XAttribute("sampleFormat", image.SampleFormat.ToString()));
            element.Add(new XAttribute("colorSpace", image.ColorSpace.ToString()));
            element.Add(new XAttribute("location", SerializeDataBlockLocation(image.PixelData)));

            if (image.PixelData.Compression != null)
            {
                element.Add(new XAttribute("compression", FormatCompression(image.PixelData.Compression)));

                if (image.PixelData.Compression.Subblocks is { Count: > 1 } subblocks)
                {
                    element.Add(new XAttribute("subblocks", FormatSubblocks(subblocks)));
                }
            }

            if (image.PixelData.Checksum != null)
            {
                element.Add(new XAttribute("checksum", FormatChecksum(image.PixelData.Checksum)));
            }

            if (image.Bounds != null)
            {
                element.Add(new XAttribute("bounds",
                    $"{image.Bounds.Lower.ToString("G17", Invariant)}:{image.Bounds.Upper.ToString("G17", Invariant)}"));
            }

            if (image.PixelStorage != XisfPixelStorage.Planar)
            {
                element.Add(new XAttribute("pixelStorage", image.PixelStorage.ToString()));
            }

            if (image.ImageType.HasValue)
            {
                element.Add(new XAttribute("imageType", image.ImageType.Value.ToString()));
            }

            if (image.Offset.HasValue)
            {
                element.Add(new XAttribute("offset", image.Offset.Value.ToString("G17", Invariant)));
            }

            if (!string.IsNullOrEmpty(image.ImageId))
            {
                element.Add(new XAttribute("id", image.ImageId));
            }

            if (image.Uuid.HasValue)
            {
                element.Add(new XAttribute("uuid", image.Uuid.Value.ToString("D")));
            }

            if (image.Properties != null)
            {
                foreach (var property in image.Properties)
                {
                    element.Add(SerializeProperty(property, options));
                }
            }

            if (image.AssociatedElements != null)
            {
                foreach (var associatedElement in image.AssociatedElements)
                {
                    element.Add(SerializeCoreElement(associatedElement, options));
                }
            }

            foreach (var unknown in image.UnknownElements)
            {
                element.Add(new XElement(unknown));
            }

            return element;
        }

        public XElement SerializeCoreElement(XisfCoreElement element, XisfWriterOptions options)
        {
            return element switch
            {
                XisfReference reference => new XElement(XisfNamespace + "Reference",
                    new XAttribute("ref", reference.RefId)),

                XisfResolution resolution => new XElement(XisfNamespace + "Resolution",
                    new XAttribute("horizontal", resolution.Horizontal.ToString("G17", Invariant)),
                    new XAttribute("vertical", resolution.Vertical.ToString("G17", Invariant)),
                    new XAttribute("unit", resolution.Unit.ToString()),
                    !string.IsNullOrEmpty(resolution.Uid) ? new XAttribute("uid", resolution.Uid) : null),

                XisfFitsKeyword fits => new XElement(XisfNamespace + "FITSKeyword",
                    new XAttribute("name", fits.Name),
                    new XAttribute("value", fits.Value),
                    new XAttribute("comment", fits.Comment),
                    !string.IsNullOrEmpty(fits.Uid) ? new XAttribute("uid", fits.Uid) : null),

                XisfColorFilterArray cfa => new XElement(XisfNamespace + "ColorFilterArray",
                    new XAttribute("pattern", cfa.Pattern),
                    new XAttribute("width", cfa.Width.ToString(Invariant)),
                    new XAttribute("height", cfa.Height.ToString(Invariant)),
                    !string.IsNullOrEmpty(cfa.Name) ? new XAttribute("name", cfa.Name) : null,
                    !string.IsNullOrEmpty(cfa.Uid) ? new XAttribute("uid", cfa.Uid) : null),

                _ => throw new NotSupportedException($"Core element type {element.GetType().Name} is not yet supported for serialization")
            };
        }

        public string SerializeDataBlockLocation(XisfDataBlock block)
        {
            return block switch
            {
                InlineDataBlock inline => $"inline:{inline.Encoding.ToString().ToLowerInvariant()}",
                EmbeddedDataBlock => "embedded",
                AttachedDataBlock attached => $"attachment:{attached.Position}:{attached.Size}",
                ExternalDataBlock external => $"url({external.Location})",
                _ => throw new ArgumentException($"Unknown data block type: {block.GetType().Name}")
            };
        }

        private string FormatGeometry(XisfImageGeometry geometry)
        {
            var dims = string.Join(":", geometry.Dimensions);
            return $"{dims}:{geometry.ChannelCount}";
        }

        private static string FormatCompression(XisfCompression compression)
        {
            var codecStr = compression.Codec switch
            {
                XisfCompressionCodec.Zlib => "zlib",
                XisfCompressionCodec.ZlibSh => "zlib+sh",
                XisfCompressionCodec.LZ4 => "lz4",
                XisfCompressionCodec.LZ4Sh => "lz4+sh",
                XisfCompressionCodec.LZ4HC => "lz4hc",
                XisfCompressionCodec.LZ4HCSh => "lz4hc+sh",
                XisfCompressionCodec.Zstd => "zstd",
                XisfCompressionCodec.ZstdSh => "zstd+sh",
                _ => throw new NotSupportedException($"Unsupported compression codec: {compression.Codec}")
            };

            // §10.6.3/10.6.5/10.6.7/10.6.9: only the "+sh" codecs carry an item-size component.
            return IsShuffledCodec(compression.Codec) && compression.ItemSize.HasValue
                ? $"{codecStr}:{compression.UncompressedSize}:{compression.ItemSize.Value}"
                : $"{codecStr}:{compression.UncompressedSize}";
        }

        private static string FormatSubblocks(IReadOnlyList<XisfCompressionSubblock> subblocks)
            => string.Join(":", subblocks.Select(s => $"{s.CompressedLength},{s.UncompressedLength}"));

        private static string FormatChecksum(XisfChecksum checksum)
        {
            // Emit the spec-compliant hyphenated names (§10.5 Table 9) so files written by
            // this library round-trip through PixInsight and other spec-conformant readers.
            var algorithmStr = checksum.Algorithm switch
            {
                XisfHashAlgorithm.SHA1 => "sha-1",
                XisfHashAlgorithm.SHA256 => "sha-256",
                XisfHashAlgorithm.SHA512 => "sha-512",
                XisfHashAlgorithm.SHA3_256 => "sha3-256",
                XisfHashAlgorithm.SHA3_512 => "sha3-512",
                _ => throw new NotSupportedException($"Unsupported hash algorithm: {checksum.Algorithm}")
            };

            var digestHex = Convert.ToHexString(checksum.Digest.ToArray()).ToLowerInvariant();
            return $"{algorithmStr}:{digestHex}";
        }

        private static string FormatTimePoint(DateTimeOffset value)
            => value.ToString(TimePointFormat, Invariant);

        // §8.3.3: encoders shall use the canonical non-finite forms.
        private static string FormatSingle(float value)
            => float.IsNaN(value) ? "NaN"
             : float.IsPositiveInfinity(value) ? "+Inf"
             : float.IsNegativeInfinity(value) ? "-Inf"
             : value.ToString("G9", Invariant);

        private static string FormatDouble(double value)
            => double.IsNaN(value) ? "NaN"
             : double.IsPositiveInfinity(value) ? "+Inf"
             : double.IsNegativeInfinity(value) ? "-Inf"
             : value.ToString("G17", Invariant);

        #endregion

        #region Helper Methods

        private XisfImageGeometry ParseGeometry(string geometryStr)
        {
            var parts = geometryStr.Split(':');
            if (parts.Length < 2)
                throw new FormatException($"Invalid geometry format: {geometryStr}");

            var dimensions = new List<uint>();
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (uint.TryParse(parts[i], out var dim))
                    dimensions.Add(dim);
                else
                    throw new FormatException($"Invalid dimension value in geometry: {parts[i]}");
            }

            if (!uint.TryParse(parts[^1], out var channelCount))
                throw new FormatException($"Invalid channel count in geometry: {parts[^1]}");

            return new XisfImageGeometry(dimensions, channelCount);
        }

        /// <summary>
        /// §7: an unrecognized codec must not be fatal at header-parse time. The declaration is
        /// carried on the block instead and surfaces when that block is materialized.
        /// </summary>
        private static (XisfCompression? Compression, string? Unsupported) ParseCompression(
            string compressionStr, string? subblocksStr)
        {
            var parts = compressionStr.Split(':');
            if (parts.Length < 2 || !ulong.TryParse(parts[1], out var uncompressedSize))
                return (null, $"Data block declares the unreadable compression specification '{compressionStr}'");

            XisfCompressionCodec? codec = parts[0].ToLowerInvariant() switch
            {
                "zlib" => XisfCompressionCodec.Zlib,
                "zlib+sh" => XisfCompressionCodec.ZlibSh,
                "lz4" => XisfCompressionCodec.LZ4,
                "lz4+sh" => XisfCompressionCodec.LZ4Sh,
                "lz4hc" => XisfCompressionCodec.LZ4HC,
                "lz4hc+sh" => XisfCompressionCodec.LZ4HCSh,
                "zstd" => XisfCompressionCodec.Zstd,
                "zstd+sh" => XisfCompressionCodec.ZstdSh,
                _ => null
            };

            if (codec == null)
                return (null, $"Data block declares the '{parts[0]}' compression codec, which this build cannot decode");

            uint? itemSize = null;
            if (parts.Length >= 3 && uint.TryParse(parts[2], out var parsedItemSize))
                itemSize = parsedItemSize;

            return (new XisfCompression(codec.Value, uncompressedSize, itemSize, ParseSubblocks(subblocksStr)), null);
        }

        private static IReadOnlyList<XisfCompressionSubblock>? ParseSubblocks(string? subblocksStr)
        {
            if (string.IsNullOrWhiteSpace(subblocksStr))
                return null;

            var subblocks = new List<XisfCompressionSubblock>();
            foreach (var entry in subblocksStr.Split(':'))
            {
                var pair = entry.Split(',');
                if (pair.Length != 2 ||
                    !ulong.TryParse(pair[0], out var compressed) ||
                    !ulong.TryParse(pair[1], out var uncompressed))
                {
                    return null;
                }

                subblocks.Add(new XisfCompressionSubblock(compressed, uncompressed));
            }

            return subblocks.Count > 0 ? subblocks : null;
        }

        private static (XisfChecksum? Checksum, string? Unsupported) ParseChecksum(string checksumStr)
        {
            var parts = checksumStr.Split(':');
            if (parts.Length != 2)
                return (null, $"Data block declares the unreadable checksum specification '{checksumStr}'");

            // The XISF 1.0 spec (§10.5 Table 9) names the SHA-2 family with a hyphen
            // (sha-1/sha-256/sha-512), which is what PixInsight and N.I.N.A. write. Accept
            // both the spec form and the legacy non-hyphenated form this library used to emit.
            XisfHashAlgorithm? algorithm = parts[0].ToLowerInvariant() switch
            {
                "sha-1" or "sha1" => XisfHashAlgorithm.SHA1,
                "sha-256" or "sha256" => XisfHashAlgorithm.SHA256,
                "sha-512" or "sha512" => XisfHashAlgorithm.SHA512,
                "sha3-256" => XisfHashAlgorithm.SHA3_256,
                "sha3-512" => XisfHashAlgorithm.SHA3_512,
                _ => null
            };

            if (algorithm == null)
                return (null, $"Data block declares the '{parts[0]}' hashing algorithm, which this build does not know");

            return (new XisfChecksum(algorithm.Value, Convert.FromHexString(parts[1])), null);
        }

        private static bool IsShuffledCodec(XisfCompressionCodec codec)
            => codec is XisfCompressionCodec.ZlibSh
                     or XisfCompressionCodec.LZ4Sh
                     or XisfCompressionCodec.LZ4HCSh
                     or XisfCompressionCodec.ZstdSh;

        #endregion

        #region Typed property values

        private static bool ParseBoolean(string text)
        {
            // §8.3.4: decoders shall accept the words and the integers 1 and 0.
            var trimmed = text.Trim();
            if (trimmed == "1")
                return true;
            if (trimmed == "0")
                return false;

            return bool.Parse(trimmed);
        }

        // §8.3.3 grammar: NaN | [-+]Inf | -?nan | -?inf. The Infinity forms this library emitted
        // through v1.2 are accepted too so its own old files keep reading.
        private static bool TryParseNonFinite(string text, out double value)
        {
            switch (text.Trim().ToLowerInvariant())
            {
                case "nan":
                case "-nan":
                case "+nan":
                    value = double.NaN;
                    return true;
                case "inf":
                case "+inf":
                case "infinity":
                case "+infinity":
                    value = double.PositiveInfinity;
                    return true;
                case "-inf":
                case "-infinity":
                    value = double.NegativeInfinity;
                    return true;
                default:
                    value = 0;
                    return false;
            }
        }

        private static float ParseSingle(string text)
            => TryParseNonFinite(text, out var nonFinite) ? (float)nonFinite : float.Parse(text, Invariant);

        private static double ParseDouble(string text)
            => TryParseNonFinite(text, out var nonFinite) ? nonFinite : double.Parse(text, Invariant);

        private static XisfProperty CreateScalar(string id, string? comment, Type clrType, string text)
        {
            if (clrType == typeof(bool)) return new XisfScalarProperty<bool>(id, ParseBoolean(text), comment);
            if (clrType == typeof(sbyte)) return new XisfScalarProperty<sbyte>(id, sbyte.Parse(text, Invariant), comment);
            if (clrType == typeof(byte)) return new XisfScalarProperty<byte>(id, byte.Parse(text, Invariant), comment);
            if (clrType == typeof(short)) return new XisfScalarProperty<short>(id, short.Parse(text, Invariant), comment);
            if (clrType == typeof(ushort)) return new XisfScalarProperty<ushort>(id, ushort.Parse(text, Invariant), comment);
            if (clrType == typeof(int)) return new XisfScalarProperty<int>(id, int.Parse(text, Invariant), comment);
            if (clrType == typeof(uint)) return new XisfScalarProperty<uint>(id, uint.Parse(text, Invariant), comment);
            if (clrType == typeof(long)) return new XisfScalarProperty<long>(id, long.Parse(text, Invariant), comment);
            if (clrType == typeof(ulong)) return new XisfScalarProperty<ulong>(id, ulong.Parse(text, Invariant), comment);
            if (clrType == typeof(float)) return new XisfScalarProperty<float>(id, ParseSingle(text), comment);
            if (clrType == typeof(double)) return new XisfScalarProperty<double>(id, ParseDouble(text), comment);

            throw new FormatException($"Unsupported scalar property type: {clrType.Name}");
        }

        private static XisfProperty CreateVector(string id, string? comment, Type clrType, ReadOnlySpan<byte> bytes)
        {
            if (clrType == typeof(sbyte)) return new XisfVectorProperty<sbyte>(id, Decode<sbyte>(bytes), comment);
            if (clrType == typeof(byte)) return new XisfVectorProperty<byte>(id, Decode<byte>(bytes), comment);
            if (clrType == typeof(short)) return new XisfVectorProperty<short>(id, Decode<short>(bytes), comment);
            if (clrType == typeof(ushort)) return new XisfVectorProperty<ushort>(id, Decode<ushort>(bytes), comment);
            if (clrType == typeof(int)) return new XisfVectorProperty<int>(id, Decode<int>(bytes), comment);
            if (clrType == typeof(uint)) return new XisfVectorProperty<uint>(id, Decode<uint>(bytes), comment);
            if (clrType == typeof(long)) return new XisfVectorProperty<long>(id, Decode<long>(bytes), comment);
            if (clrType == typeof(ulong)) return new XisfVectorProperty<ulong>(id, Decode<ulong>(bytes), comment);
            if (clrType == typeof(float)) return new XisfVectorProperty<float>(id, Decode<float>(bytes), comment);
            if (clrType == typeof(double)) return new XisfVectorProperty<double>(id, Decode<double>(bytes), comment);

            throw new FormatException($"Unsupported vector property type: {clrType.Name}");
        }

        private static XisfProperty CreateMatrix(
            string id, string? comment, Type clrType, uint rows, uint columns, ReadOnlySpan<byte> bytes)
        {
            if (clrType == typeof(sbyte)) return new XisfMatrixProperty<sbyte>(id, rows, columns, Decode<sbyte>(bytes), comment);
            if (clrType == typeof(byte)) return new XisfMatrixProperty<byte>(id, rows, columns, Decode<byte>(bytes), comment);
            if (clrType == typeof(short)) return new XisfMatrixProperty<short>(id, rows, columns, Decode<short>(bytes), comment);
            if (clrType == typeof(ushort)) return new XisfMatrixProperty<ushort>(id, rows, columns, Decode<ushort>(bytes), comment);
            if (clrType == typeof(int)) return new XisfMatrixProperty<int>(id, rows, columns, Decode<int>(bytes), comment);
            if (clrType == typeof(uint)) return new XisfMatrixProperty<uint>(id, rows, columns, Decode<uint>(bytes), comment);
            if (clrType == typeof(long)) return new XisfMatrixProperty<long>(id, rows, columns, Decode<long>(bytes), comment);
            if (clrType == typeof(ulong)) return new XisfMatrixProperty<ulong>(id, rows, columns, Decode<ulong>(bytes), comment);
            if (clrType == typeof(float)) return new XisfMatrixProperty<float>(id, rows, columns, Decode<float>(bytes), comment);
            if (clrType == typeof(double)) return new XisfMatrixProperty<double>(id, rows, columns, Decode<double>(bytes), comment);

            throw new FormatException($"Unsupported matrix property type: {clrType.Name}");
        }

        private static bool TryAddVector(XElement element, XisfProperty property)
        {
            (string TypeName, int Length, byte[] Bytes)? payload = property switch
            {
                XisfVectorProperty<sbyte> v => ("I8Vector", v.Values.Count, Encode(v.Values)),
                XisfVectorProperty<byte> v => ("UI8Vector", v.Values.Count, Encode(v.Values)),
                XisfVectorProperty<short> v => ("I16Vector", v.Values.Count, Encode(v.Values)),
                XisfVectorProperty<ushort> v => ("UI16Vector", v.Values.Count, Encode(v.Values)),
                XisfVectorProperty<int> v => ("I32Vector", v.Values.Count, Encode(v.Values)),
                XisfVectorProperty<uint> v => ("UI32Vector", v.Values.Count, Encode(v.Values)),
                XisfVectorProperty<long> v => ("I64Vector", v.Values.Count, Encode(v.Values)),
                XisfVectorProperty<ulong> v => ("UI64Vector", v.Values.Count, Encode(v.Values)),
                XisfVectorProperty<float> v => ("F32Vector", v.Values.Count, Encode(v.Values)),
                XisfVectorProperty<double> v => ("F64Vector", v.Values.Count, Encode(v.Values)),
                _ => null
            };

            if (payload == null)
                return false;

            element.Add(new XAttribute("type", payload.Value.TypeName));
            element.Add(new XAttribute("length", payload.Value.Length.ToString(Invariant)));
            AddInlineBlock(element, payload.Value.Bytes);
            return true;
        }

        private static bool TryAddMatrix(XElement element, XisfProperty property)
        {
            (string TypeName, uint Rows, uint Columns, byte[] Bytes)? payload = property switch
            {
                XisfMatrixProperty<sbyte> m => ("I8Matrix", m.Rows, m.Columns, Encode(m.Values)),
                XisfMatrixProperty<byte> m => ("UI8Matrix", m.Rows, m.Columns, Encode(m.Values)),
                XisfMatrixProperty<short> m => ("I16Matrix", m.Rows, m.Columns, Encode(m.Values)),
                XisfMatrixProperty<ushort> m => ("UI16Matrix", m.Rows, m.Columns, Encode(m.Values)),
                XisfMatrixProperty<int> m => ("I32Matrix", m.Rows, m.Columns, Encode(m.Values)),
                XisfMatrixProperty<uint> m => ("UI32Matrix", m.Rows, m.Columns, Encode(m.Values)),
                XisfMatrixProperty<long> m => ("I64Matrix", m.Rows, m.Columns, Encode(m.Values)),
                XisfMatrixProperty<ulong> m => ("UI64Matrix", m.Rows, m.Columns, Encode(m.Values)),
                XisfMatrixProperty<float> m => ("F32Matrix", m.Rows, m.Columns, Encode(m.Values)),
                XisfMatrixProperty<double> m => ("F64Matrix", m.Rows, m.Columns, Encode(m.Values)),
                _ => null
            };

            if (payload == null)
                return false;

            element.Add(new XAttribute("type", payload.Value.TypeName));
            element.Add(new XAttribute("rows", payload.Value.Rows.ToString(Invariant)));
            element.Add(new XAttribute("columns", payload.Value.Columns.ToString(Invariant)));
            AddInlineBlock(element, payload.Value.Bytes);
            return true;
        }

        /// <summary>§11.1.8/§11.1.9: an empty vector or matrix is an inline block with empty contents.</summary>
        private static void AddInlineBlock(XElement element, byte[] bytes)
        {
            element.Add(new XAttribute("location", "inline:base64"));
            if (bytes.Length > 0)
                element.Add(new XText(Convert.ToBase64String(bytes)));
        }

        private static byte[] Encode<T>(IReadOnlyList<T> values) where T : struct
        {
            var size = Unsafe.SizeOf<T>();
            var bytes = new byte[values.Count * size];

            for (var i = 0; i < values.Count; i++)
            {
                var value = values[i];
                MemoryMarshal.Write(bytes.AsSpan(i * size, size), in value);
            }

            if (!BitConverter.IsLittleEndian)
                ReverseItems(bytes, size);

            return bytes;
        }

        private static T[] Decode<T>(ReadOnlySpan<byte> bytes) where T : struct
        {
            var size = Unsafe.SizeOf<T>();
            var count = bytes.Length / size;
            var values = new T[count];

            if (BitConverter.IsLittleEndian)
            {
                for (var i = 0; i < count; i++)
                    values[i] = MemoryMarshal.Read<T>(bytes.Slice(i * size, size));

                return values;
            }

            var ordered = bytes.ToArray();
            ReverseItems(ordered, size);
            for (var i = 0; i < count; i++)
                values[i] = MemoryMarshal.Read<T>(ordered.AsSpan(i * size, size));

            return values;
        }

        private static void ReverseItems(byte[] bytes, int itemSize)
        {
            if (itemSize <= 1)
                return;

            for (var offset = 0; offset + itemSize <= bytes.Length; offset += itemSize)
                Array.Reverse(bytes, offset, itemSize);
        }

        #endregion
    }
}
