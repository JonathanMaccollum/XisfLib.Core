namespace XisfLib.Core;

/// <summary>
/// Thrown when an XISF object cannot be materialized because it declares something this build does
/// not support (an unknown compression codec, an uncomputable checksum algorithm). Per §7 the rest
/// of the unit stays accessible: header parsing succeeds and only the affected block throws.
/// </summary>
public sealed class XisfUnsupportedObjectException : Exception
{
    public XisfUnsupportedObjectException(string message) : base(message)
    {
    }

    public XisfUnsupportedObjectException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
