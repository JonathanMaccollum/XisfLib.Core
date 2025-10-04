using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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

        public XisfXmlSerializer(IXisfValidator validator)
        {
            _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        }

        #region Deserialization

        public XisfHeader DeserializeHeader(XDocument document)
        {
            var root = document.Root;
            if (root == null || root.Name.LocalName != "xisf")
                throw new FormatException("Invalid XISF header: missing or invalid root element");

            var version = root.Attribute("version")?.Value;
            if (version != "1.0")
                throw new FormatException($"Unsupported XISF version: {version}");

            // Extract initial comment if present
            string? initialComment = null;
            var firstNode = document.Nodes().FirstOrDefault();
            if (firstNode is XComment comment)
                initialComment = comment.Value;

            // Parse Metadata element (required)
            var metadataElement = root.Element(XisfNamespace + "Metadata") ??
                                  root.Element("Metadata");
            if (metadataElement == null)
                throw new FormatException("XISF header must contain a Metadata element");

            var metadata = DeserializeMetadata(metadataElement);

            // Parse core elements
            var coreElements = new Dictionary<string, XisfCoreElement>();
            foreach (var element in root.Elements())
            {
                var localName = element.Name.LocalName;
                if (localName == "Metadata" || localName == "Image" || localName == "Property")
                    continue; // Skip these for now

                var uid = element.Attribute("uid")?.Value;
                if (!string.IsNullOrEmpty(uid))
                {
                    var coreElement = DeserializeCoreElement(element);
                    coreElements[uid] = coreElement;
                }
            }

            return new XisfHeader(metadata, coreElements, initialComment);
        }

        private XisfMetadata DeserializeMetadata(XElement element)
        {
            // Parse required properties
            var creationTimeProp = element.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "Property" &&
                               e.Attribute("id")?.Value == "XISF:CreationTime");

            var creatorAppProp = element.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "Property" &&
                               e.Attribute("id")?.Value == "XISF:CreatorApplication");

            if (creationTimeProp == null || creatorAppProp == null)
                throw new FormatException("Metadata must contain XISF:CreationTime and XISF:CreatorApplication");

            var creationTimeStr = GetPropertyValue(creationTimeProp);
            if (string.IsNullOrEmpty(creationTimeStr))
                throw new FormatException("XISF:CreationTime property has no value");

            var creationTime = DateTimeOffset.Parse(creationTimeStr, CultureInfo.InvariantCulture);

            var creatorApp = GetPropertyValue(creatorAppProp);
            if (string.IsNullOrEmpty(creatorApp))
                throw new FormatException("XISF:CreatorApplication property has no value");

            // Parse optional properties
            string? creatorModule = GetMetadataStringProperty(element, "XISF:CreatorModule");
            string? creatorOS = GetMetadataStringProperty(element, "XISF:CreatorOS");

            return new XisfMetadata(
                creationTime,
                creatorApp,
                creatorModule,
                creatorOS
            );
        }

        private string? GetMetadataStringProperty(XElement metadataElement, string propertyId)
        {
            var prop = metadataElement.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "Property" &&
                               e.Attribute("id")?.Value == propertyId);
            return prop != null ? GetPropertyValue(prop) : null;
        }

        /// <summary>
        /// Gets a property value from either the value attribute or element text content.
        /// Specification Reference: Section 11.1.6 Serialization of String Properties
        /// </summary>
        private string? GetPropertyValue(XElement propertyElement)
        {
            // Try value attribute first
            var valueAttr = propertyElement.Attribute("value")?.Value;
            if (!string.IsNullOrEmpty(valueAttr))
                return valueAttr;

            // Fall back to element text content (used for String properties)
            var textValue = propertyElement.Value?.Trim();
            return string.IsNullOrEmpty(textValue) ? null : textValue;
        }

        public XisfImage DeserializeImage(XElement element)
        {
            // Parse required attributes
            var geometryStr = element.Attribute("geometry")?.Value ??
                             throw new FormatException("Image element must have geometry attribute");
            var geometry = ParseGeometry(geometryStr);

            var sampleFormatStr = element.Attribute("sampleFormat")?.Value ??
                                 throw new FormatException("Image element must have sampleFormat attribute");
            var sampleFormat = Enum.Parse<XisfSampleFormat>(sampleFormatStr);

            var colorSpaceStr = element.Attribute("colorSpace")?.Value ??
                               throw new FormatException("Image element must have colorSpace attribute");
            var colorSpace = Enum.Parse<XisfColorSpace>(colorSpaceStr);

            // Parse data block location
            var locationStr = element.Attribute("location")?.Value;
            XisfDataBlock pixelData;

            if (!string.IsNullOrEmpty(locationStr))
            {
                pixelData = DeserializeDataBlockLocation(locationStr);
            }
            else
            {
                // Check for embedded Data child element
                var dataElement = element.Element(XisfNamespace + "Data") ?? element.Element("Data");
                if (dataElement != null)
                {
                    var encodingStr = dataElement.Attribute("encoding")?.Value ?? "base64";
                    var encoding = encodingStr.ToLowerInvariant() == "hex" ? XisfEncoding.Hex : XisfEncoding.Base64;
                    var data = System.Convert.FromBase64String(dataElement.Value.Trim());
                    pixelData = new EmbeddedDataBlock(data, encoding);
                }
                else
                {
                    throw new FormatException("Image element must have either location attribute or Data child element");
                }
            }

            // Apply compression info if present
            var compressionStr = element.Attribute("compression")?.Value;
            if (!string.IsNullOrEmpty(compressionStr))
            {
                var compression = ParseCompression(compressionStr);
                pixelData = pixelData with { Compression = compression };
            }

            // Apply checksum if present
            var checksumStr = element.Attribute("checksum")?.Value;
            if (!string.IsNullOrEmpty(checksumStr))
            {
                var checksum = ParseChecksum(checksumStr);
                pixelData = pixelData with { Checksum = checksum };
            }

            // Parse optional attributes
            XisfImageBounds? bounds = null;
            var boundsStr = element.Attribute("bounds")?.Value;
            if (!string.IsNullOrEmpty(boundsStr))
            {
                var parts = boundsStr.Split(':');
                if (parts.Length == 2 &&
                    double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lower) &&
                    double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var upper))
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
                null : double.Parse(offsetStr, CultureInfo.InvariantCulture);

            var imageId = element.Attribute("id")?.Value;

            var uuidStr = element.Attribute("uuid")?.Value;
            Guid? uuid = string.IsNullOrEmpty(uuidStr) ? null : Guid.Parse(uuidStr);

            // Parse child properties and associated elements
            var properties = new List<XisfProperty>();
            var associatedElements = new List<XisfCoreElement>();

            foreach (var child in element.Elements())
            {
                var localName = child.Name.LocalName;
                if (localName == "Property")
                {
                    properties.Add(DeserializeProperty(child));
                }
                else if (localName != "Data") // Skip Data elements (already processed)
                {
                    associatedElements.Add(DeserializeCoreElement(child));
                }
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
                null, // orientation - not parsed yet
                imageId,
                uuid,
                properties.Count > 0 ? properties : null,
                associatedElements.Count > 0 ? associatedElements : null
            );
        }

        public XisfProperty DeserializeProperty(XElement element)
        {
            var id = element.Attribute("id")?.Value ??
                    throw new FormatException("Property element must have id attribute");

            var typeStr = element.Attribute("type")?.Value ??
                         throw new FormatException("Property element must have type attribute");

            var comment = element.Attribute("comment")?.Value;

            // Get value from either attribute or element text
            var valueStr = GetPropertyValue(element) ?? "";

            // Simple implementation - handles basic property types
            switch (typeStr)
            {
                case "String":
                    return new XisfStringProperty(id, valueStr, comment);

                case "Boolean":
                    var boolValue = bool.Parse(valueStr);
                    return new XisfScalarProperty<bool>(id, boolValue, comment);

                case "Int32":
                    var intValue = int.Parse(valueStr, CultureInfo.InvariantCulture);
                    return new XisfScalarProperty<int>(id, intValue, comment);

                case "UInt32":
                    var uintValue = uint.Parse(valueStr, CultureInfo.InvariantCulture);
                    return new XisfScalarProperty<uint>(id, uintValue, comment);

                case "Float32":
                    var floatValue = float.Parse(valueStr, CultureInfo.InvariantCulture);
                    return new XisfScalarProperty<float>(id, floatValue, comment);

                case "Float64":
                    var doubleValue = double.Parse(valueStr, CultureInfo.InvariantCulture);
                    return new XisfScalarProperty<double>(id, doubleValue, comment);

                case "TimePoint":
                    if (string.IsNullOrEmpty(valueStr))
                        throw new FormatException("TimePoint property must have value");
                    var timeValue = DateTimeOffset.Parse(valueStr, CultureInfo.InvariantCulture);
                    return new XisfTimePointProperty(id, timeValue, comment);

                default:
                    // For unsupported types, return a string property as fallback
                    return new XisfStringProperty(id, valueStr, comment);
            }
        }

        public XisfCoreElement DeserializeCoreElement(XElement element)
        {
            var localName = element.Name.LocalName;
            var uid = element.Attribute("uid")?.Value;

            switch (localName)
            {
                case "Reference":
                    var refId = element.Attribute("ref")?.Value ??
                               throw new FormatException("Reference element must have ref attribute");
                    return new XisfReference(refId);

                case "Resolution":
                    var h = double.Parse(element.Attribute("horizontal")?.Value ?? "72", CultureInfo.InvariantCulture);
                    var v = double.Parse(element.Attribute("vertical")?.Value ?? "72", CultureInfo.InvariantCulture);
                    var unitStr = element.Attribute("unit")?.Value;
                    var unit = string.IsNullOrEmpty(unitStr) ? XisfResolutionUnit.Inch : Enum.Parse<XisfResolutionUnit>(unitStr);
                    return new XisfResolution(h, v, unit, uid);

                case "FITSKeyword":
                    var name = element.Attribute("name")?.Value ?? "";
                    var value = element.Attribute("value")?.Value ?? "";
                    var comment = element.Attribute("comment")?.Value ?? "";
                    return new XisfFitsKeyword(name, value, comment, uid);

                default:
                    // For unsupported core elements, return a Reference with empty id
                    return new XisfReference("");
            }
        }

        public XisfDataBlock DeserializeDataBlockLocation(string location)
        {
            if (location.StartsWith("inline:"))
            {
                var encodingStr = location.Substring(7);
                var encoding = encodingStr.ToLowerInvariant() == "hex" ? XisfEncoding.Hex : XisfEncoding.Base64;
                return new InlineDataBlock(ReadOnlyMemory<byte>.Empty, encoding);
            }
            else if (location == "embedded")
            {
                return new EmbeddedDataBlock(ReadOnlyMemory<byte>.Empty, XisfEncoding.Base64);
            }
            else if (location.StartsWith("attachment:"))
            {
                var parts = location.Substring(11).Split(':');
                if (parts.Length >= 2 &&
                    ulong.TryParse(parts[0], out var position) &&
                    ulong.TryParse(parts[1], out var size))
                {
                    return new AttachedDataBlock(position, size);
                }
            }
            else if (location.StartsWith("url("))
            {
                var urlEnd = location.IndexOf(')');
                if (urlEnd > 4)
                {
                    var urlStr = location.Substring(4, urlEnd - 4);
                    var uri = new Uri(urlStr);
                    return new ExternalDataBlock(uri);
                }
            }

            throw new FormatException($"Invalid data block location: {location}");
        }

        #endregion

        #region Serialization (Stubs for now)

        public XDocument SerializeHeader(XisfHeader header, XisfWriterOptions options)
        {
            var root = new XElement("xisf",
                new XAttribute("version", "1.0"),
                new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"),
                new XAttribute(XNamespace.Get("http://www.w3.org/2001/XMLSchema-instance") + "schemaLocation",
                    "http://www.pixinsight.com/xisf http://pixinsight.com/xisf/xisf-1.0.xsd")
            );

            var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null), root);

            if (!string.IsNullOrEmpty(header.InitialComment))
            {
                doc.Root!.AddBeforeSelf(new XComment(header.InitialComment));
            }

            return doc;
        }

        public XElement SerializeProperty(XisfProperty property, XisfWriterOptions options)
        {
            throw new NotImplementedException("Property serialization not yet implemented");
        }

        public XElement SerializeImage(XisfImage image, XisfWriterOptions options)
        {
            throw new NotImplementedException("Image serialization not yet implemented");
        }

        public XElement SerializeCoreElement(XisfCoreElement element, XisfWriterOptions options)
        {
            throw new NotImplementedException("Core element serialization not yet implemented");
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

        private XisfCompression ParseCompression(string compressionStr)
        {
            var parts = compressionStr.Split(':');
            if (parts.Length < 2)
                throw new FormatException($"Invalid compression format: {compressionStr}");

            var codecStr = parts[0].ToLowerInvariant();
            var codec = codecStr switch
            {
                "zlib" => XisfCompressionCodec.Zlib,
                "zlib+sh" => XisfCompressionCodec.ZlibSh,
                "lz4" => XisfCompressionCodec.LZ4,
                "lz4+sh" => XisfCompressionCodec.LZ4Sh,
                "lz4hc" => XisfCompressionCodec.LZ4HC,
                "lz4hc+sh" => XisfCompressionCodec.LZ4HCSh,
                _ => throw new FormatException($"Unsupported compression codec: {codecStr}")
            };

            if (!ulong.TryParse(parts[1], out var uncompressedSize))
                throw new FormatException($"Invalid uncompressed size in compression: {parts[1]}");

            uint? itemSize = null;
            if (parts.Length >= 3 && uint.TryParse(parts[2], out var parsedItemSize))
                itemSize = parsedItemSize;

            return new XisfCompression(codec, uncompressedSize, itemSize);
        }

        private XisfChecksum ParseChecksum(string checksumStr)
        {
            var parts = checksumStr.Split(':');
            if (parts.Length != 2)
                throw new FormatException($"Invalid checksum format: {checksumStr}");

            var algorithmStr = parts[0].ToLowerInvariant();
            var algorithm = algorithmStr switch
            {
                "sha1" => XisfHashAlgorithm.SHA1,
                "sha256" => XisfHashAlgorithm.SHA256,
                "sha512" => XisfHashAlgorithm.SHA512,
                "sha3-256" => XisfHashAlgorithm.SHA3_256,
                "sha3-512" => XisfHashAlgorithm.SHA3_512,
                _ => throw new FormatException($"Unsupported hash algorithm: {algorithmStr}")
            };

            var digestHex = parts[1];
            var digest = Convert.FromHexString(digestHex);

            return new XisfChecksum(algorithm, digest);
        }

        #endregion
    }
}
