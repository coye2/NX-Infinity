using System;
using System.Linq;
using System.Runtime.InteropServices;
using LibHac;
using LibHac.Account;
using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.Fs.Shim;
using LibHac.Loader;
using LibHac.Ncm;
using LibHac.Ns;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;
using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Ryujinx.Cpu.Nce;
using Ryujinx.HLE.HOS;
using Ryujinx.HLE.HOS.Kernel;
using Ryujinx.HLE.HOS.Kernel.Common;
using Ryujinx.HLE.HOS.Kernel.Memory;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.HLE.Loaders.Executables;
using Ryujinx.HLE.Loaders.Processes.Extensions;
using Ryujinx.Horizon.Common;
using Ryujinx.Horizon.Sdk.Arp;
using Ryujinx.Horizon.Sdk.Ncm;
using Ryujinx.Horizon.Sdk.Ns;

namespace Ryujinx.HLE.Loaders.Processes;

internal static class ProcessLoaderHelper
{
	private const ulong CodeStartOffset = 5242880uL;

	public static LibHac.Result RegisterProgramMapInfo(Switch device, IFileSystem partitionFileSystem)
	{
		ulong num = 0uL;
		int num2 = 0;
		Span<bool> span = stackalloc bool[16];
		foreach (DirectoryEntryEx item in partitionFileSystem.EnumerateEntries("/", "*.nca"))
		{
			Nca nca = partitionFileSystem.GetNca(device.FileSystem.KeySet, item.FullPath);
			if (nca.IsProgram())
			{
				ulong programIdBase = nca.GetProgramIdBase();
				if (num == 0L && programIdBase != 0L)
				{
					num = programIdBase;
				}
				if (num != programIdBase)
				{
					num2 = 1;
					break;
				}
				span[nca.GetProgramIndex()] = true;
			}
		}
		if (num2 == 0)
		{
			for (int i = 0; i < span.Length && span[i]; i++)
			{
				num2++;
			}
		}
		if (num2 <= 0)
		{
			return LibHac.Result.Success;
		}
		Span<ProgramIndexMapInfo> span2 = stackalloc ProgramIndexMapInfo[16];
		for (int j = 0; j < num2; j++)
		{
			span2[j].ProgramId = new ProgramId(num + (uint)j);
			span2[j].MainProgramId = new LibHac.Ncm.ApplicationId(num);
			span2[j].ProgramIndex = (byte)j;
		}
		return device.System.LibHacHorizonManager.NsClient.Fs.RegisterProgramIndexMapInfo(span2.Slice(0, num2));
	}

	public static LibHac.Result EnsureSaveData(Switch device, LibHac.Ncm.ApplicationId applicationId, BlitStruct<LibHac.Ns.ApplicationControlProperty> applicationControlProperty)
	{
		Logger.Info?.Print(LogClass.Application, "Ensuring required savedata exists.", "EnsureSaveData");
		ref LibHac.Ns.ApplicationControlProperty value = ref applicationControlProperty.Value;
		if (applicationControlProperty.ByteSpan.IsZeros())
		{
			value = ref new BlitStruct<LibHac.Ns.ApplicationControlProperty>(1).Value;
			value.UserAccountSaveDataSize = 16384L;
			value.UserAccountSaveDataJournalSize = 16384L;
			value.SaveDataOwnerId = applicationId.Value;
			Logger.Warning?.Print(LogClass.Application, "No control file was found for this game. Using a dummy one instead. This may cause inaccuracies in some games.", "EnsureSaveData");
		}
		LibHac.Result result = device.System.LibHacHorizonManager.RyujinxClient.Fs.EnsureApplicationCacheStorage(out var outRequiredSize, out var _, applicationId, in value);
		if (result.IsFailure())
		{
			Logger.Error?.Print(LogClass.Application, "Error calling EnsureApplicationCacheStorage. Result code " + result.ToStringWithName(), "EnsureSaveData");
			return result;
		}
		Uid user = device.System.AccountManager.LastOpenedUser.UserId.ToLibHacUid();
		result = device.System.LibHacHorizonManager.RyujinxClient.Fs.EnsureApplicationSaveData(out outRequiredSize, applicationId, in value, in user);
		if (result.IsFailure())
		{
			Logger.Error?.Print(LogClass.Application, "Error calling EnsureApplicationSaveData. Result code " + result.ToStringWithName(), "EnsureSaveData");
		}
		return result;
	}

	public static bool LoadKip(KernelContext context, KipExecutable kip)
	{
		uint num = kip.DataOffset + (uint)kip.Data.Length;
		if (kip.BssSize != 0)
		{
			num = kip.BssOffset + kip.BssSize;
		}
		uint num2 = BitUtils.AlignUp(kip.TextOffset + num, 4096u);
		int num3 = (int)(num2 / 4096);
		ulong num4 = (ulong)(kip.Is64BitAddressSpace ? 134217728 : 2097152);
		ulong codeAddress = num4 + kip.TextOffset;
		ProcessCreationFlags processCreationFlags = ProcessCreationFlags.AddressSpace32Bit;
		processCreationFlags |= ProcessCreationFlags.EnableAslr;
		if (kip.Is64BitAddressSpace)
		{
			processCreationFlags |= ProcessCreationFlags.AddressSpace64Bit;
		}
		if (kip.Is64Bit)
		{
			processCreationFlags |= ProcessCreationFlags.Is64Bit;
		}
		ProcessCreationInfo creationInfo = new ProcessCreationInfo(kip.Name, kip.Version, kip.ProgramId, codeAddress, num3, processCreationFlags, 0, 0);
		MemoryRegion memoryRegion = (kip.UsesSecureMemory ? MemoryRegion.Service : MemoryRegion.Application);
		Ryujinx.Horizon.Common.Result result = context.MemoryManager.MemoryRegions[(int)memoryRegion].AllocatePages(out var pageList, (ulong)num3);
		if (result != Ryujinx.Horizon.Common.Result.Success)
		{
			Logger.Error?.Print(LogClass.Loader, $"Process initialization returned error \"{result}\".", "LoadKip");
			return false;
		}
		KProcess kProcess = new KProcess(context);
		ArmProcessContextFactory contextFactory = new ArmProcessContextFactory(context.Device.System.TickSource, context.Device.Gpu, string.Empty, string.Empty, diskCacheEnabled: false, null, codeAddress, num2);
		result = kProcess.InitializeKip(creationInfo, kip.Capabilities, pageList, context.ResourceLimit, memoryRegion, contextFactory);
		if (result != Ryujinx.Horizon.Common.Result.Success)
		{
			Logger.Error?.Print(LogClass.Loader, $"Process initialization returned error \"{result}\".", "LoadKip");
			return false;
		}
		result = LoadIntoMemory(kProcess, kip, num4);
		if (result != Ryujinx.Horizon.Common.Result.Success)
		{
			Logger.Error?.Print(LogClass.Loader, $"Process initialization returned error \"{result}\".", "LoadKip");
			return false;
		}
		kProcess.DefaultCpuCore = kip.IdealCoreId;
		result = kProcess.Start(kip.Priority, (ulong)kip.StackSize);
		if (result != Ryujinx.Horizon.Common.Result.Success)
		{
			Logger.Error?.Print(LogClass.Loader, $"Process start returned error \"{result}\".", "LoadKip");
			return false;
		}
		context.Processes.TryAdd(kProcess.Pid, kProcess);
		return true;
	}

	public static ProcessResult LoadNsos(Switch device, KernelContext context, MetaLoader metaLoader, BlitStruct<LibHac.Ns.ApplicationControlProperty> applicationControlProperties, bool diskCacheEnabled, string diskCacheSelector, bool allowCodeMemoryForJit, string name, ulong programId, byte programIndex, byte[] arguments = null, params ReadOnlySpan<IExecutable> executables)
	{
		context.Device.System.ServiceTable.WaitServicesReady();
		LibHac.Result npdm = metaLoader.GetNpdm(out var npdm2);
		if (npdm.IsFailure())
		{
			Logger.Error?.Print(LogClass.Loader, "Process initialization failed getting npdm. Result Code " + npdm.ToStringWithName(), "LoadNsos");
			return ProcessResult.Failed;
		}
		ref readonly Meta meta = ref npdm2.Meta;
		uint num = 0u;
		ulong num2 = (ulong)(((meta.Flags & 1) != 0) ? 134217728 : 2097152) + 5242880uL;
		ulong num3 = 0uL;
		string[] array = new string[executables.Length];
		string text = null;
		for (int i = 0; i < executables.Length; i++)
		{
			int num4 = i;
			IExecutable executable = executables[i];
			string text2;
			if (executable is NsoExecutable nsoExecutable)
			{
				text2 = Convert.ToHexString(nsoExecutable.BuildId);
			}
			else
			{
				text2 = ((!(executable is NroExecutable nroExecutable)) ? string.Empty : Convert.ToHexString(nroExecutable.Header.BuildId));
			}
			array[num4] = text2.ToUpper();
			if (executables[i] is NsoExecutable { Name: "main" } && array[i].Length == 64 && array[i].Any((char c) => c != '0'))
			{
				text = array[i];
			}
		}
		NceCpuCodePatch[] array2 = new NceCpuCodePatch[executables.Length];
		ulong[] array3 = new ulong[executables.Length];
		for (int num5 = 0; num5 < executables.Length; num5++)
		{
			IExecutable executable2 = executables[num5];
			int num6 = (int)executable2.TextOffset + executable2.Text.Length;
			uint num7 = executable2.RoOffset + (uint)executable2.Ro.Length;
			uint num8 = (uint)((int)executable2.DataOffset + executable2.Data.Length) + executable2.BssSize;
			uint num9 = (uint)num6;
			if (num9 < num7)
			{
				num9 = num7;
			}
			if (num9 < num8)
			{
				num9 = num8;
			}
			num9 = BitUtils.AlignUp(num9, 4096u);
			bool for64Bit = ((ProcessCreationFlags)meta.Flags).HasFlag(ProcessCreationFlags.Is64Bit);
			NceCpuCodePatch nceCpuCodePatch = (array2[num5] = ArmProcessContextFactory.CreateCodePatchForNce(context, for64Bit, executable2.Text));
			if (nceCpuCodePatch != null)
			{
				num3 += nceCpuCodePatch.Size;
			}
			array3[num5] = num2 + num3;
			num3 += num9;
			if (arguments != null && num == 0)
			{
				num = (uint)BitUtils.AlignDown(arguments.Length * 2 + 36872 - 1, 4096);
				num3 += num;
			}
		}
		int codePagesCount = (int)(num3 / 4096);
		int systemResourcePagesCount = (int)(meta.SystemResourceSize / 4096);
		ProcessCreationInfo creationInfo = new ProcessCreationInfo(name, (int)meta.Version, programId, num2, codePagesCount, (ProcessCreationFlags)(meta.Flags | 0x40), 0, systemResourcePagesCount);
		context.Device.System.LibHacHorizonManager.InitializeApplicationClient(new ProgramId(programId), in npdm2);
		KResourceLimit kResourceLimit = new KResourceLimit(context);
		long size = (long)context.MemoryManager.MemoryRegions[0].Size;
		Ryujinx.Horizon.Common.Result result = kResourceLimit.SetLimitValue(LimitableResource.Memory, size);
		if (result.IsSuccess)
		{
			result = kResourceLimit.SetLimitValue(LimitableResource.Thread, 608L);
		}
		if (result.IsSuccess)
		{
			result = kResourceLimit.SetLimitValue(LimitableResource.Event, 700L);
		}
		if (result.IsSuccess)
		{
			result = kResourceLimit.SetLimitValue(LimitableResource.TransferMemory, 128L);
		}
		if (result.IsSuccess)
		{
			result = kResourceLimit.SetLimitValue(LimitableResource.Session, 894L);
		}
		if (result != Ryujinx.Horizon.Common.Result.Success)
		{
			Logger.Error?.Print(LogClass.Loader, "Process initialization failed setting resource limit values.", "LoadNsos");
			return ProcessResult.Failed;
		}
		KProcess process = new KProcess(context, allowCodeMemoryForJit);
		MemoryRegion memoryRegion = (MemoryRegion)((npdm2.Acid.Flags >> 2) & 0xF);
		if (memoryRegion > MemoryRegion.NvServices)
		{
			Logger.Error?.Print(LogClass.Loader, "Process initialization failed due to invalid ACID flags.", "LoadNsos");
			return ProcessResult.Failed;
		}
		ArmProcessContextFactory armProcessContextFactory = new ArmProcessContextFactory(displayVersion: (metaLoader.GetProgramId() <= 72057594037960703L) ? (device.System.ContentManager.GetCurrentFirmwareVersion()?.VersionString ?? string.Empty) : applicationControlProperties.Value.DisplayVersionString.ToString(), tickSource: context.Device.System.TickSource, gpu: context.Device.Gpu, titleIdText: $"{programId:x16}", diskCacheEnabled: diskCacheEnabled, diskCacheSelector: diskCacheSelector, codeAddress: num2, codeSize: num3);
		result = process.Initialize(creationInfo, MemoryMarshal.Cast<byte, uint>(npdm2.KernelCapabilityData), kResourceLimit, memoryRegion, armProcessContextFactory, null, array2[0]?.Size ?? 0);
		if (result != Ryujinx.Horizon.Common.Result.Success)
		{
			Logger.Error?.Print(LogClass.Loader, $"Process initialization returned error \"{result}\".", "LoadNsos");
			return ProcessResult.Failed;
		}
		for (int num10 = 0; num10 < executables.Length; num10++)
		{
			ulong num11 = process.Context.ReservedSize + array3[num10];
			Logger.Info?.Print(LogClass.Loader, $"Loading image {num10} at 0x{num11:x16}...", "LoadNsos");
			result = LoadIntoMemory(process, executables[num10], num11, array2[num10]);
			if (result != Ryujinx.Horizon.Common.Result.Success)
			{
				Logger.Error?.Print(LogClass.Loader, $"Process initialization returned error \"{result}\".", "LoadNsos");
				return ProcessResult.Failed;
			}
		}
		process.DefaultCpuCore = meta.DefaultCpuId;
		context.Processes.TryAdd(process.Pid, process);
		ProcessTamperInfo tamperInfo = new ProcessTamperInfo(process, array, array3, process.MemoryManager.HeapRegionStart, process.MemoryManager.AliasRegionStart, process.MemoryManager.CodeRegionStart);
		device.Configuration.VirtualFileSystem.ModLoader.LoadCheats(programId, tamperInfo, device.TamperMachine);
		ProcessResult processResult = new ProcessResult(metaLoader, applicationControlProperties, diskCacheEnabled, allowCodeMemoryForJit, armProcessContextFactory.DiskCacheLoadState, process.Pid, meta.MainThreadPriority, meta.MainThreadStackSize, device.System.State.DesiredTitleLanguage, text);
		if (text != null && programId > 72057594037960703L)
		{
			int num12 = 0;
			for (int num13 = 0; num13 < executables.Length; num13++)
			{
				if (executables[num13] is NsoExecutable { Name: "main" })
				{
					num12++;
				}
			}
			if (num12 == 1)
			{
				for (int num14 = 0; num14 < executables.Length; num14++)
				{
					if (!(executables[num14] is NsoExecutable { Name: "main" } nsoExecutable4))
					{
						continue;
					}
					ulong mainAddress = process.Context.ReservedSize + array3[num14];
					device.FramePolicy.BindMainModule(process.Pid, process.TitleId.ToString("X16"), text, processResult.DisplayVersion ?? string.Empty, nsoExecutable4.TextOffset, (ulong)nsoExecutable4.Text.Length, (ulong offset, int length) =>
					{
						ulong va = checked(mainAddress + offset);
						if (!process.CpuMemory.IsMapped(va) || !process.CpuMemory.IsRangeMapped(va, (ulong)length))
						{
							throw new InvalidOperationException("The bound main text is no longer mapped.");
						}
						byte[] array4 = new byte[length];
						process.CpuMemory.Read(va, array4);
						return array4;
					}, (ulong offset, byte[] bytes) =>
					{
						process.CpuMemory.Write(checked(mainAddress + offset), (ReadOnlySpan<byte>)bytes.AsSpan());
					}, (ulong offset, ulong length) =>
					{
						process.Context.InvalidateCacheRegion(checked(mainAddress + offset), length);
					}, () => device.System.IsPaused && process.IsPaused, () => device.FileSystem.ModLoader.HasModsForPatchGuard, RuntimeInformation.ProcessArchitecture == Architecture.X64 && processResult.Is64Bit);
				}
			}
		}
		device.System.ServiceTable.ArpWriter.AcquireRegistrar(out var registrar);
		registrar.SetApplicationControlProperty(in MemoryMarshal.Cast<byte, Ryujinx.Horizon.Sdk.Ns.ApplicationControlProperty>(applicationControlProperties.ByteSpan)[0]);
		registrar.SetApplicationLaunchProperty(new ApplicationLaunchProperty
		{
			ApplicationId = new Ryujinx.Horizon.Sdk.Ncm.ApplicationId(programId),
			Version = 0u,
			Storage = Ryujinx.Horizon.Sdk.Ncm.StorageId.BuiltInUser,
			PatchStorage = Ryujinx.Horizon.Sdk.Ncm.StorageId.None,
			ApplicationKind = ApplicationKind.Application
		});
		device.System.ServiceTable.ArpReader.GetApplicationInstanceId(out var applicationInstanceId, process.Pid);
		device.System.ServiceTable.ArpWriter.AcquireApplicationProcessPropertyUpdater(out var updater, applicationInstanceId);
		updater.SetApplicationProcessProperty(process.Pid, new ApplicationProcessProperty
		{
			ProgramIndex = programIndex
		});
		return processResult;
	}

	private static Ryujinx.Horizon.Common.Result LoadIntoMemory(KProcess process, IExecutable image, ulong baseAddress, NceCpuCodePatch codePatch = null)
	{
		ulong num = baseAddress + image.TextOffset;
		ulong num2 = baseAddress + image.RoOffset;
		ulong num3 = baseAddress + image.DataOffset;
		ulong num4 = baseAddress + image.BssOffset;
		ulong num5 = num3 + (ulong)image.Data.Length;
		if (image.BssSize != 0)
		{
			num5 = num4 + image.BssSize;
		}
		process.CpuMemory.Write(num, (ReadOnlySpan<byte>)image.Text);
		process.CpuMemory.Write(num2, (ReadOnlySpan<byte>)image.Ro);
		process.CpuMemory.Write(num3, (ReadOnlySpan<byte>)image.Data);
		process.CpuMemory.Fill(num4, image.BssSize, 0);
		codePatch?.Write(process.CpuMemory, baseAddress - codePatch.Size, num);
		Ryujinx.Horizon.Common.Result result = SetProcessMemoryPermission(num, (ulong)image.Text.Length, KMemoryPermission.ReadAndExecute);
		if (result != Ryujinx.Horizon.Common.Result.Success)
		{
			return result;
		}
		result = SetProcessMemoryPermission(num2, (ulong)image.Ro.Length, KMemoryPermission.Read);
		if (result != Ryujinx.Horizon.Common.Result.Success)
		{
			return result;
		}
		return SetProcessMemoryPermission(num3, num5 - num3, KMemoryPermission.ReadAndWrite);
		Ryujinx.Horizon.Common.Result SetProcessMemoryPermission(ulong address, ulong size, KMemoryPermission permission)
		{
			if (size == 0L)
			{
				return Ryujinx.Horizon.Common.Result.Success;
			}
			size = BitUtils.AlignUp(size, 4096uL);
			return process.MemoryManager.SetProcessMemoryPermission(address, size, permission);
		}
	}
}
