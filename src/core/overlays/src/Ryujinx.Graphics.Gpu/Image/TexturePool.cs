using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Texture;
using Ryujinx.Memory.Range;

namespace Ryujinx.Graphics.Gpu.Image;

internal class TexturePool : Pool<Texture, TextureDescriptor>, IPool<TexturePool>
{
	private readonly struct DereferenceRequest
	{
		public readonly bool IsRemapped;

		public readonly Texture Texture;

		public readonly int ID;

		private DereferenceRequest(bool isRemapped, Texture texture, int id)
		{
			IsRemapped = isRemapped;
			Texture = texture;
			ID = id;
		}

		public static DereferenceRequest Remove(Texture texture)
		{
			return new DereferenceRequest(isRemapped: false, texture, 0);
		}

		public static DereferenceRequest Remap(Texture texture, int id)
		{
			return new DereferenceRequest(isRemapped: true, texture, id);
		}
	}

	private class TextureAliasList
	{
		private readonly record struct Alias(Format Format, Texture Texture);

		private readonly List<Alias> _aliases;

		public TextureAliasList()
		{
			_aliases = new List<Alias>();
		}

		public void Add(Format format, Texture texture)
		{
			_aliases.Add(new Alias(format, texture));
			texture.IncrementReferenceCount();
		}

		public Texture Find(Format format)
		{
			foreach (Alias alias in _aliases)
			{
				if (alias.Format == format)
				{
					return alias.Texture;
				}
			}
			return null;
		}

		public void Destroy()
		{
			foreach (Alias alias in _aliases)
			{
				alias.Texture.DecrementReferenceCount();
			}
			_aliases.Clear();
		}
	}

	private readonly GpuChannel _channel;

	private readonly ConcurrentQueue<DereferenceRequest> _dereferenceQueue = new ConcurrentQueue<DereferenceRequest>();

	private TextureDescriptor _defaultDescriptor;

	private int _invalidSizeReports;

	private readonly Dictionary<Texture, TextureAliasList> _aliasLists;

	public LinkedListNode<TexturePool> CacheNode { get; set; }

	public ulong CacheTimestamp { get; set; }

	public TexturePool(GpuContext context, GpuChannel channel, ulong address, int maximumId)
		: base(context, channel.MemoryManager.Physical, address, maximumId)
	{
		_channel = channel;
		_aliasLists = new Dictionary<Texture, TextureAliasList>();
	}

	private ref readonly TextureDescriptor GetInternal(int id, out Texture texture)
	{
		texture = Items[id];
		ref readonly TextureDescriptor descriptorRef = ref GetDescriptorRef(id);
		if (texture == null)
		{
			texture = PhysicalMemory.TextureCache.FindShortCache(in descriptorRef);
			if (texture == null)
			{
				if ((texture = ProcessDereferenceQueue(id)) != null)
				{
					return ref descriptorRef;
				}
				TextureInfo info = GetInfo(in descriptorRef, out var layerSize);
				texture = PhysicalMemory.TextureCache.FindOrCreateTexture(_channel.MemoryManager, TextureSearchFlags.ForSampler, info, layerSize);
				if (texture == null)
				{
					return ref descriptorRef;
				}
			}
			else
			{
				texture.SynchronizeMemory();
			}
			Items[id] = texture;
			texture.IncrementReferenceCount(this, id, descriptorRef.UnpackAddress());
			DescriptorCache[id] = descriptorRef;
		}
		else
		{
			texture.SynchronizeMemory();
		}
		return ref descriptorRef;
	}

	public override Texture Get(int id)
	{
		return Get(id, srgbSampler: true);
	}

	public Texture Get(int id, bool srgbSampler)
	{
		if ((uint)id >= Items.Length)
		{
			return null;
		}
		if (SequenceNumber != Context.SequenceNumber)
		{
			SequenceNumber = Context.SequenceNumber;
			SynchronizeMemory();
		}
		GetForBinding(id, srgbSampler, out var texture);
		return texture;
	}

	public ref readonly TextureDescriptor GetForBinding(int id, bool srgbSampler, out Texture texture)
	{
		try
		{
			if ((uint)id >= Items.Length)
			{
				texture = null;
				return ref _defaultDescriptor;
			}
			if (!srgbSampler)
			{
				ref readonly TextureDescriptor descriptorRef = ref GetDescriptorRef(id);
				if (descriptorRef.UnpackSrgb() && FormatTable.TryGetTextureFormat(descriptorRef.UnpackFormat(), isSrgb: false, out var format))
				{
					return ref GetForBinding(id, format, out texture);
				}
			}
			return ref GetInternal(id, out texture);
		}
		catch (InvalidTextureSizeException ex)
		{
			if (_invalidSizeReports < 8)
			{
				_invalidSizeReports++;
				Logger.Error?.Print(LogClass.Gpu, ex.Message, "GetForBinding");
			}
			texture = null;
			return ref _defaultDescriptor;
		}
	}

	public ref readonly TextureDescriptor GetForBinding(int id, FormatInfo formatInfo, out Texture texture)
	{
		try
		{
			if ((uint)id >= Items.Length)
			{
				texture = null;
				return ref _defaultDescriptor;
			}
			ref readonly TextureDescriptor reference = ref GetInternal(id, out texture);
			if (texture != null && formatInfo.Format != Format.R8Unorm && texture.Format != formatInfo.Format)
			{
				if (!_aliasLists.TryGetValue(texture, out var value))
				{
					_aliasLists.Add(texture, value = new TextureAliasList());
				}
				texture = value.Find(formatInfo.Format);
				if (texture == null)
				{
					TextureInfo info = ChangeFormat(GetInfo(in reference, out var layerSize), formatInfo);
					texture = PhysicalMemory.TextureCache.FindOrCreateTexture(_channel.MemoryManager, TextureSearchFlags.ForSampler, info, layerSize);
					if (texture != null)
					{
						value.Add(formatInfo.Format, texture);
					}
				}
			}
			return ref reference;
		}
		catch (InvalidTextureSizeException ex)
		{
			if (_invalidSizeReports < 8)
			{
				_invalidSizeReports++;
				Logger.Error?.Print(LogClass.Gpu, ex.Message, "GetForBinding");
			}
			texture = null;
			return ref _defaultDescriptor;
		}
	}

	public int CheckModified()
	{
		if (SequenceNumber != Context.SequenceNumber)
		{
			SequenceNumber = Context.SequenceNumber;
			SynchronizeMemory();
		}
		return ModifiedSequenceNumber;
	}

	public void ForceRemove(Texture texture, int id, bool deferred)
	{
		Texture texture2 = Interlocked.Exchange(ref Items[id], null);
		if (deferred)
		{
			if (texture2 != null)
			{
				_dereferenceQueue.Enqueue(DereferenceRequest.Remove(texture));
			}
		}
		else
		{
			texture.DecrementReferenceCount();
			RemoveAliasList(texture);
		}
	}

	public void QueueUpdateMapping(Texture texture, int id)
	{
		if (Interlocked.Exchange(ref Items[id], null) == texture)
		{
			_dereferenceQueue.Enqueue(DereferenceRequest.Remap(texture, id));
		}
	}

	private Texture ProcessDereferenceQueue(int id = -1)
	{
		DereferenceRequest result;
		while (_dereferenceQueue.TryDequeue(out result))
		{
			Texture texture = result.Texture;
			if (result.IsRemapped && texture.Group.Storage == texture && !texture.HasViews && !texture.Group.HasCopyDependencies)
			{
				ref readonly TextureDescriptor descriptorRef = ref GetDescriptorRef(result.ID);
				ulong num = descriptorRef.UnpackAddress();
				if (!descriptorRef.Equals(ref DescriptorCache[result.ID]))
				{
					texture.DecrementReferenceCount();
					continue;
				}
				MultiRange multiRange = _channel.MemoryManager.Physical.TextureCache.UpdatePartiallyMapped(_channel.MemoryManager, num, texture);
				if (multiRange.Count == 1 && multiRange.GetSubRange(0).Address == ulong.MaxValue)
				{
					texture.DecrementReferenceCount();
					continue;
				}
				Items[result.ID] = texture;
				texture.IncrementReferenceCount(this, result.ID, num);
				texture.DecrementReferenceCount();
				multiRange = _channel.MemoryManager.GetPhysicalRegions(num, texture.Size);
				if (!multiRange.Equals(texture.Range) && !_channel.MemoryManager.Physical.TextureCache.UpdateMapping(texture, multiRange))
				{
					if (Interlocked.Exchange(ref Items[result.ID], null) != null)
					{
						texture.DecrementReferenceCount(this, result.ID);
					}
					continue;
				}
				if (result.ID == id)
				{
					return texture;
				}
			}
			else
			{
				texture.DecrementReferenceCount();
			}
			RemoveAliasList(texture);
		}
		return null;
	}

	protected override void InvalidateRangeImpl(ulong address, ulong size)
	{
		ProcessDereferenceQueue();
		ulong num = address + size;
		while (address < num)
		{
			int num2 = (int)((address - Address) / 32);
			Texture texture = Items[num2];
			if (texture != null)
			{
				ref TextureDescriptor reference = ref DescriptorCache[num2];
				if (!GetDescriptorRefAddress(address).Equals(ref reference))
				{
					if (texture.HasOneReference())
					{
						_channel.MemoryManager.Physical.TextureCache.AddShortCache(texture, ref reference);
					}
					if (Interlocked.Exchange(ref Items[num2], null) != null)
					{
						texture.DecrementReferenceCount(this, num2);
						RemoveAliasList(texture);
					}
				}
			}
			address += 32;
		}
	}

	private static TextureInfo GetInfo(in TextureDescriptor descriptor, out int layerSize)
	{
		int num = descriptor.UnpackDepth();
		int num2 = descriptor.UnpackLevels();
		TextureMsaaMode msaaMode = descriptor.UnpackTextureMsaaMode();
		int num3 = msaaMode.SamplesInX();
		int num4 = msaaMode.SamplesInY();
		int stride = descriptor.UnpackStride();
		bool flag = descriptor.UnpackTextureDescriptorType() == TextureDescriptorType.Linear;
		Target target = descriptor.UnpackTextureTarget().Convert((num3 | num4) != 1);
		int num5 = ((target == Target.TextureBuffer) ? descriptor.UnpackBufferTextureWidth() : descriptor.UnpackWidth());
		int num6 = descriptor.UnpackHeight();
		if (target == Target.Texture2DMultisample || target == Target.Texture2DMultisampleArray)
		{
			num5 *= num3;
			num6 *= num4;
		}
		switch (target)
		{
		case Target.Texture1D:
			target = Target.Texture2D;
			num6 = 1;
			break;
		case Target.Texture1DArray:
			target = Target.Texture2DArray;
			num6 = 1;
			break;
		}
		uint num7 = descriptor.UnpackFormat();
		bool flag2 = descriptor.UnpackSrgb();
		ulong num8 = descriptor.UnpackAddress();
		if (!FormatTable.TryGetTextureFormat(num7, flag2, out var format))
		{
			if (num8 != 0L && num7 != 0)
			{
				Logger.Error?.Print(LogClass.Gpu, $"Invalid texture format 0x{num7:X} (sRGB: {flag2}).", "GetInfo");
			}
			format = FormatInfo.Default;
		}
		int gobBlocksInY = descriptor.UnpackGobBlocksInY();
		int gobBlocksInZ = descriptor.UnpackGobBlocksInZ();
		int gobBlocksInTileX = descriptor.UnpackGobBlocksInTileX();
		layerSize = 0;
		int num9 = descriptor.UnpackBaseLevel();
		int num10 = descriptor.UnpackMaxLevelInclusive();
		if ((num9 != 0 || num10 + 1 != num2) && target != Target.TextureBuffer && !flag)
		{
			int depth = TextureInfo.GetDepth(target, num);
			int layers = TextureInfo.GetLayers(target, num);
			SizeInfo blockLinearTextureSize = SizeCalculator.GetBlockLinearTextureSize(num5, num6, depth, num2, layers, format.BlockWidth, format.BlockHeight, format.BytesPerPixel, gobBlocksInY, gobBlocksInZ, gobBlocksInTileX);
			layerSize = blockLinearTextureSize.LayerSize;
			if (num9 != 0 && num9 < num2)
			{
				num8 += (ulong)blockLinearTextureSize.GetMipOffset(num9);
				num5 = Math.Max(1, num5 >> num9);
				num6 = Math.Max(1, num6 >> num9);
				if (target == Target.Texture3D)
				{
					num = Math.Max(1, num >> num9);
				}
				(gobBlocksInY, gobBlocksInZ) = SizeCalculator.GetMipGobBlockSizes(num6, depth, format.BlockHeight, gobBlocksInY, gobBlocksInZ, num9);
			}
			num2 = num10 - num9 + 1;
		}
		num2 = ClampLevels(target, num5, num6, num, num2);
		SwizzleComponent swizzleComponent = descriptor.UnpackSwizzleR().Convert();
		SwizzleComponent swizzleComponent2 = descriptor.UnpackSwizzleG().Convert();
		SwizzleComponent swizzleComponent3 = descriptor.UnpackSwizzleB().Convert();
		SwizzleComponent swizzleComponent4 = descriptor.UnpackSwizzleA().Convert();
		Format format2 = format.Format;
		DepthStencilMode depthStencilMode = GetDepthStencilMode(
			format2,
			swizzleComponent,
			swizzleComponent2,
			swizzleComponent3,
			swizzleComponent4);
		if (format.Format.IsDepthOrStencil())
		{
			swizzleComponent = SwizzleComponent.Red;
			swizzleComponent2 = SwizzleComponent.Red;
			swizzleComponent3 = SwizzleComponent.Red;
			swizzleComponent4 = ((depthStencilMode == DepthStencilMode.Depth) ? SwizzleComponent.One : SwizzleComponent.Red);
		}
		return new TextureInfo(num8, num5, num6, num, num2, num3, num4, stride, flag, gobBlocksInY, gobBlocksInZ, gobBlocksInTileX, target, format, depthStencilMode, swizzleComponent, swizzleComponent2, swizzleComponent3, swizzleComponent4);
	}

	private static int ClampLevels(Target target, int width, int height, int depthOrLayers, int levels)
	{
		int num = width;
		if (target != Target.Texture1D && target != Target.Texture1DArray)
		{
			num = Math.Max(num, height);
		}
		if (target == Target.Texture3D)
		{
			num = Math.Max(num, depthOrLayers);
		}
		int val = BitOperations.Log2((uint)num) + 1;
		return Math.Min(levels, val);
	}

	private static DepthStencilMode GetDepthStencilMode(Format format, params ReadOnlySpan<SwizzleComponent> components)
	{
		SwizzleComponent swizzleComponent = components[0];
		for (int i = 1; i < 4; i++)
		{
			if (IsRG(swizzleComponent))
			{
				break;
			}
			swizzleComponent = components[i];
		}
		if (!IsRG(swizzleComponent))
		{
			return DepthStencilMode.Depth;
		}
		if (format == Format.D24UnormS8Uint)
		{
			if (swizzleComponent != SwizzleComponent.Red)
			{
				return DepthStencilMode.Depth;
			}
			return DepthStencilMode.Stencil;
		}
		if (swizzleComponent != SwizzleComponent.Red)
		{
			return DepthStencilMode.Stencil;
		}
		return DepthStencilMode.Depth;
	}

	private static bool IsRG(SwizzleComponent component)
	{
		if (component != SwizzleComponent.Red)
		{
			return component == SwizzleComponent.Green;
		}
		return true;
	}

	private static TextureInfo ChangeFormat(in TextureInfo info, FormatInfo dstFormat)
	{
		int num = info.Width;
		if (info.FormatInfo.BytesPerPixel != dstFormat.BytesPerPixel)
		{
			num = num * info.FormatInfo.BytesPerPixel / dstFormat.BytesPerPixel;
		}
		return new TextureInfo(info.GpuAddress, num, info.Height, info.DepthOrLayers, info.Levels, info.SamplesInX, info.SamplesInY, info.Stride, info.IsLinear, info.GobBlocksInY, info.GobBlocksInZ, info.GobBlocksInTileX, info.Target, dstFormat, info.DepthStencilMode, info.SwizzleR, info.SwizzleG, info.SwizzleB, info.SwizzleA);
	}

	private void RemoveAliasList(Texture texture)
	{
		if (_aliasLists.TryGetValue(texture, out var value))
		{
			_aliasLists.Remove(texture);
			value.Destroy();
		}
	}

	protected override void Delete(Texture item)
	{
		if (item != null)
		{
			item.DecrementReferenceCount(this);
			RemoveAliasList(item);
		}
	}

	public override void Dispose()
	{
		ProcessDereferenceQueue();
		base.Dispose();
	}
}
