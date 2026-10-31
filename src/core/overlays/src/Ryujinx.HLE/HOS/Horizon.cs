using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using LibHac.Common;
using LibHac.Common.Keys;
using LibHac.Fs;
using LibHac.Fs.Shim;
using LibHac.FsSystem;
using LibHac.Tools.FsSystem;
using Ryujinx.Cpu;
using Ryujinx.HLE.FileSystem;
using Ryujinx.HLE.HOS.Kernel;
using Ryujinx.HLE.HOS.Kernel.Memory;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.HLE.HOS.Services;
using Ryujinx.HLE.HOS.Services.Account.Acc;
using Ryujinx.HLE.HOS.Services.Am.AppletAE.AllSystemAppletProxiesService.SystemAppletProxy;
using Ryujinx.HLE.HOS.Services.Apm;
using Ryujinx.HLE.HOS.Services.Caps;
using Ryujinx.HLE.HOS.Services.Mii;
using Ryujinx.HLE.HOS.Services.Nfc.AmiiboDecryption;
using Ryujinx.HLE.HOS.Services.Nfc.Nfp;
using Ryujinx.HLE.HOS.Services.Nfc.Nfp.NfpManager;
using Ryujinx.HLE.HOS.Services.Nv;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostCtrl;
using Ryujinx.HLE.HOS.Services.Pcv.Bpc;
using Ryujinx.HLE.HOS.Services.Sdb.Pl;
using Ryujinx.HLE.HOS.Services.Settings;
using Ryujinx.HLE.HOS.Services.Sm;
using Ryujinx.HLE.HOS.Services.SurfaceFlinger;
using Ryujinx.HLE.HOS.Services.Time;
using Ryujinx.HLE.HOS.Services.Time.Clock;
using Ryujinx.HLE.HOS.SystemState;
using Ryujinx.HLE.Loaders.Executables;
using Ryujinx.HLE.Loaders.Processes;
using Ryujinx.Horizon;

namespace Ryujinx.HLE.HOS;

public class Horizon : IDisposable
{
	internal const int HidSize = 262144;

	internal const int FontSize = 17825792;

	internal const int IirsSize = 32768;

	internal const int TimeSize = 4096;

	internal const int AppletCaptureBufferSize = 3686400;

	private bool _isDisposed;

	internal KernelContext KernelContext { get; }

	internal Switch Device { get; private set; }

	internal ITickSource TickSource { get; }

	internal SurfaceFlinger SurfaceFlinger { get; private set; }

	public SystemStateMgr State { get; private set; }

	internal PerformanceState PerformanceState { get; private set; }

	internal AppletStateMgr AppletState { get; private set; }

	internal List<NfpDevice> NfpDevices { get; private set; }

	internal SmRegistry SmRegistry { get; private set; }

	internal ServerBase SmServer { get; private set; }

	internal ServerBase BsdServer { get; private set; }

	internal ServerBase FsServer { get; private set; }

	internal ServerBase HidServer { get; private set; }

	internal ServerBase NvDrvServer { get; private set; }

	internal ServerBase TimeServer { get; private set; }

	internal ServerBase ViServer { get; private set; }

	internal ServerBase ViServerM { get; private set; }

	internal ServerBase ViServerS { get; private set; }

	internal ServerBase LdnServer { get; private set; }

	internal KSharedMemory HidSharedMem { get; private set; }

	internal KSharedMemory FontSharedMem { get; private set; }

	internal KSharedMemory IirsSharedMem { get; private set; }

	internal KTransferMemory AppletCaptureBufferTransfer { get; private set; }

	internal SharedFontManager SharedFontManager { get; private set; }

	internal AccountManager AccountManager { get; private set; }

	internal ContentManager ContentManager { get; private set; }

	internal CaptureManager CaptureManager { get; private set; }

	internal KEvent VsyncEvent { get; private set; }

	internal KEvent DisplayResolutionChangeEvent { get; private set; }

	public KeySet KeySet => Device.FileSystem.KeySet;

	public bool EnablePtc { get; set; }

	public IntegrityCheckLevel FsIntegrityCheckLevel { get; set; }

	public int GlobalAccessLogMode { get; set; }

	internal SharedMemoryStorage HidStorage { get; private set; }

	internal NvHostSyncpt HostSyncpoint { get; private set; }

	internal LibHacHorizonManager LibHacHorizonManager { get; private set; }

	internal ServiceTable ServiceTable { get; private set; }

	public bool IsPaused { get; private set; }

	public Horizon(Switch device)
	{
		TickSource = new TickSource(19200000uL);
		KernelContext = new KernelContext(TickSource, device, device.Memory, device.Configuration.MemoryConfiguration.ToKernelMemorySize(), device.Configuration.MemoryConfiguration.ToKernelMemoryArrange());
		Device = device;
		State = new SystemStateMgr();
		PerformanceState = new PerformanceState();
		NfpDevices = new List<NfpDevice>();
		KMemoryRegionManager obj = KernelContext.MemoryManager.MemoryRegions[3];
		ulong address = obj.Address;
		ulong address2 = obj.Address + 262144;
		ulong address3 = obj.Address + 262144 + 17825792;
		ulong address4 = obj.Address + 262144 + 17825792 + 32768;
		ulong address5 = obj.Address + 262144 + 17825792 + 32768 + 4096;
		KPageList kPageList = new KPageList();
		KPageList kPageList2 = new KPageList();
		KPageList kPageList3 = new KPageList();
		KPageList kPageList4 = new KPageList();
		KPageList kPageList5 = new KPageList();
		kPageList.AddRange(address, 64uL);
		kPageList2.AddRange(address2, 4352uL);
		kPageList3.AddRange(address3, 8uL);
		kPageList4.AddRange(address4, 1uL);
		kPageList5.AddRange(address5, 900uL);
		SharedMemoryStorage sharedMemoryStorage = new SharedMemoryStorage(KernelContext, kPageList);
		SharedMemoryStorage storage = new SharedMemoryStorage(KernelContext, kPageList2);
		SharedMemoryStorage storage2 = new SharedMemoryStorage(KernelContext, kPageList3);
		SharedMemoryStorage sharedMemoryStorage2 = new SharedMemoryStorage(KernelContext, kPageList4);
		SharedMemoryStorage storage3 = new SharedMemoryStorage(KernelContext, kPageList5);
		HidStorage = sharedMemoryStorage;
		HidSharedMem = new KSharedMemory(KernelContext, sharedMemoryStorage, 0uL, KMemoryPermission.None, KMemoryPermission.Read);
		FontSharedMem = new KSharedMemory(KernelContext, storage, 0uL, KMemoryPermission.None, KMemoryPermission.Read);
		IirsSharedMem = new KSharedMemory(KernelContext, storage2, 0uL, KMemoryPermission.None, KMemoryPermission.Read);
		KSharedMemory sharedMemory = new KSharedMemory(KernelContext, sharedMemoryStorage2, 0uL, KMemoryPermission.None, KMemoryPermission.Read);
		TimeManager.Instance.Initialize(device, this, sharedMemory, sharedMemoryStorage2, 4096);
		AppletCaptureBufferTransfer = new KTransferMemory(KernelContext, storage3);
		AppletState = new AppletStateMgr(this);
		AppletState.SetFocus(isFocused: true);
		VsyncEvent = new KEvent(KernelContext);
		DisplayResolutionChangeEvent = new KEvent(KernelContext);
		SharedFontManager = new SharedFontManager(device, storage);
		AccountManager = device.Configuration.AccountManager;
		ContentManager = device.Configuration.ContentManager;
		CaptureManager = new CaptureManager(device);
		LibHacHorizonManager = device.Configuration.LibHacHorizonManager;
		UInt128 clockSourceId = new UInt128(3936201629770877889uL, 1587776953269826180uL);
		IRtcManager.GetExternalRtcValue(out var rtcValue);
		TimeSpanType timeSpanType = TimeSpanType.FromSeconds((long)rtcValue);
		TimeSpanType timeSpanType2 = TimeSpanType.FromSeconds(device.Configuration.SystemTimeOffset);
		TimeSpanType timeSpanType3 = new TimeSpanType(timeSpanType.NanoSeconds + timeSpanType2.NanoSeconds);
		if (timeSpanType.IsDaylightSavingTime() && !timeSpanType3.IsDaylightSavingTime())
		{
			timeSpanType2 = timeSpanType2.AddSeconds(3600L);
		}
		else if (!timeSpanType.IsDaylightSavingTime() && timeSpanType3.IsDaylightSavingTime())
		{
			timeSpanType2 = timeSpanType2.AddSeconds(-3600L);
		}
		timeSpanType = new TimeSpanType(timeSpanType.NanoSeconds + timeSpanType2.NanoSeconds);
		TimeManager.Instance.SetupStandardSteadyClock(TickSource, clockSourceId, TimeSpanType.Zero, TimeSpanType.Zero, TimeSpanType.Zero, isRtcResetDetected: false);
		TimeManager.Instance.SetupStandardLocalSystemClock(TickSource, default, timeSpanType.ToSeconds());
		TimeManager.Instance.StandardLocalSystemClock.GetClockContext(TickSource, out var context);
		if (NxSettings.Settings.TryGetValue("time!standard_network_clock_sufficient_accuracy_minutes", out var value))
		{
			TimeSpanType sufficientAccuracy = new TimeSpanType((int)value * 60000000000L);
			TimeManager.Instance.SetupStandardNetworkSystemClock(context, sufficientAccuracy);
		}
		TimeManager.Instance.SetupStandardUserSystemClock(TickSource, isAutomaticCorrectionEnabled: true, context.SteadyTimePoint);
		TimeManager.Instance.SetupEphemeralNetworkSystemClock();
		DatabaseImpl.Instance.InitializeDatabase(TickSource, LibHacHorizonManager.SdbClient);
		HostSyncpoint = new NvHostSyncpt(device);
		SurfaceFlinger = new SurfaceFlinger(device);
	}

	public void InitializeServices()
	{
		SmRegistry = new SmRegistry();
		SmServer = new ServerBase(KernelContext, "SmServer", () => new IUserInterface(KernelContext, SmRegistry));
		SmServer.InitDone.WaitOne();
		BsdServer = new ServerBase(KernelContext, "BsdServer");
		FsServer = new ServerBase(KernelContext, "FsServer");
		HidServer = new ServerBase(KernelContext, "HidServer");
		NvDrvServer = new ServerBase(KernelContext, "NvservicesServer");
		TimeServer = new ServerBase(KernelContext, "TimeServer");
		ViServer = new ServerBase(KernelContext, "ViServerU");
		ViServerM = new ServerBase(KernelContext, "ViServerM");
		ViServerS = new ServerBase(KernelContext, "ViServerS");
		LdnServer = new ServerBase(KernelContext, "LdnServer");
		StartNewServices();
	}

	private void StartNewServices()
	{
		HorizonFsClient fsClient = new HorizonFsClient(this);
		ServiceTable = new ServiceTable();
		foreach (ServiceEntry service in ServiceTable.GetServices(new HorizonOptions(Device.Configuration.IgnoreMissingServices, LibHacHorizonManager.BcatClient, fsClient, AccountManager, Device.AudioDeviceDriver, TickSource)))
		{
			ProcessCreationInfo creationInfo = new ProcessCreationInfo("Service", 1, 0uL, 134217728uL, 1, ProcessCreationFlags.PoolPartitionShift | ProcessCreationFlags.EnableAslr | ProcessCreationFlags.PoolPartitionSystem, 0, 0);
			uint[] array = new uint[6] { 0u, 536870863u, 545259503u, 1205863951u, 4767743u, 16809983u };
			array[0] = (uint)((KScheduler.CpuCoresCount - 1 << 24) + (KScheduler.CpuCoresCount - 1 << 16) + 25591);
			uint[] array2 = array;
			KernelStatic.StartInitialProcess(KernelContext, creationInfo, array2, 44, () =>
			{
				service.Start(KernelContext.Syscall, KernelStatic.GetCurrentProcess().CpuMemory, KernelStatic.GetCurrentThread().ThreadContext);
			});
		}
	}

	public bool LoadKip(string kipPath)
	{
		using SharedRef<IStorage> inStorage = new SharedRef<IStorage>(new LocalStorage(kipPath, FileAccess.Read));
		return ProcessLoaderHelper.LoadKip(KernelContext, new KipExecutable(in inStorage));
	}

	public void ChangeDockedModeState(bool newState)
	{
		if (newState != State.DockedMode)
		{
			State.DockedMode = newState;
			PerformanceState.PerformanceMode = (State.DockedMode ? PerformanceMode.Boost : PerformanceMode.Default);
			AppletState.Messages.Enqueue(AppletMessage.OperationModeChanged);
			AppletState.Messages.Enqueue(AppletMessage.PerformanceModeChanged);
			AppletState.MessageEvent.ReadableEvent.Signal();
			SignalDisplayResolutionChange();
			Device.Configuration.RefreshInputConfig?.Invoke();
		}
	}

	public void ReturnFocus()
	{
		AppletState.SetFocus(isFocused: true);
	}

	public void SimulateWakeUpMessage()
	{
		AppletState.Messages.Enqueue(AppletMessage.Resume);
		AppletState.MessageEvent.ReadableEvent.Signal();
	}

	public void ScanAmiibo(int nfpDeviceId, string amiiboId, bool useRandomUuid)
	{
		if (VirtualAmiibo.ApplicationBytes.Length != 0)
		{
			VirtualAmiibo.ApplicationBytes = Array.Empty<byte>();
			VirtualAmiibo.InputBin = string.Empty;
		}
		if (NfpDevices[nfpDeviceId].State == NfpDeviceState.SearchingForTag)
		{
			NfpDevices[nfpDeviceId].State = NfpDeviceState.TagFound;
			NfpDevices[nfpDeviceId].AmiiboId = amiiboId;
			NfpDevices[nfpDeviceId].UseRandomUuid = useRandomUuid;
		}
	}

	public void ScanAmiiboFromBin(string path)
	{
		VirtualAmiibo.InputBin = path;
		if (VirtualAmiibo.ApplicationBytes.Length != 0)
		{
			VirtualAmiibo.ApplicationBytes = Array.Empty<byte>();
		}
		VirtualAmiiboFile virtualAmiiboFile = AmiiboBinReader.ReadBinFile(File.ReadAllBytes(path));
		if (SearchingForAmiibo(out var nfpDeviceId))
		{
			NfpDevices[nfpDeviceId].State = NfpDeviceState.TagFound;
			NfpDevices[nfpDeviceId].AmiiboId = virtualAmiiboFile.AmiiboId;
			NfpDevices[nfpDeviceId].UseRandomUuid = false;
		}
	}

	public bool SearchingForAmiibo(out int nfpDeviceId)
	{
		nfpDeviceId = 0;
		for (int i = 0; i < NfpDevices.Count; i++)
		{
			if (NfpDevices[i].State == NfpDeviceState.SearchingForTag)
			{
				nfpDeviceId = i;
				return true;
			}
		}
		return false;
	}

	public void SignalDisplayResolutionChange()
	{
		DisplayResolutionChangeEvent.ReadableEvent.Signal();
	}

	public void SignalVsync()
	{
		VsyncEvent.ReadableEvent.Signal();
	}

	public void Dispose()
	{
		GC.SuppressFinalize(this);
		Dispose(disposing: true);
	}

	protected virtual void Dispose(bool disposing)
	{
		if (!(!_isDisposed & disposing))
		{
			return;
		}
		_isDisposed = true;
		if (IsPaused)
		{
			Device.AudioDeviceDriver.GetPauseEvent().Set();
			TickSource.Resume();
		}
		List<KProcess> terminatedProcesses = new List<KProcess>();
		KProcess owner = new KProcess(KernelContext);
		KThread kThread = new KThread(KernelContext);
		kThread.Initialize(0uL, 0uL, 0uL, 3, 0, owner, ThreadType.Kernel, () =>
		{
			lock (KernelContext.Processes)
			{
				foreach (KProcess item in KernelContext.Processes.Values.Where((KProcess x) => x.IsApplication))
				{
					item.Terminate();
					terminatedProcesses.Add(item);
				}
				SurfaceFlinger.Dispose();
				foreach (KProcess item2 in KernelContext.Processes.Values.Where((KProcess x) => !x.IsApplication))
				{
					item2.Terminate();
					terminatedProcesses.Add(item2);
				}
			}
			KernelStatic.GetCurrentThread().Exit();
		});
		kThread.Start();
		while (kThread.HostThread.ThreadState == ThreadState.Unstarted)
		{
			Thread.Sleep(10);
		}
		kThread.HostThread.Join();
		INvDrvServices.Destroy();
		KernelContext.WaitForHostThreads(TimeSpan.FromSeconds(20L));
		foreach (KProcess item3 in terminatedProcesses)
		{
			item3.DecrementReferenceCount();
		}
		KernelContext.Processes.Clear();
		if (LibHacHorizonManager.ApplicationClient != null)
		{
			LibHacHorizonManager.PmClient.Fs.UnregisterProgram(LibHacHorizonManager.ApplicationClient.Os.GetCurrentProcessId().Value).ThrowIfFailure();
		}
		KernelContext.Dispose();
	}

	public void TogglePauseEmulation(bool pause)
	{
		lock (KernelContext.Processes)
		{
			foreach (KProcess value in KernelContext.Processes.Values)
			{
				if (value.IsApplication)
				{
					value.SetActivity(pause);
				}
			}
			if (pause && !IsPaused)
			{
				Device.AudioDeviceDriver.GetPauseEvent().Reset();
				TickSource.Suspend();
			}
			else if (!pause && IsPaused)
			{
				Device.AudioDeviceDriver.GetPauseEvent().Set();
				TickSource.Resume();
			}
		}
		IsPaused = pause;
	}
}
