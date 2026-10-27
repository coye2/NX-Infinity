using System;
using System.Collections.Generic;
using System.Threading;
using Ryujinx.Graphics.Gpu.Synchronization;
using Ryujinx.Memory.Tracking;

namespace Ryujinx.Graphics.Gpu.Image;

internal class TextureGroupHandle : ISyncActionHandler, IDisposable
{
	private const int FlushBalanceIncrement = 6;

	private const int FlushBalanceWriteCost = 1;

	private const int FlushBalanceThreshold = 7;

	private const int FlushBalanceMax = 60;

	private const int FlushBalanceMin = -10;

	private readonly TextureGroup _group;

	private int _bindCount;

	private readonly int _firstLevel;

	private readonly int _firstLayer;

	private ulong _registeredSync;

	private ulong _registeredBufferSync = ulong.MaxValue;

	private ulong _registeredBufferGuestSync = ulong.MaxValue;

	private ulong _modifiedSync;

	private int _actionRegistered;

	private bool _syncActionRegistered;

	private int _flushBalance;

	public int Offset { get; }

	public int Size { get; }

	public int BaseSlice { get; }

	public int SliceCount { get; }

	public List<Texture> Overlaps { get; }

	public RegionHandle[] Handles { get; private set; }

	internal ulong[] TrackingOffsets { get; private set; }

	public bool Modified { get; set; }

	public List<TextureDependency> Dependencies { get; }

	public bool NeedsCopy => DeferredCopy != null;

	public TextureGroupHandle DeferredCopy { get; set; }

	public TextureGroupHandle(TextureGroup group, int offset, ulong size, IEnumerable<Texture> views, int firstLayer, int firstLevel, int baseSlice, int sliceCount, RegionHandle[] handles, ulong[] trackingOffsets)
	{
		_group = group;
		_firstLayer = firstLayer;
		_firstLevel = firstLevel;
		Offset = offset;
		Size = (int)size;
		Overlaps = new List<Texture>();
		Dependencies = new List<TextureDependency>();
		BaseSlice = baseSlice;
		SliceCount = sliceCount;
		if (views != null)
		{
			RecalculateOverlaps(group, views);
		}
		Handles = handles;
		TrackingOffsets = trackingOffsets;
		if (group.Storage.Info.IsLinear)
		{
			_flushBalance = 13;
		}
		for (int i = 0; i < handles.Length; i++)
		{
			handles[i].RegisterDirtyEvent(DirtyAction);
		}
	}

	internal void ReplaceTracking(RegionHandle[] handles, ulong[] offsets)
	{
		HashSet<RegionHandle> hashSet = new HashSet<RegionHandle>(Handles);
		HashSet<RegionHandle> hashSet2 = new HashSet<RegionHandle>(handles);
		RegionHandle[] array = handles;
		foreach (RegionHandle regionHandle in array)
		{
			if (!hashSet.Contains(regionHandle))
			{
				regionHandle.RegisterDirtyEvent(DirtyAction);
				if (Modified)
				{
					_group.RegisterAction(this, regionHandle);
				}
			}
		}
		RegionHandle[] handles2 = Handles;
		Handles = handles;
		TrackingOffsets = offsets;
		if (handles.Length == 0)
		{
			Modified = false;
		}
		array = handles2;
		foreach (RegionHandle regionHandle2 in array)
		{
			if (!hashSet2.Contains(regionHandle2))
			{
				regionHandle2.Dispose();
			}
		}
	}

	private void DirtyAction()
	{
		_group.Storage.SignalGroupDirty();
		lock (Overlaps)
		{
			foreach (Texture overlap in Overlaps)
			{
				overlap.SignalGroupDirty();
			}
		}
		DeferredCopy = null;
	}

	public void DiscardData()
	{
		DeferredCopy = null;
		RegionHandle[] handles = Handles;
		foreach (RegionHandle regionHandle in handles)
		{
			if (regionHandle.Dirty)
			{
				regionHandle.Reprotect();
			}
		}
	}

	public void RecalculateOverlaps(TextureGroup group, IEnumerable<Texture> views)
	{
		lock (Overlaps)
		{
			int num = Offset + Size;
			Overlaps.Clear();
			foreach (Texture view in views)
			{
				int num2 = group.FindOffset(view);
				if (num2 < num && Offset < num2 + (int)view.Size)
				{
					Overlaps.Add(view);
				}
			}
		}
	}

	private bool NextSyncCopies()
	{
		return _flushBalance - 1 > 7;
	}

	private bool ModifyFlushBalance(int modifier)
	{
		int flushBalance;
		int num;
		do
		{
			flushBalance = _flushBalance;
			num = Math.Max(-10, Math.Min(60, flushBalance + modifier));
		}
		while (Interlocked.CompareExchange(ref _flushBalance, num, flushBalance) != flushBalance);
		return num > 7;
	}

	public void AddOverlap(int offset, Texture view)
	{
		if (OverlapsWith(offset, (int)view.Size))
		{
			lock (Overlaps)
			{
				Overlaps.Add(view);
			}
		}
	}

	public void RemoveOverlap(int offset, Texture view)
	{
		if (OverlapsWith(offset, (int)view.Size))
		{
			lock (Overlaps)
			{
				Overlaps.Remove(view);
			}
		}
	}

	private void RegisterSync(GpuContext context)
	{
		if (!_syncActionRegistered)
		{
			_modifiedSync = context.SyncNumber;
			context.RegisterSyncAction(this, syncpointOnly: true);
			_syncActionRegistered = true;
		}
		if (Interlocked.Exchange(ref _actionRegistered, 1) == 0)
		{
			_group.RegisterAction(this);
		}
	}

	public void SignalModified(GpuContext context)
	{
		Modified = true;
		foreach (TextureDependency dependency in Dependencies)
		{
			dependency.SignalModified();
		}
		RegisterSync(context);
	}

	public void SignalModifying(bool bound, GpuContext context)
	{
		SignalModified(context);
		if (!bound && _syncActionRegistered && NextSyncCopies())
		{
			context.CreateHostSyncIfNeeded(HostSyncFlags.Force);
		}
		_bindCount = Math.Max(0, _bindCount + (bound ? 1 : (-1)));
	}

	public void SynchronizeDependents()
	{
		foreach (TextureDependency dependency in Dependencies)
		{
			TextureGroupHandle handle = dependency.Other.Handle;
			if (handle.DeferredCopy == this)
			{
				handle._group.Storage.SynchronizeMemory();
			}
		}
	}

	public bool Sync(GpuContext context)
	{
		bool flag = _registeredBufferGuestSync != ulong.MaxValue;
		ulong num = (flag ? _registeredBufferGuestSync : _registeredSync);
		ulong num2 = context.SyncNumber - num;
		ModifyFlushBalance(6);
		if ((long)num2 > 0L)
		{
			context.Renderer.WaitSync(num);
			if ((long)(_modifiedSync - num) > 0L)
			{
				return flag;
			}
			Modified = false;
			return flag;
		}
		return false;
	}

	public void ClearActionRegistered()
	{
		Interlocked.Exchange(ref _actionRegistered, 0);
	}

	public void SyncPreAction(bool syncpoint)
	{
		if ((syncpoint || NextSyncCopies()) && ModifyFlushBalance(0) && _registeredBufferSync != _modifiedSync)
		{
			_group.FlushIntoBuffer(this);
			_registeredBufferSync = _modifiedSync;
		}
	}

	public bool SyncAction(bool syncpoint)
	{
		_group.Storage.SignalModifiedDirty();
		bool flag = _registeredBufferSync == _modifiedSync;
		if (!flag)
		{
			_registeredBufferSync = ulong.MaxValue;
		}
		lock (Overlaps)
		{
			foreach (Texture overlap in Overlaps)
			{
				overlap.SignalModifiedDirty();
			}
		}
		_registeredSync = _modifiedSync;
		_syncActionRegistered = false;
		if (Interlocked.Exchange(ref _actionRegistered, 1) == 0)
		{
			_group.RegisterAction(this);
		}
		if (syncpoint)
		{
			_registeredBufferGuestSync = _registeredBufferSync;
		}
		if (!syncpoint)
		{
			return !flag;
		}
		return true;
	}

	public void DeferCopy(TextureGroupHandle copyFrom)
	{
		Modified = false;
		DeferredCopy = copyFrom;
		_group.Storage.SignalGroupDirty();
		foreach (Texture overlap in Overlaps)
		{
			overlap.SignalGroupDirty();
		}
	}

	public void CreateCopyDependency(TextureGroupHandle other, bool copyToOther = false)
	{
		foreach (TextureDependency dependency in Dependencies)
		{
			if (dependency.Other.Handle == other)
			{
				return;
			}
		}
		_group.HasCopyDependencies = true;
		other._group.HasCopyDependencies = true;
		TextureDependency textureDependency = new TextureDependency(this);
		TextureDependency textureDependency2 = (textureDependency.Other = new TextureDependency(other));
		textureDependency2.Other = textureDependency;
		Dependencies.Add(textureDependency);
		other.Dependencies.Add(textureDependency2);
		TextureDependency[] array = Dependencies.ToArray();
		foreach (TextureDependency textureDependency3 in array)
		{
			if (textureDependency3 != textureDependency && textureDependency3.Other.Handle != other)
			{
				textureDependency3.Other.Handle.CreateCopyDependency(other);
			}
		}
		array = other.Dependencies.ToArray();
		foreach (TextureDependency textureDependency4 in array)
		{
			if (textureDependency4 != textureDependency2 && textureDependency4.Other.Handle != this)
			{
				textureDependency4.Other.Handle.CreateCopyDependency(this);
				if (copyToOther && Modified)
				{
					textureDependency4.Other.Handle.DeferCopy(this);
				}
			}
		}
	}

	public void RemoveDependency(TextureDependency dependency)
	{
		Dependencies.Remove(dependency);
	}

	private bool CheckDirty()
	{
		return Array.Exists(Handles, (RegionHandle handle) => handle.Dirty);
	}

	public bool Copy(GpuContext context, TextureGroupHandle fromHandle = null)
	{
		bool result = false;
		bool flag = false;
		if (fromHandle == null)
		{
			fromHandle = DeferredCopy;
			if (fromHandle != null)
			{
				flag = true;
				if (fromHandle._bindCount == 0)
				{
					DeferredCopy = null;
				}
			}
		}
		else
		{
			flag = !fromHandle.CheckDirty() && (fromHandle.Modified || !Modified);
		}
		if (flag)
		{
			Texture storage = fromHandle._group.Storage;
			Texture storage2 = _group.Storage;
			if (storage.ScaleFactor != storage2.ScaleFactor)
			{
				storage2.PropagateScale(storage);
			}
			storage.HostTexture.CopyTo(storage2.HostTexture, fromHandle._firstLayer, _firstLayer, fromHandle._firstLevel, _firstLevel);
			if (fromHandle.Modified)
			{
				Modified = true;
				RegisterSync(context);
			}
			result = true;
		}
		return result;
	}

	public bool HasDependencyTo(TextureGroup group)
	{
		foreach (TextureDependency dependency in Dependencies)
		{
			if (dependency.Other.Handle._group == group)
			{
				return true;
			}
		}
		return false;
	}

	public void Inherit(TextureGroupHandle old, bool withCopies)
	{
		Modified |= old.Modified;
		if (!withCopies)
		{
			return;
		}
		TextureDependency[] array = old.Dependencies.ToArray();
		foreach (TextureDependency textureDependency in array)
		{
			CreateCopyDependency(textureDependency.Other.Handle);
			if (textureDependency.Other.Handle.DeferredCopy == old)
			{
				textureDependency.Other.Handle.DeferredCopy = this;
			}
		}
		DeferredCopy = old.DeferredCopy;
	}

	public bool OverlapsWith(int offset, int size)
	{
		if (Offset < offset + size)
		{
			return offset < Offset + Size;
		}
		return false;
	}

	public void Dispose()
	{
		RegionHandle[] handles = Handles;
		for (int i = 0; i < handles.Length; i++)
		{
			handles[i].Dispose();
		}
		TextureDependency[] array = Dependencies.ToArray();
		foreach (TextureDependency textureDependency in array)
		{
			textureDependency.Other.Handle.RemoveDependency(textureDependency.Other);
		}
	}
}
