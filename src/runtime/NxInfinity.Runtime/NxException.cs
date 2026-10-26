#nullable enable
using System;

namespace NxInfinity.Runtime;

/// <summary>Stable machine-readable failures; backend errors keep their code.</summary>
public sealed class NxException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

