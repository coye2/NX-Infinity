namespace Ryujinx.Graphics.Gpu.Image;

internal readonly struct TextureIncompatibleOverlap(TextureGroup group, TextureViewCompatibility compatibility)
{
	public readonly TextureGroup Group = group;

	public readonly TextureViewCompatibility Compatibility = compatibility;
}
