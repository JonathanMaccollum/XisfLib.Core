using System;
using System.IO;
using System.Net.Http;

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
}
