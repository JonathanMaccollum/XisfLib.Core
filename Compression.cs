using System;
using System.Collections.Generic;
using System.IO;
using ZstdSharp;

namespace XisfLib.Core.Implementations
{
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
            XisfCompressionCodec.Zstd,
            XisfCompressionCodec.ZstdSh,
        };

        public bool SupportsCodec(XisfCompressionCodec codec)
        {
            return SupportedCodecs.Contains(codec);
        }

        public ReadOnlyMemory<byte> Compress(ReadOnlyMemory<byte> data, XisfCompressionCodec codec)
        {
            if (!SupportsCodec(codec))
            {
                throw new NotSupportedException($"Compression codec {codec} is not supported");
            }

            return codec switch
            {
                XisfCompressionCodec.Zlib or XisfCompressionCodec.ZlibSh => CompressZlib(data),
                XisfCompressionCodec.LZ4 or XisfCompressionCodec.LZ4Sh => CompressLZ4(data),
                XisfCompressionCodec.LZ4HC or XisfCompressionCodec.LZ4HCSh => CompressLZ4HC(data),
                XisfCompressionCodec.Zstd or XisfCompressionCodec.ZstdSh => CompressZStd(data),
                _ => throw new NotSupportedException($"Unsupported codec: {codec}")
            };
        }

        public bool TryDecompress(
            ReadOnlyMemory<byte> compressed,
            XisfCompressionCodec codec,
            Span<byte> destination)
        {
            if (!SupportsCodec(codec))
            {
                throw new NotSupportedException($"Compression codec {codec} is not supported");
            }

            return codec switch
            {
                XisfCompressionCodec.Zlib or XisfCompressionCodec.ZlibSh => TryDecompressZlib(compressed, destination),
                XisfCompressionCodec.LZ4 or XisfCompressionCodec.LZ4Sh or
                XisfCompressionCodec.LZ4HC or XisfCompressionCodec.LZ4HCSh =>
                    K4os.Compression.LZ4.LZ4Codec.Decode(compressed.Span, destination) == destination.Length,
                XisfCompressionCodec.Zstd or XisfCompressionCodec.ZstdSh => TryDecompressZStd(compressed, destination),
                _ => throw new NotSupportedException($"Unsupported codec: {codec}")
            };
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

        /// <summary>
        /// The trailing one-byte read rejects output longer than the destination, and an Adler-32
        /// trailer that is present is verified. A stream ending without its trailer, or followed by
        /// trailing bytes, is still accepted: ZLibStream reports neither.
        /// </summary>
        private static bool TryDecompressZlib(ReadOnlyMemory<byte> compressed, Span<byte> destination)
        {
            var inputStream = System.Runtime.InteropServices.MemoryMarshal.TryGetArray(compressed, out var segment)
                ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
                : new MemoryStream(compressed.ToArray(), writable: false);

            try
            {
                using (inputStream)
                using (var zlibStream = new System.IO.Compression.ZLibStream(inputStream, System.IO.Compression.CompressionMode.Decompress))
                {
                    var totalRead = 0;
                    int bytesRead;
                    while (totalRead < destination.Length &&
                           (bytesRead = zlibStream.Read(destination[totalRead..])) > 0)
                    {
                        totalRead += bytesRead;
                    }

                    Span<byte> excess = stackalloc byte[1];
                    return totalRead == destination.Length && zlibStream.Read(excess) == 0;
                }
            }
            // Inflate errors other than Z_DATA_ERROR (Z_NEED_DICT among them) surface as the internal
            // ZLibException, an IOException. The source is in memory, so no other IOException arises.
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                return false;
            }
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

        private ReadOnlyMemory<byte> CompressZStd(ReadOnlyMemory<byte> data)
        {
            var source = data.ToArray();
            using var compressor = new Compressor();
            var compressed = compressor.Wrap(source);
            return compressed.ToArray();
        }

        private static bool TryDecompressZStd(ReadOnlyMemory<byte> compressed, Span<byte> destination)
        {
            try
            {
                using var decompressor = new Decompressor();
                return decompressor.Unwrap(compressed.Span, destination) == destination.Length;
            }
            catch (ZstdException)
            {
                return false;
            }
        }

        public ReadOnlyMemory<byte> ApplyByteShuffle(ReadOnlyMemory<byte> data, uint itemSize)
        {
            if (itemSize <= 1)
                return data;

            var input = data.ToArray();
            var output = new byte[input.Length];
            var numberOfItems = (uint)(input.Length / itemSize);

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
                XisfCompressionCodec.Zstd or XisfCompressionCodec.ZstdSh => uint.MaxValue,
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
