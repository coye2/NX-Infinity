using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Ryujinx.Common;
using Ryujinx.Common.Memory;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.Graphics.Texture;
using Ryujinx.Memory;
using Ryujinx.Memory.Range;
using Ryujinx.Memory.Tracking;

namespace Ryujinx.Graphics.Gpu.Image;

internal class TextureGroup : IDisposable
{
	private delegate void HandlesCallbackDelegate(int baseHandle, int regionCount, bool split = false);

	private const int GranularLayerThreshold = 8;

	private readonly GpuContext _context;

	private readonly PhysicalMemory _physicalMemory;

	private int[] _allOffsets;

	private int[] _sliceSizes;

	private readonly bool _is3D;

	private readonly bool _isBuffer;

	private bool _hasMipViews;

	private bool _hasLayerViews;

	private readonly int _layers;

	private readonly int _levels;

	private Texture[] _views;

	private TextureGroupHandle[] _handles;

	private bool[] _loadNeeded;

	private readonly List<TextureIncompatibleOverlap> _incompatibleOverlaps;

	private bool _incompatibleOverlapsDirty = true;

	private readonly bool _flushIncompatibleOverlaps;

	private BufferHandle _flushBuffer;

	private bool _flushBufferImported;

	private bool _flushBufferInvalid;

	public Texture Storage { get; }

	public bool HasCopyDependencies { get; set; }

	public bool HasFlushBuffer => _flushBuffer != BufferHandle.Null;

	public bool HasIncompatibleOverlaps => _incompatibleOverlaps.Count > 0;

	public long ModifiedSequence { get; private set; }

	private MultiRange TextureRange => Storage.Range;

	public TextureGroup(GpuContext context, PhysicalMemory physicalMemory, Texture storage, List<TextureIncompatibleOverlap> incompatibleOverlaps)
	{
		Storage = storage;
		_context = context;
		_physicalMemory = physicalMemory;
		_is3D = storage.Info.Target == Target.Texture3D;
		_isBuffer = storage.Info.Target == Target.TextureBuffer;
		_layers = storage.Info.GetSlices();
		_levels = storage.Info.Levels;
		_incompatibleOverlaps = incompatibleOverlaps;
		_flushIncompatibleOverlaps = TextureCompatibility.IsFormatHostIncompatible(storage.Info, context.Capabilities);
	}

	public void Initialize(ref SizeInfo size, bool hasLayerViews, bool hasMipViews)
	{
		_allOffsets = size.AllOffsets;
		_sliceSizes = size.SliceSizes;
		if (Storage.Target.HasDepthOrLayers() && Storage.Info.GetSlices() > 8)
		{
			_hasLayerViews = true;
			_hasMipViews = true;
		}
		else
		{
			(bool, bool) tuple = PropagateGranularity(hasLayerViews, hasMipViews);
			_hasLayerViews = tuple.Item1;
			_hasMipViews = tuple.Item2;
			MultiRange range = Storage.Range;
			for (int i = 0; i < range.Count; i++)
			{
				if (range.GetSubRange(i).Address == ulong.MaxValue)
				{
					_hasLayerViews = true;
					_hasMipViews = true;
					break;
				}
			}
		}
		RecalculateHandleRegions();
	}

	public void InitializeOverlaps()
	{
		foreach (TextureIncompatibleOverlap incompatibleOverlap in _incompatibleOverlaps)
		{
			if (incompatibleOverlap.Compatibility == TextureViewCompatibility.LayoutIncompatible)
			{
				CreateCopyDependency(incompatibleOverlap.Group, copyTo: false);
			}
			incompatibleOverlap.Group._incompatibleOverlaps.Add(new TextureIncompatibleOverlap(this, incompatibleOverlap.Compatibility));
			incompatibleOverlap.Group._incompatibleOverlapsDirty = true;
		}
		if (_incompatibleOverlaps.Count > 0)
		{
			SignalIncompatibleOverlapModified();
		}
	}

	private void SignalAllDirty()
	{
		Storage.SignalGroupDirty();
		if (_views != null)
		{
			Texture[] views = _views;
			for (int i = 0; i < views.Length; i++)
			{
				views[i].SignalGroupDirty();
			}
		}
	}

	private void SignalIncompatibleOverlapModified()
	{
		_incompatibleOverlapsDirty = true;
		if (_flushIncompatibleOverlaps)
		{
			SignalAllDirty();
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool FlushIncompatibleOverlapsIfNeeded()
	{
		if (_flushIncompatibleOverlaps && _incompatibleOverlapsDirty)
		{
			bool flag = false;
			foreach (TextureIncompatibleOverlap incompatibleOverlap in _incompatibleOverlaps)
			{
				flag |= incompatibleOverlap.Group.Storage.FlushModified();
			}
			_incompatibleOverlapsDirty = false;
			return flag;
		}
		return false;
	}

	public bool CheckDirty(Texture texture, bool consume)
	{
		bool dirty = false;
		EvaluateRelevantHandles(texture, (int baseHandle, int regionCount, bool split) =>
		{
			for (int i = 0; i < regionCount; i++)
			{
				RegionHandle[] handles = _handles[baseHandle + i].Handles;
				foreach (RegionHandle regionHandle in handles)
				{
					if (regionHandle.Dirty)
					{
						if (consume)
						{
							regionHandle.Reprotect();
						}
						dirty = true;
					}
				}
			}
		});
		return dirty;
	}

	public void DiscardData(Texture texture)
	{
		EvaluateRelevantHandles(texture, (int baseHandle, int regionCount, bool split) =>
		{
			for (int i = 0; i < regionCount; i++)
			{
				_handles[baseHandle + i].DiscardData();
			}
		});
	}

	public void SynchronizeMemory(Texture texture)
	{
		FlushIncompatibleOverlapsIfNeeded();
		EvaluateRelevantHandles(texture, (int baseHandle, int regionCount, bool split) =>
		{
			bool flag = false;
			bool flag2 = false;
			bool flag3 = false;
			for (int i = 0; i < regionCount; i++)
			{
				TextureGroupHandle textureGroupHandle = _handles[baseHandle + i];
				bool modified = textureGroupHandle.Modified;
				bool flag4 = false;
				bool flag5 = false;
				RegionHandle[] handles = textureGroupHandle.Handles;
				foreach (RegionHandle regionHandle in handles)
				{
					if (regionHandle.Dirty)
					{
						regionHandle.Reprotect();
						flag4 = true;
					}
					else
					{
						flag5 |= regionHandle.Unmapped;
					}
				}
				if (modified)
				{
					flag4 = false;
				}
				if (textureGroupHandle.NeedsCopy && textureGroupHandle.Copy(_context))
				{
					flag2 = (byte)((flag2 ? 1u : 0u) | 1u) != 0;
					flag4 = false;
				}
				else
				{
					flag2 |= modified;
					flag |= flag4;
				}
				if (textureGroupHandle.NeedsCopy)
				{
					texture.SignalGroupDirty();
				}
				bool flag6 = flag4 && !flag5;
				flag3 |= !flag6;
				_loadNeeded[baseHandle + i] = flag6;
			}
			if (flag)
			{
				if (flag3 || (_handles.Length > 1 && (flag2 | split)))
				{
					SynchronizePartial(baseHandle, regionCount);
				}
				else
				{
					texture.SynchronizeFull();
				}
			}
		});
	}

	private void SynchronizePartial(int baseHandle, int regionCount)
	{
		int num = -1;
		int num2 = 0;
		ReadOnlySpan<byte> readOnlySpan = ReadOnlySpan<byte>.Empty;
		for (int i = 0; i < regionCount; i++)
		{
			if (!_loadNeeded[baseHandle + i])
			{
				continue;
			}
			(int, int, int, int, int) handleInformation = GetHandleInformation(baseHandle + i);
			if (num <= i - 1)
			{
				num = i;
				if (_is3D)
				{
					for (int j = i + 1; j < regionCount && _loadNeeded[baseHandle + j]; j++)
					{
						num = j;
					}
				}
				(int, int, int, int, int) tuple = ((num == i) ? handleInformation : GetHandleInformation(baseHandle + num));
				num2 = _allOffsets[handleInformation.Item5];
				int num3 = Math.Min(_allOffsets[tuple.Item5 + tuple.Item4 * tuple.Item3 - 1] + _sliceSizes[tuple.Item2 + tuple.Item3 - 1], (int)Storage.Size) - num2;
				readOnlySpan = _physicalMemory.GetSpan(Storage.Range.Slice((ulong)num2, (ulong)num3));
			}
			for (int k = 0; k < handleInformation.Item4; k++)
			{
				for (int l = 0; l < handleInformation.Item3; l++)
				{
					int offsetIndex = GetOffsetIndex(handleInformation.Item1 + k, handleInformation.Item2 + l);
					int num4 = _allOffsets[offsetIndex];
					ReadOnlySpan<byte> data = readOnlySpan.Slice(num4 - num2);
					MemoryOwner<byte> data2 = Storage.ConvertToHostCompatibleFormat(data, handleInformation.Item2 + l, single: true);
					Storage.SetData(data2, handleInformation.Item1 + k, handleInformation.Item2 + l);
				}
			}
		}
	}

	public void SynchronizeDependents(Texture texture)
	{
		EvaluateRelevantHandles(texture, (int baseHandle, int regionCount, bool split) =>
		{
			for (int i = 0; i < regionCount; i++)
			{
				_handles[baseHandle + i].SynchronizeDependents();
			}
		});
	}

	private bool ShouldFlushTriggerTracking()
	{
		foreach (TextureIncompatibleOverlap incompatibleOverlap in _incompatibleOverlaps)
		{
			if (incompatibleOverlap.Group._flushIncompatibleOverlaps)
			{
				return true;
			}
		}
		return false;
	}

	private void FlushTextureDataSliceToGuest(bool tracked, int sliceIndex, bool inBuffer, ITexture texture = null)
	{
		(int BaseLayer, int BaseLevel) layerLevelForView = GetLayerLevelForView(sliceIndex);
		int item = layerLevelForView.BaseLayer;
		int item2 = layerLevelForView.BaseLevel;
		int num = _allOffsets[sliceIndex];
		int num2 = Math.Min(num + _sliceSizes[item2], (int)Storage.Size) - num;
		using WritableRegion writableRegion = _physicalMemory.GetWritableRegion(Storage.Range.Slice((ulong)num, (ulong)num2), tracked);
		Memory<byte> memory;
		if (inBuffer)
		{
			using (PinnedSpan<byte> pinnedSpan = _context.Renderer.GetBufferData(_flushBuffer, num, num2))
			{
				Texture storage = Storage;
				memory = writableRegion.Memory;
				storage.ConvertFromHostCompatibleFormat(memory.Span, pinnedSpan.Get(), item2, single: true);
				return;
			}
		}
		Texture storage2 = Storage;
		memory = writableRegion.Memory;
		storage2.GetTextureDataSliceFromGpu(memory.Span, item, item2, tracked, texture);
	}

	private void FlushSliceRange(bool tracked, int sliceStart, int sliceEnd, bool inBuffer, ITexture texture = null)
	{
		for (int i = sliceStart; i < sliceEnd; i++)
		{
			FlushTextureDataSliceToGuest(tracked, i, inBuffer, texture);
		}
	}

	public bool FlushModified(Texture texture, bool tracked)
	{
		tracked = tracked || ShouldFlushTriggerTracking();
		bool flushed = false;
		EvaluateRelevantHandles(texture, (int baseHandle, int regionCount, bool split) =>
		{
			int num = 0;
			int num2 = 0;
			bool flag = true;
			for (int i = 0; i < regionCount; i++)
			{
				TextureGroupHandle textureGroupHandle = _handles[baseHandle + i];
				if (textureGroupHandle.Modified)
				{
					if (num2 < textureGroupHandle.BaseSlice)
					{
						if (num2 > num)
						{
							FlushSliceRange(tracked, num, num2, inBuffer: false);
							flushed = true;
						}
						num = textureGroupHandle.BaseSlice;
					}
					num2 = textureGroupHandle.BaseSlice + textureGroupHandle.SliceCount;
					if (tracked)
					{
						textureGroupHandle.Modified = false;
						foreach (Texture overlap in textureGroupHandle.Overlaps)
						{
							overlap.SignalModifiedDirty();
						}
					}
				}
				else
				{
					flag = false;
				}
			}
			if (num2 > num)
			{
				if (flag && !split)
				{
					texture.Flush(tracked);
				}
				else
				{
					FlushSliceRange(tracked, num, num2, inBuffer: false);
				}
				flushed = true;
			}
		});
		Storage.SignalModifiedDirty();
		return flushed;
	}

	public void FlushIntoBuffer(TextureGroupHandle handle)
	{
		if (_flushBufferInvalid && _flushBuffer != BufferHandle.Null)
		{
			_flushBufferInvalid = false;
			_context.Renderer.DeleteBuffer(_flushBuffer);
			_flushBuffer = BufferHandle.Null;
		}
		if (_flushBuffer == BufferHandle.Null)
		{
			if (!TextureCompatibility.CanTextureFlush(Storage.Info, _context.Capabilities))
			{
				return;
			}
			nint num = ((Storage.Info.IsLinear && Storage.Info.Stride >= Storage.Info.Width * Storage.Info.FormatInfo.BytesPerPixel) ? _physicalMemory.GetHostPointer(Storage.Range) : 0);
			if (num != 0 && _context.Renderer.PrepareHostMapping(num, Storage.Size))
			{
				_flushBuffer = _context.Renderer.CreateBuffer(num, (int)Storage.Size);
				_flushBufferImported = true;
			}
			else
			{
				_flushBuffer = _context.Renderer.CreateBuffer((int)Storage.Size, BufferAccess.HostMemory);
				_flushBufferImported = false;
			}
			Storage.BlacklistScale();
		}
		int baseSlice = handle.BaseSlice;
		int num2 = baseSlice + handle.SliceCount;
		for (int i = baseSlice; i < num2; i++)
		{
			var (layer, num3) = GetLayerLevelForView(i);
			Storage.GetFlushTexture().CopyTo(new BufferRange(_flushBuffer, _allOffsets[i], _sliceSizes[num3]), layer, num3, _flushBufferImported ? Storage.Info.Stride : 0);
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void ClearIncompatibleOverlaps(Texture texture)
	{
		if (!_incompatibleOverlapsDirty)
		{
			return;
		}
		foreach (TextureIncompatibleOverlap incompatibleOverlap in _incompatibleOverlaps)
		{
			incompatibleOverlap.Group.ClearModified(texture.Range, this);
			incompatibleOverlap.Group.SignalIncompatibleOverlapModified();
		}
		_incompatibleOverlapsDirty = false;
	}

	public void SignalModified(Texture texture)
	{
		ModifiedSequence = _context.GetModifiedSequence();
		ClearIncompatibleOverlaps(texture);
		EvaluateRelevantHandles(texture, (int baseHandle, int regionCount, bool split) =>
		{
			for (int i = 0; i < regionCount; i++)
			{
				_handles[baseHandle + i].SignalModified(_context);
			}
		});
	}

	public void SignalModifying(Texture texture, bool bound)
	{
		ModifiedSequence = _context.GetModifiedSequence();
		ClearIncompatibleOverlaps(texture);
		EvaluateRelevantHandles(texture, (int baseHandle, int regionCount, bool split) =>
		{
			for (int i = 0; i < regionCount; i++)
			{
				_handles[baseHandle + i].SignalModifying(bound, _context);
			}
		});
	}

	public void RegisterAction(TextureGroupHandle group)
	{
		RegionHandle[] handles = group.Handles;
		foreach (RegionHandle handle in handles)
		{
			RegisterAction(group, handle);
		}
	}

	internal void RegisterAction(TextureGroupHandle group, RegionHandle handle)
	{
		handle.RegisterAction((ulong address, ulong size) =>
		{
			FlushAction(group, address, size);
		});
	}

	private (bool HasLayerViews, bool HasMipViews) PropagateGranularity(bool hasLayerViews, bool hasMipViews)
	{
		if (_is3D)
		{
			hasMipViews |= hasLayerViews;
		}
		else
		{
			hasLayerViews |= hasMipViews;
		}
		return (HasLayerViews: hasLayerViews, HasMipViews: hasMipViews);
	}

	private void EvaluateRelevantHandles(Texture texture, HandlesCallbackDelegate callback)
	{
		if (texture == Storage || (!_hasMipViews && !_hasLayerViews))
		{
			callback(0, _handles.Length);
		}
		else
		{
			EvaluateRelevantHandles(texture.FirstLayer, texture.FirstLevel, texture.Info.GetSlices(), texture.Info.Levels, callback);
		}
	}

	private void EvaluateRelevantHandles(int firstLayer, int firstLevel, int slices, int levels, HandlesCallbackDelegate callback)
	{
		int num = ((!_hasLayerViews) ? 1 : slices);
		int num2 = ((!_hasMipViews) ? 1 : levels);
		if (_isBuffer)
		{
			return;
		}
		if (_is3D)
		{
			if (!_hasLayerViews)
			{
				callback(firstLevel, num2);
				return;
			}
			var (num3, num4) = Get3DLevelRange(firstLevel);
			if (levels > 1 && slices < _layers)
			{
				while (levels-- > 1)
				{
					callback(firstLayer + num3, slices);
					num3 += num4;
					num4 = Math.Max(num4 >> 1, 1);
					slices = Math.Max(num4 >> 1, 1);
				}
			}
			else
			{
				int num5 = Math.Min(num4, slices);
				while (levels-- > 1)
				{
					num4 = Math.Max(num4 >> 1, 1);
					num5 += num4;
				}
				callback(firstLayer + num3, num5);
			}
			return;
		}
		int num6 = ((!_hasMipViews) ? 1 : _levels);
		if (slices > 1 && levels < _levels)
		{
			for (int i = 0; i < slices; i++)
			{
				callback(firstLevel + (firstLayer + i) * num6, num2, split: true);
			}
		}
		else
		{
			callback(firstLevel + firstLayer * num6, num2 + (num - 1) * num6);
		}
	}

	private (int Index, int Count) Get3DLevelRange(int level)
	{
		int num = 0;
		int num2 = _layers;
		while (level-- > 0)
		{
			num += num2;
			num2 = Math.Max(num2 >> 1, 1);
		}
		return (Index: num, Count: num2);
	}

	private (int BaseLayer, int BaseLevel, int Levels, int Layers, int Index) GetHandleInformation(int handleIndex)
	{
		int item = (_hasMipViews ? 1 : _levels);
		int item2 = (_hasLayerViews ? 1 : _layers);
		int item3;
		int num;
		int num3;
		if (_is3D)
		{
			if (_hasLayerViews)
			{
				item3 = handleIndex;
				num = 0;
				int num2 = _layers;
				while (handleIndex >= num2)
				{
					handleIndex -= num2;
					num++;
					num2 = Math.Max(num2 >> 1, 1);
				}
				num3 = handleIndex;
			}
			else
			{
				num3 = 0;
				num = handleIndex;
				item3 = Get3DLevelRange(num).Index;
			}
		}
		else
		{
			num = (_hasMipViews ? (handleIndex % _levels) : 0);
			num3 = (_hasMipViews ? (handleIndex / _levels) : handleIndex);
			item3 = num + num3 * _levels;
		}
		return (BaseLayer: num3, BaseLevel: num, Levels: item, Layers: item2, Index: item3);
	}

	private (int BaseLayer, int BaseLevel) GetLayerLevelForView(int index)
	{
		if (_is3D)
		{
			int num = 0;
			int num2 = _layers;
			while (index >= num2)
			{
				index -= num2;
				num++;
				num2 = Math.Max(num2 >> 1, 1);
			}
			return (BaseLayer: index, BaseLevel: num);
		}
		return (BaseLayer: index / _levels, BaseLevel: index % _levels);
	}

	public int FindOffset(Texture texture)
	{
		return _allOffsets[GetOffsetIndex(texture.FirstLayer, texture.FirstLevel)];
	}

	public int GetOffsetIndex(int layer, int level)
	{
		if (_is3D)
		{
			return layer + Get3DLevelRange(level).Index;
		}
		return level + layer * _levels;
	}

	private RegionHandle GenerateHandle(ulong address, ulong size)
	{
		return _physicalMemory.BeginTracking(address, size, ResourceKind.Texture);
	}

	private TextureGroupHandle GenerateHandles(int viewStart, int views, TextureRangeIndex currentRange, TextureRangeIndex previousRange, TextureGroupHandle previous)
	{
		int num = viewStart + views - 1;
		int item = GetLayerLevelForView(num).BaseLevel;
		int num2 = _allOffsets[viewStart];
		int num3 = _allOffsets[num] + _sliceSizes[item] - num2;
		ulong num4 = Math.Min((ulong)num3, Storage.Size - (ulong)num2);
		if (previous != null && previous.BaseSlice == viewStart && previous.SliceCount == views && previousRange != null && currentRange.Matches(previousRange, (ulong)num2, num4) && previous.Handles.All((RegionHandle handle) => !handle.Unmapped))
		{
			return previous;
		}
		List<RegionHandle> result = new List<RegionHandle>();
		List<ulong> trackingOffsets = new List<ulong>();
		if (previous != null && previous.BaseSlice == viewStart && previous.SliceCount == views && previousRange != null)
		{
			ulong num5 = (ulong)num2;
			for (int num6 = 0; num6 < previous.Handles.Length; num6++)
			{
				RegionHandle regionHandle = previous.Handles[num6];
				ulong num7 = previous.TrackingOffsets[num6];
				if (num5 < num7)
				{
					AddRange(num5, num7 - num5);
				}
				if (!regionHandle.Unmapped && currentRange.Matches(previousRange, num7, regionHandle.RealSize))
				{
					result.Add(regionHandle);
					trackingOffsets.Add(num7);
				}
				else
				{
					AddRange(num7, regionHandle.RealSize);
				}
				num5 = num7 + regionHandle.RealSize;
			}
			ulong num8 = (ulong)num2 + num4;
			if (num5 < num8)
			{
				AddRange(num5, num8 - num5);
			}
			previous.ReplaceTracking(result.ToArray(), trackingOffsets.ToArray());
			return previous;
		}
		AddRange((ulong)num2, num4);
		var (firstLayer, num9) = GetLayerLevelForView(viewStart);
		if (_hasLayerViews && _hasMipViews)
		{
			num3 = _sliceSizes[num9];
		}
		num2 = _allOffsets[viewStart];
		ulong val = Storage.Size - (ulong)num2;
		return new TextureGroupHandle(this, num2, Math.Min(val, (ulong)num3), _views, firstLayer, num9, viewStart, views, result.ToArray(), trackingOffsets.ToArray());
		void AddRange(ulong start, ulong length)
		{
			foreach (MemoryRange item2 in currentRange.Slice(start, length))
			{
				if (item2.Address != ulong.MaxValue)
				{
					result.Add(GenerateHandle(item2.Address, item2.Size));
					trackingOffsets.Add(start);
				}
				start += item2.Size;
			}
		}
	}

	public void UpdateViews(List<Texture> views, Texture texture)
	{
		_views = views.ToArray();
		bool flag = _hasLayerViews;
		bool flag2 = _hasMipViews;
		bool flag3 = false;
		if (!(flag & flag2))
		{
			foreach (Texture view in views)
			{
				if (view.Info.GetSlices() < _layers)
				{
					flag = true;
				}
				if (view.Info.Levels < _levels)
				{
					flag2 = true;
				}
			}
			(flag, flag2) = PropagateGranularity(flag, flag2);
			if (flag != _hasLayerViews || flag2 != _hasMipViews)
			{
				_hasLayerViews = flag;
				_hasMipViews = flag2;
				RecalculateHandleRegions();
				flag3 = true;
			}
		}
		if (!flag3)
		{
			if (texture != null)
			{
				int offset = FindOffset(texture);
				TextureGroupHandle[] handles = _handles;
				for (int i = 0; i < handles.Length; i++)
				{
					handles[i].AddOverlap(offset, texture);
				}
			}
			else
			{
				TextureGroupHandle[] handles = _handles;
				for (int i = 0; i < handles.Length; i++)
				{
					handles[i].RecalculateOverlaps(this, views);
				}
			}
		}
		SignalAllDirty();
	}

	public void RemoveView(List<Texture> views, Texture view)
	{
		_views = views.ToArray();
		int offset = FindOffset(view);
		TextureGroupHandle[] handles = _handles;
		for (int i = 0; i < handles.Length; i++)
		{
			handles[i].RemoveOverlap(offset, view);
		}
	}

	private void InheritHandles(TextureGroupHandle[] oldHandles, TextureGroupHandle[] handles, int relativeOffset)
	{
		HashSet<TextureGroupHandle> hashSet = new HashSet<TextureGroupHandle>(oldHandles);
		HashSet<TextureGroupHandle> hashSet2 = new HashSet<TextureGroupHandle>(handles);
		TextureGroupHandle[] array = handles;
		foreach (TextureGroupHandle group in array)
		{
			if (relativeOffset == 0 && hashSet.Contains(group))
			{
				continue;
			}
			RegionHandle[] handles2 = group.Handles;
			foreach (RegionHandle regionHandle in handles2)
			{
				bool flag = false;
				foreach (TextureGroupHandle textureGroupHandle in oldHandles)
				{
					if (!group.OverlapsWith(textureGroupHandle.Offset + relativeOffset, textureGroupHandle.Size))
					{
						continue;
					}
					RegionHandle[] handles3 = textureGroupHandle.Handles;
					foreach (RegionHandle regionHandle2 in handles3)
					{
						if (regionHandle.OverlapsWith(regionHandle2.Address, regionHandle2.Size))
						{
							flag |= regionHandle2.Dirty;
						}
					}
					group.Inherit(textureGroupHandle, group.Offset == textureGroupHandle.Offset + relativeOffset);
				}
				if (flag && !regionHandle.Dirty)
				{
					regionHandle.Reprotect(asDirty: true);
				}
				if (group.Modified)
				{
					regionHandle.RegisterAction((ulong address, ulong size) =>
					{
						FlushAction(group, address, size);
					});
				}
			}
		}
		array = oldHandles;
		foreach (TextureGroupHandle textureGroupHandle2 in array)
		{
			if (!hashSet2.Contains(textureGroupHandle2))
			{
				textureGroupHandle2.Modified = false;
			}
		}
	}

	public void Inherit(TextureGroup other)
	{
		bool flag = _hasLayerViews || other._hasLayerViews;
		bool flag2 = _hasMipViews || other._hasMipViews;
		if (flag != _hasLayerViews || flag2 != _hasMipViews)
		{
			_hasLayerViews = flag;
			_hasMipViews = flag2;
			RecalculateHandleRegions();
		}
		foreach (TextureIncompatibleOverlap incompatibleOverlap in other._incompatibleOverlaps)
		{
			RegisterIncompatibleOverlap(incompatibleOverlap, copy: false);
			incompatibleOverlap.Group._incompatibleOverlaps.RemoveAll((TextureIncompatibleOverlap overlap) => overlap.Group == other);
		}
		int relativeOffset = Storage.Range.FindOffset(other.Storage.Range);
		InheritHandles(other._handles, _handles, relativeOffset);
	}

	private void ReplaceHandles(TextureGroupHandle[] handles, bool rangeChanged)
	{
		if (_handles != null)
		{
			HashSet<TextureGroupHandle> hashSet = new HashSet<TextureGroupHandle>(_handles);
			HashSet<TextureGroupHandle> hashSet2 = new HashSet<TextureGroupHandle>(handles);
			TextureGroupHandle[] array = handles;
			foreach (TextureGroupHandle textureGroupHandle in array)
			{
				if (hashSet.Contains(textureGroupHandle))
				{
					continue;
				}
				if (rangeChanged)
				{
					if (textureGroupHandle.Handles.Length == 0)
					{
						continue;
					}
					HashSet<(ulong, ulong)> hashSet3 = new HashSet<(ulong, ulong)>();
					TextureGroupHandle[] handles2 = _handles;
					RegionHandle[] handles3;
					foreach (TextureGroupHandle textureGroupHandle2 in handles2)
					{
						if (textureGroupHandle.OverlapsWith(textureGroupHandle2.Offset, textureGroupHandle2.Size))
						{
							handles3 = textureGroupHandle2.Handles;
							foreach (RegionHandle regionHandle in handles3)
							{
								hashSet3.Add((regionHandle.RealAddress, regionHandle.RealSize));
							}
						}
					}
					handles3 = textureGroupHandle.Handles;
					foreach (RegionHandle regionHandle2 in handles3)
					{
						if (hashSet3.Contains((regionHandle2.RealAddress, regionHandle2.RealSize)))
						{
							regionHandle2.Reprotect();
						}
					}
				}
				else
				{
					RegionHandle[] handles3 = textureGroupHandle.Handles;
					for (int j = 0; j < handles3.Length; j++)
					{
						handles3[j].Reprotect();
					}
				}
			}
			InheritHandles(_handles, handles, 0);
			array = _handles;
			foreach (TextureGroupHandle textureGroupHandle3 in array)
			{
				if (!hashSet2.Contains(textureGroupHandle3))
				{
					RegionHandle[] handles3 = textureGroupHandle3.Handles;
					for (int j = 0; j < handles3.Length; j++)
					{
						handles3[j].Dispose();
					}
				}
			}
		}
		_handles = handles;
		_loadNeeded = new bool[_handles.Length];
	}

	private void RecalculateHandleRegions(bool rangeChanged = false, MultiRange? previousRange = null)
	{
		using (RyuStutterTrace.Measure("texture.tracking.rebuild", TextureRange.Count, RyuStutterTrace.Enabled ? $"{Storage.Info.Target} {Storage.Info.Width}x{Storage.Info.Height}x{Storage.Info.DepthOrLayers} levels={Storage.Info.Levels}" : null))
		{
			Dictionary<int, TextureGroupHandle> dictionary = ((rangeChanged && _handles != null) ? _handles.ToDictionary((TextureGroupHandle handle) => handle.BaseSlice) : null);
			TextureGroupHandle[] array;
			if (_isBuffer)
			{
				array = Array.Empty<TextureGroupHandle>();
			}
			else if (!_hasMipViews && !_hasLayerViews)
			{
				RegionHandle[] array2 = new RegionHandle[TextureRange.Count];
				ulong[] array3 = new ulong[TextureRange.Count];
				int num = 0;
				ulong num2 = 0uL;
				for (int num3 = 0; num3 < TextureRange.Count; num3++)
				{
					MemoryRange subRange = TextureRange.GetSubRange(num3);
					if (subRange.Address != ulong.MaxValue)
					{
						array3[num] = num2;
						array2[num++] = GenerateHandle(subRange.Address, subRange.Size);
					}
					num2 += subRange.Size;
				}
				if (num != TextureRange.Count)
				{
					Array.Resize(ref array2, num);
					Array.Resize(ref array3, num);
				}
				TextureGroupHandle textureGroupHandle = new TextureGroupHandle(this, 0, Storage.Size, _views, 0, 0, 0, _allOffsets.Length, array2, array3);
				array = new TextureGroupHandle[1] { textureGroupHandle };
			}
			else
			{
				TextureRangeIndex currentRange = new TextureRangeIndex(TextureRange);
				object obj;
				if (previousRange.HasValue)
				{
					MultiRange valueOrDefault = previousRange.GetValueOrDefault();
					obj = new TextureRangeIndex(valueOrDefault);
				}
				else
				{
					obj = null;
				}
				TextureRangeIndex previousRange2 = (TextureRangeIndex)obj;
				int num4 = ((!_hasLayerViews) ? 1 : _layers);
				int num5 = ((!_hasMipViews) ? 1 : _levels);
				int num6 = 0;
				if (_is3D)
				{
					List<TextureGroupHandle> list = new List<TextureGroupHandle>();
					for (int num7 = 0; num7 < num5; num7++)
					{
						for (int num8 = 0; num8 < num4; num8++)
						{
							(int Index, int Count) tuple = Get3DLevelRange(num7);
							int item = tuple.Index;
							int item2 = tuple.Count;
							item += num8;
							item2 = (_hasLayerViews ? 1 : item2);
							list.Add(GenerateHandles(item, item2, currentRange, previousRange2, dictionary?.GetValueOrDefault(item)));
						}
						num4 = Math.Max(1, num4 >> 1);
					}
					array = list.ToArray();
				}
				else
				{
					array = new TextureGroupHandle[num4 * num5];
					for (int num9 = 0; num9 < num4; num9++)
					{
						for (int num10 = 0; num10 < num5; num10++)
						{
							int num11 = num10 + num9 * _levels;
							int views = (_hasMipViews ? 1 : _levels);
							array[num6++] = GenerateHandles(num11, views, currentRange, previousRange2, dictionary?.GetValueOrDefault(num11));
						}
					}
				}
			}
			ReplaceHandles(array, rangeChanged);
		}
	}

	public void RangeChanged(MultiRange previousRange)
	{
		_hasLayerViews = true;
		_hasMipViews = true;
		RecalculateHandleRegions(rangeChanged: true, previousRange);
		SignalAllDirty();
	}

	private void EnsureFullSubdivision()
	{
		if (!_hasLayerViews || !_hasMipViews)
		{
			_hasLayerViews = true;
			_hasMipViews = true;
			RecalculateHandleRegions();
		}
	}

	public void CreateCopyDependency(Texture other, int firstLayer, int firstLevel, bool copyTo)
	{
		TextureGroup textureGroup = other.Group;
		EnsureFullSubdivision();
		textureGroup.EnsureFullSubdivision();
		List<(int BaseHandle, int RegionCount)> targetRange = new List<(int, int)>();
		List<(int BaseHandle, int RegionCount)> otherRange = new List<(int, int)>();
		EvaluateRelevantHandles(firstLayer, firstLevel, other.Info.GetSlices(), other.Info.Levels, (int baseHandle, int regionCount, bool split) =>
		{
			targetRange.Add((baseHandle, regionCount));
		});
		textureGroup.EvaluateRelevantHandles(other, (int baseHandle, int regionCount, bool split) =>
		{
			otherRange.Add((baseHandle, regionCount));
		});
		int num = 0;
		int num2 = 0;
		(int, int) tuple = (0, 0);
		(int, int) tuple2 = (0, 0);
		while (true)
		{
			if (tuple.Item2 == 0)
			{
				if (num >= targetRange.Count)
				{
					break;
				}
				tuple = targetRange[num++];
			}
			if (tuple2.Item2 == 0)
			{
				if (num2 >= otherRange.Count)
				{
					break;
				}
				tuple2 = otherRange[num2++];
			}
			TextureGroupHandle textureGroupHandle = _handles[tuple.Item1++];
			TextureGroupHandle textureGroupHandle2 = other.Group._handles[tuple2.Item1++];
			tuple.Item2--;
			tuple2.Item2--;
			textureGroupHandle.CreateCopyDependency(textureGroupHandle2, copyTo);
			if (copyTo)
			{
				textureGroupHandle2.Copy(_context, textureGroupHandle);
			}
			else
			{
				textureGroupHandle.Copy(_context, textureGroupHandle2);
			}
		}
	}

	public void CreateCopyDependency(TextureGroup other, bool copyTo)
	{
		for (int i = 0; i < _allOffsets.Length; i++)
		{
			int item = GetLayerLevelForView(i).BaseLevel;
			ulong address = Storage.Range.Slice((ulong)_allOffsets[i], 1uL).GetSubRange(0).Address;
			for (int j = 0; j < other._handles.Length; j++)
			{
				int item2 = other.GetLayerLevelForView(j).BaseLevel;
				ulong address2 = other.Storage.Range.Slice((ulong)other._allOffsets[j], 1uL).GetSubRange(0).Address;
				if (address != address2)
				{
					continue;
				}
				TextureInfo info = Storage.Info;
				TextureInfo info2 = other.Storage.Info;
				if (TextureCompatibility.ViewLayoutCompatible(info, info2, item, item2) && TextureCompatibility.CopySizeMatches(info, info2, item, item2))
				{
					EnsureFullSubdivision();
					other.EnsureFullSubdivision();
					TextureGroupHandle textureGroupHandle = _handles[i];
					TextureGroupHandle textureGroupHandle2 = other._handles[j];
					textureGroupHandle.CreateCopyDependency(textureGroupHandle2, copyTo);
					if (copyTo)
					{
						textureGroupHandle2.Copy(_context, textureGroupHandle);
					}
					else
					{
						textureGroupHandle.Copy(_context, textureGroupHandle2);
					}
				}
			}
		}
	}

	public void RegisterIncompatibleOverlap(TextureIncompatibleOverlap other, bool copy)
	{
		if (!_incompatibleOverlaps.Any((TextureIncompatibleOverlap overlap) => overlap.Group == other.Group))
		{
			if (copy && other.Compatibility == TextureViewCompatibility.LayoutIncompatible)
			{
				CreateCopyDependency(other.Group, copyTo: false);
			}
			_incompatibleOverlaps.Add(other);
			other.Group._incompatibleOverlaps.Add(new TextureIncompatibleOverlap(this, other.Compatibility));
		}
		other.Group.SignalIncompatibleOverlapModified();
		SignalIncompatibleOverlapModified();
	}

	public void ClearModified(MultiRange range, TextureGroup ignore = null)
	{
		TextureGroupHandle[] handles = _handles;
		foreach (TextureGroupHandle textureGroupHandle in handles)
		{
			MultiRange other = Storage.Range.Slice((ulong)textureGroupHandle.Offset, (ulong)textureGroupHandle.Size);
			if (!range.OverlapsWith(other) || (ignore != null && textureGroupHandle.HasDependencyTo(ignore)) || !textureGroupHandle.Modified)
			{
				continue;
			}
			textureGroupHandle.Modified = false;
			textureGroupHandle.DeferredCopy = null;
			Storage.SignalModifiedDirty();
			lock (textureGroupHandle.Overlaps)
			{
				foreach (Texture overlap in textureGroupHandle.Overlaps)
				{
					overlap.SignalModifiedDirty();
				}
			}
		}
		Storage.SignalModifiedDirty();
		Texture[] views = _views;
		if (views != null)
		{
			Texture[] array = views;
			for (int i = 0; i < array.Length; i++)
			{
				array[i].SignalModifiedDirty();
			}
		}
	}

	public void FlushAction(TextureGroupHandle handle, ulong address, ulong size)
	{
		handle.ClearActionRegistered();
		if (!handle.Modified)
		{
			return;
		}
		bool isGpuThread = _context.IsGpuThread();
		if (isGpuThread)
		{
			handle.Modified = false;
		}
		_context.Renderer.BackgroundContextAction(() =>
		{
			bool flag = !isGpuThread && handle.Sync(_context);
			Storage.SignalModifiedDirty();
			lock (handle.Overlaps)
			{
				foreach (Texture overlap in handle.Overlaps)
				{
					overlap.SignalModifiedDirty();
				}
			}
			if (TextureCompatibility.CanTextureFlush(Storage.Info, _context.Capabilities) && (!flag || !_flushBufferImported))
			{
				FlushSliceRange(tracked: false, handle.BaseSlice, handle.BaseSlice + handle.SliceCount, flag, Storage.GetFlushTexture());
			}
		});
	}

	public void Unmapped()
	{
		if (_flushBufferImported)
		{
			_flushBufferInvalid = true;
		}
	}

	public void Dispose()
	{
		TextureGroupHandle[] handles = _handles;
		for (int i = 0; i < handles.Length; i++)
		{
			handles[i].Dispose();
		}
		foreach (TextureIncompatibleOverlap incompatibleOverlap in _incompatibleOverlaps)
		{
			incompatibleOverlap.Group._incompatibleOverlaps.RemoveAll((TextureIncompatibleOverlap overlap) => overlap.Group == this);
		}
		if (_flushBuffer != BufferHandle.Null)
		{
			_context.Renderer.DeleteBuffer(_flushBuffer);
		}
	}
}
