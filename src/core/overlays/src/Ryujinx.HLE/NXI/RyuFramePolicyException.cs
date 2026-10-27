using System;

namespace Ryujinx.HLE;

public sealed class RyuFramePolicyException(string code, string message) : Exception(message)
{
	public string Code { get; } = code;
}
