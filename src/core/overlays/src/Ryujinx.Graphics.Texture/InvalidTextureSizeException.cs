using System;

namespace Ryujinx.Graphics.Texture;

public sealed class InvalidTextureSizeException : Exception
{
	public InvalidTextureSizeException(Exception inner = null)
		: base("Texture dimensions exceed the supported storage range.", inner)
	{
	}
}
