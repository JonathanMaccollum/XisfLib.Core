using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XisfLib.Core.Implementations
{
    /// <summary>
    /// Provides cryptographic checksum calculation and validation for XISF data blocks.
    /// Specification Reference: Section 10.5 XISF Data Block Checksum, Table 9
    /// </summary>
    internal sealed class ChecksumProvider : IChecksumProvider
    {
        private static readonly Dictionary<XisfHashAlgorithm, Func<HashAlgorithm>> AlgorithmFactories = new()
        {
            [XisfHashAlgorithm.SHA1] = () => SHA1.Create(),
            [XisfHashAlgorithm.SHA256] = () => SHA256.Create(),
            [XisfHashAlgorithm.SHA512] = () => SHA512.Create(),
            // SHA3 algorithms would require additional dependencies
            // [XisfHashAlgorithm.SHA3_256] = () => new SHA3_256(),
            // [XisfHashAlgorithm.SHA3_512] = () => new SHA3_512(),
        };

        /// <summary>
        /// Gets the list of supported hash algorithms.
        /// SHA-1 is mandatory per specification.
        /// </summary>
        public IReadOnlyList<XisfHashAlgorithm> SupportedAlgorithms { get; }

        public ChecksumProvider()
        {
            SupportedAlgorithms = AlgorithmFactories.Keys.ToList();
        }

        /// <summary>
        /// Calculates a checksum for data using the specified algorithm.
        /// </summary>
        public ReadOnlyMemory<byte> Calculate(ReadOnlyMemory<byte> data, XisfHashAlgorithm algorithm)
        {
            if (!AlgorithmFactories.TryGetValue(algorithm, out var factory))
            {
                throw new NotSupportedException($"Hash algorithm {algorithm} is not supported");
            }

            using var hasher = factory();
            
            // For small data, we can use the simple API
            if (data.Length <= 4096)
            {
                return hasher.ComputeHash(data.ToArray());
            }

            // For larger data, use streaming approach to avoid array allocation
            var buffer = ArrayPool<byte>.Shared.Rent(data.Length);
            try
            {
                data.CopyTo(buffer);
                return hasher.ComputeHash(buffer, 0, data.Length);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }

        /// <summary>
        /// Calculates a checksum asynchronously for large data.
        /// </summary>
        public async Task<ReadOnlyMemory<byte>> CalculateAsync(
            ReadOnlyMemory<byte> data, 
            XisfHashAlgorithm algorithm,
            CancellationToken cancellationToken = default)
        {
            if (!AlgorithmFactories.TryGetValue(algorithm, out var factory))
            {
                throw new NotSupportedException($"Hash algorithm {algorithm} is not supported");
            }

            // For very large data, we can process in chunks
            const int chunkSize = 81920; // 80 KB chunks
            
            using var hasher = factory();
            hasher.Initialize();

            for (int offset = 0; offset < data.Length; offset += chunkSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                int currentChunkSize = Math.Min(chunkSize, data.Length - offset);
                var chunk = data.Slice(offset, currentChunkSize);
                
                // Convert to array for the hash transform
                var buffer = ArrayPool<byte>.Shared.Rent(currentChunkSize);
                try
                {
                    chunk.CopyTo(buffer);
                    
                    if (offset + currentChunkSize >= data.Length)
                    {
                        // Last chunk
                        hasher.TransformFinalBlock(buffer, 0, currentChunkSize);
                    }
                    else
                    {
                        // Intermediate chunk
                        hasher.TransformBlock(buffer, 0, currentChunkSize, null, 0);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                }

                // Yield control periodically for cooperative multitasking
                if (offset % (chunkSize * 10) == 0)
                {
                    await Task.Yield();
                }
            }

            return hasher.Hash ?? Array.Empty<byte>();
        }

        /// <summary>
        /// Validates data against a checksum.
        /// </summary>
        public bool Validate(ReadOnlyMemory<byte> data, XisfChecksum checksum)
        {
            var calculated = Calculate(data, checksum.Algorithm);
            return calculated.Span.SequenceEqual(checksum.Digest.Span);
        }

        /// <summary>
        /// Validates data against a checksum asynchronously.
        /// </summary>
        public async Task<bool> ValidateAsync(
            ReadOnlyMemory<byte> data, 
            XisfChecksum checksum,
            CancellationToken cancellationToken = default)
        {
            var calculated = await CalculateAsync(data, checksum.Algorithm, cancellationToken);
            return calculated.Span.SequenceEqual(checksum.Digest.Span);
        }

        /// <summary>
        /// Converts a checksum to its hexadecimal string representation.
        /// Per specification, lowercase hex digits must be used.
        /// </summary>
        public string ToHexString(ReadOnlyMemory<byte> checksum)
        {
            if (checksum.IsEmpty)
                return string.Empty;

            var sb = new StringBuilder(checksum.Length * 2);
            foreach (var b in checksum.Span)
            {
                sb.Append(b.ToString("x2")); // Lowercase hex per specification
            }
            return sb.ToString();
        }

        /// <summary>
        /// Parses a hexadecimal string to a checksum.
        /// Supports both uppercase and lowercase for flexibility.
        /// </summary>
        public ReadOnlyMemory<byte> FromHexString(string hex)
        {
            if (string.IsNullOrEmpty(hex))
                return ReadOnlyMemory<byte>.Empty;

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
    /// Extension methods for checksum operations.
    /// </summary>
    internal static class ChecksumExtensions
    {
        /// <summary>
        /// Creates an XisfChecksum from a hex string representation.
        /// Format: "algorithm:hex_digest" per specification.
        /// </summary>
        public static XisfChecksum ParseChecksum(this string checksumAttribute)
        {
            if (string.IsNullOrWhiteSpace(checksumAttribute))
                throw new ArgumentException("Checksum attribute cannot be empty", nameof(checksumAttribute));

            var parts = checksumAttribute.Split(':');
            if (parts.Length != 2)
                throw new FormatException($"Invalid checksum format: {checksumAttribute}. Expected 'algorithm:digest'");

            var algorithm = ParseAlgorithm(parts[0]);
            var provider = new ChecksumProvider();
            var digest = provider.FromHexString(parts[1]);

            return new XisfChecksum(algorithm, digest);
        }

        /// <summary>
        /// Converts an XisfChecksum to its attribute string representation.
        /// </summary>
        public static string ToAttributeString(this XisfChecksum checksum, IChecksumProvider provider)
        {
            var algorithmName = GetAlgorithmName(checksum.Algorithm);
            var hexDigest = provider.ToHexString(checksum.Digest);
            return $"{algorithmName}:{hexDigest}";
        }

        private static XisfHashAlgorithm ParseAlgorithm(string name)
        {
            return name.ToLowerInvariant() switch
            {
                "sha-1" or "sha1" => XisfHashAlgorithm.SHA1,
                "sha-256" or "sha256" => XisfHashAlgorithm.SHA256,
                "sha-512" or "sha512" => XisfHashAlgorithm.SHA512,
                "sha3-256" => XisfHashAlgorithm.SHA3_256,
                "sha3-512" => XisfHashAlgorithm.SHA3_512,
                _ => throw new NotSupportedException($"Unknown hash algorithm: {name}")
            };
        }

        private static string GetAlgorithmName(XisfHashAlgorithm algorithm)
        {
            return algorithm switch
            {
                XisfHashAlgorithm.SHA1 => "sha-1",
                XisfHashAlgorithm.SHA256 => "sha-256",
                XisfHashAlgorithm.SHA512 => "sha-512",
                XisfHashAlgorithm.SHA3_256 => "sha3-256",
                XisfHashAlgorithm.SHA3_512 => "sha3-512",
                _ => throw new NotSupportedException($"Unknown hash algorithm: {algorithm}")
            };
        }
    }
}