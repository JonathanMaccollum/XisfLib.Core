using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

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
            // Note: Byte shuffling is handled by DataBlockProcessor, not here
            var decompressed = compression.Codec switch
            {
                XisfCompressionCodec.Zlib or XisfCompressionCodec.ZlibSh => DecompressZlib(compressed, compression.UncompressedSize),
                XisfCompressionCodec.LZ4 or XisfCompressionCodec.LZ4Sh => DecompressLZ4(compressed, compression.UncompressedSize),
                XisfCompressionCodec.LZ4HC or XisfCompressionCodec.LZ4HCSh => DecompressLZ4(compressed, compression.UncompressedSize),
                _ => throw new NotSupportedException($"Unsupported codec: {compression.Codec}")
            };

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
