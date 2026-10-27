using System.IO;
using LibHac;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using Ryujinx.HLE.FileSystem;

namespace Ryujinx.HLE.Utilities;

public static class PartitionFileSystemUtils
{
	public static IFileSystem OpenApplicationFileSystem(string path, VirtualFileSystem fileSystem, bool throwOnFailure = true)
	{
		return OpenApplicationFileSystem(File.OpenRead(path), Path.GetExtension(path).ToLower() == ".xci", fileSystem, throwOnFailure);
	}

	public static IFileSystem OpenApplicationFileSystem(Stream stream, bool isXci, VirtualFileSystem fileSystem, bool throwOnFailure = true, bool leaveOpen = false)
	{
		IFileSystem fileSystem2;
		if (isXci)
		{
			fileSystem2 = new Xci(fileSystem.KeySet, stream.AsStorage(leaveOpen)).OpenPartition(XciPartitionType.Secure);
		}
		else
		{
			PartitionFileSystem partitionFileSystem = new PartitionFileSystem();
			Result result = partitionFileSystem.Initialize(stream.AsStorage(leaveOpen));
			if (throwOnFailure)
			{
				result.ThrowIfFailure();
			}
			else if (result.IsFailure())
			{
				return null;
			}
			fileSystem2 = partitionFileSystem;
		}
		fileSystem.ImportTickets(fileSystem2);
		return fileSystem2;
	}
}
