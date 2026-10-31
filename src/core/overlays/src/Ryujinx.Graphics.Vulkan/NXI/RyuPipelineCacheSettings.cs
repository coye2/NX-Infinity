namespace Ryujinx.Graphics.Vulkan;

public static class RyuPipelineCacheSettings
{
	public static volatile bool FastGraphicsCompilation;

	internal static string Status = "disabled";

	internal static string Identity;

	internal static int LoadedBytes;

	internal static int SavedBytes;

	public static string Directory { get; set; }

	public static object Snapshot()
	{
		return new
		{
			schemaVersion = 1,
			status = Status,
			loadedBytes = LoadedBytes,
			savedBytes = SavedBytes,
			identity = Identity,
			fastGraphicsCompilation = FastGraphicsCompilation,
			persistencePoint = "orderly_renderer_shutdown"
		};
	}
}
