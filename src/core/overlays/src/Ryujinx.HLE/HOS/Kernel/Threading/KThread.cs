using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Ryujinx.Common.Logging;
using Ryujinx.Cpu;
using Ryujinx.HLE.HOS.Kernel.Common;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.HLE.HOS.Kernel.SupervisorCall;
using Ryujinx.Horizon.Common;

namespace Ryujinx.HLE.HOS.Kernel.Threading;

internal class KThread : KSynchronizationObject, IKFutureSchedulerObject
{
	private const int TlsUserDisableCountOffset = 256;

	private const int TlsUserInterruptFlagOffset = 258;

	public const int MaxWaitSyncObjects = 64;

	private ManualResetEvent _schedulerWaitEvent;

	private long _totalTimeRunning;

	private ulong _entrypoint;

	private ThreadStart _customThreadStart;

	private bool _forcedUnschedulable;

	private ulong _tlsAddress;

	private readonly LinkedList<KThread> _mutexWaiters;

	private LinkedListNode<KThread> _mutexWaiterNode;

	private readonly LinkedList<KThread> _pinnedWaiters;

	private ThreadSchedState _forcePauseFlags;

	private ThreadSchedState _forcePausePermissionFlags;

	private ulong _originalAffinityMask;

	private int _originalPreferredCore;

	private int _originalBasePriority;

	private int _coreMigrationDisableCount;

	private int _shallBeTerminated;

	private int _hasExited;

	private bool _hasBeenInitialized;

	private bool _hasBeenReleased;

	private readonly Lock _activityOperationLock = new Lock();

	public ManualResetEvent SchedulerWaitEvent => _schedulerWaitEvent;

	public Thread HostThread { get; private set; }

	public IExecutionContext Context { get; private set; }

	public KThreadContext ThreadContext { get; private set; }

	public int DynamicPriority { get; set; }

	public ulong AffinityMask { get; set; }

	public ulong ThreadUid { get; private set; }

	public long TotalTimeRunning => _totalTimeRunning;

	public KSynchronizationObject SignaledObj { get; set; }

	public ulong CondVarAddress { get; set; }

	public bool IsSchedulable
	{
		get
		{
			if (_customThreadStart == null)
			{
				return !_forcedUnschedulable;
			}
			return false;
		}
	}

	public ulong MutexAddress { get; set; }

	public int KernelWaitersCount { get; private set; }

	public KProcess Owner { get; private set; }

	public ulong TlsAddress => _tlsAddress;

	public KSynchronizationObject[] WaitSyncObjects { get; }

	public int[] WaitSyncHandles { get; }

	public long LastScheduledTime { get; set; }

	public LinkedListNode<KThread>[] SiblingsPerCore { get; private set; }

	public LinkedList<KThread> Withholder { get; set; }

	public LinkedListNode<KThread> WithholderNode { get; set; }

	public LinkedListNode<KThread> ProcessListNode { get; set; }

	public KThread MutexOwner { get; private set; }

	public int ThreadHandleForUserMutex { get; set; }

	public Result ObjSyncResult { get; set; }

	public int BasePriority { get; set; }

	public int PreferredCore { get; set; }

	public int CurrentCore { get; set; }

	public int ActiveCore { get; set; }

	public bool IsPinned { get; private set; }

	public ThreadSchedState SchedFlags { get; private set; }

	private bool ShallBeTerminated => _shallBeTerminated != 0;

	public bool TerminationRequested
	{
		get
		{
			if (!ShallBeTerminated)
			{
				return SchedFlags == ThreadSchedState.TerminationPending;
			}
			return true;
		}
	}

	public bool SyncCancelled { get; set; }

	public bool WaitingSync { get; set; }

	public bool WaitingInArbitration { get; set; }

	public KThread(KernelContext context)
		: base(context)
	{
		WaitSyncObjects = new KSynchronizationObject[64];
		WaitSyncHandles = new int[64];
		SiblingsPerCore = new LinkedListNode<KThread>[KScheduler.CpuCoresCount];
		_mutexWaiters = new LinkedList<KThread>();
		_pinnedWaiters = new LinkedList<KThread>();
	}

	public Result Initialize(ulong entrypoint, ulong argsPtr, ulong stackTop, int priority, int cpuCore, KProcess owner, ThreadType type, ThreadStart customThreadStart = null)
	{
		if ((uint)type > 3u)
		{
			throw new ArgumentException($"Invalid thread type \"{type}\".");
		}
		PreferredCore = cpuCore;
		AffinityMask |= (ulong)(1L << cpuCore);
		SchedFlags = ThreadSchedState.None;
		ActiveCore = cpuCore;
		ObjSyncResult = KernelResult.ThreadNotStarted;
		DynamicPriority = priority;
		BasePriority = priority;
		CurrentCore = cpuCore;
		IsPinned = false;
		_entrypoint = entrypoint;
		_customThreadStart = customThreadStart;
		if (type == ThreadType.User)
		{
			if (owner.AllocateThreadLocalStorage(out _tlsAddress) != Result.Success)
			{
				return KernelResult.OutOfMemory;
			}
			MemoryHelper.FillWithZeros(owner.CpuMemory, _tlsAddress, 512);
		}
		bool flag;
		if (owner != null)
		{
			Owner = owner;
			owner.IncrementReferenceCount();
			owner.IncrementThreadCount();
			flag = owner.Flags.HasFlag(ProcessCreationFlags.Is64Bit);
		}
		else
		{
			flag = true;
		}
		HostThread = new Thread(ThreadStart);
		Context = owner?.CreateExecutionContext() ?? new ProcessExecutionContext();
		ThreadContext = new KThreadContext(Context);
		Context.IsAarch32 = !flag;
		Context.SetX(0, argsPtr);
		if (flag)
		{
			Context.SetX(18, KSystemControl.GenerateRandom() | 1);
			Context.SetX(31, stackTop);
		}
		else
		{
			Context.SetX(13, (uint)stackTop);
		}
		Context.TpidrroEl0 = (long)_tlsAddress;
		ThreadUid = KernelContext.NewThreadUid();
		HostThread.Name = ((customThreadStart != null) ? $"HLE.OsThread.{ThreadUid}" : $"HLE.GuestThread.{ThreadUid}");
		_hasBeenInitialized = true;
		_forcePausePermissionFlags = ThreadSchedState.ForcePauseMask;
		if (owner != null)
		{
			owner.AddThread(this);
			if (owner.IsPaused)
			{
				KernelContext.CriticalSection.Enter();
				if (TerminationRequested)
				{
					KernelContext.CriticalSection.Leave();
					return Result.Success;
				}
				_forcePauseFlags |= ThreadSchedState.ProcessPauseFlag;
				CombineForcePauseFlags();
				KernelContext.CriticalSection.Leave();
			}
		}
		return Result.Success;
	}

	public Result Start()
	{
		if (!KernelContext.KernelInitialized)
		{
			KernelContext.CriticalSection.Enter();
			if (!TerminationRequested)
			{
				_forcePauseFlags |= ThreadSchedState.KernelInitPauseFlag;
				CombineForcePauseFlags();
			}
			KernelContext.CriticalSection.Leave();
		}
		Result result = KernelResult.ThreadTerminating;
		KernelContext.CriticalSection.Enter();
		if (!ShallBeTerminated)
		{
			KThread currentThread = KernelStatic.GetCurrentThread();
			while (SchedFlags != ThreadSchedState.TerminationPending && (currentThread == null || !currentThread.TerminationRequested))
			{
				if ((SchedFlags & ThreadSchedState.LowMask) != 0)
				{
					result = KernelResult.InvalidState;
					break;
				}
				if (currentThread == null || currentThread._forcePauseFlags == ThreadSchedState.None)
				{
					if (Owner != null && _forcePauseFlags != ThreadSchedState.None)
					{
						CombineForcePauseFlags();
					}
					SetNewSchedFlags(ThreadSchedState.Running);
					StartHostThread();
					result = Result.Success;
					break;
				}
				currentThread.CombineForcePauseFlags();
				KernelContext.CriticalSection.Leave();
				KernelContext.CriticalSection.Enter();
				if (currentThread.ShallBeTerminated)
				{
					break;
				}
			}
		}
		KernelContext.CriticalSection.Leave();
		return result;
	}

	public ThreadSchedState PrepareForTermination()
	{
		KernelContext.CriticalSection.Enter();
		if (Owner != null && Owner.PinnedThreads[KernelStatic.GetCurrentThread().CurrentCore] == this)
		{
			Owner.UnpinThread(this);
		}
		if (Interlocked.Exchange(ref _shallBeTerminated, 1) == 0)
		{
			if ((SchedFlags & ThreadSchedState.LowMask) == 0)
			{
				SchedFlags = ThreadSchedState.TerminationPending;
			}
			else
			{
				if (_forcePauseFlags != ThreadSchedState.None)
				{
					_forcePauseFlags &= ~ThreadSchedState.ThreadPauseFlag;
					ThreadSchedState schedFlags = SchedFlags;
					SchedFlags &= ThreadSchedState.LowMask;
					AdjustScheduling(schedFlags);
				}
				if (BasePriority >= 16)
				{
					SetPriority(15);
				}
				if ((SchedFlags & ThreadSchedState.LowMask) == ThreadSchedState.Running)
				{
					Context.RequestInterrupt();
				}
				SignaledObj = null;
				ObjSyncResult = KernelResult.ThreadTerminating;
				ReleaseAndResume();
			}
		}
		ThreadSchedState schedFlags2 = SchedFlags;
		KernelContext.CriticalSection.Leave();
		return schedFlags2 & ThreadSchedState.LowMask;
	}

	public void Terminate()
	{
		if (PrepareForTermination() != ThreadSchedState.TerminationPending)
		{
			KernelContext.Synchronization.WaitFor(new KSynchronizationObject[1] { this }, -1L, out var _);
		}
	}

	public void HandlePostSyscall()
	{
		ThreadSchedState threadSchedState;
		do
		{
			if (TerminationRequested)
			{
				Exit();
				break;
			}
			KernelContext.CriticalSection.Enter();
			if (TerminationRequested)
			{
				threadSchedState = ThreadSchedState.TerminationPending;
			}
			else
			{
				if (_forcePauseFlags != ThreadSchedState.None)
				{
					CombineForcePauseFlags();
				}
				threadSchedState = ThreadSchedState.Running;
			}
			KernelContext.CriticalSection.Leave();
		}
		while (threadSchedState == ThreadSchedState.TerminationPending);
	}

	public void Exit()
	{
		if (Owner != null)
		{
			Owner.ResourceLimit?.Release(LimitableResource.Thread, 0L, 1L);
			_hasBeenReleased = true;
		}
		KernelContext.CriticalSection.Enter();
		_forcePauseFlags &= ~ThreadSchedState.ForcePauseMask;
		_forcePausePermissionFlags = ThreadSchedState.None;
		bool flag = ExitImpl();
		Context.StopRunning();
		KernelContext.CriticalSection.Leave();
		if (flag)
		{
			DecrementReferenceCount();
		}
	}

	private bool ExitImpl()
	{
		KernelContext.CriticalSection.Enter();
		SetNewSchedFlags(ThreadSchedState.TerminationPending);
		bool result = Interlocked.Exchange(ref _hasExited, 1) == 0;
		Signal();
		KernelContext.CriticalSection.Leave();
		return result;
	}

	private int GetEffectiveRunningCore()
	{
		for (int i = 0; i < KScheduler.CpuCoresCount; i++)
		{
			if (KernelContext.Schedulers[i].CurrentThread == this)
			{
				return i;
			}
		}
		return -1;
	}

	public Result Sleep(long timeout)
	{
		KernelContext.CriticalSection.Enter();
		if (TerminationRequested)
		{
			KernelContext.CriticalSection.Leave();
			return KernelResult.ThreadTerminating;
		}
		SetNewSchedFlags(ThreadSchedState.Paused);
		if (timeout > 0)
		{
			KernelContext.TimeManager.ScheduleFutureInvocation(this, timeout);
		}
		KernelContext.CriticalSection.Leave();
		if (timeout > 0)
		{
			KernelContext.TimeManager.UnscheduleFutureInvocation(this);
		}
		return Result.Success;
	}

	public void SetPriority(int priority)
	{
		KernelContext.CriticalSection.Enter();
		if (IsPinned)
		{
			_originalBasePriority = priority;
		}
		else
		{
			BasePriority = priority;
		}
		UpdatePriorityInheritance();
		KernelContext.CriticalSection.Leave();
	}

	public void Suspend(ThreadSchedState type)
	{
		_forcePauseFlags |= type;
		CombineForcePauseFlags();
	}

	public void Resume(ThreadSchedState type)
	{
		ThreadSchedState forcePauseFlags = _forcePauseFlags;
		_forcePauseFlags &= (ThreadSchedState)(ushort)(~(int)type);
		if (((uint)forcePauseFlags & (uint)(ushort)(~(int)type)) == 0)
		{
			ThreadSchedState schedFlags = SchedFlags;
			SchedFlags &= ThreadSchedState.LowMask;
			AdjustScheduling(schedFlags);
		}
	}

	public Result SetActivity(bool pause)
	{
		using (_activityOperationLock.EnterScope())
		{
			Result result = Result.Success;
			KernelContext.CriticalSection.Enter();
			ThreadSchedState threadSchedState = SchedFlags & ThreadSchedState.LowMask;
			if (threadSchedState != ThreadSchedState.Paused && threadSchedState != ThreadSchedState.Running)
			{
				KernelContext.CriticalSection.Leave();
				return KernelResult.InvalidState;
			}
			if (!TerminationRequested)
			{
				if (pause)
				{
					if ((_forcePauseFlags & ThreadSchedState.ThreadPauseFlag) == 0)
					{
						Suspend(ThreadSchedState.ThreadPauseFlag);
					}
					else
					{
						result = KernelResult.InvalidState;
					}
				}
				else if ((_forcePauseFlags & ThreadSchedState.ThreadPauseFlag) != 0)
				{
					Resume(ThreadSchedState.ThreadPauseFlag);
				}
				else
				{
					result = KernelResult.InvalidState;
				}
			}
			KernelContext.CriticalSection.Leave();
			if ((result == Result.Success) & pause)
			{
				bool flag = true;
				while (flag)
				{
					KernelContext.CriticalSection.Enter();
					if (TerminationRequested)
					{
						KernelContext.CriticalSection.Leave();
						break;
					}
					flag = false;
					if (IsPinned)
					{
						KThread currentThread = KernelStatic.GetCurrentThread();
						if (currentThread.TerminationRequested)
						{
							KernelContext.CriticalSection.Leave();
							result = KernelResult.ThreadTerminating;
							break;
						}
						_pinnedWaiters.AddLast(currentThread);
						currentThread.Reschedule(ThreadSchedState.Paused);
					}
					else
					{
						flag = GetEffectiveRunningCore() >= 0;
					}
					KernelContext.CriticalSection.Leave();
				}
			}
			return result;
		}
	}

	public Result GetThreadContext3(out ThreadContext context)
	{
		context = default;
		using (_activityOperationLock.EnterScope())
		{
			KernelContext.CriticalSection.Enter();
			if ((_forcePauseFlags & ThreadSchedState.ThreadPauseFlag) == 0)
			{
				KernelContext.CriticalSection.Leave();
				return KernelResult.InvalidState;
			}
			if (!TerminationRequested)
			{
				context = GetCurrentContext();
			}
			KernelContext.CriticalSection.Leave();
		}
		return Result.Success;
	}

	private static uint GetPsr(IExecutionContext context)
	{
		return context.Pstate & 0xFF0FFE20u;
	}

	private ThreadContext GetCurrentContext()
	{
		ThreadContext result = default;
		if (Owner.Flags.HasFlag(ProcessCreationFlags.Is64Bit))
		{
			for (int i = 0; i < result.Registers.Length; i++)
			{
				result.Registers[i] = Context.GetX(i);
			}
			for (int j = 0; j < result.FpuRegisters.Length; j++)
			{
				result.FpuRegisters[j] = Context.GetV(j);
			}
			result.Fp = Context.GetX(29);
			result.Lr = Context.GetX(30);
			result.Sp = Context.GetX(31);
			result.Pc = Context.Pc;
			result.Pstate = GetPsr(Context);
			result.Tpidr = (ulong)Context.TpidrroEl0;
		}
		else
		{
			for (int k = 0; k < 15; k++)
			{
				result.Registers[k] = (uint)Context.GetX(k);
			}
			for (int l = 0; l < 16; l++)
			{
				result.FpuRegisters[l] = Context.GetV(l);
			}
			result.Pc = (uint)Context.Pc;
			result.Pstate = GetPsr(Context);
			result.Tpidr = (uint)Context.TpidrroEl0;
		}
		result.Fpcr = Context.Fpcr;
		result.Fpsr = Context.Fpsr;
		return result;
	}

	public void CancelSynchronization()
	{
		KernelContext.CriticalSection.Enter();
		if ((SchedFlags & ThreadSchedState.LowMask) != ThreadSchedState.Paused || !WaitingSync)
		{
			SyncCancelled = true;
		}
		else if (Withholder != null)
		{
			Withholder.Remove(WithholderNode);
			SetNewSchedFlags(ThreadSchedState.Running);
			Withholder = null;
			SyncCancelled = true;
		}
		else
		{
			SignaledObj = null;
			ObjSyncResult = KernelResult.Cancelled;
			SetNewSchedFlags(ThreadSchedState.Running);
			SyncCancelled = false;
		}
		KernelContext.CriticalSection.Leave();
	}

	public Result SetCoreAndAffinityMask(int newCore, ulong newAffinityMask)
	{
		using (_activityOperationLock.EnterScope())
		{
			KernelContext.CriticalSection.Enter();
			bool flag = _coreMigrationDisableCount != 0;
			if (newCore == -3)
			{
				newCore = (flag ? _originalPreferredCore : PreferredCore);
				if ((newAffinityMask & (ulong)(1L << newCore)) == 0L)
				{
					KernelContext.CriticalSection.Leave();
					return KernelResult.InvalidCombination;
				}
			}
			if (flag)
			{
				_originalPreferredCore = newCore;
				_originalAffinityMask = newAffinityMask;
			}
			else
			{
				ulong affinityMask = AffinityMask;
				PreferredCore = newCore;
				AffinityMask = newAffinityMask;
				if (affinityMask != newAffinityMask)
				{
					int activeCore = ActiveCore;
					if (activeCore >= 0 && ((AffinityMask >> activeCore) & 1) == 0L)
					{
						if (PreferredCore < 0)
						{
							ActiveCore = 63 - BitOperations.LeadingZeroCount(AffinityMask);
						}
						else
						{
							ActiveCore = PreferredCore;
						}
					}
					AdjustSchedulingForNewAffinity(affinityMask, activeCore);
				}
			}
			KernelContext.CriticalSection.Leave();
			bool flag2 = true;
			while (flag2)
			{
				KernelContext.CriticalSection.Enter();
				if (TerminationRequested)
				{
					KernelContext.CriticalSection.Leave();
					break;
				}
				flag2 = false;
				int effectiveRunningCore = GetEffectiveRunningCore();
				if (effectiveRunningCore >= 0 && ((ulong)(1L << effectiveRunningCore) & AffinityMask) == 0L)
				{
					if (IsPinned)
					{
						KThread currentThread = KernelStatic.GetCurrentThread();
						if (currentThread.TerminationRequested)
						{
							KernelContext.CriticalSection.Leave();
							return KernelResult.ThreadTerminating;
						}
						_pinnedWaiters.AddLast(currentThread);
						currentThread.Reschedule(ThreadSchedState.Paused);
					}
					else
					{
						flag2 = true;
					}
				}
				KernelContext.CriticalSection.Leave();
			}
			return Result.Success;
		}
	}

	private void CombineForcePauseFlags()
	{
		ThreadSchedState schedFlags = SchedFlags;
		ThreadSchedState threadSchedState = SchedFlags & ThreadSchedState.LowMask;
		SchedFlags = threadSchedState | (_forcePauseFlags & _forcePausePermissionFlags);
		AdjustScheduling(schedFlags);
	}

	private void SetNewSchedFlags(ThreadSchedState newFlags)
	{
		KernelContext.CriticalSection.Enter();
		ThreadSchedState schedFlags = SchedFlags;
		SchedFlags = (schedFlags & ThreadSchedState.HighMask) | newFlags;
		if ((schedFlags & ThreadSchedState.LowMask) != newFlags)
		{
			AdjustScheduling(schedFlags);
		}
		KernelContext.CriticalSection.Leave();
	}

	public void ReleaseAndResume()
	{
		KernelContext.CriticalSection.Enter();
		if ((SchedFlags & ThreadSchedState.LowMask) == ThreadSchedState.Paused)
		{
			if (Withholder != null)
			{
				Withholder.Remove(WithholderNode);
				SetNewSchedFlags(ThreadSchedState.Running);
				Withholder = null;
			}
			else
			{
				SetNewSchedFlags(ThreadSchedState.Running);
			}
		}
		KernelContext.CriticalSection.Leave();
	}

	public void Reschedule(ThreadSchedState newFlags)
	{
		KernelContext.CriticalSection.Enter();
		ThreadSchedState schedFlags = SchedFlags;
		SchedFlags = (schedFlags & ThreadSchedState.HighMask) | (newFlags & ThreadSchedState.LowMask);
		AdjustScheduling(schedFlags);
		KernelContext.CriticalSection.Leave();
	}

	public void AddMutexWaiter(KThread requester)
	{
		AddToMutexWaitersList(requester);
		requester.MutexOwner = this;
		UpdatePriorityInheritance();
	}

	public void RemoveMutexWaiter(KThread thread)
	{
		if (thread._mutexWaiterNode?.List != null)
		{
			_mutexWaiters.Remove(thread._mutexWaiterNode);
		}
		thread.MutexOwner = null;
		UpdatePriorityInheritance();
	}

	public KThread RelinquishMutex(ulong mutexAddress, out int count)
	{
		count = 0;
		if (_mutexWaiters.First == null)
		{
			return null;
		}
		KThread kThread = null;
		LinkedListNode<KThread> linkedListNode = _mutexWaiters.First;
		while (true)
		{
			if (linkedListNode != null && linkedListNode.Value.MutexAddress != mutexAddress)
			{
				linkedListNode = linkedListNode.Next;
				continue;
			}
			if (linkedListNode == null)
			{
				break;
			}
			LinkedListNode<KThread>? next = linkedListNode.Next;
			_mutexWaiters.Remove(linkedListNode);
			linkedListNode.Value.MutexOwner = kThread;
			if (kThread != null)
			{
				kThread.AddToMutexWaitersList(linkedListNode.Value);
			}
			else
			{
				kThread = linkedListNode.Value;
			}
			count++;
			linkedListNode = next;
			if (linkedListNode == null)
			{
				break;
			}
		}
		if (kThread != null)
		{
			UpdatePriorityInheritance();
			kThread.UpdatePriorityInheritance();
		}
		return kThread;
	}

	private void UpdatePriorityInheritance()
	{
		int num = BasePriority;
		if (_mutexWaiters.First != null)
		{
			int dynamicPriority = _mutexWaiters.First.Value.DynamicPriority;
			if (dynamicPriority < num)
			{
				num = dynamicPriority;
			}
		}
		if (num != DynamicPriority)
		{
			int dynamicPriority2 = DynamicPriority;
			DynamicPriority = num;
			AdjustSchedulingForNewPriority(dynamicPriority2);
			if (MutexOwner != null)
			{
				MutexOwner._mutexWaiters.Remove(_mutexWaiterNode);
				MutexOwner.AddToMutexWaitersList(this);
				MutexOwner.UpdatePriorityInheritance();
			}
		}
	}

	private void AddToMutexWaitersList(KThread thread)
	{
		LinkedListNode<KThread> linkedListNode = _mutexWaiters.First;
		int dynamicPriority = thread.DynamicPriority;
		while (linkedListNode != null && linkedListNode.Value.DynamicPriority <= dynamicPriority)
		{
			linkedListNode = linkedListNode.Next;
		}
		if (linkedListNode != null)
		{
			thread._mutexWaiterNode = _mutexWaiters.AddBefore(linkedListNode, thread);
		}
		else
		{
			thread._mutexWaiterNode = _mutexWaiters.AddLast(thread);
		}
	}

	private void AdjustScheduling(ThreadSchedState oldFlags)
	{
		if (oldFlags == SchedFlags)
		{
			return;
		}
		if (!IsSchedulable)
		{
			if (!_forcedUnschedulable)
			{
				StartHostThread();
				if (SchedFlags == ThreadSchedState.Running)
				{
					_schedulerWaitEvent.Set();
				}
				else
				{
					_schedulerWaitEvent.Reset();
				}
			}
			return;
		}
		if (oldFlags == ThreadSchedState.Running)
		{
			if (ActiveCore >= 0)
			{
				KernelContext.PriorityQueue.Unschedule(DynamicPriority, ActiveCore, this);
			}
			for (int i = 0; i < KScheduler.CpuCoresCount; i++)
			{
				if (i != ActiveCore && ((AffinityMask >> i) & 1) != 0L)
				{
					KernelContext.PriorityQueue.Unsuggest(DynamicPriority, i, this);
				}
			}
		}
		else if (SchedFlags == ThreadSchedState.Running)
		{
			if (ActiveCore >= 0)
			{
				KernelContext.PriorityQueue.Schedule(DynamicPriority, ActiveCore, this);
			}
			for (int j = 0; j < KScheduler.CpuCoresCount; j++)
			{
				if (j != ActiveCore && ((AffinityMask >> j) & 1) != 0L)
				{
					KernelContext.PriorityQueue.Suggest(DynamicPriority, j, this);
				}
			}
		}
		KernelContext.ThreadReselectionRequested = true;
	}

	private void AdjustSchedulingForNewPriority(int oldPriority)
	{
		if (SchedFlags != ThreadSchedState.Running || !IsSchedulable)
		{
			return;
		}
		if (ActiveCore >= 0)
		{
			KernelContext.PriorityQueue.Unschedule(oldPriority, ActiveCore, this);
		}
		for (int i = 0; i < KScheduler.CpuCoresCount; i++)
		{
			if (i != ActiveCore && ((AffinityMask >> i) & 1) != 0L)
			{
				KernelContext.PriorityQueue.Unsuggest(oldPriority, i, this);
			}
		}
		KThread currentThread = KernelStatic.GetCurrentThread();
		if (ActiveCore >= 0)
		{
			if (currentThread == this)
			{
				KernelContext.PriorityQueue.SchedulePrepend(DynamicPriority, ActiveCore, this);
			}
			else
			{
				KernelContext.PriorityQueue.Schedule(DynamicPriority, ActiveCore, this);
			}
		}
		for (int j = 0; j < KScheduler.CpuCoresCount; j++)
		{
			if (j != ActiveCore && ((AffinityMask >> j) & 1) != 0L)
			{
				KernelContext.PriorityQueue.Suggest(DynamicPriority, j, this);
			}
		}
		KernelContext.ThreadReselectionRequested = true;
	}

	private void AdjustSchedulingForNewAffinity(ulong oldAffinityMask, int oldCore)
	{
		if (SchedFlags != ThreadSchedState.Running || DynamicPriority >= 64 || !IsSchedulable)
		{
			return;
		}
		for (int i = 0; i < KScheduler.CpuCoresCount; i++)
		{
			if (((oldAffinityMask >> i) & 1) != 0L)
			{
				if (i == oldCore)
				{
					KernelContext.PriorityQueue.Unschedule(DynamicPriority, i, this);
				}
				else
				{
					KernelContext.PriorityQueue.Unsuggest(DynamicPriority, i, this);
				}
			}
		}
		for (int j = 0; j < KScheduler.CpuCoresCount; j++)
		{
			if (((AffinityMask >> j) & 1) != 0L)
			{
				if (j == ActiveCore)
				{
					KernelContext.PriorityQueue.Schedule(DynamicPriority, j, this);
				}
				else
				{
					KernelContext.PriorityQueue.Suggest(DynamicPriority, j, this);
				}
			}
		}
		KernelContext.ThreadReselectionRequested = true;
	}

	public void SetEntryArguments(long argsPtr, int threadHandle)
	{
		Context.SetX(0, (ulong)argsPtr);
		Context.SetX(1, (ulong)threadHandle);
	}

	public void TimeUp()
	{
		ReleaseAndResume();
	}

	public string GetGuestStackTrace()
	{
		return Owner.Debugger.GetGuestStackTrace(this);
	}

	public string GetGuestRegisterPrintout()
	{
		return Owner.Debugger.GetCpuRegisterPrintout(this);
	}

	public void PrintGuestStackTrace()
	{
		Logger.Info?.Print(LogClass.Cpu, "Guest stack trace:\n" + GetGuestStackTrace() + "\n", "PrintGuestStackTrace");
	}

	public void PrintGuestRegisterPrintout()
	{
		Logger.Info?.Print(LogClass.Cpu, "Guest CPU registers:\n" + GetGuestRegisterPrintout() + "\n", "PrintGuestRegisterPrintout");
	}

	public void AddCpuTime(long ticks)
	{
		Interlocked.Add(ref _totalTimeRunning, ticks);
	}

	public void StartHostThread()
	{
		if (_schedulerWaitEvent != null)
		{
			return;
		}
		ManualResetEvent manualResetEvent = new ManualResetEvent(initialState: false);
		if (Interlocked.CompareExchange(ref _schedulerWaitEvent, manualResetEvent, null) == null)
		{
			bool flag = false;
			try
			{
				KernelContext.RegisterHostThread(HostThread);
				flag = true;
				HostThread.Start();
				return;
			}
			catch
			{
				if (flag)
				{
					KernelContext.CompleteHostThread(HostThread);
				}
				if (Interlocked.CompareExchange(ref _schedulerWaitEvent, null, manualResetEvent) == manualResetEvent)
				{
					manualResetEvent.Dispose();
				}
				throw;
			}
		}
		manualResetEvent.Dispose();
	}

	private void ThreadStart()
	{
		try
		{
			_schedulerWaitEvent.WaitOne();
			KernelStatic.SetKernelContext(KernelContext, this);
			if (TerminationRequested)
			{
				Exit();
			}
			else if (_customThreadStart != null)
			{
				_customThreadStart();
				Exit();
				HandlePostSyscall();
			}
			else
			{
				Owner.Context.Execute(Context, _entrypoint);
			}
			Context.Dispose();
			_schedulerWaitEvent.Dispose();
		}
		finally
		{
			KernelContext.CompleteHostThread(HostThread);
		}
	}

	public void MakeUnschedulable()
	{
		_forcedUnschedulable = true;
	}

	public override bool IsSignaled()
	{
		return _hasExited != 0;
	}

	protected override void Destroy()
	{
		if (_hasBeenInitialized)
		{
			FreeResources();
			bool flag = Owner != null || _hasBeenReleased;
			if (Owner != null)
			{
				Owner.ResourceLimit?.Release(LimitableResource.Thread, 1L, (!flag) ? 1 : 0);
				Owner.DecrementReferenceCount();
			}
			else
			{
				KernelContext.ResourceLimit.Release(LimitableResource.Thread, 1L, (!flag) ? 1 : 0);
			}
		}
	}

	private void FreeResources()
	{
		Owner?.RemoveThread(this);
		if (_tlsAddress != 0L && Owner?.FreeThreadLocalStorage(_tlsAddress) != Result.Success)
		{
			throw new InvalidOperationException("Unexpected failure freeing thread local storage.");
		}
		KernelContext.CriticalSection.Enter();
		foreach (KThread mutexWaiter in _mutexWaiters)
		{
			mutexWaiter.MutexOwner = null;
			mutexWaiter._originalPreferredCore = 0;
			mutexWaiter.ObjSyncResult = KernelResult.InvalidState;
			mutexWaiter.ReleaseAndResume();
		}
		KernelContext.CriticalSection.Leave();
		Owner?.DecrementThreadCountAndTerminateIfZero();
	}

	public void Pin()
	{
		IsPinned = true;
		_coreMigrationDisableCount++;
		int activeCore = ActiveCore;
		_originalPreferredCore = PreferredCore;
		_originalAffinityMask = AffinityMask;
		ActiveCore = CurrentCore;
		PreferredCore = CurrentCore;
		AffinityMask = (ulong)(1L << CurrentCore);
		if (activeCore != CurrentCore || _originalAffinityMask != AffinityMask)
		{
			AdjustSchedulingForNewAffinity(_originalAffinityMask, activeCore);
		}
		_originalBasePriority = BasePriority;
		BasePriority = Math.Min(_originalBasePriority, BitOperations.TrailingZeroCount(Owner.Capabilities.AllowedThreadPriosMask) - 1);
		UpdatePriorityInheritance();
		_forcePausePermissionFlags &= ~ThreadSchedState.ThreadPauseFlag;
		CombineForcePauseFlags();
	}

	public void Unpin()
	{
		IsPinned = false;
		_coreMigrationDisableCount--;
		ulong affinityMask = AffinityMask;
		int activeCore = ActiveCore;
		PreferredCore = _originalPreferredCore;
		AffinityMask = _originalAffinityMask;
		if (AffinityMask != affinityMask && (AffinityMask & (ulong)(1L << ActiveCore)) != 0L)
		{
			if (PreferredCore >= 0)
			{
				ActiveCore = PreferredCore;
			}
			else
			{
				ActiveCore = 63 - BitOperations.LeadingZeroCount(AffinityMask);
			}
			AdjustSchedulingForNewAffinity(affinityMask, activeCore);
		}
		BasePriority = _originalBasePriority;
		UpdatePriorityInheritance();
		if (!TerminationRequested)
		{
			_forcePausePermissionFlags |= ThreadSchedState.ThreadPauseFlag;
			CombineForcePauseFlags();
		}
		foreach (KThread pinnedWaiter in _pinnedWaiters)
		{
			pinnedWaiter.ReleaseAndResume();
		}
		_pinnedWaiters.Clear();
	}

	public void SynchronizePreemptionState()
	{
		KernelContext.CriticalSection.Enter();
		if (Owner != null && Owner.PinnedThreads[CurrentCore] == this)
		{
			ClearUserInterruptFlag();
			Owner.UnpinThread(this);
		}
		KernelContext.CriticalSection.Leave();
	}

	public ushort GetUserDisableCount()
	{
		return Owner.CpuMemory.Read<ushort>(_tlsAddress + 256);
	}

	public void SetUserInterruptFlag()
	{
		Owner.CpuMemory.Write(_tlsAddress + 258, (ushort)1);
	}

	public void ClearUserInterruptFlag()
	{
		Owner.CpuMemory.Write(_tlsAddress + 258, (ushort)0);
	}
}
