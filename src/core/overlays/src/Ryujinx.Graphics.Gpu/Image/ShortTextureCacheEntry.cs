namespace Ryujinx.Graphics.Gpu.Image;

internal class ShortTextureCacheEntry
{
	public bool IsAutoDelete;

	public readonly TextureDescriptor Descriptor;

	public readonly int InvalidatedSequence;

	public readonly Texture Texture;

	public ShortTextureCacheEntry(TextureDescriptor descriptor, Texture texture)
	{
		Descriptor = descriptor;
		InvalidatedSequence = texture.InvalidatedSequence;
		Texture = texture;
	}

	public ShortTextureCacheEntry(Texture texture)
	{
		IsAutoDelete = true;
		InvalidatedSequence = texture.InvalidatedSequence;
		Texture = texture;
	}
}
