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

                case "ColorFilterArray":
                    var pattern = element.Attribute("pattern")?.Value ??
                                 throw new FormatException("ColorFilterArray element must have pattern attribute");
                    var widthStr = element.Attribute("width")?.Value ??
                                  throw new FormatException("ColorFilterArray element must have width attribute");
                    var heightStr = element.Attribute("height")?.Value ??
                                   throw new FormatException("ColorFilterArray element must have height attribute");
                    var cfaWidth = uint.Parse(widthStr, CultureInfo.InvariantCulture);
                    var cfaHeight = uint.Parse(heightStr, CultureInfo.InvariantCulture);
                    var cfaName = element.Attribute("name")?.Value;
                    return new XisfColorFilterArray(pattern, cfaWidth, cfaHeight, cfaName, uid);

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

        #region Serialization

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

            // Add Metadata element
            var metadataElement = SerializeMetadata(header.Metadata, options);
            root.Add(metadataElement);

            // Add core elements
            foreach (var kvp in header.CoreElements)
            {
                var coreElement = SerializeCoreElement(kvp.Value, options);
                root.Add(coreElement);
            }

            return doc;
        }

        private XElement SerializeMetadata(XisfMetadata metadata, XisfWriterOptions options)
        {
            var element = new XElement("Metadata");

            // Required properties
            element.Add(new XElement("Property",
                new XAttribute("id", "XISF:CreationTime"),
                new XAttribute("type", "TimePoint"),
                new XAttribute("value", metadata.CreationTime.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture))));

            element.Add(new XElement("Property",
                new XAttribute("id", "XISF:CreatorApplication"),
                new XAttribute("type", "String"),
                new XAttribute("value", metadata.CreatorApplication)));

            // Optional properties
            if (!string.IsNullOrEmpty(metadata.CreatorModule))
            {
                element.Add(new XElement("Property",
                    new XAttribute("id", "XISF:CreatorModule"),
                    new XAttribute("type", "String"),
                    new XAttribute("value", metadata.CreatorModule)));
            }

            if (!string.IsNullOrEmpty(metadata.CreatorOS))
            {
                element.Add(new XElement("Property",
                    new XAttribute("id", "XISF:CreatorOS"),
                    new XAttribute("type", "String"),
                    new XAttribute("value", metadata.CreatorOS)));
            }

            return element;
        }

        public XElement SerializeProperty(XisfProperty property, XisfWriterOptions options)
        {
            var element = new XElement("Property",
                new XAttribute("id", property.Id));

            if (!string.IsNullOrEmpty(property.Comment))
            {
                element.Add(new XAttribute("comment", property.Comment));
            }

            switch (property)
            {
                case XisfScalarProperty<bool> boolProp:
                    element.Add(new XAttribute("type", "Boolean"));
                    element.Add(new XAttribute("value", boolProp.Value.ToString().ToLowerInvariant()));
                    break;

                case XisfScalarProperty<int> intProp:
                    element.Add(new XAttribute("type", "Int32"));
                    element.Add(new XAttribute("value", intProp.Value.ToString(CultureInfo.InvariantCulture)));
                    break;

                case XisfScalarProperty<uint> uintProp:
                    element.Add(new XAttribute("type", "UInt32"));
                    element.Add(new XAttribute("value", uintProp.Value.ToString(CultureInfo.InvariantCulture)));
                    break;

                case XisfScalarProperty<float> floatProp:
                    element.Add(new XAttribute("type", "Float32"));
                    element.Add(new XAttribute("value", floatProp.Value.ToString("G9", CultureInfo.InvariantCulture)));
                    break;

                case XisfScalarProperty<double> doubleProp:
                    element.Add(new XAttribute("type", "Float64"));
                    element.Add(new XAttribute("value", doubleProp.Value.ToString("G17", CultureInfo.InvariantCulture)));
                    break;

                case XisfStringProperty stringProp:
                    element.Add(new XAttribute("type", "String"));
                    element.Add(new XAttribute("value", stringProp.Value));
                    break;

                case XisfTimePointProperty timeProp:
                    element.Add(new XAttribute("type", "TimePoint"));
                    element.Add(new XAttribute("value", timeProp.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture)));
                    break;

                default:
                    throw new NotSupportedException($"Property type {property.GetType().Name} is not yet supported for serialization");
            }

            return element;
        }

        public XElement SerializeImage(XisfImage image, XisfWriterOptions options)
        {
            var element = new XElement("Image");

            // Required attributes
            var geometryStr = FormatGeometry(image.Geometry);
            element.Add(new XAttribute("geometry", geometryStr));
            element.Add(new XAttribute("sampleFormat", image.SampleFormat.ToString()));
            element.Add(new XAttribute("colorSpace", image.ColorSpace.ToString()));

            // Data block location
            var location = SerializeDataBlockLocation(image.PixelData);
            element.Add(new XAttribute("location", location));

            // Compression
            if (image.PixelData.Compression != null)
            {
                var compressionStr = FormatCompression(image.PixelData.Compression);
                element.Add(new XAttribute("compression", compressionStr));
            }

            // Checksum
            if (image.PixelData.Checksum != null)
            {
                var checksumStr = FormatChecksum(image.PixelData.Checksum);
                element.Add(new XAttribute("checksum", checksumStr));
            }

            // Optional attributes
            if (image.Bounds != null)
            {
                element.Add(new XAttribute("bounds",
                    $"{image.Bounds.Lower.ToString("G17", CultureInfo.InvariantCulture)}:{image.Bounds.Upper.ToString("G17", CultureInfo.InvariantCulture)}"));
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
                element.Add(new XAttribute("offset", image.Offset.Value.ToString("G17", CultureInfo.InvariantCulture)));
            }

            if (!string.IsNullOrEmpty(image.ImageId))
            {
                element.Add(new XAttribute("id", image.ImageId));
            }

            if (image.Uuid.HasValue)
            {
                element.Add(new XAttribute("uuid", image.Uuid.Value.ToString("D")));
            }

            // Child properties
            if (image.Properties != null)
            {
                foreach (var property in image.Properties)
                {
                    element.Add(SerializeProperty(property, options));
                }
            }

            // Associated elements
            if (image.AssociatedElements != null)
            {
                foreach (var associatedElement in image.AssociatedElements)
                {
                    // Skip invalid Reference elements (e.g., from unsupported deserialized elements)
                    if (associatedElement is XisfReference reference && string.IsNullOrEmpty(reference.RefId))
                        continue;

                    element.Add(SerializeCoreElement(associatedElement, options));
                }
            }

            return element;
        }

        public XElement SerializeCoreElement(XisfCoreElement element, XisfWriterOptions options)
        {
            return element switch
            {
                XisfReference reference => new XElement("Reference", new XAttribute("ref", reference.RefId)),

                XisfResolution resolution => new XElement("Resolution",
                    new XAttribute("horizontal", resolution.Horizontal.ToString("G17", CultureInfo.InvariantCulture)),
                    new XAttribute("vertical", resolution.Vertical.ToString("G17", CultureInfo.InvariantCulture)),
                    new XAttribute("unit", resolution.Unit.ToString()),
                    !string.IsNullOrEmpty(resolution.Uid) ? new XAttribute("uid", resolution.Uid) : null),

                XisfFitsKeyword fits => new XElement("FITSKeyword",
                    new XAttribute("name", fits.Name),
                    new XAttribute("value", fits.Value),
                    new XAttribute("comment", fits.Comment),
                    !string.IsNullOrEmpty(fits.Uid) ? new XAttribute("uid", fits.Uid) : null),

                XisfColorFilterArray cfa => new XElement("ColorFilterArray",
                    new XAttribute("pattern", cfa.Pattern),
                    new XAttribute("width", cfa.Width.ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("height", cfa.Height.ToString(CultureInfo.InvariantCulture)),
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

        private string FormatCompression(XisfCompression compression)
        {
            var codecStr = compression.Codec switch
            {
                XisfCompressionCodec.Zlib => "zlib",
                XisfCompressionCodec.ZlibSh => "zlib+sh",
                XisfCompressionCodec.LZ4 => "lz4",
                XisfCompressionCodec.LZ4Sh => "lz4+sh",
                XisfCompressionCodec.LZ4HC => "lz4hc",
                XisfCompressionCodec.LZ4HCSh => "lz4hc+sh",
                _ => throw new NotSupportedException($"Unsupported compression codec: {compression.Codec}")
            };

            var result = $"{codecStr}:{compression.UncompressedSize}";
            if (compression.ItemSize.HasValue)
            {
                result += $":{compression.ItemSize.Value}";
            }

            return result;
        }

        private string FormatChecksum(XisfChecksum checksum)
        {
            var algorithmStr = checksum.Algorithm switch
            {
                XisfHashAlgorithm.SHA1 => "sha1",
                XisfHashAlgorithm.SHA256 => "sha256",
                XisfHashAlgorithm.SHA512 => "sha512",
                XisfHashAlgorithm.SHA3_256 => "sha3-256",
                XisfHashAlgorithm.SHA3_512 => "sha3-512",
                _ => throw new NotSupportedException($"Unsupported hash algorithm: {checksum.Algorithm}")
            };

            var digestHex = Convert.ToHexString(checksum.Digest.ToArray()).ToLowerInvariant();
            return $"{algorithmStr}:{digestHex}";
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
