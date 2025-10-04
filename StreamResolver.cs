using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace XisfLib.Core.Implementations
{
    /// <summary>
    /// Resolves and provides access to various stream sources for XISF data.
    /// Specification Reference: Section 10.3 XISF Data Block Location
    /// </summary>
    internal sealed class StreamResolver : IStreamResolver
    {
        private readonly Func<string, Stream>? _fileStreamProvider;
        private readonly Func<Uri, Stream>? _uriStreamProvider;
        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;

        public StreamResolver(
            Func<string, Stream>? fileStreamProvider = null,
            Func<Uri, Stream>? uriStreamProvider = null,
            HttpClient? httpClient = null)
        {
            _fileStreamProvider = fileStreamProvider;
            _uriStreamProvider = uriStreamProvider;
            
            if (httpClient == null)
            {
                _httpClient = new HttpClient();
                _ownsHttpClient = true;
            }
            else
            {
                _httpClient = httpClient;
                _ownsHttpClient = false;
            }
        }

        /// <summary>
        /// Resolves a file path to a stream.
        /// Handles both absolute and relative paths per specification.
        /// </summary>
        public Stream ResolveFileStream(string path, FileMode mode, FileAccess access)
        {
            if (_fileStreamProvider != null)
            {
                return _fileStreamProvider(path);
            }

            // Normalize path separators to platform-specific
            var normalizedPath = NormalizePath(path);
            
            return new FileStream(normalizedPath, mode, access, FileShare.Read, 4096, useAsync: true);
        }

        /// <summary>
        /// Resolves a file path to a stream asynchronously.
        /// </summary>
        public Task<Stream> ResolveFileStreamAsync(
            string path, 
            FileMode mode, 
            FileAccess access,
            CancellationToken cancellationToken = default)
        {
            // For file streams, the async version just wraps the sync version
            // since FileStream constructor is not async
            return Task.FromResult(ResolveFileStream(path, mode, access));
        }

        /// <summary>
        /// Resolves a URI to a stream for external data blocks.
        /// Specification Reference: location="url(...)"
        /// </summary>
        public async Task<Stream> ResolveUriStreamAsync(
            Uri uri,
            CancellationToken cancellationToken = default)
        {
            if (_uriStreamProvider != null)
            {
                return _uriStreamProvider(uri);
            }

            // Handle different URI schemes
            switch (uri.Scheme.ToLowerInvariant())
            {
                case "file":
                    var localPath = uri.LocalPath;
                    return ResolveFileStream(localPath, FileMode.Open, FileAccess.Read);

                case "http":
                case "https":
                    // Download the content to a memory stream for random access
                    var response = await _httpClient.GetAsync(uri, cancellationToken);
                    response.EnsureSuccessStatusCode();
                    
                    var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                    return new MemoryStream(content, writable: false);

                case "ftp":
                    // FTP support would require additional implementation
                    throw new NotSupportedException($"FTP protocol is not yet supported");

                default:
                    throw new NotSupportedException($"URI scheme '{uri.Scheme}' is not supported");
            }
        }

        /// <summary>
        /// Creates a view stream for an attached data block within a parent stream.
        /// Specification Reference: location="attachment:position:size"
        /// </summary>
        public Stream ResolveAttachedStream(Stream parent, ulong position, ulong size)
        {
            if (parent == null)
                throw new ArgumentNullException(nameof(parent));

            if (!parent.CanSeek)
                throw new ArgumentException("Parent stream must be seekable for attached blocks", nameof(parent));

            if (!parent.CanRead)
                throw new ArgumentException("Parent stream must be readable", nameof(parent));

            // Create a bounded substream view
            return new SubStream(parent, (long)position, (long)size, leaveOpen: true);
        }

        /// <summary>
        /// Resolves a path relative to the header directory.
        /// Specification Reference: location="path(@header_dir/...)"
        /// </summary>
        public string ResolveRelativePath(string headerPath, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(headerPath))
                throw new ArgumentException("Header path cannot be empty", nameof(headerPath));

            if (string.IsNullOrWhiteSpace(relativePath))
                throw new ArgumentException("Relative path cannot be empty", nameof(relativePath));

            // Handle @header_dir placeholder
            const string headerDirPlaceholder = "@header_dir/";
            if (relativePath.StartsWith(headerDirPlaceholder, StringComparison.Ordinal))
            {
                relativePath = relativePath.Substring(headerDirPlaceholder.Length);
            }

            // Get directory of header file
            var headerDirectory = Path.GetDirectoryName(headerPath) ?? string.Empty;

            // Normalize path separators from UNIX style to platform-specific
            relativePath = NormalizePath(relativePath);

            // Combine paths
            var fullPath = Path.Combine(headerDirectory, relativePath);

            // Resolve any .. or . components
            fullPath = Path.GetFullPath(fullPath);

            return fullPath;
        }

        /// <summary>
        /// Opens an XISF data blocks file and provides access to its index.
        /// Specification Reference: Section 9.4 XISF Data Blocks File
        /// </summary>
        public async Task<IXisfDataBlocksFile> OpenDataBlocksFileAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            var stream = ResolveFileStream(path, FileMode.Open, FileAccess.ReadWrite);
            
            try
            {
                var dataBlocksFile = new XisfDataBlocksFile(stream);
                await dataBlocksFile.LoadIndexAsync(cancellationToken);
                return dataBlocksFile;
            }
            catch
            {
                stream?.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Normalizes path separators from UNIX style to platform-specific.
        /// XISF specification requires UNIX-style paths internally.
        /// </summary>
        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;

            // Replace UNIX separators with platform-specific ones
            if (Path.DirectorySeparatorChar != '/')
            {
                path = path.Replace('/', Path.DirectorySeparatorChar);
            }

            return path;
        }

        public void Dispose()
        {
            if (_ownsHttpClient)
            {
                _httpClient?.Dispose();
            }
        }
    }

    /// <summary>
    /// Provides a view into a portion of another stream.
    /// Used for attached data blocks within monolithic files.
    /// </summary>
    internal sealed class SubStream : Stream
    {
        private readonly Stream _baseStream;
        private readonly long _offset;
        private readonly long _length;
        private readonly bool _leaveOpen;
        private long _position;
        private bool _disposed;

        public SubStream(Stream baseStream, long offset, long length, bool leaveOpen = false)
        {
            _baseStream = baseStream ?? throw new ArgumentNullException(nameof(baseStream));
            _offset = offset;
            _length = length;
            _leaveOpen = leaveOpen;
            _position = 0;

            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset), "Offset cannot be negative");
            
            if (length < 0)
                throw new ArgumentOutOfRangeException(nameof(length), "Length cannot be negative");
            
            if (!baseStream.CanSeek)
                throw new ArgumentException("Base stream must be seekable", nameof(baseStream));
        }

        public override bool CanRead => _baseStream.CanRead;
        public override bool CanSeek => _baseStream.CanSeek;
        public override bool CanWrite => false; // Read-only view
        public override long Length => _length;
        
        public override long Position
        {
            get => _position;
            set
            {
                if (value < 0 || value > _length)
                    throw new ArgumentOutOfRangeException(nameof(value));
                _position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SubStream));

            // Limit read to available data
            count = (int)Math.Min(count, _length - _position);
            if (count <= 0)
                return 0;

            // Seek to correct position in base stream
            _baseStream.Position = _offset + _position;
            
            // Read from base stream
            int bytesRead = _baseStream.Read(buffer, offset, count);
            _position += bytesRead;
            
            return bytesRead;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SubStream));

            // Limit read to available data
            count = (int)Math.Min(count, _length - _position);
            if (count <= 0)
                return 0;

            // Seek to correct position in base stream
            _baseStream.Position = _offset + _position;
            
            // Read from base stream
            int bytesRead = await _baseStream.ReadAsync(buffer, offset, count, cancellationToken);
            _position += bytesRead;
            
            return bytesRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SubStream));

            long newPosition = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentException("Invalid seek origin", nameof(origin))
            };

            if (newPosition < 0 || newPosition > _length)
                throw new ArgumentOutOfRangeException(nameof(offset), "Seek position is outside the substream bounds");

            _position = newPosition;
            return _position;
        }

        public override void Flush()
        {
            // No-op for read-only stream
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException("SubStream is read-only");
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException("SubStream is read-only");
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing && !_leaveOpen)
                {
                    _baseStream?.Dispose();
                }
                _disposed = true;
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Implementation of XISF data blocks file access.
    /// Specification Reference: Section 9.4 XISF Data Blocks File
    /// </summary>
    internal sealed class XisfDataBlocksFile : IXisfDataBlocksFile
    {
        private readonly Stream _stream;
        private readonly bool _ownsStream;
        private XisfDataBlocksFileHeader _header = null!;
        private XisfBlockIndex _blockIndex = null!;
        private bool _disposed;

        public XisfDataBlocksFile(Stream stream, bool ownsStream = true)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _ownsStream = ownsStream;
            
            if (!stream.CanRead || !stream.CanSeek)
                throw new ArgumentException("Stream must be readable and seekable", nameof(stream));
        }

        public XisfDataBlocksFileHeader Header => _header;
        public XisfBlockIndex BlockIndex => _blockIndex;

        public async Task LoadIndexAsync(CancellationToken cancellationToken = default)
        {
            // Read and validate file header
            _header = await ReadHeaderAsync(cancellationToken);
            
            // Read block index
            _blockIndex = await ReadBlockIndexAsync(cancellationToken);
        }

        private async Task<XisfDataBlocksFileHeader> ReadHeaderAsync(CancellationToken cancellationToken)
        {
            _stream.Position = 0;
            
            var buffer = new byte[16]; // 8 bytes signature + 8 bytes reserved
            await _stream.ReadAsync(buffer, 0, 16, cancellationToken);
            
            var signature = new ReadOnlyMemory<byte>(buffer, 0, 8);
            var reserved = BitConverter.ToUInt64(buffer, 8);
            
            var header = new XisfDataBlocksFileHeader(signature, reserved);
            
            if (!header.IsValid)
                throw new InvalidDataException("Invalid XISF data blocks file header");
            
            return header;
        }

        private async Task<XisfBlockIndex> ReadBlockIndexAsync(CancellationToken cancellationToken)
        {
            var nodes = new List<XisfBlockIndexNode>();
            ulong nextNodePosition = 16; // Start after header
            
            while (nextNodePosition != 0)
            {
                _stream.Position = (long)nextNodePosition;
                
                // Read node header (4 + 4 + 8 = 16 bytes)
                var nodeHeader = new byte[16];
                await _stream.ReadAsync(nodeHeader, 0, 16, cancellationToken);
                
                uint length = BitConverter.ToUInt32(nodeHeader, 0);
                uint reserved = BitConverter.ToUInt32(nodeHeader, 4);
                ulong nextNode = BitConverter.ToUInt64(nodeHeader, 8);
                
                // Read block index elements (40 bytes each)
                var elements = new List<XisfBlockIndexElement>();
                for (uint i = 0; i < length; i++)
                {
                    var elementData = new byte[40];
                    await _stream.ReadAsync(elementData, 0, 40, cancellationToken);
                    
                    var element = new XisfBlockIndexElement(
                        UniqueId: BitConverter.ToUInt64(elementData, 0),
                        BlockPosition: BitConverter.ToUInt64(elementData, 8),
                        BlockLength: BitConverter.ToUInt64(elementData, 16),
                        UncompressedBlockLength: BitConverter.ToUInt64(elementData, 24),
                        Reserved: BitConverter.ToUInt64(elementData, 32)
                    );
                    
                    elements.Add(element);
                }
                
                nodes.Add(new XisfBlockIndexNode(length, reserved, nextNode, elements));
                nextNodePosition = nextNode;
            }
            
            return new XisfBlockIndex(nodes);
        }

        public async Task<ReadOnlyMemory<byte>> ReadBlockAsync(
            ulong uniqueId,
            CancellationToken cancellationToken = default)
        {
            // Find the block in the index
            var element = FindBlockElement(uniqueId);
            if (element == null || element.IsFree)
                throw new ArgumentException($"Block with ID {uniqueId:X16} not found", nameof(uniqueId));
            
            // Read the block data
            _stream.Position = (long)element.BlockPosition;
            var buffer = new byte[element.BlockLength];
            await _stream.ReadAsync(buffer, 0, (int)element.BlockLength, cancellationToken);
            
            return buffer;
        }

        public Task<ulong> WriteBlockAsync(
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken = default)
        {
            // Writing to data blocks files would require index management
            // This is a complex operation that would need to:
            // 1. Find or allocate space in the file
            // 2. Update the block index
            // 3. Handle fragmentation
            throw new NotImplementedException("Writing to XISF data blocks files is not yet implemented");
        }

        public Stream GetBlockStream(ulong uniqueId)
        {
            var element = FindBlockElement(uniqueId);
            if (element == null || element.IsFree)
                throw new ArgumentException($"Block with ID {uniqueId:X16} not found", nameof(uniqueId));
            
            return new SubStream(_stream, (long)element.BlockPosition, (long)element.BlockLength, leaveOpen: true);
        }

        private XisfBlockIndexElement? FindBlockElement(ulong uniqueId)
        {
            foreach (var node in _blockIndex.Nodes)
            {
                foreach (var element in node.Elements)
                {
                    if (element.UniqueId == uniqueId)
                        return element;
                }
            }
            return null;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                if (_ownsStream)
                {
                    _stream?.Dispose();
                }
                _disposed = true;
            }
        }
    }
}