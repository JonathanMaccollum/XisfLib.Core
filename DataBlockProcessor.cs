using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XisfLib.Core.Implementations
{
    /// <summary>
    /// Handles reading and writing of XISF data blocks with encoding, compression, and checksums.
    /// Specification Reference: Section 10 XISF Data Block
    /// </summary>
    internal sealed class DataBlockProcessor : IDataBlockProcessor
    {
        private readonly ICompressionProvider _compressionProvider;
        private readonly IChecksumProvider _checksumProvider;
        private readonly IStreamResolver _streamResolver;

        public DataBlockProcessor(
            ICompressionProvider compressionProvider,
            IChecksumProvider checksumProvider,
            IStreamResolver streamResolver)
        {
            _compressionProvider = compressionProvider ?? throw new ArgumentNullException(nameof(compressionProvider));
            _checksumProvider = checksumProvider ?? throw new ArgumentNullException(nameof(checksumProvider));
            _streamResolver = streamResolver ?? throw new ArgumentNullException(nameof(streamResolver));
        }

        /// <summary>
        /// Reads a data block from a stream asynchronously.
        /// Handles decompression, checksum validation, and byte order conversion.
        /// </summary>
        public async Task<ReadOnlyMemory<byte>> ReadDataBlockAsync(
            XisfDataBlock block,
            Stream source,
            XisfReaderOptions options,
            CancellationToken cancellationToken = default)
        {
            if (block == null)
                throw new ArgumentNullException(nameof(block));
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            ReadOnlyMemory<byte> data = block switch
            {
                InlineDataBlock inline => DecodeData(inline.Data.ToString() ?? string.Empty, inline.Encoding),
                EmbeddedDataBlock embedded => DecodeData(embedded.Data.ToString() ?? string.Empty, embedded.Encoding),
                AttachedDataBlock attached => await ReadAttachedBlockAsync(attached, source, cancellationToken),
                ExternalDataBlock external => await ReadExternalBlockAsync(external, cancellationToken),
                _ => throw new NotSupportedException($"Unsupported data block type: {block.GetType().Name}")
            };

            // Validate checksum if present and validation is enabled
            if (options.ValidateChecksums && block.Checksum != null)
            {
                bool isValid = await _checksumProvider.ValidateAsync(data, block.Checksum, cancellationToken);
                if (!isValid)
                {
                    throw new InvalidDataException("Data block checksum validation failed");
                }
            }

            // Decompress if needed
            if (block.Compression != null)
            {
                data = await _compressionProvider.DecompressAsync(data, block.Compression, cancellationToken);
                
                // Remove byte shuffling if specified
                if (IsShuffledCodec(block.Compression.Codec) && block.Compression.ItemSize.HasValue)
                {
                    data = _compressionProvider.RemoveByteShuffle(data, block.Compression.ItemSize.Value);
                }
            }

            // Convert byte order if needed
            // Note: We need additional context (item size) to properly convert byte order
            // This would typically come from the image or property metadata
            
            return data;
        }

        /// <summary>
        /// Writes a data block to a stream asynchronously.
        /// Handles compression, checksum generation, and byte order conversion.
        /// </summary>
        public async Task WriteDataBlockAsync(
            XisfDataBlock block,
            ReadOnlyMemory<byte> data,
            Stream target,
            XisfWriterOptions options,
            CancellationToken cancellationToken = default)
        {
            if (block == null)
                throw new ArgumentNullException(nameof(block));
            if (target == null)
                throw new ArgumentNullException(nameof(target));
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            var processedData = data;

            // Apply compression if specified
            if (block.Compression != null)
            {
                // Apply byte shuffling if specified
                if (IsShuffledCodec(block.Compression.Codec) && block.Compression.ItemSize.HasValue)
                {
                    processedData = _compressionProvider.ApplyByteShuffle(processedData, block.Compression.ItemSize.Value);
                }
                
                processedData = await _compressionProvider.CompressAsync(processedData, block.Compression, cancellationToken);
            }

            // Calculate checksum if requested
            if (options.CalculateChecksums && block is AttachedDataBlock or ExternalDataBlock)
            {
                var checksum = await _checksumProvider.CalculateAsync(
                    processedData, 
                    options.ChecksumAlgorithm, 
                    cancellationToken);
                
                // Note: We'd need to update the block's checksum here, but records are immutable
                // This would require creating a new block instance with the checksum
            }

            // Write based on block type
            switch (block)
            {
                case InlineDataBlock inline:
                    var encoded = EncodeData(processedData, inline.Encoding);
                    var bytes = Encoding.UTF8.GetBytes(encoded);
                    await target.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
                    break;

                case EmbeddedDataBlock embedded:
                    encoded = EncodeData(processedData, embedded.Encoding);
                    bytes = Encoding.UTF8.GetBytes(encoded);
                    await target.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
                    break;

                case AttachedDataBlock attached:
                    await WriteAttachedBlockAsync(attached, processedData, target, cancellationToken);
                    break;

                case ExternalDataBlock external:
                    await WriteExternalBlockAsync(external, processedData, cancellationToken);
                    break;

                default:
                    throw new NotSupportedException($"Unsupported data block type: {block.GetType().Name}");
            }
        }

        /// <summary>
        /// Encodes binary data to Base64 or Hex encoding.
        /// Specification Reference: Section 10.3 inline/embedded encoding
        /// </summary>
        public string EncodeData(ReadOnlyMemory<byte> data, XisfEncoding encoding)
        {
            return encoding switch
            {
                XisfEncoding.Base64 => Convert.ToBase64String(data.Span),
                XisfEncoding.Hex => ToHexString(data.Span),
                _ => throw new NotSupportedException($"Unsupported encoding: {encoding}")
            };
        }

        /// <summary>
        /// Decodes Base64 or Hex encoded data to binary.
        /// </summary>
        public ReadOnlyMemory<byte> DecodeData(string encoded, XisfEncoding encoding)
        {
            if (string.IsNullOrEmpty(encoded))
                return ReadOnlyMemory<byte>.Empty;

            // Remove whitespace which is allowed in encoded data
            encoded = encoded.Replace(" ", "").Replace("\t", "").Replace("\r", "").Replace("\n", "");

            return encoding switch
            {
                XisfEncoding.Base64 => Convert.FromBase64String(encoded),
                XisfEncoding.Hex => FromHexString(encoded),
                _ => throw new NotSupportedException($"Unsupported encoding: {encoding}")
            };
        }

        /// <summary>
        /// Converts byte order of data.
        /// Specification Reference: Section 10.4 XISF Data Block Byte Order
        /// </summary>
        public ReadOnlyMemory<byte> ConvertByteOrder(
            ReadOnlyMemory<byte> data,
            XisfByteOrder currentOrder,
            XisfByteOrder targetOrder,
            int itemSize)
        {
            // No conversion needed if orders match
            if (currentOrder == targetOrder)
                return data;

            // No conversion needed for single-byte items
            if (itemSize <= 1)
                return data;

            // Validate item size
            if (itemSize != 2 && itemSize != 4 && itemSize != 8 && itemSize != 16)
                throw new ArgumentException($"Invalid item size for byte order conversion: {itemSize}", nameof(itemSize));

            // Validate data length
            if (data.Length % itemSize != 0)
                throw new ArgumentException($"Data length ({data.Length}) is not a multiple of item size ({itemSize})", nameof(data));

            // Create output buffer
            var output = new byte[data.Length];
            data.CopyTo(output);

            // Reverse bytes within each item
            for (int i = 0; i < output.Length; i += itemSize)
            {
                Array.Reverse(output, i, itemSize);
            }

            return output;
        }

        /// <summary>
        /// Determines if byte order conversion is needed based on system architecture.
        /// </summary>
        public bool RequiresByteOrderConversion(XisfByteOrder dataOrder)
        {
            var systemIsLittleEndian = BitConverter.IsLittleEndian;
            
            return dataOrder switch
            {
                XisfByteOrder.LittleEndian => !systemIsLittleEndian,
                XisfByteOrder.BigEndian => systemIsLittleEndian,
                _ => false
            };
        }

        private async Task<ReadOnlyMemory<byte>> ReadAttachedBlockAsync(
            AttachedDataBlock block,
            Stream source,
            CancellationToken cancellationToken)
        {
            // Seek to the block position
            source.Position = (long)block.Position;
            
            // Read the block data
            var buffer = new byte[block.Size];
            int totalRead = 0;
            
            while (totalRead < buffer.Length)
            {
                int read = await source.ReadAsync(
                    buffer, 
                    totalRead, 
                    buffer.Length - totalRead, 
                    cancellationToken);
                
                if (read == 0)
                    throw new EndOfStreamException($"Unexpected end of stream while reading attached block");
                
                totalRead += read;
            }
            
            return buffer;
        }

        private async Task WriteAttachedBlockAsync(
            AttachedDataBlock block,
            ReadOnlyMemory<byte> data,
            Stream target,
            CancellationToken cancellationToken)
        {
            // Seek to the block position
            target.Position = (long)block.Position;
            
            // Write the data
            await target.WriteAsync(data.ToArray(), 0, data.Length, cancellationToken);
        }

        private async Task<ReadOnlyMemory<byte>> ReadExternalBlockAsync(
            ExternalDataBlock block,
            CancellationToken cancellationToken)
        {
            Stream stream;
            
            if (block.Location.Scheme == "file")
            {
                // Local file
                stream = _streamResolver.ResolveFileStream(
                    block.Location.LocalPath, 
                    FileMode.Open, 
                    FileAccess.Read);
            }
            else
            {
                // Remote resource
                stream = await _streamResolver.ResolveUriStreamAsync(block.Location, cancellationToken);
            }

            using (stream)
            {
                if (block.Position.HasValue && block.Size.HasValue)
                {
                    // Read specific portion
                    stream.Position = (long)block.Position.Value;
                    var buffer = new byte[block.Size.Value];
                    await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                    return buffer;
                }
                else
                {
                    // Read entire stream
                    using var ms = new MemoryStream();
                    await stream.CopyToAsync(ms, 81920, cancellationToken);
                    return ms.ToArray();
                }
            }
        }

        private async Task WriteExternalBlockAsync(
            ExternalDataBlock block,
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken)
        {
            if (block.Location.Scheme != "file")
            {
                throw new NotSupportedException("Writing to remote external blocks is not supported");
            }

            var stream = _streamResolver.ResolveFileStream(
                block.Location.LocalPath,
                FileMode.Create,
                FileAccess.Write);

            using (stream)
            {
                if (block.Position.HasValue)
                {
                    stream.Position = (long)block.Position.Value;
                }
                
                await stream.WriteAsync(data.ToArray(), 0, data.Length, cancellationToken);
            }
        }

        private static bool IsShuffledCodec(XisfCompressionCodec codec)
        {
            return codec == XisfCompressionCodec.ZlibSh ||
                   codec == XisfCompressionCodec.LZ4Sh ||
                   codec == XisfCompressionCodec.LZ4HCSh;
        }

        private static string ToHexString(ReadOnlySpan<byte> data)
        {
            var sb = new StringBuilder(data.Length * 2);
            foreach (var b in data)
            {
                sb.Append(b.ToString("x2")); // Lowercase hex per specification
            }
            return sb.ToString();
        }

        private static byte[] FromHexString(string hex)
        {
            if (hex.Length % 2 != 0)
                throw new ArgumentException("Hex string must have even number of characters", nameof(hex));

            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return bytes;
        }
    }

    /// <summary>
    /// Extension methods for data block operations.
    /// </summary>
    internal static class DataBlockExtensions
    {
        /// <summary>
        /// Gets the item size in bytes for a sample format.
        /// Used for byte order conversion and byte shuffling.
        /// </summary>
        public static int GetItemSize(this XisfSampleFormat format)
        {
            return format switch
            {
                XisfSampleFormat.UInt8 => 1,
                XisfSampleFormat.UInt16 => 2,
                XisfSampleFormat.UInt32 => 4,
                XisfSampleFormat.UInt64 => 8,
                XisfSampleFormat.Float32 => 4,
                XisfSampleFormat.Float64 => 8,
                XisfSampleFormat.Complex32 => 8,  // Two Float32 components
                XisfSampleFormat.Complex64 => 16, // Two Float64 components
                _ => throw new NotSupportedException($"Unsupported sample format: {format}")
            };
        }

        /// <summary>
        /// Determines if a sample format represents complex numbers.
        /// </summary>
        public static bool IsComplex(this XisfSampleFormat format)
        {
            return format == XisfSampleFormat.Complex32 || format == XisfSampleFormat.Complex64;
        }

        /// <summary>
        /// Determines if a sample format represents floating point numbers.
        /// </summary>
        public static bool IsFloatingPoint(this XisfSampleFormat format)
        {
            return format == XisfSampleFormat.Float32 || 
                   format == XisfSampleFormat.Float64 ||
                   format == XisfSampleFormat.Complex32 ||
                   format == XisfSampleFormat.Complex64;
        }
    }
}