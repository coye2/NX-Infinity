using System;
using System.Collections.Generic;
using System.IO;
using LibHac.Common;
using LibHac.Common.Keys;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Ncm;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;
using LibHac.Tools.Ncm;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Utilities;
using Ryujinx.HLE.FileSystem;

namespace Ryujinx.HLE.Loaders.Processes.Extensions;

public static class PartitionFileSystemExtensions
{
	private static readonly DownloadableContentJsonSerializerContext _contentSerializerContext = new DownloadableContentJsonSerializerContext(JsonHelper.GetDefaultSerializerOptions());

	public static Dictionary<ulong, ContentMetaData> GetContentData(this IFileSystem partitionFileSystem, ContentMetaType contentType, VirtualFileSystem fileSystem, IntegrityCheckLevel checkLevel)
	{
		fileSystem.ImportTickets(partitionFileSystem);
		Dictionary<ulong, ContentMetaData> dictionary = new Dictionary<ulong, ContentMetaData>();
		foreach (DirectoryEntryEx item in partitionFileSystem.EnumerateEntries("/", "*.cnmt.nca"))
		{
			Cnmt cnmt = partitionFileSystem.GetNca(fileSystem.KeySet, item.FullPath).GetCnmt(checkLevel, contentType);
			if (cnmt != null)
			{
				ContentMetaData contentMetaData = new ContentMetaData(partitionFileSystem, cnmt);
				if (contentMetaData.Type == contentType)
				{
					dictionary.TryAdd(contentMetaData.ApplicationId, contentMetaData);
				}
			}
		}
		return dictionary;
	}

	internal static (bool, ProcessResult) TryLoad<TMetaData, TFormat, THeader, TEntry>(this PartitionFileSystemCore<TMetaData, TFormat, THeader, TEntry> partitionFileSystem, Switch device, Stream stream, ulong applicationId, out string errorMessage, string extension, Stream updateStream = null) where TMetaData : PartitionFileSystemMetaCore<TFormat, THeader, TEntry>, new() where TFormat : IPartitionFileSystemFormat where THeader : unmanaged, IPartitionFileSystemHeader where TEntry : unmanaged, IPartitionFileSystemEntry
	{
		errorMessage = null;
		Nca nca = null;
		Nca patchNca = null;
		Nca controlNca = null;
		try
		{
			Dictionary<ulong, ContentMetaData> contentData = partitionFileSystem.GetContentData(ContentMetaType.Application, device.FileSystem, device.System.FsIntegrityCheckLevel);
			ContentMetaData value;
			if (applicationId == 0L)
			{
				using Dictionary<ulong, ContentMetaData>.Enumerator enumerator = contentData.GetEnumerator();
				if (enumerator.MoveNext())
				{
					var (_, contentMetaData2) = enumerator.Current;
					nca = contentMetaData2.GetNcaByType(device.FileSystem.KeySet, LibHac.Ncm.ContentType.Program, device.Configuration.UserChannelPersistence.Index);
					controlNca = contentMetaData2.GetNcaByType(device.FileSystem.KeySet, LibHac.Ncm.ContentType.Control, device.Configuration.UserChannelPersistence.Index);
				}
			}
			else if (contentData.TryGetValue(applicationId, out value))
			{
				nca = value.GetNcaByType(device.FileSystem.KeySet, LibHac.Ncm.ContentType.Program, device.Configuration.UserChannelPersistence.Index);
				controlNca = value.GetNcaByType(device.FileSystem.KeySet, LibHac.Ncm.ContentType.Control, device.Configuration.UserChannelPersistence.Index);
			}
			ProcessLoaderHelper.RegisterProgramMapInfo(device, partitionFileSystem).ThrowIfFailure();
		}
		catch (Exception ex)
		{
			errorMessage = "Unable to load: " + ex.Message;
			return (false, ProcessResult.Failed);
		}
		if (nca != null)
		{
			if (nca.Header.ContentType != NcaContentType.Program)
			{
				errorMessage = "Selected NCA file is not a \"Program\" NCA";
				return (false, ProcessResult.Failed);
			}
			var (nca2, nca3) = nca.GetUpdateData(device.FileSystem, device.System.FsIntegrityCheckLevel, device.Configuration.UserChannelPersistence.Index, out var _, updateStream);
			if (nca2 != null)
			{
				patchNca = nca2;
			}
			if (nca3 != null)
			{
				controlNca = nca3;
			}
			device.Configuration.ContentManager.ClearAocData();
			string text = System.IO.Path.Combine(AppDataManager.GamesDirPath, nca.GetProgramIdBase().ToString("x16"), "dlc.json");
			if (File.Exists(text))
			{
				foreach (DownloadableContentContainer item in JsonHelper.DeserializeFromFile(text, _contentSerializerContext.ListDownloadableContentContainer))
				{
					foreach (DownloadableContentNca downloadableContentNca in item.DownloadableContentNcaList)
					{
						if (File.Exists(item.ContainerPath))
						{
							if (downloadableContentNca.Enabled)
							{
								device.Configuration.ContentManager.AddAocItemFromFile(downloadableContentNca.TitleId, item.ContainerPath, downloadableContentNca.FullPath);
							}
						}
						else
						{
							Logger.Warning?.Print(LogClass.Application, "Cannot find AddOnContent file " + item.ContainerPath + ". It may have been moved or renamed.", "TryLoad");
						}
					}
				}
			}
			return (true, nca.Load(device, patchNca, controlNca));
		}
		errorMessage = $"Unable to load: Could not find Main NCA for title \"{applicationId:X16}\"";
		return (false, ProcessResult.Failed);
	}

	public static Nca GetNca(this IFileSystem fileSystem, KeySet keySet, string path)
	{
		using UniqueRef<IFile> uniqueRef = default;
		fileSystem.OpenFile(ref uniqueRef.Ref, path.ToU8Span(), OpenMode.Read).ThrowIfFailure();
		return new Nca(keySet, uniqueRef.Release().AsStorage());
	}
}
