using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            int itemSize = 1,
            CancellationToken cancellationToken = default)
        {
            if (block == null)
                throw new ArgumentNullException(nameof(block));
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            RejectUnsupported(block);

            ReadOnlyMemory<byte> stored = block switch
            {
                InlineDataBlock inline => inline.Data,
                EmbeddedDataBlock embedded => embedded.Data,
                AttachedDataBlock attached => await ReadAttachedBlockAsync(attached, source, cancellationToken),
                ExternalDataBlock external => await ReadExternalBlockAsync(external, cancellationToken),
                _ => throw new NotSupportedException($"Unsupported data block type: {block.GetType().Name}")
            };

            return Materialize(stored, block, options, itemSize);
        }

        public ReadOnlyMemory<byte> ReadDataBlock(
            XisfDataBlock block,
            Stream? source,
            XisfReaderOptions options,
            int itemSize = 1)
        {
            if (block == null)
                throw new ArgumentNullException(nameof(block));
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            RejectUnsupported(block);

            ReadOnlyMemory<byte> stored = block switch
            {
                InlineDataBlock inline => inline.Data,
                EmbeddedDataBlock embedded => embedded.Data,
                AttachedDataBlock attached => ReadAttachedBlock(attached, source),
                ExternalDataBlock => throw new NotSupportedException(
                    "External data blocks can only be read through the asynchronous read path"),
                _ => throw new NotSupportedException($"Unsupported data block type: {block.GetType().Name}")
            };

            return Materialize(stored, block, options, itemSize);
        }

        /// <summary>
        /// Applies, in §10.5/§10.6/§10.4 order, checksum verification (never after decompression),
        /// decompression, removal of byte shuffling and byte-order conversion of the logical data.
        /// </summary>
        private ReadOnlyMemory<byte> Materialize(
            ReadOnlyMemory<byte> stored,
            XisfDataBlock block,
            XisfReaderOptions options,
            int itemSize)
        {
            if (options.ValidateChecksums && block.Checksum != null)
            {
                if (!_checksumProvider.SupportedAlgorithms.Contains(block.Checksum.Algorithm))
                {
                    throw new XisfUnsupportedObjectException(
                        $"Data block declares the {block.Checksum.Algorithm} hashing algorithm, " +
                        "which this build cannot compute");
                }

                if (!_checksumProvider.Validate(stored, block.Checksum))
                {
                    throw new InvalidDataException("Data block checksum validation failed");
                }
            }

            var data = block.Compression != null
                ? DecompressBlock(stored, block.Compression, options.TryDecompressEqualLengthSubblocks)
                : stored;

            if (block.ByteOrder != XisfByteOrder.LittleEndian)
            {
                data = ConvertByteOrder(data, block.ByteOrder, XisfByteOrder.LittleEndian, itemSize);
            }

            return data;
        }

        private static void RejectUnsupported(XisfDataBlock block)
        {
            if (block.UnsupportedDeclaration != null)
                throw new XisfUnsupportedObjectException(block.UnsupportedDeclaration);

            if (block.Compression is { } compression && compression.UncompressedSize > (ulong)Array.MaxLength)
            {
                throw new NotSupportedException(
                    $"Data block declares {compression.UncompressedSize} uncompressed bytes; " +
                    $"blocks larger than {Array.MaxLength} bytes cannot be materialized");
            }

            if (block is AttachedDataBlock attached && attached.Size > (ulong)Array.MaxLength)
            {
                throw new NotSupportedException(
                    $"Attached data block declares {attached.Size} bytes; " +
                    $"blocks larger than {Array.MaxLength} bytes cannot be materialized");
            }
        }

        /// <summary>
        /// §10.6.1: a block without a subblocks attribute is one implicit subblock. A subblock with
        /// equal lengths is stored uncompressed (optionally probed first for legacy compressed data);
        /// every other subblock must decompress to exactly its declared length. Subblocks land in the
        /// still-shuffled stream, which is unshuffled once after concatenation.
        /// </summary>
        private ReadOnlyMemory<byte> DecompressBlock(
            ReadOnlyMemory<byte> stored,
            XisfCompression compression,
            bool tryDecompressEqualLength)
        {
            var subblocks = compression.Subblocks is { Count: > 0 } declared
                ? declared
                : new[] { new XisfCompressionSubblock((ulong)stored.Length, compression.UncompressedSize) };

            ReconcileSubblocks(subblocks, stored.Length, compression.UncompressedSize);

            var output = new byte[compression.UncompressedSize];
            var read = 0;
            var written = 0;

            for (var index = 0; index < subblocks.Count; index++)
            {
                var source = stored.Slice(read, (int)subblocks[index].CompressedLength);
                var destination = output.AsSpan(written, (int)subblocks[index].UncompressedLength);

                if (source.Length == destination.Length)
                {
                    if (!tryDecompressEqualLength ||
                        !_compressionProvider.TryDecompress(source, compression.Codec, destination))
                    {
                        source.Span.CopyTo(destination);
                    }
                }
                else if (!_compressionProvider.TryDecompress(source, compression.Codec, destination))
                {
                    throw new InvalidDataException(
                        $"Compression subblock {index} ({source.Length} bytes) does not decompress " +
                        $"to exactly its declared {destination.Length} bytes");
                }

                read += source.Length;
                written += destination.Length;
            }

            ReadOnlyMemory<byte> data = output;

            if (IsShuffledCodec(compression.Codec) && compression.ItemSize.HasValue)
            {
                data = _compressionProvider.RemoveByteShuffle(data, compression.ItemSize.Value);
            }

            return data;
        }

        private static void ReconcileSubblocks(
            IReadOnlyList<XisfCompressionSubblock> subblocks,
            int storedLength,
            ulong uncompressedSize)
        {
            var storedRemaining = (ulong)storedLength;
            var uncompressedRemaining = uncompressedSize;

            foreach (var subblock in subblocks)
            {
                if (subblock.CompressedLength > storedRemaining)
                    throw new InvalidDataException(
                        $"Compression subblocks declare more than the {storedLength} stored bytes of the data block");
                if (subblock.UncompressedLength > uncompressedRemaining)
                    throw new InvalidDataException(
                        $"Compression subblocks declare more than the {uncompressedSize} uncompressed bytes of the data block");

                storedRemaining -= subblock.CompressedLength;
                uncompressedRemaining -= subblock.UncompressedLength;
            }

            if (storedRemaining != 0)
                throw new InvalidDataException(
                    $"Compression subblocks leave {storedRemaining} of the {storedLength} stored bytes of the data block undeclared");
            if (uncompressedRemaining != 0)
                throw new InvalidDataException(
                    $"Compression subblocks leave {uncompressedRemaining} of the {uncompressedSize} uncompressed bytes of the data block undeclared");
        }

        public (ReadOnlyMemory<byte> Stored, XisfCompression? Compression) CompressBlock(
            ReadOnlyMemory<byte> data,
            XisfCompression? compression)
        {
            if (compression == null)
                return (data, null);

            var payload = data;
            if (IsShuffledCodec(compression.Codec) && compression.ItemSize.HasValue)
            {
                payload = _compressionProvider.ApplyByteShuffle(payload, compression.ItemSize.Value);
            }

            var final = compression with { UncompressedSize = (ulong)data.Length, Subblocks = null };

            if (compression.Subblocks is { Count: > 1 } &&
                compression.Subblocks.Aggregate(0UL, (sum, s) => sum + s.UncompressedLength) == (ulong)payload.Length)
            {
                var pieces = new List<ReadOnlyMemory<byte>>();
                var subblocks = new List<XisfCompressionSubblock>();
                var offset = 0;
                var anyReduced = false;

                foreach (var declared in compression.Subblocks)
                {
                    var raw = payload.Slice(offset, (int)declared.UncompressedLength);
                    var piece = _compressionProvider.Compress(raw, compression.Codec);

                    // §10.6.1: a subblock that compression does not shorten is stored uncompressed,
                    // and its bytes come from the shuffled payload, not the original data.
                    if (piece.Length < raw.Length)
                        anyReduced = true;
                    else
                        piece = raw;

                    pieces.Add(piece);
                    subblocks.Add(new XisfCompressionSubblock((ulong)piece.Length, declared.UncompressedLength));
                    offset += (int)declared.UncompressedLength;
                }

                if (!anyReduced)
                    return (data, null);

                var stored = new byte[pieces.Sum(p => p.Length)];
                var written = 0;
                foreach (var piece in pieces)
                {
                    piece.Span.CopyTo(stored.AsSpan(written));
                    written += piece.Length;
                }

                return (stored, final with { Subblocks = subblocks });
            }

            // §10.6.1: when nothing shrinks, the block is written without compression, so the
            // original (unshuffled) data is returned rather than the shuffled payload.
            var whole = _compressionProvider.Compress(payload, compression.Codec);
            return whole.Length < payload.Length ? (whole, final) : (data, null);
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

        private static ReadOnlyMemory<byte> ReadAttachedBlock(AttachedDataBlock block, Stream? source)
        {
            if (source == null)
                throw new InvalidOperationException("An attached data block requires the containing stream");

            source.Position = (long)block.Position;
            var buffer = new byte[block.Size];
            source.ReadExactly(buffer);
            return buffer;
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

        private static bool IsShuffledCodec(XisfCompressionCodec codec)
        {
            return codec == XisfCompressionCodec.ZlibSh ||
                   codec == XisfCompressionCodec.LZ4Sh ||
                   codec == XisfCompressionCodec.LZ4HCSh ||
                   codec == XisfCompressionCodec.ZstdSh;
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