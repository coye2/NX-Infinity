using System;

namespace NxInfinity.Core;

internal sealed class NxError(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
