using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace XisfLib.Core.Implementations
{
    /// <summary>
    /// Factory for creating infrastructure components with proper dependencies.
    /// </summary>
    internal sealed class XisfComponentFactory : IXisfComponentFactory
    {
        private readonly HttpClient? _httpClient;
        private readonly bool _reuseComponents;
        
        // Cached component instances for reuse
        private IXisfXmlSerializer? _xmlSerializer;
        private ICompressionProvider? _compressionProvider;
        private IChecksumProvider? _checksumProvider;
        private IXisfValidator? _validator;

        public XisfComponentFactory(HttpClient? httpClient = null, bool reuseComponents = true)
        {
            _httpClient = httpClient;
            _reuseComponents = reuseComponents;
        }

        /// <summary>
        /// Creates an XML serializer instance.
        /// </summary>
        public IXisfXmlSerializer CreateXmlSerializer()
        {
            if (_reuseComponents && _xmlSerializer != null)
                return _xmlSerializer;

            var serializer = new XisfXmlSerializer(CreateValidator());
            
            if (_reuseComponents)
                _xmlSerializer = serializer;
            
            return serializer;
        }

        /// <summary>
        /// Creates a data block processor with the necessary providers.
        /// </summary>
        public IDataBlockProcessor CreateDataBlockProcessor(
            ICompressionProvider compressionProvider,
            IChecksumProvider checksumProvider)
        {
            var streamResolver = CreateStreamResolver();
            return new DataBlockProcessor(compressionProvider, checksumProvider, streamResolver);
        }

        /// <summary>
        /// Creates a compression provider with support for standard codecs.
        /// </summary>
        public ICompressionProvider CreateCompressionProvider()
        {
            if (_reuseComponents && _compressionProvider != null)
                return _compressionProvider;

            var provider = new CompressionProvider();
            
            if (_reuseComponents)
                _compressionProvider = provider;
            
            return provider;
        }

        /// <summary>
        /// Creates a checksum provider with support for standard algorithms.
        /// </summary>
        public IChecksumProvider CreateChecksumProvider()
        {
            if (_reuseComponents && _checksumProvider != null)
                return _checksumProvider;

            var provider = new ChecksumProvider();
            
            if (_reuseComponents)
                _checksumProvider = provider;
            
            return provider;
        }

        /// <summary>
        /// Creates a stream resolver with the specified options.
        /// </summary>
        public IStreamResolver CreateStreamResolver(
            Func<string, Stream>? fileStreamProvider = null,
            Func<Uri, Stream>? uriStreamProvider = null)
        {
            return new StreamResolver(fileStreamProvider, uriStreamProvider, _httpClient);
        }

        /// <summary>
        /// Creates a validator instance.
        /// </summary>
        public IXisfValidator CreateValidator()
        {
            if (_reuseComponents && _validator != null)
                return _validator;

            var validator = new XisfValidator();
            
            if (_reuseComponents)
                _validator = validator;
            
            return validator;
        }
    }

    /// <summary>
    /// Provides validation services for XISF data structures.
    /// </summary>
    internal sealed class XisfValidator : IXisfValidator
    {
        // Property identifier regex per specification Section 8.4.1
        private static readonly Regex PropertyIdRegex = new(
            @"^[_a-zA-Z][_a-zA-Z0-9]*(:([_a-zA-Z][_a-zA-Z0-9])+)*$",
            RegexOptions.Compiled);

        // Unique identifier regex per specification Section 11
        private static readonly Regex UniqueIdRegex = new(
            @"^[_a-zA-Z][_a-zA-Z0-9]*$",
            RegexOptions.Compiled);

        /// <summary>
        /// Validates an XISF unit for specification compliance.
        /// </summary>
        public ValidationResult ValidateUnit(XisfUnit unit)
        {
            var errors = new List<string>();
            var warnings = new List<string>();

            if (unit == null)
            {
                errors.Add("XISF unit cannot be null");
                return new ValidationResult(false, errors, warnings);
            }

            // Validate header
            var headerResult = ValidateHeader(unit.Header);
            errors.AddRange(headerResult.Errors);
            warnings.AddRange(headerResult.Warnings);

            // Validate images
            foreach (var image in unit.Images)
            {
                var imageResult = ValidateImage(image);
                errors.AddRange(imageResult.Errors);
                warnings.AddRange(imageResult.Warnings);
            }

            // Validate global properties
            foreach (var property in unit.GlobalProperties)
            {
                if (!IsValidPropertyId(property.Id))
                {
                    errors.Add($"Invalid property identifier: {property.Id}");
                }
            }

            // Check for duplicate UIDs
            var uids = new HashSet<string>();
            foreach (var element in unit.Header.CoreElements.Values)
            {
                if (!string.IsNullOrEmpty(element.Uid))
                {
                    if (!uids.Add(element.Uid))
                    {
                        errors.Add($"Duplicate UID found: {element.Uid}");
                    }
                }
            }

            return new ValidationResult(errors.Count == 0, errors, warnings);
        }

        /// <summary>
        /// Validates an XISF header structure.
        /// </summary>
        public ValidationResult ValidateHeader(XisfHeader header)
        {
            var errors = new List<string>();
            var warnings = new List<string>();

            if (header == null)
            {
                errors.Add("XISF header cannot be null");
                return new ValidationResult(false, errors, warnings);
            }

            // Validate metadata (mandatory properties per Section 11.4.1)
            if (header.Metadata == null)
            {
                errors.Add("XISF metadata is required");
            }
            else
            {
                if (header.Metadata.CreationTime == default)
                {
                    errors.Add("XISF:CreationTime is required");
                }

                if (string.IsNullOrWhiteSpace(header.Metadata.CreatorApplication))
                {
                    errors.Add("XISF:CreatorApplication is required");
                }
            }

            // Validate core element UIDs
            foreach (var kvp in header.CoreElements)
            {
                if (!IsValidUniqueId(kvp.Key))
                {
                    errors.Add($"Invalid core element UID: {kvp.Key}");
                }
            }

            return new ValidationResult(errors.Count == 0, errors, warnings);
        }

        /// <summary>
        /// Validates an XISF image definition.
        /// </summary>
        public ValidationResult ValidateImage(XisfImage image)
        {
            var errors = new List<string>();
            var warnings = new List<string>();

            if (image == null)
            {
                errors.Add("XISF image cannot be null");
                return new ValidationResult(false, errors, warnings);
            }

            // Validate geometry
            if (image.Geometry == null)
            {
                errors.Add("Image geometry is required");
            }
            else
            {
                if (image.Geometry.Dimensions == null || image.Geometry.Dimensions.Count == 0)
                {
                    errors.Add("Image must have at least one dimension");
                }
                else
                {
                    foreach (var dim in image.Geometry.Dimensions)
                    {
                        if (dim == 0)
                        {
                            errors.Add("Image dimensions must be greater than zero");
                            break;
                        }
                    }
                }

                if (image.Geometry.ChannelCount == 0)
                {
                    errors.Add("Image must have at least one channel");
                }
            }

            // Validate bounds for floating point images (Section 11.5.1)
            if (image.SampleFormat == XisfSampleFormat.Float32 || image.SampleFormat == XisfSampleFormat.Float64)
            {
                if (image.Bounds == null)
                {
                    errors.Add("Bounds attribute is required for floating point images");
                }
                else if (image.Bounds.Lower >= image.Bounds.Upper)
                {
                    errors.Add("Image bounds lower value must be less than upper value");
                }
            }

            // Validate pixel data block
            if (image.PixelData == null)
            {
                errors.Add("Image pixel data block is required");
            }

            // Validate image ID if present
            if (!string.IsNullOrEmpty(image.ImageId) && !IsValidPropertyId(image.ImageId))
            {
                errors.Add($"Invalid image ID: {image.ImageId}");
            }

            // Validate offset if present
            if (image.Offset.HasValue && image.Offset.Value < 0)
            {
                errors.Add("Image offset must be non-negative");
            }

            // Check color space and channel count consistency
            if (image.ColorSpace == XisfColorSpace.RGB && image.Geometry?.ChannelCount < 3)
            {
                warnings.Add("RGB color space typically requires at least 3 channels");
            }

            // Validate associated properties
            if (image.Properties != null)
            {
                foreach (var property in image.Properties)
                {
                    if (!IsValidPropertyId(property.Id))
                    {
                        errors.Add($"Invalid property identifier in image: {property.Id}");
                    }
                }
            }

            return new ValidationResult(errors.Count == 0, errors, warnings);
        }

        /// <summary>
        /// Validates a property identifier format.
        /// Specification Reference: Section 8.4.1 Property Identifier
        /// </summary>
        public bool IsValidPropertyId(string id)
        {
            if (string.IsNullOrEmpty(id))
                return false;

            return PropertyIdRegex.IsMatch(id);
        }

        /// <summary>
        /// Validates a unique element identifier format.
        /// Specification Reference: Section 11 XISF Core Elements
        /// </summary>
        public bool IsValidUniqueId(string uid)
        {
            if (string.IsNullOrEmpty(uid))
                return false;

            return UniqueIdRegex.IsMatch(uid);
        }
    }

    /// <summary>
    /// Compression provider implementation supporting Zlib and LZ4 codecs.
    /// Specification Reference: Section 10.6 XISF Data Block Compression
    /// </summary>
    internal sealed class CompressionProvider : ICompressionProvider
    {
        private static readonly HashSet<XisfCompressionCodec> SupportedCodecs = new()
        {
            XisfCompressionCodec.Zlib,
            XisfCompressionCodec.ZlibSh,
            XisfCompressionCodec.LZ4,
            XisfCompressionCodec.LZ4Sh,
            XisfCompressionCodec.LZ4HC,
            XisfCompressionCodec.LZ4HCSh,
        };

        public bool SupportsCodec(XisfCompressionCodec codec)
        {
            return SupportedCodecs.Contains(codec);
        }

        public async Task<ReadOnlyMemory<byte>> CompressAsync(
            ReadOnlyMemory<byte> data,
            XisfCompression compression,
            CancellationToken cancellationToken = default)
        {
            if (!SupportsCodec(compression.Codec))
            {
                throw new NotSupportedException($"Compression codec {compression.Codec} is not supported");
            }

            await Task.Yield(); // Cooperative async

            var dataToCompress = data;

            // Apply byte shuffling if needed
            if (IsShuffledCodec(compression.Codec) && compression.ItemSize.HasValue)
            {
                dataToCompress = ApplyByteShuffle(dataToCompress, compression.ItemSize.Value);
            }

            // Compress based on codec
            return compression.Codec switch
            {
                XisfCompressionCodec.Zlib or XisfCompressionCodec.ZlibSh => CompressZlib(dataToCompress),
                XisfCompressionCodec.LZ4 or XisfCompressionCodec.LZ4Sh => CompressLZ4(dataToCompress),
                XisfCompressionCodec.LZ4HC or XisfCompressionCodec.LZ4HCSh => CompressLZ4HC(dataToCompress),
                _ => throw new NotSupportedException($"Unsupported codec: {compression.Codec}")
            };
        }

        public async Task<ReadOnlyMemory<byte>> DecompressAsync(
            ReadOnlyMemory<byte> compressed,
            XisfCompression compression,
            CancellationToken cancellationToken = default)
        {
            if (!SupportsCodec(compression.Codec))
            {
                throw new NotSupportedException($"Compression codec {compression.Codec} is not supported");
            }

            await Task.Yield(); // Cooperative async

            // Decompress based on codec
            var decompressed = compression.Codec switch
            {
                XisfCompressionCodec.Zlib or XisfCompressionCodec.ZlibSh => DecompressZlib(compressed, compression.UncompressedSize),
                XisfCompressionCodec.LZ4 or XisfCompressionCodec.LZ4Sh => DecompressLZ4(compressed, compression.UncompressedSize),
                XisfCompressionCodec.LZ4HC or XisfCompressionCodec.LZ4HCSh => DecompressLZ4(compressed, compression.UncompressedSize),
                _ => throw new NotSupportedException($"Unsupported codec: {compression.Codec}")
            };

            // Remove byte shuffling if needed
            if (IsShuffledCodec(compression.Codec) && compression.ItemSize.HasValue)
            {
                decompressed = RemoveByteShuffle(decompressed, compression.ItemSize.Value);
            }

            return decompressed;
        }

        private static bool IsShuffledCodec(XisfCompressionCodec codec)
        {
            return codec == XisfCompressionCodec.ZlibSh ||
                   codec == XisfCompressionCodec.LZ4Sh ||
                   codec == XisfCompressionCodec.LZ4HCSh;
        }

        private ReadOnlyMemory<byte> CompressZlib(ReadOnlyMemory<byte> data)
        {
            using var inputStream = new MemoryStream(data.ToArray());
            using var outputStream = new MemoryStream();
            using (var zlibStream = new System.IO.Compression.ZLibStream(outputStream, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            {
                inputStream.CopyTo(zlibStream);
            }
            return outputStream.ToArray();
        }

        private ReadOnlyMemory<byte> DecompressZlib(ReadOnlyMemory<byte> compressed, ulong uncompressedSize)
        {
            using var inputStream = new MemoryStream(compressed.ToArray());
            using var zlibStream = new System.IO.Compression.ZLibStream(inputStream, System.IO.Compression.CompressionMode.Decompress);

            var output = new byte[uncompressedSize];
            int totalRead = 0;
            int bytesRead;

            while (totalRead < (int)uncompressedSize &&
                   (bytesRead = zlibStream.Read(output, totalRead, (int)uncompressedSize - totalRead)) > 0)
            {
                totalRead += bytesRead;
            }

            if (totalRead != (int)uncompressedSize)
            {
                throw new InvalidDataException($"Decompressed size mismatch: expected {uncompressedSize}, got {totalRead}");
            }

            return output;
        }

        private ReadOnlyMemory<byte> CompressLZ4(ReadOnlyMemory<byte> data)
        {
            var source = data.ToArray();
            var target = new byte[K4os.Compression.LZ4.LZ4Codec.MaximumOutputSize(source.Length)];
            var encodedLength = K4os.Compression.LZ4.LZ4Codec.Encode(
                source, 0, source.Length,
                target, 0, target.Length,
                K4os.Compression.LZ4.LZ4Level.L00_FAST);

            return target.AsMemory(0, encodedLength);
        }

        private ReadOnlyMemory<byte> CompressLZ4HC(ReadOnlyMemory<byte> data)
        {
            var source = data.ToArray();
            var target = new byte[K4os.Compression.LZ4.LZ4Codec.MaximumOutputSize(source.Length)];
            var encodedLength = K4os.Compression.LZ4.LZ4Codec.Encode(
                source, 0, source.Length,
                target, 0, target.Length,
                K4os.Compression.LZ4.LZ4Level.L09_HC);

            return target.AsMemory(0, encodedLength);
        }

        private ReadOnlyMemory<byte> DecompressLZ4(ReadOnlyMemory<byte> compressed, ulong uncompressedSize)
        {
            var source = compressed.ToArray();
            var target = new byte[uncompressedSize];

            var decodedLength = K4os.Compression.LZ4.LZ4Codec.Decode(
                source, 0, source.Length,
                target, 0, target.Length);

            if (decodedLength != (int)uncompressedSize)
            {
                throw new InvalidDataException($"Decompressed size mismatch: expected {uncompressedSize}, got {decodedLength}");
            }

            return target;
        }

        public ReadOnlyMemory<byte> ApplyByteShuffle(ReadOnlyMemory<byte> data, uint itemSize)
        {
            if (itemSize <= 1)
                return data;

            var input = data.ToArray();
            var output = new byte[input.Length];
            var numberOfItems = (uint)(input.Length / itemSize);

            // Apply byte shuffling per specification Section 10.6.2
            int outputIndex = 0;
            for (uint j = 0; j < itemSize; j++)
            {
                for (uint i = 0; i < numberOfItems; i++)
                {
                    output[outputIndex++] = input[i * itemSize + j];
                }
            }

            // Copy any remaining bytes
            var remainder = input.Length % itemSize;
            if (remainder > 0)
            {
                Array.Copy(input, numberOfItems * itemSize, output, outputIndex, remainder);
            }

            return output;
        }

        public ReadOnlyMemory<byte> RemoveByteShuffle(ReadOnlyMemory<byte> shuffled, uint itemSize)
        {
            if (itemSize <= 1)
                return shuffled;

            var input = shuffled.ToArray();
            var output = new byte[input.Length];
            var numberOfItems = (uint)(input.Length / itemSize);

            // Reverse byte shuffling
            int inputIndex = 0;
            for (uint j = 0; j < itemSize; j++)
            {
                for (uint i = 0; i < numberOfItems; i++)
                {
                    output[i * itemSize + j] = input[inputIndex++];
                }
            }

            // Copy any remaining bytes
            var remainder = input.Length % itemSize;
            if (remainder > 0)
            {
                Array.Copy(input, inputIndex, output, numberOfItems * itemSize, remainder);
            }

            return output;
        }

        public ulong GetMaxBlockSize(XisfCompressionCodec codec)
        {
            // Most compression algorithms have a maximum block size
            // Zlib typically supports up to 2^32 - 1 bytes
            return codec switch
            {
                XisfCompressionCodec.Zlib or XisfCompressionCodec.ZlibSh => uint.MaxValue,
                XisfCompressionCodec.LZ4 or XisfCompressionCodec.LZ4Sh => uint.MaxValue,
                XisfCompressionCodec.LZ4HC or XisfCompressionCodec.LZ4HCSh => uint.MaxValue,
                _ => uint.MaxValue
            };
        }

        public IEnumerable<ReadOnlyMemory<byte>> SplitIntoSubblocks(
            ReadOnlyMemory<byte> data,
            XisfCompressionCodec codec)
        {
            var maxBlockSize = GetMaxBlockSize(codec);
            
            if ((ulong)data.Length <= maxBlockSize)
            {
                // No splitting needed
                yield return data;
                yield break;
            }

            // Split into chunks
            int offset = 0;
            while (offset < data.Length)
            {
                int chunkSize = (int)Math.Min(maxBlockSize, (ulong)(data.Length - offset));
                yield return data.Slice(offset, chunkSize);
                offset += chunkSize;
            }
        }
    }
}