using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using XisfLib.Core.Implementations;

namespace XisfLib.Core
{
    /// <summary>
    /// Public API for reading XISF (Extensible Image Serialization Format) files.
    /// Supports both monolithic (.xisf) and distributed (.xish + .xisb) XISF units.
    /// </summary>
    public sealed class XisfReader : IDisposable
    {
        private readonly IXisfComponentFactory _componentFactory;
        private readonly StorageStrategyFactory _strategyFactory;
        private bool _disposed;

        /// <summary>
        /// Creates a new XISF reader with default options.
        /// </summary>
        public XisfReader() : this(new XisfReaderOptions())
        {
        }

        /// <summary>
        /// Creates a new XISF reader with the specified options.
        /// </summary>
        /// <param name="options">Reader configuration options.</param>
        public XisfReader(XisfReaderOptions options)
        {
            Options = options ?? throw new ArgumentNullException(nameof(options));
            _componentFactory = new XisfComponentFactory();
            _strategyFactory = new StorageStrategyFactory(_componentFactory);
        }

        /// <summary>
        /// Gets the reader options.
        /// </summary>
        public XisfReaderOptions Options { get; }

        /// <summary>
        /// Reads an XISF unit from the specified file path.
        /// </summary>
        /// <param name="filePath">Path to the XISF file (.xisf or .xish).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The deserialized XISF unit.</returns>
        public async Task<XisfUnit> ReadAsync(string filePath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path cannot be null or empty", nameof(filePath));

            if (!File.Exists(filePath))
                throw new FileNotFoundException($"XISF file not found: {filePath}", filePath);

            using var stream = File.OpenRead(filePath);
            return await ReadAsync(stream, filePath, cancellationToken);
        }

        /// <summary>
        /// Reads an XISF unit from a stream.
        /// </summary>
        /// <param name="stream">Stream containing XISF data.</param>
        /// <param name="hint">Optional hint about the file type (file extension like ".xisf" or ".xish").</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The deserialized XISF unit.</returns>
        public async Task<XisfUnit> ReadAsync(Stream stream, string? hint = null, CancellationToken cancellationToken = default)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            if (!stream.CanRead)
                throw new ArgumentException("Stream must be readable", nameof(stream));

            ObjectDisposedException.ThrowIf(_disposed, this);

            // Determine strategy based on hint or try to detect from stream
            IStorageStrategy strategy;
            if (!string.IsNullOrEmpty(hint))
            {
                strategy = _strategyFactory.CreateStrategy(hint);
            }
            else
            {
                // Try to detect format by reading first bytes
                strategy = await DetectStrategyAsync(stream, cancellationToken);
            }

            return await strategy.ReadAsync(stream, Options, cancellationToken);
        }

        /// <summary>
        /// Reads only the header information from an XISF file without loading pixel data.
        /// </summary>
        /// <param name="filePath">Path to the XISF file.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The XISF header information.</returns>
        public async Task<XisfHeader> ReadHeaderAsync(string filePath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path cannot be null or empty", nameof(filePath));

            if (!File.Exists(filePath))
                throw new FileNotFoundException($"XISF file not found: {filePath}", filePath);

            using var stream = File.OpenRead(filePath);

            // Read file header
            var headerBytes = new byte[16];
            await stream.ReadAsync(headerBytes.AsMemory(0, 16), cancellationToken);

            var signature = new byte[8];
            Array.Copy(headerBytes, 0, signature, 0, 8);
            var headerLength = BitConverter.ToUInt32(headerBytes, 8);

            // Read XML header
            var xmlHeaderBytes = new byte[headerLength];
            await stream.ReadAsync(xmlHeaderBytes.AsMemory(0, (int)headerLength), cancellationToken);

            var xmlHeaderText = System.Text.Encoding.UTF8.GetString(xmlHeaderBytes);
            var xmlDocument = System.Xml.Linq.XDocument.Parse(xmlHeaderText);

            var xmlSerializer = _componentFactory.CreateXmlSerializer();
            return xmlSerializer.DeserializeHeader(xmlDocument);
        }

        /// <summary>
        /// Validates an XISF file without fully deserializing it.
        /// </summary>
        /// <param name="filePath">Path to the XISF file.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>True if the file appears to be a valid XISF file.</returns>
        public async Task<bool> ValidateAsync(string filePath, CancellationToken cancellationToken = default)
        {
            try
            {
                await ReadHeaderAsync(filePath, cancellationToken);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task<IStorageStrategy> DetectStrategyAsync(Stream stream, CancellationToken cancellationToken)
        {
            if (!stream.CanSeek)
            {
                // If can't seek, default to monolithic
                return _strategyFactory.CreateMonolithicStrategy();
            }

            var originalPosition = stream.Position;

            try
            {
                var signatureBytes = new byte[8];
                await stream.ReadAsync(signatureBytes.AsMemory(0, 8), cancellationToken);

                var signature = System.Text.Encoding.ASCII.GetString(signatureBytes);

                stream.Position = originalPosition;

                return signature switch
                {
                    "XISF0100" => _strategyFactory.CreateMonolithicStrategy(),
                    "XISB0100" => throw new FormatException("XISB data blocks files cannot be read directly. Please read the .xish header file instead."),
                    _ => _strategyFactory.CreateDistributedStrategy() // Assume distributed (plain XML)
                };
            }
            catch
            {
                stream.Position = originalPosition;
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
        }
    }
}
