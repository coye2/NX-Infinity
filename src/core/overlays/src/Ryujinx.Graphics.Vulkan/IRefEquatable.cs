namespace Ryujinx.Graphics.Vulkan;

internal interface IRefEquatable<T>
{
	bool Equals(ref T other);
}
