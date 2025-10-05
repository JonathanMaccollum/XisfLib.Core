namespace XisfLib.Core;

// ==================== Basic Enums (No Dependencies) ====================

public enum XisfSampleFormat 
{ 
    UInt8, UInt16, UInt32, UInt64, 
    Float32, Float64, 
    Complex32, Complex64 
}

public enum XisfColorSpace { Gray, RGB, CIELab }

public enum XisfPixelStorage { Planar, Normal }

public enum XisfImageType 
{
    Bias, Dark, Flat, Light,
    MasterBias, MasterDark, MasterFlat, MasterLight,
    DefectMap, RejectionMapHigh, RejectionMapLow,
    BinaryRejectionMapHigh, BinaryRejectionMapLow,
    SlopeMap, WeightMap
}

public enum XisfImageOrientation 
{
    None,
    Flip,
    Rotate90,
    Rotate90Flip,
    RotateNeg90,
    RotateNeg90Flip,
    Rotate180,
    Rotate180Flip
}

public enum XisfAlignment { Left, Right, Center }
public enum XisfSignMode { Auto, Force }
public enum XisfFloatMode { Auto, Scientific, Fixed }
public enum XisfBoolMode { Alpha, Numeric }
public enum XisfNumericBase { Binary, Octal, Decimal, Hexadecimal }

public enum XisfEncoding { Base64, Hex }
public enum XisfByteOrder { LittleEndian, BigEndian }
public enum XisfHashAlgorithm { SHA1, SHA256, SHA512, SHA3_256, SHA3_512 }
public enum XisfCompressionCodec { Zlib, ZlibSh, LZ4, LZ4Sh, LZ4HC, LZ4HCSh }
public enum XisfResolutionUnit { Inch, Centimeter }

// ==================== Simple Value Types ====================

public sealed record XisfComplex<T>(T Real, T Imaginary) where T : struct;

public sealed record XisfImageBounds(double Lower, double Upper);

public sealed record XisfImageGeometry(IReadOnlyList<uint> Dimensions, uint ChannelCount)
{
    public uint Width => Dimensions.Count > 0 ? Dimensions[0] : 0;
    public uint Height => Dimensions.Count > 1 ? Dimensions[1] : 0;
    public uint Depth => Dimensions.Count > 2 ? Dimensions[2] : 0;
}

public sealed record XisfChromaticity(double X, double Y);
public sealed record XisfLuminance(double Y);

public sealed record XisfDisplayParameters(
    double MidtonesBalance,
    double ShadowsClipping,
    double HighlightsClipping,
    double ShadowsExpansion,
    double HighlightsExpansion
);

public sealed record XisfCompressionSubblock(ulong CompressedLength, ulong UncompressedLength);

public sealed record XisfTableField(
    string Id, 
    Type PropertyType, 
    string? Header = null, 
    XisfPropertyFormat? Format = null
);

public sealed record XisfTableRow(IReadOnlyList<object> CellValues);

// ==================== Format/Configuration Types ====================

public sealed record XisfPropertyFormat(
    uint? Width = null,
    char? FillChar = null,
    XisfAlignment? Alignment = null,
    XisfSignMode? SignMode = null,
    uint? Precision = null,
    XisfFloatMode? FloatMode = null,
    XisfBoolMode? BoolMode = null,
    XisfNumericBase? Base = null,
    string? Unit = null
);

public sealed record XisfChecksum(XisfHashAlgorithm Algorithm, ReadOnlyMemory<byte> Digest);

public sealed record XisfCompression(
    XisfCompressionCodec Codec, 
    ulong UncompressedSize, 
    uint? ItemSize = null,
    IReadOnlyList<XisfCompressionSubblock>? Subblocks = null
);

public sealed record XisfTableStructure(IReadOnlyList<XisfTableField> Fields);

public sealed record XisfKeyInfo(
    string? SubjectName = null,
    string? IssuerName = null,
    string? SerialNumber = null,
    ReadOnlyMemory<byte>? Certificate = null
);

// ==================== Abstract Base Types ====================

public abstract record XisfProperty(string Id, string? Comment = null, XisfPropertyFormat? Format = null);

public abstract record XisfDataBlock(
    XisfByteOrder ByteOrder = XisfByteOrder.LittleEndian,
    XisfChecksum? Checksum = null,
    XisfCompression? Compression = null
);

public abstract record XisfCoreElement(string? Uid = null);

public abstract record XisfGamma;

public abstract record XisfStorageModel;

// ==================== Concrete Property Types ====================

public sealed record XisfScalarProperty<T>(
    string Id, 
    T Value, 
    string? Comment = null, 
    XisfPropertyFormat? Format = null
) : XisfProperty(Id, Comment, Format) where T : struct;

public sealed record XisfComplexProperty<T>(
    string Id, 
    XisfComplex<T> Value, 
    string? Comment = null, 
    XisfPropertyFormat? Format = null
) : XisfProperty(Id, Comment, Format) where T : struct;

public sealed record XisfStringProperty(
    string Id, 
    string Value, 
    string? Comment = null, 
    XisfPropertyFormat? Format = null
) : XisfProperty(Id, Comment, Format);

public sealed record XisfTimePointProperty(
    string Id, 
    DateTimeOffset Value, 
    string? Comment = null
) : XisfProperty(Id, Comment);

public sealed record XisfVectorProperty<T>(
    string Id, 
    IReadOnlyList<T> Values, 
    string? Comment = null, 
    XisfPropertyFormat? Format = null
) : XisfProperty(Id, Comment, Format) where T : struct;

public sealed record XisfMatrixProperty<T>(
    string Id, 
    uint Rows, 
    uint Columns, 
    IReadOnlyList<T> Values, 
    string? Comment = null, 
    XisfPropertyFormat? Format = null
) : XisfProperty(Id, Comment, Format) where T : struct;

public sealed record XisfTableProperty(
    string Id, 
    XisfTableStructure Structure, 
    IReadOnlyList<XisfTableRow> Rows, 
    string? Caption = null,
    string? Comment = null
) : XisfProperty(Id, Comment);

// ==================== Concrete Data Block Types ====================

public sealed record InlineDataBlock(
    ReadOnlyMemory<byte> Data, 
    XisfEncoding Encoding, 
    XisfByteOrder ByteOrder = XisfByteOrder.LittleEndian,
    XisfChecksum? Checksum = null,
    XisfCompression? Compression = null
) : XisfDataBlock(ByteOrder, Checksum, Compression);

public sealed record EmbeddedDataBlock(
    ReadOnlyMemory<byte> Data, 
    XisfEncoding Encoding, 
    XisfByteOrder ByteOrder = XisfByteOrder.LittleEndian,
    XisfChecksum? Checksum = null,
    XisfCompression? Compression = null
) : XisfDataBlock(ByteOrder, Checksum, Compression);

public sealed record AttachedDataBlock(
    ulong Position, 
    ulong Size, 
    XisfByteOrder ByteOrder = XisfByteOrder.LittleEndian, 
    XisfChecksum? Checksum = null, 
    XisfCompression? Compression = null
) : XisfDataBlock(ByteOrder, Checksum, Compression);

public sealed record ExternalDataBlock(
    Uri Location, 
    ulong? Position = null, 
    ulong? Size = null, 
    string? IndexId = null, 
    XisfByteOrder ByteOrder = XisfByteOrder.LittleEndian, 
    XisfChecksum? Checksum = null, 
    XisfCompression? Compression = null
) : XisfDataBlock(ByteOrder, Checksum, Compression);

// ==================== Concrete Core Element Types ====================

public sealed record FixedGamma(double Value) : XisfGamma;
public sealed record SRgbGamma() : XisfGamma;

public sealed record XisfReference(string RefId) : XisfCoreElement();

public sealed record XisfColorFilterArray(
    string Pattern,
    uint Width,
    uint Height,
    string? Name = null,
    string? Uid = null
) : XisfCoreElement(Uid);

public sealed record XisfResolution(
    double Horizontal,
    double Vertical,
    XisfResolutionUnit Unit = XisfResolutionUnit.Inch,
    string? Uid = null
) : XisfCoreElement(Uid);

public sealed record XisfFitsKeyword(
    string Name, 
    string Value, 
    string Comment, 
    string? Uid = null
) : XisfCoreElement(Uid);

public sealed record XisfIccProfile(
    XisfDataBlock ProfileData, 
    string? Uid = null
) : XisfCoreElement(Uid);

public sealed record XisfRgbWorkingSpace(
    XisfGamma Gamma,
    XisfChromaticity RedPrimary,
    XisfChromaticity GreenPrimary,
    XisfChromaticity BluePrimary,
    XisfLuminance RedLuminance,
    XisfLuminance GreenLuminance,
    XisfLuminance BlueLuminance,
    string? Name = null,
    string? Uid = null
) : XisfCoreElement(Uid);

public sealed record XisfDisplayFunction(
    XisfDisplayParameters RedOrGrayParameters,
    XisfDisplayParameters GreenParameters,
    XisfDisplayParameters BlueParameters,
    XisfDisplayParameters LightnessParameters,
    string? Name = null,
    string? Uid = null
) : XisfCoreElement(Uid);

public sealed record XisfThumbnail(
    XisfImageGeometry Geometry,
    XisfSampleFormat SampleFormat,
    XisfColorSpace ColorSpace,
    XisfDataBlock PixelData,
    XisfPixelStorage PixelStorage = XisfPixelStorage.Planar,
    string? Uid = null
) : XisfCoreElement(Uid);

// ==================== Concrete Storage Model Types ====================

public sealed record MonolithicStorage() : XisfStorageModel;
public sealed record DistributedStorage(string HeaderFileName, IReadOnlyList<string> DataBlockFiles) : XisfStorageModel;

// ==================== File Structure Components ====================

public sealed record XisfFileHeader(
    ReadOnlyMemory<byte> Signature,
    uint HeaderLength,
    uint Reserved
)
{
    public static ReadOnlyMemory<byte> ValidSignature => "XISF0100"u8.ToArray();
    public bool IsValid => Signature.Span.SequenceEqual(ValidSignature.Span) && HeaderLength >= 65;
}

public sealed record XisfDataBlocksFileHeader(
    ReadOnlyMemory<byte> Signature,
    ulong Reserved
)
{
    public static ReadOnlyMemory<byte> ValidSignature => "XISB0100"u8.ToArray();
    public bool IsValid => Signature.Span.SequenceEqual(ValidSignature.Span) && Reserved == 0;
}

public sealed record XisfBlockIndexElement(
    ulong UniqueId,
    ulong BlockPosition,
    ulong BlockLength,
    ulong UncompressedBlockLength,
    ulong Reserved
)
{
    public bool IsFree => BlockPosition == 0;
}

public sealed record XisfBlockIndexNode(
    uint Length,
    uint Reserved,
    ulong NextNode,
    IReadOnlyList<XisfBlockIndexElement> Elements
);

public sealed record XisfBlockIndex(IReadOnlyList<XisfBlockIndexNode> Nodes);

public sealed record XisfSignature(
    string Algorithm,
    ReadOnlyMemory<byte> SignatureValue,
    XisfKeyInfo KeyInfo
);

// ==================== Complex Aggregate Types ====================

public sealed record XisfMetadata(
    DateTimeOffset CreationTime,
    string CreatorApplication,
    string? CreatorModule = null,
    string? CreatorOS = null,
    IReadOnlyList<string>? Authors = null,
    string? Title = null,
    string? Description = null,
    string? Abstract = null,
    string? AccessRights = null,
    IReadOnlyList<string>? BibliographicReferences = null,
    string? BriefDescription = null,
    int? CompressionLevel = null,
    string? CompressionCodecs = null,
    IReadOnlyList<string>? Contributors = null,
    string? Copyright = null,
    string? Keywords = null,
    string? Languages = null,
    string? License = null,
    DateTimeOffset? OriginalCreationTime = null,
    IReadOnlyList<string>? RelatedResources = null
);

public sealed record XisfImage(
    XisfImageGeometry Geometry,
    XisfSampleFormat SampleFormat,
    XisfColorSpace ColorSpace,
    XisfDataBlock PixelData,
    XisfImageBounds? Bounds = null,
    XisfPixelStorage PixelStorage = XisfPixelStorage.Planar,
    XisfImageType? ImageType = null,
    double? Offset = null,
    XisfImageOrientation? Orientation = null,
    string? ImageId = null,
    Guid? Uuid = null,
    IReadOnlyList<XisfProperty>? Properties = null,
    IReadOnlyList<XisfCoreElement>? AssociatedElements = null
);

public sealed record XisfHeader(
    XisfMetadata Metadata,
    IReadOnlyDictionary<string, XisfCoreElement> CoreElements,
    string? InitialComment = null
);

// ==================== Top-Level Container ====================

public sealed record XisfUnit(
    XisfStorageModel StorageModel,
    XisfHeader Header,
    IReadOnlyList<XisfImage> Images,
    IReadOnlyList<XisfProperty> GlobalProperties,
    XisfSignature? Signature = null
);

// ==================== Configuration ====================

public sealed record XisfReaderOptions(
    bool ValidateChecksums = true,
    bool LoadThumbnails = true,
    bool LoadExternalReferences = true,
    Func<string, Stream>? FileStreamProvider = null,
    Func<Uri, Stream>? UriStreamProvider = null
);

public sealed record XisfWriterOptions(
    XisfCompressionCodec? DefaultCompression = null,
    bool CalculateChecksums = false,
    XisfHashAlgorithm ChecksumAlgorithm = XisfHashAlgorithm.SHA1,
    bool PrettyPrintXml = false,
    Func<string, Stream>? FileStreamProvider = null
);

// ==================== Factory Methods ====================

public static class XisfFactory
{
    public static XisfUnit CreateMonolithic(XisfMetadata metadata, params XisfImage[] images)
        => new(new MonolithicStorage(), 
               new XisfHeader(metadata, new Dictionary<string, XisfCoreElement>()),
               images,
               Array.Empty<XisfProperty>());

    public static XisfUnit CreateDistributed(XisfMetadata metadata, string headerFileName, 
        IEnumerable<string> dataFiles, params XisfImage[] images)
        => new(new DistributedStorage(headerFileName, dataFiles.ToList()),
               new XisfHeader(metadata, new Dictionary<string, XisfCoreElement>()),
               images,
               Array.Empty<XisfProperty>());

    public static XisfMetadata CreateMinimalMetadata(string creatorApplication)
        => new(DateTimeOffset.UtcNow, creatorApplication);

    public static XisfImage CreateGrayscaleImage(uint width, uint height, 
        XisfSampleFormat sampleFormat, XisfDataBlock pixelData)
        => new(new XisfImageGeometry(new[] { width, height }, 1),
               sampleFormat, XisfColorSpace.Gray, pixelData);

    public static XisfImage CreateRgbImage(uint width, uint height, 
        XisfSampleFormat sampleFormat, XisfDataBlock pixelData)
        => new(new XisfImageGeometry(new[] { width, height }, 3),
               sampleFormat, XisfColorSpace.RGB, pixelData);
}