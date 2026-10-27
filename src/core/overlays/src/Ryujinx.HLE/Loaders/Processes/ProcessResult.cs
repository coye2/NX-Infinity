using LibHac.Common;
using LibHac.Common.FixedArrays;
using LibHac.Loader;
using LibHac.Ns;
using Ryujinx.Common.Logging;
using Ryujinx.Cpu;
using Ryujinx.HLE.HOS.Kernel;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.HLE.HOS.SystemState;
using Ryujinx.HLE.Loaders.Processes.Extensions;
using Ryujinx.Horizon.Common;

namespace Ryujinx.HLE.Loaders.Processes;

public class ProcessResult
{
	private readonly byte _mainThreadPriority;

	private readonly uint _mainThreadStackSize;

	public readonly IDiskCacheLoadState DiskCacheLoadState;

	public readonly MetaLoader MetaLoader;

	public readonly ApplicationControlProperty ApplicationControlProperties;

	public readonly ulong ProcessId;

	public readonly string Name;

	public readonly string DisplayVersion;

	public readonly string MainBuildId;

	public readonly ulong ProgramId;

	public readonly string ProgramIdText;

	public readonly bool Is64Bit;

	public readonly bool DiskCacheEnabled;

	public readonly bool AllowCodeMemoryForJit;

	public static ProcessResult Failed => new ProcessResult(null, new BlitStruct<ApplicationControlProperty>(1), diskCacheEnabled: false, allowCodeMemoryForJit: false, null, 0uL, 0, 0u, TitleLanguage.AmericanEnglish);

	public ProcessResult(MetaLoader metaLoader, BlitStruct<ApplicationControlProperty> applicationControlProperties, bool diskCacheEnabled, bool allowCodeMemoryForJit, IDiskCacheLoadState diskCacheLoadState, ulong pid, byte mainThreadPriority, uint mainThreadStackSize, TitleLanguage titleLanguage, string mainBuildId = null)
	{
		_mainThreadPriority = mainThreadPriority;
		_mainThreadStackSize = mainThreadStackSize;
		DiskCacheLoadState = diskCacheLoadState;
		ProcessId = pid;
		MainBuildId = mainBuildId;
		MetaLoader = metaLoader;
		ApplicationControlProperties = applicationControlProperties.Value;
		if (metaLoader != null)
		{
			ulong programId = metaLoader.GetProgramId();
			Name = ApplicationControlProperties.Title[(int)titleLanguage].NameString.ToString();
			if (string.IsNullOrWhiteSpace(Name))
			{
				foreach (ApplicationControlProperty.ApplicationTitle applicationTitle in ApplicationControlProperties.Title)
				{
					if (applicationTitle.Name[0] == 0)
					{
						Name = applicationTitle.NameString.ToString();
					}
				}
			}
			DisplayVersion = ApplicationControlProperties.DisplayVersionString.ToString();
			ProgramId = programId;
			ProgramIdText = $"{programId:x16}";
			Is64Bit = metaLoader.IsProgram64Bit();
		}
		DiskCacheEnabled = diskCacheEnabled;
		AllowCodeMemoryForJit = allowCodeMemoryForJit;
	}

	public string GetKernelState(Switch device)
	{
		KernelContext kernelContext = device.System.KernelContext;
		kernelContext.CriticalSection.Enter();
		try
		{
			KProcess value;
			return kernelContext.Processes.TryGetValue(ProcessId, out value) ? value.State.ToString() : "Unavailable";
		}
		finally
		{
			kernelContext.CriticalSection.Leave();
		}
	}

	public bool Start(Switch device)
	{
		device.Configuration.ContentManager.LoadEntries(device);
		if (device.System.IsPaused)
		{
			device.System.KernelContext.Processes[ProcessId].SetActivity(pause: true);
		}
		Result result = device.System.KernelContext.Processes[ProcessId].Start(_mainThreadPriority, _mainThreadStackSize);
		if (result != Result.Success)
		{
			Logger.Error?.Print(LogClass.Loader, $"Process start returned error \"{result}\".", "Start");
			return false;
		}
		string value = ((ProgramId > 72057594037960703L) ? DisplayVersion : (device.System.ContentManager.GetCurrentFirmwareVersion()?.VersionString ?? "?"));
		Logger.Info?.Print(LogClass.Loader, $"Application Loaded: {Name} v{value} [{ProgramIdText}] [{(Is64Bit ? "64-bit" : "32-bit")}]", "Start");
		return true;
	}
}
