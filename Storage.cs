using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace XisfLib.Core.Implementations
{
    /// <summary>
    /// Strategy interface for reading XISF units from different storage models.
    /// </summary>
    internal interface IStorageStrategy
    {
        Task<XisfUnit> ReadAsync(Stream stream, XisfReaderOptions options, CancellationToken cancellationToken = default);
        Task<XisfMetadataUnit> ReadMetadataAsync(Stream stream, XisfReaderOptions options, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Storage strategy for monolithic XISF files (.xisf).
    /// Specification Reference: Section 9.2 Monolithic XISF File
    /// </summary>
    internal sealed class MonolithicStorageStrategy : IStorageStrategy
    {
        private readonly IXisfXmlSerializer _xmlSerializer;
        private readonly IDataBlockProcessor _dataBlockProcessor;

        public MonolithicStorageStrategy(IXisfXmlSerializer xmlSerializer, IDataBlockProcessor dataBlockProcessor)
        {
            _xmlSerializer = xmlSerializer ?? throw new ArgumentNullException(nameof(xmlSerializer));
            _dataBlockProcessor = dataBlockProcessor ?? throw new ArgumentNullException(nameof(dataBlockProcessor));
        }

        public async Task<XisfUnit> ReadAsync(Stream stream, XisfReaderOptions options, CancellationToken cancellationToken = default)
        {
            // Read and validate file header (16 bytes)
            var headerBytes = new byte[16];
            await stream.ReadAsync(headerBytes.AsMemory(0, 16), cancellationToken);

            var fileHeader = ParseFileHeader(headerBytes);
            if (!fileHeader.IsValid)
                throw new FormatException("Invalid XISF file header signature");

            // Read XML header
            var xmlHeaderBytes = new byte[fileHeader.HeaderLength];
            await stream.ReadAsync(xmlHeaderBytes.AsMemory(0, (int)fileHeader.HeaderLength), cancellationToken);

            var xmlHeaderText = Encoding.UTF8.GetString(xmlHeaderBytes);
            var xmlDocument = XDocument.Parse(xmlHeaderText);

            // Deserialize header
            var header = _xmlSerializer.DeserializeHeader(xmlDocument);

            // Parse images from XML
            var root = xmlDocument.Root!;
            var imageElements = root.Elements().Where(e => e.Name.LocalName == "Image").ToList();
            var images = new List<XisfImage>();

            foreach (var imageElement in imageElements)
            {
                var image = _xmlSerializer.DeserializeImage(imageElement);

                // If image has attached data block, read it
                if (image.PixelData is AttachedDataBlock attachedBlock)
                {
                    var pixelData = await _dataBlockProcessor.ReadDataBlockAsync(
                        attachedBlock,
                        stream,
                        options,
                        cancellationToken);

                    // Update image with actual pixel data
                    image = image with
                    {
                        PixelData = new InlineDataBlock(pixelData, XisfEncoding.Base64)
                    };
                }

                images.Add(image);
            }

            // Parse global properties
            var propertyElements = root.Elements().Where(e => e.Name.LocalName == "Property" &&
                                                             e.Parent?.Name.LocalName == "xisf").ToList();
            var globalProperties = new List<XisfProperty>();

            foreach (var propElement in propertyElements)
            {
                globalProperties.Add(_xmlSerializer.DeserializeProperty(propElement));
            }

            return new XisfUnit(
                new MonolithicStorage(),
                header,
                images,
                globalProperties
            );
        }

        public async Task<XisfMetadataUnit> ReadMetadataAsync(Stream stream, XisfReaderOptions options, CancellationToken cancellationToken = default)
        {
            // Read and validate file header (16 bytes)
            var headerBytes = new byte[16];
            await stream.ReadAsync(headerBytes.AsMemory(0, 16), cancellationToken);

            var fileHeader = ParseFileHeader(headerBytes);
            if (!fileHeader.IsValid)
                throw new FormatException("Invalid XISF file header signature");

            // Read XML header
            var xmlHeaderBytes = new byte[fileHeader.HeaderLength];
            await stream.ReadAsync(xmlHeaderBytes.AsMemory(0, (int)fileHeader.HeaderLength), cancellationToken);

            var xmlHeaderText = Encoding.UTF8.GetString(xmlHeaderBytes);
            var xmlDocument = XDocument.Parse(xmlHeaderText);

            // Deserialize header
            var header = _xmlSerializer.DeserializeHeader(xmlDocument);

            // Parse image metadata from XML (without loading pixel data)
            var root = xmlDocument.Root!;
            var imageElements = root.Elements().Where(e => e.Name.LocalName == "Image").ToList();
            var imageMetadataList = new List<XisfImageMetadata>();

            foreach (var imageElement in imageElements)
            {
                var image = _xmlSerializer.DeserializeImage(imageElement);

                // Extract data block info without loading pixel data
                XisfDataBlockInfo dataBlockInfo;
                if (image.PixelData is AttachedDataBlock attachedBlock)
                {
                    dataBlockInfo = new XisfDataBlockInfo(
                        attachedBlock.Position,
                        attachedBlock.Size,
                        attachedBlock.ByteOrder,
                        attachedBlock.Checksum,
                        attachedBlock.Compression
                    );
                }
                else if (image.PixelData is InlineDataBlock inlineBlock)
                {
                    dataBlockInfo = new XisfDataBlockInfo(
                        0, // Inline blocks don't have file positions
                        (ulong)inlineBlock.Data.Length,
                        inlineBlock.ByteOrder,
                        inlineBlock.Checksum,
                        inlineBlock.Compression
                    );
                }
                else
                {
                    dataBlockInfo = new XisfDataBlockInfo(0, 0);
                }

                // Create image metadata without pixel data
                var imageMetadata = new XisfImageMetadata(
                    image.Geometry,
                    image.SampleFormat,
                    image.ColorSpace,
                    dataBlockInfo,
                    image.Bounds,
                    image.PixelStorage,
                    image.ImageType,
                    image.Offset,
                    image.Orientation,
                    image.ImageId,
                    image.Uuid,
                    image.Properties,
                    image.AssociatedElements
                );

                imageMetadataList.Add(imageMetadata);
            }

            // Parse global properties
            var propertyElements = root.Elements().Where(e => e.Name.LocalName == "Property" &&
                                                             e.Parent?.Name.LocalName == "xisf").ToList();
            var globalProperties = new List<XisfProperty>();

            foreach (var propElement in propertyElements)
            {
                globalProperties.Add(_xmlSerializer.DeserializeProperty(propElement));
            }

            return new XisfMetadataUnit(
                new MonolithicStorage(),
                header,
                imageMetadataList,
                globalProperties
            );
        }

        private XisfFileHeader ParseFileHeader(byte[] headerBytes)
        {
            var signature = new byte[8];
            Array.Copy(headerBytes, 0, signature, 0, 8);

            var headerLength = BitConverter.ToUInt32(headerBytes, 8);
            var reserved = BitConverter.ToUInt32(headerBytes, 12);

            return new XisfFileHeader(signature, headerLength, reserved);
        }
    }

    /// <summary>
    /// Storage strategy for distributed XISF units (.xish header + .xisb data blocks).
    /// Specification Reference: Section 9.3 XISF Header File, Section 9.4 XISF Data Blocks File
    /// </summary>
    internal sealed class DistributedStorageStrategy : IStorageStrategy
    {
        private readonly IXisfXmlSerializer _xmlSerializer;
        private readonly IDataBlockProcessor _dataBlockProcessor;
        private readonly IStreamResolver _streamResolver;

        public DistributedStorageStrategy(
            IXisfXmlSerializer xmlSerializer,
            IDataBlockProcessor dataBlockProcessor,
            IStreamResolver streamResolver)
        {
            _xmlSerializer = xmlSerializer ?? throw new ArgumentNullException(nameof(xmlSerializer));
            _dataBlockProcessor = dataBlockProcessor ?? throw new ArgumentNullException(nameof(dataBlockProcessor));
            _streamResolver = streamResolver ?? throw new ArgumentNullException(nameof(streamResolver));
        }

        public async Task<XisfUnit> ReadAsync(Stream stream, XisfReaderOptions options, CancellationToken cancellationToken = default)
        {
            // For distributed XISF, the stream contains only the XML header (no binary header)
            var xmlHeaderText = await new StreamReader(stream, Encoding.UTF8).ReadToEndAsync();
            var xmlDocument = XDocument.Parse(xmlHeaderText);

            // Deserialize header
            var header = _xmlSerializer.DeserializeHeader(xmlDocument);

            // Parse images from XML
            var root = xmlDocument.Root!;
            var imageElements = root.Elements().Where(e => e.Name.LocalName == "Image").ToList();
            var images = new List<XisfImage>();

            foreach (var imageElement in imageElements)
            {
                var image = _xmlSerializer.DeserializeImage(imageElement);

                // For distributed storage, external blocks need to be resolved
                if (image.PixelData is ExternalDataBlock externalBlock)
                {
                    // External blocks in distributed units are typically in .xisb files
                    // The stream resolver handles this
                    if (options.LoadExternalReferences)
                    {
                        var pixelData = await _dataBlockProcessor.ReadDataBlockAsync(
                            externalBlock,
                            stream, // Will be resolved by the stream resolver
                            options,
                            cancellationToken);

                        image = image with
                        {
                            PixelData = new InlineDataBlock(pixelData, XisfEncoding.Base64)
                        };
                    }
                }

                images.Add(image);
            }

            // Parse global properties
            var propertyElements = root.Elements().Where(e => e.Name.LocalName == "Property" &&
                                                             e.Parent?.Name.LocalName == "xisf").ToList();
            var globalProperties = new List<XisfProperty>();

            foreach (var propElement in propertyElements)
            {
                globalProperties.Add(_xmlSerializer.DeserializeProperty(propElement));
            }

            // Determine the data block files (would need to be provided or discovered)
            var dataBlockFiles = new List<string>();

            return new XisfUnit(
                new DistributedStorage("header.xish", dataBlockFiles),
                header,
                images,
                globalProperties
            );
        }

        public async Task<XisfMetadataUnit> ReadMetadataAsync(Stream stream, XisfReaderOptions options, CancellationToken cancellationToken = default)
        {
            // For distributed XISF, the stream contains only the XML header (no binary header)
            var xmlHeaderText = await new StreamReader(stream, Encoding.UTF8).ReadToEndAsync();
            var xmlDocument = XDocument.Parse(xmlHeaderText);

            // Deserialize header
            var header = _xmlSerializer.DeserializeHeader(xmlDocument);

            // Parse image metadata from XML (without loading pixel data)
            var root = xmlDocument.Root!;
            var imageElements = root.Elements().Where(e => e.Name.LocalName == "Image").ToList();
            var imageMetadataList = new List<XisfImageMetadata>();

            foreach (var imageElement in imageElements)
            {
                var image = _xmlSerializer.DeserializeImage(imageElement);

                // Extract data block info without loading pixel data
                XisfDataBlockInfo dataBlockInfo;
                if (image.PixelData is ExternalDataBlock externalBlock)
                {
                    dataBlockInfo = new XisfDataBlockInfo(
                        externalBlock.Position ?? 0,
                        externalBlock.Size ?? 0,
                        externalBlock.ByteOrder,
                        externalBlock.Checksum,
                        externalBlock.Compression
                    );
                }
                else if (image.PixelData is InlineDataBlock inlineBlock)
                {
                    dataBlockInfo = new XisfDataBlockInfo(
                        0,
                        (ulong)inlineBlock.Data.Length,
                        inlineBlock.ByteOrder,
                        inlineBlock.Checksum,
                        inlineBlock.Compression
                    );
                }
                else
                {
                    dataBlockInfo = new XisfDataBlockInfo(0, 0);
                }

                // Create image metadata without pixel data
                var imageMetadata = new XisfImageMetadata(
                    image.Geometry,
                    image.SampleFormat,
                    image.ColorSpace,
                    dataBlockInfo,
                    image.Bounds,
                    image.PixelStorage,
                    image.ImageType,
                    image.Offset,
                    image.Orientation,
                    image.ImageId,
                    image.Uuid,
                    image.Properties,
                    image.AssociatedElements
                );

                imageMetadataList.Add(imageMetadata);
            }

            // Parse global properties
            var propertyElements = root.Elements().Where(e => e.Name.LocalName == "Property" &&
                                                             e.Parent?.Name.LocalName == "xisf").ToList();
            var globalProperties = new List<XisfProperty>();

            foreach (var propElement in propertyElements)
            {
                globalProperties.Add(_xmlSerializer.DeserializeProperty(propElement));
            }

            // Determine the data block files (would need to be provided or discovered)
            var dataBlockFiles = new List<string>();

            return new XisfMetadataUnit(
                new DistributedStorage("header.xish", dataBlockFiles),
                header,
                imageMetadataList,
                globalProperties
            );
        }
    }

    /// <summary>
    /// Factory for creating the appropriate storage strategy based on file type.
    /// </summary>
    internal sealed class StorageStrategyFactory
    {
        private readonly IXisfComponentFactory _componentFactory;

        public StorageStrategyFactory(IXisfComponentFactory componentFactory)
        {
            _componentFactory = componentFactory ?? throw new ArgumentNullException(nameof(componentFactory));
        }

        public IStorageStrategy CreateStrategy(string filePath)
        {
            var extension = Path.GetExtension(filePath).ToLowerInvariant();

            var xmlSerializer = _componentFactory.CreateXmlSerializer();
            var compressionProvider = _componentFactory.CreateCompressionProvider();
            var checksumProvider = _componentFactory.CreateChecksumProvider();
            var dataBlockProcessor = _componentFactory.CreateDataBlockProcessor(
                compressionProvider,
                checksumProvider);

            return extension switch
            {
                ".xisf" => new MonolithicStorageStrategy(xmlSerializer, dataBlockProcessor),
                ".xish" => new DistributedStorageStrategy(
                    xmlSerializer,
                    dataBlockProcessor,
                    _componentFactory.CreateStreamResolver()),
                _ => new MonolithicStorageStrategy(xmlSerializer, dataBlockProcessor) // Default to monolithic
            };
        }

        public IStorageStrategy CreateMonolithicStrategy()
        {
            var xmlSerializer = _componentFactory.CreateXmlSerializer();
            var compressionProvider = _componentFactory.CreateCompressionProvider();
            var checksumProvider = _componentFactory.CreateChecksumProvider();
            var dataBlockProcessor = _componentFactory.CreateDataBlockProcessor(
                compressionProvider,
                checksumProvider);

            return new MonolithicStorageStrategy(xmlSerializer, dataBlockProcessor);
        }

        public IStorageStrategy CreateDistributedStrategy()
        {
            var xmlSerializer = _componentFactory.CreateXmlSerializer();
            var compressionProvider = _componentFactory.CreateCompressionProvider();
            var checksumProvider = _componentFactory.CreateChecksumProvider();
            var dataBlockProcessor = _componentFactory.CreateDataBlockProcessor(
                compressionProvider,
                checksumProvider);

            return new DistributedStorageStrategy(
                xmlSerializer,
                dataBlockProcessor,
                _componentFactory.CreateStreamResolver());
        }
    }
}
