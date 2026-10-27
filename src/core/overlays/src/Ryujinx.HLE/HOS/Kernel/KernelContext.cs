using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Ryujinx.Cpu;
using Ryujinx.HLE.HOS.Kernel.Common;
using Ryujinx.HLE.HOS.Kernel.Memory;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.HLE.HOS.Kernel.SupervisorCall;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.Memory;

namespace Ryujinx.HLE.HOS.Kernel;

internal class KernelContext : IDisposable
{
	private ulong _kipId;

	private ulong _processId;

	private ulong _threadUid;

	private readonly object _hostThreadLock = new object();

	private readonly HashSet<Thread> _hostThreads = new HashSet<Thread>();

	private bool _hostThreadsClosing;

	public long PrivilegedProcessLowestId { get; set; } = 1L;

	public long PrivilegedProcessHighestId { get; set; } = 8L;

	public bool EnableVersionChecks { get; set; }

	public bool KernelInitialized { get; }

	public bool Running { get; private set; }

	public Switch Device { get; }

	public MemoryBlock Memory { get; }

	public ITickSource TickSource { get; }

	public Syscall Syscall { get; }

	public SyscallHandler SyscallHandler { get; }

	public KResourceLimit ResourceLimit { get; }

	public KMemoryManager MemoryManager { get; }

	public KMemoryBlockSlabManager LargeMemoryBlockSlabManager { get; }

	public KMemoryBlockSlabManager SmallMemoryBlockSlabManager { get; }

	public KSlabHeap UserSlabHeapPages { get; }

	public KCriticalSection CriticalSection { get; }

	public KScheduler[] Schedulers { get; }

	public KPriorityQueue PriorityQueue { get; }

	public KTimeManager TimeManager { get; }

	public KSynchronization Synchronization { get; }

	public KContextIdManager ContextIdManager { get; }

	public ConcurrentDictionary<ulong, KProcess> Processes { get; }

	public ConcurrentDictionary<string, KAutoObject> AutoObjectNames { get; }

	public bool ThreadReselectionRequested { get; set; }

	public KernelContext(ITickSource tickSource, Switch device, MemoryBlock memory, MemorySize memorySize, MemoryArrange memoryArrange)
	{
		TickSource = tickSource;
		Device = device;
		Memory = memory;
		KScheduler.CpuCoresCount = device.CpuCoresCount;
		Running = true;
		Syscall = new Syscall(this);
		SyscallHandler = new SyscallHandler(this);
		ResourceLimit = new KResourceLimit(this);
		KernelInit.InitializeResourceLimit(ResourceLimit, memorySize);
		MemoryManager = new KMemoryManager(memorySize, memoryArrange);
		LargeMemoryBlockSlabManager = new KMemoryBlockSlabManager(20000uL);
		SmallMemoryBlockSlabManager = new KMemoryBlockSlabManager(10000uL);
		UserSlabHeapPages = new KSlabHeap(2148421632uL, 4096uL, 4055040uL);
		CommitMemory(937984uL, 4055040uL);
		CriticalSection = new KCriticalSection(this);
		Schedulers = new KScheduler[KScheduler.CpuCoresCount];
		PriorityQueue = new KPriorityQueue();
		TimeManager = new KTimeManager(this);
		Synchronization = new KSynchronization(this);
		ContextIdManager = new KContextIdManager();
		for (int i = 0; i < KScheduler.CpuCoresCount; i++)
		{
			Schedulers[i] = new KScheduler(this, i);
		}
		StartPreemptionThread();
		KernelInitialized = true;
		Processes = new ConcurrentDictionary<ulong, KProcess>();
		AutoObjectNames = new ConcurrentDictionary<string, KAutoObject>();
		_kipId = 1uL;
		_processId = 81uL;
	}

	private void StartPreemptionThread()
	{
		Thread thread = new Thread(PreemptionThreadStart);
		thread.Name = "HLE.PreemptionThread";
		thread.Start();
		void PreemptionThreadStart()
		{
			KScheduler.PreemptionThreadLoop(this);
		}
	}

	public void CommitMemory(ulong address, ulong size)
	{
		ulong pageSize = MemoryBlock.GetPageSize();
		ulong num = address + size;
		address &= ~(pageSize - 1);
		num = (num + (pageSize - 1)) & ~(pageSize - 1);
		Memory.Commit(address, num - address);
	}

	public ulong NewThreadUid()
	{
		return Interlocked.Increment(ref _threadUid) - 1;
	}

	public ulong NewKipId()
	{
		return Interlocked.Increment(ref _kipId) - 1;
	}

	public ulong NewProcessId()
	{
		return Interlocked.Increment(ref _processId) - 1;
	}

	internal void RegisterHostThread(Thread thread)
	{
		lock (_hostThreadLock)
		{
			if (_hostThreadsClosing)
			{
				throw new InvalidOperationException("Cannot start a host thread after kernel teardown has begun draining host threads.");
			}
			if (!_hostThreads.Add(thread))
			{
				throw new InvalidOperationException("The host thread was already registered.");
			}
		}
	}

	internal void CompleteHostThread(Thread thread)
	{
		lock (_hostThreadLock)
		{
			_hostThreads.Remove(thread);
			Monitor.PulseAll(_hostThreadLock);
		}
	}

	internal void WaitForHostThreads(TimeSpan timeout)
	{
		long timestamp = Stopwatch.GetTimestamp();
		lock (_hostThreadLock)
		{
			if (_hostThreads.Contains(Thread.CurrentThread))
			{
				throw new InvalidOperationException("A kernel host thread cannot wait for its own completion.");
			}
			_hostThreadsClosing = true;
			while (_hostThreads.Count != 0)
			{
				TimeSpan timeSpan = timeout - Stopwatch.GetElapsedTime(timestamp);
				if ((timeSpan <= TimeSpan.Zero || !Monitor.Wait(_hostThreadLock, timeSpan)) && _hostThreads.Count != 0)
				{
					string value = string.Join(", ", from x in _hostThreads.Take(8)
						select $"{x.ManagedThreadId}:{x.Name}");
					throw new TimeoutException($"Kernel host threads did not complete before teardown: count={_hostThreads.Count}; threads={value}. Process ownership remains retained.");
				}
			}
		}
	}

	public void Dispose()
	{
		Running = false;
		for (int i = 0; i < KScheduler.CpuCoresCount; i++)
		{
			Schedulers[i].Dispose();
		}
		TimeManager.Dispose();
	}
}
