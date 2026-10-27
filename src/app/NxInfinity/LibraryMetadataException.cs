using System;

namespace NxInfinity;

public sealed class LibraryMetadataException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}
