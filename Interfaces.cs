using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace XisfLib.Core
{
    /// <summary>
    /// Provides XML serialization and deserialization for XISF headers and elements.
    /// Specification Reference: Section 9.5 XISF Header, Section 11 Core Elements
    /// </summary>
    internal interface IXisfXmlSerializer
    {
        /// <summary>
        /// Serializes an XISF header to XML.
        /// </summary>
        XDocument SerializeHeader(XisfHeader header, XisfWriterOptions options);

        /// <summary>
        /// Deserializes an XISF header from XML.
        /// </summary>
        XisfHeader DeserializeHeader(XDocument document);

        /// <summary>
        /// Serializes an XISF property to an XML element.
        /// Specification Reference: Section 11.1 Property Core Element
        /// </summary>
        XElement SerializeProperty(XisfProperty property, XisfWriterOptions options);

        /// <summary>
        /// Deserializes an XISF property from an XML element.
        /// </summary>
        XisfProperty DeserializeProperty(XElement element);

        /// <summary>
        /// Serializes an XISF image to an XML element.
        /// Specification Reference: Section 11.5 Image Core Element
        /// </summary>
        XElement SerializeImage(XisfImage image, XisfWriterOptions options);

        /// <summary>
        /// Deserializes an XISF image from an XML element.
        /// </summary>
        XisfImage DeserializeImage(XElement element);

        /// <summary>
        /// Serializes a core element to XML.
        /// </summary>
        XElement SerializeCoreElement(XisfCoreElement element, XisfWriterOptions options);

        /// <summary>
        /// Deserializes a core element from XML.
        /// </summary>
        XisfCoreElement DeserializeCoreElement(XElement element);

        /// <summary>
        /// Serializes a data block reference to location attribute format.
        /// Specification Reference: Section 10.3 XISF Data Block Location
        /// </summary>
        string SerializeDataBlockLocation(XisfDataBlock block);

        /// <summary>
        /// Deserializes a data block reference from location attribute format.
        /// </summary>
        XisfDataBlock DeserializeDataBlockLocation(string location);
    }

    /// <summary>
    /// Handles reading and writing of XISF data blocks with encoding, compression, and checksums.
    /// Specification Reference: Section 10 XISF Data Block
    /// </summary>
    internal interface IDataBlockProcessor
    {
        /// <summary>
        /// Reads a data block from a stream asynchronously.
        /// Handles decompression, checksum validation, and byte order conversion.
        /// </summary>
        Task<ReadOnlyMemory<byte>> ReadDataBlockAsync(
            XisfDataBlock block, 
            Stream source, 
            XisfReaderOptions options,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Writes a data block to a stream asynchronously.
        /// Handles compression, checksum generation, and byte order conversion.
        /// </summary>
        Task WriteDataBlockAsync(
            XisfDataBlock block, 
            ReadOnlyMemory<byte> data, 
            Stream target, 
            XisfWriterOptions options,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Encodes binary data to Base64 or Hex encoding.
        /// Specification Reference: Section 10.3 inline/embedded encoding
        /// </summary>
        string EncodeData(ReadOnlyMemory<byte> data, XisfEncoding encoding);

        /// <summary>
        /// Decodes Base64 or Hex encoded data to binary.
        /// </summary>
        ReadOnlyMemory<byte> DecodeData(string encoded, XisfEncoding encoding);

        /// <summary>
        /// Converts byte order of data.
        /// Specification Reference: Section 10.4 XISF Data Block Byte Order
        /// </summary>
        ReadOnlyMemory<byte> ConvertByteOrder(
            ReadOnlyMemory<byte> data, 
            XisfByteOrder currentOrder, 
            XisfByteOrder targetOrder, 
            int itemSize);

        /// <summary>
        /// Determines if byte order conversion is needed based on system architecture.
        /// </summary>
        bool RequiresByteOrderConversion(XisfByteOrder dataOrder);
    }

    /// <summary>
    /// Provides compression and decompression services for XISF data blocks.
    /// Specification Reference: Section 10.6 XISF Data Block Compression
    /// </summary>
    internal interface ICompressionProvider
    {
        /// <summary>
        /// Checks if a compression codec is supported.
        /// </summary>
        bool SupportsCodec(XisfCompressionCodec codec);

        /// <summary>
        /// Compresses data using the specified compression settings.
        /// Specification Reference: Sections 10.6.3-10.6.8
        /// </summary>
        Task<ReadOnlyMemory<byte>> CompressAsync(
            ReadOnlyMemory<byte> data, 
            XisfCompression compression,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Decompresses data using the specified compression settings.
        /// </summary>
        Task<ReadOnlyMemory<byte>> DecompressAsync(
            ReadOnlyMemory<byte> compressed, 
            XisfCompression compression,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Applies byte shuffling transformation for improved compression.
        /// Specification Reference: Section 10.6.2 Byte Shuffling
        /// </summary>
        ReadOnlyMemory<byte> ApplyByteShuffle(ReadOnlyMemory<byte> data, uint itemSize);

        /// <summary>
        /// Reverses byte shuffling transformation after decompression.
        /// </summary>
        ReadOnlyMemory<byte> RemoveByteShuffle(ReadOnlyMemory<byte> shuffled, uint itemSize);

        /// <summary>
        /// Gets the maximum supported block size for a codec.
        /// </summary>
        ulong GetMaxBlockSize(XisfCompressionCodec codec);

        /// <summary>
        /// Splits data into subblocks for compression if needed.
        /// </summary>
        IEnumerable<ReadOnlyMemory<byte>> SplitIntoSubblocks(
            ReadOnlyMemory<byte> data, 
            XisfCompressionCodec codec);
    }

    /// <summary>
    /// Provides cryptographic checksum calculation and validation.
    /// Specification Reference: Section 10.5 XISF Data Block Checksum
    /// </summary>
    internal interface IChecksumProvider
    {
        /// <summary>
        /// Calculates a checksum for data using the specified algorithm.
        /// Specification Reference: Table 9 - Cryptographic Hashing Algorithms
        /// </summary>
        ReadOnlyMemory<byte> Calculate(ReadOnlyMemory<byte> data, XisfHashAlgorithm algorithm);

        /// <summary>
        /// Calculates a checksum asynchronously for large data.
        /// </summary>
        Task<ReadOnlyMemory<byte>> CalculateAsync(
            ReadOnlyMemory<byte> data, 
            XisfHashAlgorithm algorithm,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Validates data against a checksum.
        /// </summary>
        bool Validate(ReadOnlyMemory<byte> data, XisfChecksum checksum);

        /// <summary>
        /// Validates data against a checksum asynchronously.
        /// </summary>
        Task<bool> ValidateAsync(
            ReadOnlyMemory<byte> data, 
            XisfChecksum checksum,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the list of supported hash algorithms.
        /// SHA-1 must be supported per specification.
        /// </summary>
        IReadOnlyList<XisfHashAlgorithm> SupportedAlgorithms { get; }

        /// <summary>
        /// Converts a checksum to its hexadecimal string representation.
        /// </summary>
        string ToHexString(ReadOnlyMemory<byte> checksum);

        /// <summary>
        /// Parses a hexadecimal string to a checksum.
        /// </summary>
        ReadOnlyMemory<byte> FromHexString(string hex);
    }

    /// <summary>
    /// Resolves and provides access to various stream sources for XISF data.
    /// Specification Reference: Section 10.3 XISF Data Block Location
    /// </summary>
    internal interface IStreamResolver
    {
        /// <summary>
        /// Resolves a file path to a stream.
        /// Handles both absolute and relative paths per specification.
        /// </summary>
        Stream ResolveFileStream(string path, FileMode mode, FileAccess access);

        /// <summary>
        /// Resolves a file path to a stream asynchronously.
        /// </summary>
        Task<Stream> ResolveFileStreamAsync(
            string path, 
            FileMode mode, 
            FileAccess access,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Resolves a URI to a stream for external data blocks.
        /// Specification Reference: location="url(...)"
        /// </summary>
        Task<Stream> ResolveUriStreamAsync(
            Uri uri,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Creates a view stream for an attached data block within a parent stream.
        /// Specification Reference: location="attachment:position:size"
        /// </summary>
        Stream ResolveAttachedStream(Stream parent, ulong position, ulong size);

        /// <summary>
        /// Resolves a path relative to the header directory.
        /// Specification Reference: location="path(@header_dir/...)"
        /// </summary>
        string ResolveRelativePath(string headerPath, string relativePath);

        /// <summary>
        /// Opens an XISF data blocks file and provides access to its index.
        /// Specification Reference: Section 9.4 XISF Data Blocks File
        /// </summary>
        Task<IXisfDataBlocksFile> OpenDataBlocksFileAsync(
            string path,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Represents an open XISF data blocks file with index access.
    /// Specification Reference: Section 9.4 XISF Data Blocks File
    /// </summary>
    internal interface IXisfDataBlocksFile : IDisposable
    {
        /// <summary>
        /// Gets the file header.
        /// </summary>
        XisfDataBlocksFileHeader Header { get; }

        /// <summary>
        /// Gets the block index.
        /// </summary>
        XisfBlockIndex BlockIndex { get; }

        /// <summary>
        /// Reads a data block by its unique identifier.
        /// </summary>
        Task<ReadOnlyMemory<byte>> ReadBlockAsync(
            ulong uniqueId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Writes a data block and updates the index.
        /// </summary>
        Task<ulong> WriteBlockAsync(
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets a stream for reading a specific block.
        /// </summary>
        Stream GetBlockStream(ulong uniqueId);
    }

    /// <summary>
    /// Factory for creating infrastructure components with proper dependencies.
    /// </summary>
    internal interface IXisfComponentFactory
    {
        /// <summary>
        /// Creates an XML serializer instance.
        /// </summary>
        IXisfXmlSerializer CreateXmlSerializer();

        /// <summary>
        /// Creates a data block processor with the necessary providers.
        /// </summary>
        IDataBlockProcessor CreateDataBlockProcessor(
            ICompressionProvider compressionProvider,
            IChecksumProvider checksumProvider);

        /// <summary>
        /// Creates a compression provider with support for standard codecs.
        /// </summary>
        ICompressionProvider CreateCompressionProvider();

        /// <summary>
        /// Creates a checksum provider with support for standard algorithms.
        /// </summary>
        IChecksumProvider CreateChecksumProvider();

        /// <summary>
        /// Creates a stream resolver with the specified options.
        /// </summary>
        IStreamResolver CreateStreamResolver(
            Func<string, Stream>? fileStreamProvider = null,
            Func<Uri, Stream>? uriStreamProvider = null);

        /// <summary>
        /// Creates a validator instance.
        /// </summary>
        IXisfValidator CreateValidator();
    }

    /// <summary>
    /// Provides validation services for XISF data structures.
    /// </summary>
    internal interface IXisfValidator
    {
        /// <summary>
        /// Validates an XISF unit for specification compliance.
        /// </summary>
        ValidationResult ValidateUnit(XisfUnit unit);

        /// <summary>
        /// Validates an XISF header structure.
        /// </summary>
        ValidationResult ValidateHeader(XisfHeader header);

        /// <summary>
        /// Validates an XISF image definition.
        /// </summary>
        ValidationResult ValidateImage(XisfImage image);

        /// <summary>
        /// Validates a property identifier format.
        /// Specification Reference: Section 8.4.1 Property Identifier
        /// </summary>
        bool IsValidPropertyId(string id);

        /// <summary>
        /// Validates a unique element identifier format.
        /// Specification Reference: Section 11 XISF Core Elements
        /// </summary>
        bool IsValidUniqueId(string uid);
    }

    /// <summary>
    /// Result of a validation operation.
    /// </summary>
    internal record ValidationResult(
        bool IsValid,
        IReadOnlyList<string> Errors,
        IReadOnlyList<string> Warnings);
}