using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using XisfLib.Core.Implementations;

namespace XisfLib.Core
{
    /// <summary>
    /// Public API for writing XISF (Extensible Image Serialization Format) files.
    /// Supports both monolithic (.xisf) and distributed (.xish + .xisb) XISF units.
    /// Specification Reference: Section 7.1 Baseline Encoder Requirements
    /// </summary>
    public sealed class XisfWriter : IDisposable
    {
        private readonly IXisfComponentFactory _componentFactory;
        private readonly IXisfValidator _validator;
        private bool _disposed;

        /// <summary>
        /// Creates a new XISF writer with default options.
        /// </summary>
        public XisfWriter() : this(new XisfWriterOptions())
        {
        }

        /// <summary>
        /// Creates a new XISF writer with the specified options.
        /// </summary>
        /// <param name="options">Writer configuration options.</param>
        public XisfWriter(XisfWriterOptions options)
        {
            Options = options ?? throw new ArgumentNullException(nameof(options));
            _componentFactory = new XisfComponentFactory();
            _validator = _componentFactory.CreateValidator();
        }

        /// <summary>
        /// Gets the writer options.
        /// </summary>
        public XisfWriterOptions Options { get; }

        /// <summary>
        /// Writes an XISF unit to the specified file path.
        /// The file type is determined by the extension (.xisf for monolithic, .xish for distributed).
        /// </summary>
        /// <param name="unit">The XISF unit to write.</param>
        /// <param name="filePath">Path where the XISF file will be written.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public async Task WriteAsync(XisfUnit unit, string filePath, CancellationToken cancellationToken = default)
        {
            if (unit == null)
                throw new ArgumentNullException(nameof(unit));
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path cannot be null or empty", nameof(filePath));

            ObjectDisposedException.ThrowIf(_disposed, this);

            // Validate the unit before writing
            var validationResult = _validator.ValidateUnit(unit);
            if (!validationResult.IsValid)
            {
                var errorMessage = string.Join(Environment.NewLine, validationResult.Errors);
                throw new InvalidOperationException($"XISF unit validation failed:{Environment.NewLine}{errorMessage}");
            }

            // Ensure directory exists
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Determine storage model from unit or file extension
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            var storageModel = unit.StorageModel;

            if (storageModel is MonolithicStorage || extension == ".xisf")
            {
                await WriteMonolithicAsync(unit, filePath, cancellationToken);
            }
            else if (storageModel is DistributedStorage || extension == ".xish")
            {
                await WriteDistributedAsync(unit, filePath, cancellationToken);
            }
            else
            {
                // Default to monolithic
                await WriteMonolithicAsync(unit, filePath, cancellationToken);
            }
        }

        /// <summary>
        /// Writes an XISF unit to a stream.
        /// Note: Stream writing is only supported for monolithic format.
        /// </summary>
        /// <param name="unit">The XISF unit to write.</param>
        /// <param name="stream">Stream where XISF data will be written.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public async Task WriteAsync(XisfUnit unit, Stream stream, CancellationToken cancellationToken = default)
        {
            if (unit == null)
                throw new ArgumentNullException(nameof(unit));
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));
            if (!stream.CanWrite)
                throw new ArgumentException("Stream must be writable", nameof(stream));

            ObjectDisposedException.ThrowIf(_disposed, this);

            // Validate the unit before writing
            var validationResult = _validator.ValidateUnit(unit);
            if (!validationResult.IsValid)
            {
                var errorMessage = string.Join(Environment.NewLine, validationResult.Errors);
                throw new InvalidOperationException($"XISF unit validation failed:{Environment.NewLine}{errorMessage}");
            }

            await WriteMonolithicToStreamAsync(unit, stream, cancellationToken);
        }

        /// <summary>
        /// Validates an XISF unit without writing it.
        /// </summary>
        /// <param name="unit">The XISF unit to validate.</param>
        /// <returns>True if the unit is valid and can be written.</returns>
        public bool Validate(XisfUnit unit)
        {
            if (unit == null)
                return false;

            var result = _validator.ValidateUnit(unit);
            return result.IsValid;
        }

        private async Task WriteMonolithicAsync(XisfUnit unit, string filePath, CancellationToken cancellationToken)
        {
            using var stream = File.Create(filePath);
            await WriteMonolithicToStreamAsync(unit, stream, cancellationToken);
        }

        private async Task WriteMonolithicToStreamAsync(XisfUnit unit, Stream stream, CancellationToken cancellationToken)
        {
            var xmlSerializer = _componentFactory.CreateXmlSerializer();
            var compressionProvider = _componentFactory.CreateCompressionProvider();
            var checksumProvider = _componentFactory.CreateChecksumProvider();
            var streamResolver = _componentFactory.CreateStreamResolver(Options.FileStreamProvider);
            var dataBlockProcessor = _componentFactory.CreateDataBlockProcessor(compressionProvider, checksumProvider);

            // Step 1: Extract pixel data from all images and prepare for compression
            var imageData = new System.Collections.Generic.List<(XisfImage Image, ReadOnlyMemory<byte> PixelData)>();

            foreach (var image in unit.Images)
            {
                var pixelData = await ExtractPixelDataAsync(image, dataBlockProcessor, cancellationToken);
                imageData.Add((image, pixelData));
            }

            // Step 2: Apply compression if requested
            var processedImageData = new System.Collections.Generic.List<(XisfImage Image, ReadOnlyMemory<byte> ProcessedData, XisfCompression? Compression)>();

            foreach (var (image, pixelData) in imageData)
            {
                ReadOnlyMemory<byte> processedData = pixelData;
                XisfCompression? compression = null;

                if (Options.DefaultCompression.HasValue)
                {
                    var itemSize = (uint)image.SampleFormat.GetItemSize();
                    var uncompressedSize = (ulong)pixelData.Length;

                    compression = new XisfCompression(
                        Options.DefaultCompression.Value,
                        uncompressedSize,
                        itemSize);

                    processedData = await compressionProvider.CompressAsync(pixelData, compression, cancellationToken);
                }

                processedImageData.Add((image, processedData, compression));
            }

            // Step 3: Calculate attachment positions
            // Start after file header (16 bytes) + XML header (to be calculated)
            var tempImages = new System.Collections.Generic.List<XisfImage>();
            ulong currentPosition = 16; // File header size

            // First, create a temporary XML to calculate its size
            var tempXmlDoc = xmlSerializer.SerializeHeader(unit.Header, Options);
            var tempRoot = tempXmlDoc.Root!;

            // Add temporary images with placeholder positions
            foreach (var (image, processedData, compression) in processedImageData)
            {
                var tempBlock = new AttachedDataBlock(0, 0, Compression: compression);
                var tempImage = image with { PixelData = tempBlock };
                tempImages.Add(tempImage);

                var tempImageElement = xmlSerializer.SerializeImage(tempImage, Options);
                tempRoot.Add(tempImageElement);
            }

            // Add global properties
            foreach (var property in unit.GlobalProperties)
            {
                var propertyElement = xmlSerializer.SerializeProperty(property, Options);
                tempRoot.Add(propertyElement);
            }

            // Calculate XML size
            var xmlSettings = new System.Xml.XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false), // No BOM
                Indent = Options.PrettyPrintXml,
                IndentChars = "  ",
                OmitXmlDeclaration = false
            };

            byte[] tempXmlBytes;
            using (var ms = new MemoryStream())
            {
                using (var xmlWriter = System.Xml.XmlWriter.Create(ms, xmlSettings))
                {
                    tempXmlDoc.WriteTo(xmlWriter);
                }
                tempXmlBytes = ms.ToArray();
            }

            // Step 4: Generate final XML - iteratively until size stabilizes
            byte[] finalXmlBytes = tempXmlBytes;
            for (int iteration = 0; iteration < 5; iteration++)
            {
                currentPosition = 16 + (ulong)finalXmlBytes.Length;
                var finalImages = new System.Collections.Generic.List<XisfImage>();

                for (int i = 0; i < processedImageData.Count; i++)
                {
                    var (image, processedData, compression) = processedImageData[i];
                    var size = (ulong)processedData.Length;

                    var attachedBlock = new AttachedDataBlock(currentPosition, size, Compression: compression);
                    var finalImage = image with { PixelData = attachedBlock };
                    finalImages.Add(finalImage);

                    currentPosition += size;
                }

                // Generate XML with these positions
                var iterXmlDoc = xmlSerializer.SerializeHeader(unit.Header, Options);
                var iterRoot = iterXmlDoc.Root!;

                foreach (var image in finalImages)
                {
                    var imageElement = xmlSerializer.SerializeImage(image, Options);
                    iterRoot.Add(imageElement);
                }

                foreach (var property in unit.GlobalProperties)
                {
                    var propertyElement = xmlSerializer.SerializeProperty(property, Options);
                    iterRoot.Add(propertyElement);
                }

                byte[] iterXmlBytes;
                using (var ms = new MemoryStream())
                {
                    using (var xmlWriter = System.Xml.XmlWriter.Create(ms, xmlSettings))
                    {
                        iterXmlDoc.WriteTo(xmlWriter);
                    }
                    iterXmlBytes = ms.ToArray();
                }

                // Check if size stabilized
                if (iterXmlBytes.Length == finalXmlBytes.Length)
                {
                    finalXmlBytes = iterXmlBytes;
                    break;
                }

                finalXmlBytes = iterXmlBytes;
            }

            // Step 6: Write file header (Specification Section 9.2)
            var signature = Encoding.ASCII.GetBytes("XISF0100");
            await stream.WriteAsync(signature, 0, 8, cancellationToken);

            var headerLength = (uint)finalXmlBytes.Length;
            await stream.WriteAsync(BitConverter.GetBytes(headerLength), 0, 4, cancellationToken);

            var reserved = (uint)0;
            await stream.WriteAsync(BitConverter.GetBytes(reserved), 0, 4, cancellationToken);

            // Step 7: Write XML header
            await stream.WriteAsync(finalXmlBytes, 0, finalXmlBytes.Length, cancellationToken);

            // Step 8: Write attached data blocks in order
            for (int i = 0; i < processedImageData.Count; i++)
            {
                var (_, processedData, _) = processedImageData[i];
                await stream.WriteAsync(processedData.ToArray(), 0, processedData.Length, cancellationToken);
            }

            await stream.FlushAsync(cancellationToken);
        }

        private Task WriteDistributedAsync(XisfUnit unit, string filePath, CancellationToken cancellationToken)
        {
            var xmlSerializer = _componentFactory.CreateXmlSerializer();

            // Serialize to XML (without binary data blocks)
            var xmlDocument = xmlSerializer.SerializeHeader(unit.Header, Options);

            // Add images to XML
            var root = xmlDocument.Root!;
            foreach (var image in unit.Images)
            {
                var imageElement = xmlSerializer.SerializeImage(image, Options);
                root.Add(imageElement);
            }

            // Add global properties to XML
            foreach (var property in unit.GlobalProperties)
            {
                var propertyElement = xmlSerializer.SerializeProperty(property, Options);
                root.Add(propertyElement);
            }

            // Write XML header file
            var xmlSettings = new System.Xml.XmlWriterSettings
            {
                Encoding = Encoding.UTF8,
                Indent = Options.PrettyPrintXml,
                IndentChars = "  ",
                OmitXmlDeclaration = false
            };

            using (var stream = File.Create(filePath))
            using (var xmlWriter = System.Xml.XmlWriter.Create(stream, xmlSettings))
            {
                xmlDocument.WriteTo(xmlWriter);
            }

            // Write external data blocks if needed
            // (Implementation would depend on specific requirements for distributed storage)
            return Task.CompletedTask;
        }

        private Task<ReadOnlyMemory<byte>> ExtractPixelDataAsync(
            XisfImage image,
            IDataBlockProcessor dataBlockProcessor,
            CancellationToken cancellationToken)
        {
            var result = image.PixelData switch
            {
                InlineDataBlock inline => inline.Data,
                EmbeddedDataBlock embedded => embedded.Data,
                AttachedDataBlock attached =>
                    throw new InvalidOperationException("Cannot extract pixel data from attached block without source stream"),
                ExternalDataBlock external =>
                    throw new InvalidOperationException("Cannot extract pixel data from external block in this context"),
                _ => throw new NotSupportedException($"Unsupported data block type: {image.PixelData.GetType().Name}")
            };
            return Task.FromResult(result);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
        }
    }
}
