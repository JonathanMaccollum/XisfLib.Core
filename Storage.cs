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
            await stream.ReadExactlyAsync(headerBytes.AsMemory(0, 16), cancellationToken);

            var fileHeader = ParseFileHeader(headerBytes);
            if (!fileHeader.IsValid)
                throw new FormatException("Invalid XISF file header signature");

            // Read XML header
            var xmlHeaderBytes = new byte[fileHeader.HeaderLength];
            await stream.ReadExactlyAsync(xmlHeaderBytes.AsMemory(0, (int)fileHeader.HeaderLength), cancellationToken);

            var xmlHeaderText = Encoding.UTF8.GetString(xmlHeaderBytes);
            var xmlDocument = XDocument.Parse(xmlHeaderText);

            // Deserialize header
            var header = _xmlSerializer.DeserializeHeader(xmlDocument);

            // Parse images from XML
            var root = xmlDocument.Root!;
            var imageElements = root.Elements().Where(e => e.Name.LocalName == "Image").ToList();
            var images = new List<XisfImage>();

            XisfBlockReader blockReader = (block, itemSize) =>
                _dataBlockProcessor.ReadDataBlock(block, stream, options, itemSize);

            foreach (var imageElement in imageElements)
            {
                var image = _xmlSerializer.DeserializeImage(imageElement, blockReader);

                if (image.PixelData is not ExternalDataBlock)
                {
                    var pixelData = await _dataBlockProcessor.ReadDataBlockAsync(
                        image.PixelData,
                        stream,
                        options,
                        ByteOrderItemSize(image.SampleFormat),
                        cancellationToken);

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
                globalProperties.Add(_xmlSerializer.DeserializeProperty(propElement, blockReader));
            }

            return new XisfUnit(
                new MonolithicStorage(),
                header,
                images,
                globalProperties
            );
        }

        /// <summary>
        /// §10.4: byte order applies to the ordered multibyte units of the logical data. For the
        /// complex formats that unit is the component, not the whole sample.
        /// </summary>
        internal static int ByteOrderItemSize(XisfSampleFormat format) => format switch
        {
            XisfSampleFormat.Complex32 => 4,
            XisfSampleFormat.Complex64 => 8,
            _ => format.GetItemSize()
        };

        public async Task<XisfMetadataUnit> ReadMetadataAsync(Stream stream, XisfReaderOptions options, CancellationToken cancellationToken = default)
        {
            // Read and validate file header (16 bytes)
            var headerBytes = new byte[16];
            await stream.ReadExactlyAsync(headerBytes.AsMemory(0, 16), cancellationToken);

            var fileHeader = ParseFileHeader(headerBytes);
            if (!fileHeader.IsValid)
                throw new FormatException("Invalid XISF file header signature");

            // Read XML header
            var xmlHeaderBytes = new byte[fileHeader.HeaderLength];
            await stream.ReadExactlyAsync(xmlHeaderBytes.AsMemory(0, (int)fileHeader.HeaderLength), cancellationToken);

            var xmlHeaderText = Encoding.UTF8.GetString(xmlHeaderBytes);
            var xmlDocument = XDocument.Parse(xmlHeaderText);

            // Deserialize header
            var header = _xmlSerializer.DeserializeHeader(xmlDocument);

            // Parse image metadata from XML (without loading pixel data)
            var root = xmlDocument.Root!;
            var imageElements = root.Elements().Where(e => e.Name.LocalName == "Image").ToList();
            var imageMetadataList = new List<XisfImageMetadata>();

            XisfBlockReader blockReader = (block, itemSize) => IsAvailable(block, stream)
                ? _dataBlockProcessor.ReadDataBlock(block, stream, options, itemSize)
                : (ReadOnlyMemory<byte>?)null;

            foreach (var imageElement in imageElements)
            {
                var image = _xmlSerializer.DeserializeImage(imageElement, blockReader);

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
                else if (image.PixelData is EmbeddedDataBlock embeddedBlock)
                {
                    dataBlockInfo = new XisfDataBlockInfo(
                        0,
                        (ulong)embeddedBlock.Data.Length,
                        embeddedBlock.ByteOrder,
                        embeddedBlock.Checksum,
                        embeddedBlock.Compression
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
                globalProperties.Add(_xmlSerializer.DeserializeProperty(propElement, blockReader));
            }

            return new XisfMetadataUnit(
                new MonolithicStorage(),
                header,
                imageMetadataList,
                globalProperties
            );
        }

        private static bool IsAvailable(XisfDataBlock block, Stream stream)
            => block is not AttachedDataBlock attached ||
               (stream.CanSeek &&
                attached.Size <= (ulong)stream.Length &&
                attached.Position <= (ulong)stream.Length - attached.Size);

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

            XisfBlockReader blockReader = (block, itemSize) =>
                _dataBlockProcessor.ReadDataBlock(block, null, options, itemSize);

            foreach (var imageElement in imageElements)
            {
                var image = _xmlSerializer.DeserializeImage(imageElement, blockReader);

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
                            MonolithicStorageStrategy.ByteOrderItemSize(image.SampleFormat),
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
                globalProperties.Add(_xmlSerializer.DeserializeProperty(propElement, blockReader));
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

            XisfBlockReader blockReader = (block, itemSize) =>
                _dataBlockProcessor.ReadDataBlock(block, null, options, itemSize);

            foreach (var imageElement in imageElements)
            {
                var image = _xmlSerializer.DeserializeImage(imageElement, blockReader);

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
                globalProperties.Add(_xmlSerializer.DeserializeProperty(propElement, blockReader));
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
