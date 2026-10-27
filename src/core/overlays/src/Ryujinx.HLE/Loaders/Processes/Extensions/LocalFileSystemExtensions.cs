using System.IO;
using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Loader;
using LibHac.Ncm;
using LibHac.Ns;
using LibHac.Tools.FsSystem;
using Ryujinx.HLE.HOS;
using Ryujinx.HLE.Loaders.Processes.Extensions;

namespace Ryujinx.HLE.Loaders.Processes;

internal static class LocalFileSystemExtensions
{
	public static ProcessResult Load(this LocalFileSystem exeFs, Switch device, string romFsPath = "")
	{
		MetaLoader npdm = exeFs.GetNpdm();
		BlitStruct<ApplicationControlProperty> blitStruct = new BlitStruct<ApplicationControlProperty>(1);
		ulong programId = npdm.GetProgramId();
		using (UniqueRef<IFile> uniqueRef = default)
		{
			if (exeFs.OpenFile(ref uniqueRef.Ref, "/control.nacp".ToU8Span(), OpenMode.Read).IsSuccess())
			{
				uniqueRef.Get.GetSize(out var size).ThrowIfFailure();
				if (size != blitStruct.ByteSpan.Length)
				{
					throw new InvalidDataException("Unpacked control.nacp does not match the native NACP structure size.");
				}
				uniqueRef.Get.Read(out var bytesRead, 0L, blitStruct.ByteSpan).ThrowIfFailure();
				if (bytesRead != size)
				{
					throw new EndOfStreamException();
				}
			}
		}
		ModLoader modLoader = device.Configuration.VirtualFileSystem.ModLoader;
		modLoader.CollectMods(
			new[] { programId },
			new[] { ModLoader.GetModsBasePath(), ModLoader.GetSdModsBasePath() });
		if (programId != 0L)
		{
			ProcessLoaderHelper.EnsureSaveData(device, new LibHac.Ncm.ApplicationId(programId), blitStruct);
		}
		ProcessResult processResult = exeFs.Load(device, blitStruct, npdm, 0);
		if (!string.IsNullOrEmpty(romFsPath))
		{
			IStorage baseStorage = new LocalStorage(romFsPath, FileAccess.Read);
			baseStorage = device.Configuration.VirtualFileSystem.ModLoader.ApplyRomFsMods(programId, baseStorage);
			device.Configuration.VirtualFileSystem.SetRomFs(processResult.ProcessId, baseStorage.AsStream(FileAccess.Read));
		}
		return processResult;
	}
}
