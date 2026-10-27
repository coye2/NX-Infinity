using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Ryujinx.Common;
using Ryujinx.Cpu;
using Ryujinx.Graphics.Device;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu.Engine.GPFifo;
using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.Graphics.Gpu.Shader;
using Ryujinx.Graphics.Gpu.Synchronization;

namespace Ryujinx.Graphics.Gpu;

public sealed class GpuContext : IDisposable
{
	private const int NsToTicksFractionNumerator = 384;

	private const int NsToTicksFractionDenominator = 625;

	internal Capabilities Capabilities;

	private Thread _gpuThread;

	private bool _pendingSync;

	private long _modifiedSequence;

	private readonly ulong _firstTimestamp;

	private readonly ManualResetEvent _gpuReadyEvent;

	public ManualResetEvent HostInitalized { get; }

	public IRenderer Renderer { get; }

	public GPFifoDevice GPFifo { get; }

	public SynchronizationManager Synchronization { get; }

	public Window Window { get; }

	internal int SequenceNumber { get; private set; }

	internal ulong SyncNumber { get; private set; }

	internal List<ISyncActionHandler> SyncActions { get; }

	internal List<ISyncActionHandler> SyncpointActions { get; }

	internal List<BufferMigration> BufferMigrations { get; }

	internal Queue<Action> DeferredActions { get; }

	internal ConcurrentDictionary<ulong, PhysicalMemory> PhysicalMemoryRegistry { get; }

	internal SupportBufferUpdater SupportBufferUpdater { get; }

	public event Action<ShaderCacheState, int, int> ShaderCacheStateChanged;

	public GpuContext(IRenderer renderer)
	{
		Renderer = renderer;
		GPFifo = new GPFifoDevice(this);
		Synchronization = new SynchronizationManager();
		Window = new Window(this);
		HostInitalized = new ManualResetEvent(initialState: false);
		_gpuReadyEvent = new ManualResetEvent(initialState: false);
		SyncActions = new List<ISyncActionHandler>();
		SyncpointActions = new List<ISyncActionHandler>();
		BufferMigrations = new List<BufferMigration>();
		DeferredActions = new Queue<Action>();
		PhysicalMemoryRegistry = new ConcurrentDictionary<ulong, PhysicalMemory>();
		SupportBufferUpdater = new SupportBufferUpdater(renderer);
		_firstTimestamp = ConvertNanosecondsToTicks((ulong)PerformanceCounter.ElapsedNanoseconds);
	}

	public GpuChannel CreateChannel()
	{
		return new GpuChannel(this);
	}

	public MemoryManager CreateMemoryManager(ulong pid, ulong cpuMemorySize)
	{
		if (!PhysicalMemoryRegistry.TryGetValue(pid, out var value))
		{
			throw new ArgumentException("The PID is invalid or the process was not registered", "pid");
		}
		return new MemoryManager(value, cpuMemorySize);
	}

	public DeviceMemoryManager CreateDeviceMemoryManager(ulong pid)
	{
		if (!PhysicalMemoryRegistry.TryGetValue(pid, out var value))
		{
			throw new ArgumentException("The PID is invalid or the process was not registered", "pid");
		}
		return value.CreateDeviceMemoryManager();
	}

	public void RegisterProcess(ulong pid, IVirtualMemoryManagerTracked cpuMemory)
	{
		PhysicalMemory physicalMemory = new PhysicalMemory(this, cpuMemory);
		if (!PhysicalMemoryRegistry.TryAdd(pid, physicalMemory))
		{
			throw new ArgumentException("The PID was already registered", "pid");
		}
		physicalMemory.ShaderCache.ShaderCacheStateChanged += ShaderCacheStateUpdate;
	}

	public void UnregisterProcess(ulong pid)
	{
		if (PhysicalMemoryRegistry.TryRemove(pid, out var value))
		{
			value.ShaderCache.ShaderCacheStateChanged -= ShaderCacheStateUpdate;
			value.Dispose();
		}
	}

	private static ulong ConvertNanosecondsToTicks(ulong nanoseconds)
	{
		ulong num = nanoseconds / 625;
		ulong num2 = num * 625;
		ulong num3 = (nanoseconds - num2) * 384 / 625;
		return num * 384 + num3;
	}

	internal long GetModifiedSequence()
	{
		return _modifiedSequence++;
	}

	internal ulong GetTimestamp()
	{
		ulong num = ConvertNanosecondsToTicks((ulong)PerformanceCounter.ElapsedNanoseconds) - _firstTimestamp;
		if (GraphicsConfig.FastGpuTime)
		{
			num /= 256;
		}
		return num;
	}

	private void ShaderCacheStateUpdate(ShaderCacheState state, int current, int total)
	{
		ShaderCacheStateChanged?.Invoke(state, current, total);
	}

	public void InitializeShaderCache(CancellationToken cancellationToken)
	{
		HostInitalized.WaitOne();
		foreach (PhysicalMemory value in PhysicalMemoryRegistry.Values)
		{
			value.ShaderCache.Initialize(cancellationToken);
		}
		_gpuReadyEvent.Set();
	}

	public void WaitUntilGpuReady()
	{
		_gpuReadyEvent.WaitOne();
	}

	public void SignalHostShutdown()
	{
		_gpuReadyEvent.Set();
	}

	public void SetGpuThread()
	{
		_gpuThread = Thread.CurrentThread;
		Capabilities = Renderer.GetCapabilities();
	}

	public bool IsGpuThread()
	{
		return _gpuThread == Thread.CurrentThread;
	}

	public void ProcessShaderCacheQueue()
	{
		foreach (PhysicalMemory value in PhysicalMemoryRegistry.Values)
		{
			value.ShaderCache.ProcessShaderCacheQueue();
		}
	}

	internal void AdvanceSequence()
	{
		SequenceNumber++;
	}

	internal void RegisterBufferMigration(BufferMigration migration)
	{
		BufferMigrations.Add(migration);
		_pendingSync = true;
	}

	internal void RegisterSyncAction(ISyncActionHandler action, bool syncpointOnly = false)
	{
		if (syncpointOnly)
		{
			SyncpointActions.Add(action);
			return;
		}
		SyncActions.Add(action);
		_pendingSync = true;
	}

	internal void CreateHostSyncIfNeeded(HostSyncFlags flags)
	{
		bool syncpoint = flags.HasFlag(HostSyncFlags.Syncpoint);
		bool strict = flags.HasFlag(HostSyncFlags.Strict);
		bool flag = flags.HasFlag(HostSyncFlags.Force);
		if (BufferMigrations.Count > 0)
		{
			ulong currentSync = Renderer.GetCurrentSync();
			for (int i = 0; i < BufferMigrations.Count; i++)
			{
				BufferMigration bufferMigration = BufferMigrations[i];
				if ((long)(currentSync - bufferMigration.SyncNumber) >= 0L)
				{
					bufferMigration.Dispose();
					BufferMigrations.RemoveAt(i--);
				}
			}
		}
		if (flag || _pendingSync || (syncpoint && SyncpointActions.Count > 0))
		{
			foreach (ISyncActionHandler syncAction in SyncActions)
			{
				syncAction.SyncPreAction(syncpoint);
			}
			foreach (ISyncActionHandler syncpointAction in SyncpointActions)
			{
				syncpointAction.SyncPreAction(syncpoint);
			}
			Renderer.CreateSync(SyncNumber, strict);
			SyncNumber++;
			SyncActions.RemoveAll((ISyncActionHandler action) => action.SyncAction(syncpoint));
			SyncpointActions.RemoveAll((ISyncActionHandler action) => action.SyncAction(syncpoint));
		}
		_pendingSync = false;
	}

	internal void RunDeferredActions()
	{
		Action result;
		while (DeferredActions.TryDequeue(out result))
		{
			result();
		}
	}

	public void Dispose()
	{
		GPFifo.Dispose();
		HostInitalized.Dispose();
		_gpuReadyEvent.Dispose();
		foreach (PhysicalMemory value in PhysicalMemoryRegistry.Values)
		{
			value.Dispose();
		}
		SupportBufferUpdater.Dispose();
		PhysicalMemoryRegistry.Clear();
		RunDeferredActions();
		Renderer.Dispose();
	}
}
