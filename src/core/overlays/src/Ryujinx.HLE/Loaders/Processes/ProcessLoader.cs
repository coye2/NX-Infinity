using System;
using System.Collections.Concurrent;
using System.IO;
using LibHac.Common;
using LibHac.Common.FixedArrays;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Ns;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;
using LibHac.Tools.Ro;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Gpu;
using Ryujinx.HLE.HOS.Kernel;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.HLE.Loaders.Executables;
using Ryujinx.HLE.Loaders.Processes.Extensions;

namespace Ryujinx.HLE.Loaders.Processes;

public class ProcessLoader
{
	private readonly Switch _device;

	private readonly ConcurrentDictionary<ulong, ProcessResult> _processesByPid;

	private ulong _latestPid;

	public ProcessResult ActiveApplication => _processesByPid[_latestPid];

	public ulong? GetRuntimeProgramId(ulong processId)
	{
		KernelContext kernelContext = _device.System.KernelContext;
		kernelContext.CriticalSection.Enter();
		try
		{
			KProcess value;
			return kernelContext.Processes.TryGetValue(processId, out value) ? new ulong?(value.TitleId) : ((ulong?)null);
		}
		finally
		{
			kernelContext.CriticalSection.Leave();
		}
	}

	public ProcessLoader(Switch device)
	{
		_device = device;
		_processesByPid = new ConcurrentDictionary<ulong, ProcessResult>();
	}

	public bool LoadXci(string path, ulong applicationId)
	{
		FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read);
		return LoadXci(stream, applicationId);
	}

	public bool LoadXci(Stream stream, ulong applicationId, Stream updateStream = null)
	{
		Xci xci = new Xci(_device.Configuration.VirtualFileSystem.KeySet, stream.AsStorage());
		if (!xci.HasPartition(XciPartitionType.Secure))
		{
			Logger.Error?.Print(LogClass.Loader, "Unable to load XCI: Could not find XCI Secure partition", "LoadXci");
			return false;
		}
		var (flag, processResult) = xci.OpenPartition(XciPartitionType.Secure).TryLoad(_device, stream, applicationId, out var errorMessage, "xci", updateStream);
		if (!flag)
		{
			Logger.Error?.Print(LogClass.Loader, errorMessage, "TryLoad");
			return false;
		}
		if (processResult.ProcessId != 0L && _processesByPid.TryAdd(processResult.ProcessId, processResult) && processResult.Start(_device))
		{
			_latestPid = processResult.ProcessId;
			return true;
		}
		return false;
	}

	public bool LoadNsp(string path, ulong applicationId)
	{
		FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read);
		return LoadNsp(stream, applicationId);
	}

	public bool LoadNsp(Stream stream, ulong applicationId, Stream updateStream = null)
	{
		PartitionFileSystem partitionFileSystem = new PartitionFileSystem();
		partitionFileSystem.Initialize(stream.AsStorage()).ThrowIfFailure();
		var (flag, processResult) = partitionFileSystem.TryLoad(_device, stream, applicationId, out var errorMessage, "nsp", updateStream);
		if (processResult.ProcessId == 0L)
		{
			processResult = partitionFileSystem.Load(_device, new BlitStruct<ApplicationControlProperty>(1), partitionFileSystem.GetNpdm(), 0, isHomebrew: true);
		}
		if (processResult.ProcessId != 0L && _processesByPid.TryAdd(processResult.ProcessId, processResult) && processResult.Start(_device))
		{
			_latestPid = processResult.ProcessId;
			return true;
		}
		if (!flag)
		{
			Logger.Error?.Print(LogClass.Loader, errorMessage, "TryLoad");
		}
		return false;
	}

	public bool LoadNca(string path)
	{
		FileStream ncaStream = new FileStream(path, FileMode.Open, FileAccess.Read);
		return LoadNca(ncaStream);
	}

	public bool LoadNca(Stream ncaStream)
	{
		ProcessResult processResult = new Nca(_device.Configuration.VirtualFileSystem.KeySet, ncaStream.AsStorage(keepOpen: false)).Load(_device, null, null);
		if (processResult.ProcessId != 0L && _processesByPid.TryAdd(processResult.ProcessId, processResult) && processResult.Start(_device))
		{
			if (processResult.ProgramId > 72057594037929983L)
			{
				_latestPid = processResult.ProcessId;
			}
			return true;
		}
		return false;
	}

	public bool LoadUnpackedNca(string exeFsDirPath, string romFsPath = null)
	{
		ProcessResult processResult = new LocalFileSystem(exeFsDirPath).Load(_device, romFsPath);
		if (processResult.ProcessId != 0L && _processesByPid.TryAdd(processResult.ProcessId, processResult) && processResult.Start(_device))
		{
			_latestPid = processResult.ProcessId;
			return true;
		}
		return false;
	}

	public bool LoadNxo(string path)
	{
		BlitStruct<ApplicationControlProperty> applicationControlProperties = new BlitStruct<ApplicationControlProperty>(1);
		IFileSystem fileSystem = null;
		Stream stream = null;
		string text = "";
		ulong programId = 0uL;
		IExecutable executable;
		if (System.IO.Path.GetExtension(path).ToLower() == ".nro")
		{
			IExecutable nroExecutable = (executable = new NroExecutable(new FileStream(path, FileMode.Open).AsStorage(), 0uL, 0uL));
			IStorage storage = ((Nro)nroExecutable).OpenNroAssetSection(NroAssetType.RomFs, false);
			storage.GetSize(out var size).ThrowIfFailure();
			if (size != 0L)
			{
				stream = storage.AsStream();
			}
			IStorage storage2 = ((Nro)nroExecutable).OpenNroAssetSection(NroAssetType.Nacp, false);
			storage2.GetSize(out var size2).ThrowIfFailure();
			if (size2 != 0L)
			{
				storage2.Read(0L, applicationControlProperties.ByteSpan);
				text = applicationControlProperties.Value.Title[(int)_device.System.State.DesiredTitleLanguage].NameString.ToString();
				if (string.IsNullOrWhiteSpace(text))
				{
					foreach (ApplicationControlProperty.ApplicationTitle applicationTitle in applicationControlProperties.Value.Title)
					{
						if (applicationTitle.Name[0] == 0)
						{
							text = applicationTitle.NameString.ToString();
						}
					}
				}
				if (applicationControlProperties.Value.PresenceGroupId != 0L)
				{
					programId = applicationControlProperties.Value.PresenceGroupId;
				}
				else if (applicationControlProperties.Value.SaveDataOwnerId != 0L)
				{
					programId = applicationControlProperties.Value.SaveDataOwnerId;
				}
				else if (applicationControlProperties.Value.AddOnContentBaseId != 0L)
				{
					programId = applicationControlProperties.Value.AddOnContentBaseId - 4096;
				}
			}
		}
		else
		{
			text = System.IO.Path.GetFileNameWithoutExtension(path);
			executable = new NsoExecutable(new LocalStorage(path, FileAccess.Read), text);
		}
		GraphicsConfig.TitleId = null;
		_device.Gpu.HostInitalized.Set();
		ProcessResult processResult = ProcessLoaderHelper.LoadNsos(_device, _device.System.KernelContext, fileSystem.GetNpdm(), applicationControlProperties, false, null, true, text, programId, 0, null, (IExecutable)executable);
		if (processResult.ProcessId != 0L)
		{
			if (stream != null)
			{
				_device.Configuration.VirtualFileSystem.SetRomFs(processResult.ProcessId, stream);
			}
			if (_processesByPid.TryAdd(processResult.ProcessId, processResult) && processResult.Start(_device))
			{
				_latestPid = processResult.ProcessId;
				return true;
			}
		}
		return false;
	}

	public bool LoadNxo(Stream stream, bool isNro, string name)
	{
		BlitStruct<ApplicationControlProperty> applicationControlProperties = new BlitStruct<ApplicationControlProperty>(1);
		IFileSystem fileSystem = null;
		Stream stream2 = null;
		string text = "";
		ulong programId = 0uL;
		IExecutable executable;
		if (isNro)
		{
			IExecutable nroExecutable = (executable = new NroExecutable(stream.AsStorage(), 0uL, 0uL));
			IStorage storage = ((Nro)nroExecutable).OpenNroAssetSection(NroAssetType.RomFs, false);
			storage.GetSize(out var size).ThrowIfFailure();
			if (size != 0L)
			{
				stream2 = storage.AsStream();
			}
			IStorage storage2 = ((Nro)nroExecutable).OpenNroAssetSection(NroAssetType.Nacp, false);
			storage2.GetSize(out var size2).ThrowIfFailure();
			if (size2 != 0L)
			{
				storage2.Read(0L, applicationControlProperties.ByteSpan);
				text = applicationControlProperties.Value.Title[(int)_device.System.State.DesiredTitleLanguage].NameString.ToString();
				if (string.IsNullOrWhiteSpace(text))
				{
					text = Array.Find(applicationControlProperties.Value.Title.AsReadOnlySpan().ToArray(), (ApplicationControlProperty.ApplicationTitle x) => x.Name[0] != 0).NameString.ToString();
				}
				if (applicationControlProperties.Value.PresenceGroupId != 0L)
				{
					programId = applicationControlProperties.Value.PresenceGroupId;
				}
				else if (applicationControlProperties.Value.SaveDataOwnerId != 0L)
				{
					programId = applicationControlProperties.Value.SaveDataOwnerId;
				}
				else if (applicationControlProperties.Value.AddOnContentBaseId != 0L)
				{
					programId = applicationControlProperties.Value.AddOnContentBaseId - 4096;
				}
			}
		}
		else
		{
			executable = new NsoExecutable(new LocalStorage(name, FileAccess.Read), text);
		}
		GraphicsConfig.TitleId = null;
		_device.Gpu.HostInitalized.Set();
		ProcessResult processResult = ProcessLoaderHelper.LoadNsos(_device, _device.System.KernelContext, fileSystem.GetNpdm(), applicationControlProperties, false, null, true, text, programId, 0, null, (IExecutable)executable);
		if (processResult.ProcessId != 0L)
		{
			if (stream2 != null)
			{
				_device.Configuration.VirtualFileSystem.SetRomFs(processResult.ProcessId, stream2);
			}
			if (_processesByPid.TryAdd(processResult.ProcessId, processResult) && processResult.Start(_device))
			{
				_latestPid = processResult.ProcessId;
				return true;
			}
		}
		return false;
	}
}
