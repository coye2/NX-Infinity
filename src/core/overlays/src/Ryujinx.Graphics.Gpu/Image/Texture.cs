using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Memory;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.Graphics.Texture;
using Ryujinx.Graphics.Texture.Astc;
using Ryujinx.Memory;
using Ryujinx.Memory.Range;

namespace Ryujinx.Graphics.Gpu.Image;

internal class Texture : IMultiRangeItem, IDisposable
{
	private struct TexturePoolOwner
	{
		public TexturePool Pool;

		public int ID;

		public ulong GpuAddress;
	}

	private const int ByteComparisonSwitchThreshold = 4;

	private const int ScaledSetWeight = 10;

	private const int ScaledSetThreshold = 30;

	private const int MinLevelsForForceAnisotropy = 5;

	private GpuContext _context;

	private PhysicalMemory _physicalMemory;

	private SizeInfo _sizeInfo;

	private int _depth;

	private int _layers;

	private bool _hasData;

	private bool _dirty = true;

	private int _updateCount;

	private byte[] _currentData;

	private bool _modifiedStale = true;

	private ITexture _arrayViewTexture;

	private Target _arrayViewTarget;

	private ITexture _flushHostTexture;

	private ITexture _setHostTexture;

	private int _scaledSetScore;

	private Texture _viewStorage;

	private List<Texture> _views;

	private int _referenceCount;

	private List<TexturePoolOwner> _poolOwners;

	public Format Format => Info.FormatInfo.Format;

	public Target Target { get; private set; }

	public int Width { get; private set; }

	public int Height { get; private set; }

	public TextureInfo Info { get; private set; }

	public bool CanForceAnisotropy { get; private set; }

	public float ScaleFactor { get; private set; }

	public TextureScaleMode ScaleMode { get; private set; }

	public TextureGroup Group { get; private set; }

	public bool ChangedMapping { get; private set; }

	public bool AlwaysFlushOnOverlap { get; private set; }

	public int InvalidatedSequence { get; private set; }

	public int FirstLayer { get; private set; }

	public int FirstLevel { get; private set; }

	public ITexture HostTexture { get; private set; }

	public LinkedListNode<Texture> CacheNode { get; set; }

	public ShortTextureCacheEntry ShortCacheEntry { get; set; }

	public bool HadPoolOwner { get; private set; }

	public MultiRange Range { get; private set; }

	public int LayerSize => _sizeInfo.LayerSize;

	public ulong Size => (ulong)_sizeInfo.TotalSize;

	public bool IsView => _viewStorage != this;

	public bool HasViews => _views.Count > 0;

	private Texture(GpuContext context, PhysicalMemory physicalMemory, TextureInfo info, SizeInfo sizeInfo, MultiRange range, int firstLayer, int firstLevel, float scaleFactor, TextureScaleMode scaleMode)
	{
		InitializeTexture(context, physicalMemory, info, sizeInfo, range);
		FirstLayer = firstLayer;
		FirstLevel = firstLevel;
		ScaleFactor = scaleFactor;
		ScaleMode = scaleMode;
		InitializeData(isView: true);
	}

	public Texture(GpuContext context, PhysicalMemory physicalMemory, TextureInfo info, SizeInfo sizeInfo, MultiRange range, TextureScaleMode scaleMode)
	{
		ScaleFactor = 1f;
		ScaleMode = scaleMode;
		InitializeTexture(context, physicalMemory, info, sizeInfo, range);
	}

	private void InitializeTexture(GpuContext context, PhysicalMemory physicalMemory, TextureInfo info, SizeInfo sizeInfo, MultiRange range)
	{
		_context = context;
		_physicalMemory = physicalMemory;
		_sizeInfo = sizeInfo;
		Range = range;
		SetInfo(info);
		_viewStorage = this;
		_views = new List<Texture>();
		_poolOwners = new List<TexturePoolOwner>();
	}

	public void InitializeData(bool isView, bool withData = false)
	{
		withData |= Group != null && Group.FlushIncompatibleOverlapsIfNeeded();
		if (withData)
		{
			TextureCreateInfo createInfo = TextureCache.GetCreateInfo(Info, _context.Capabilities, ScaleFactor);
			HostTexture = _context.Renderer.CreateTexture(createInfo);
			SynchronizeMemory();
			if (ScaleMode == TextureScaleMode.Scaled)
			{
				SetScale(GraphicsConfig.ResScale);
			}
			return;
		}
		_hasData = true;
		if (!isView)
		{
			CheckModified(consume: true);
			if (ScaleMode == TextureScaleMode.Scaled)
			{
				ScaleFactor = GraphicsConfig.ResScale;
			}
			TextureCreateInfo createInfo2 = TextureCache.GetCreateInfo(Info, _context.Capabilities, ScaleFactor);
			HostTexture = _context.Renderer.CreateTexture(createInfo2);
		}
	}

	public void InitializeGroup(bool hasLayerViews, bool hasMipViews, List<TextureIncompatibleOverlap> incompatibleOverlaps)
	{
		Group = new TextureGroup(_context, _physicalMemory, this, incompatibleOverlaps);
		Group.Initialize(ref _sizeInfo, hasLayerViews, hasMipViews);
	}

	public Texture CreateView(TextureInfo info, SizeInfo sizeInfo, MultiRange range, int firstLayer, int firstLevel)
	{
		Texture texture = new Texture(_context, _physicalMemory, info, sizeInfo, range, FirstLayer + firstLayer, FirstLevel + firstLevel, ScaleFactor, ScaleMode);
		TextureCreateInfo createInfo = TextureCache.GetCreateInfo(info, _context.Capabilities, ScaleFactor);
		texture.HostTexture = HostTexture.CreateView(createInfo, firstLayer, firstLevel);
		_viewStorage.AddView(texture);
		return texture;
	}

	private void AddView(Texture texture)
	{
		IncrementReferenceCount();
		_views.Add(texture);
		texture._viewStorage = this;
		Group.UpdateViews(_views, texture);
		if (texture.Group != null && texture.Group != Group && texture.Group.Storage == texture)
		{
			Group.Inherit(texture.Group);
			texture.Group.Dispose();
		}
		texture.Group = Group;
	}

	private void RemoveView(Texture texture)
	{
		_views.Remove(texture);
		Group.RemoveView(_views, texture);
		texture._viewStorage = texture;
		DecrementReferenceCount();
	}

	public void ReplaceRange(MultiRange range)
	{
		MultiRange range2 = Range;
		Range = range;
		Group.RangeChanged(range2);
	}

	public void CreateCopyDependency(Texture contained, int layer, int level, bool copyTo)
	{
		if (contained.Group != Group)
		{
			Group.CreateCopyDependency(contained, FirstLayer + layer, FirstLevel + level, copyTo);
		}
	}

	private bool AllowScaledSetData()
	{
		_scaledSetScore += 10;
		if (_scaledSetScore >= 30)
		{
			BlacklistScale();
			return false;
		}
		return true;
	}

	public void BlacklistScale()
	{
		ScaleMode = TextureScaleMode.Blacklisted;
		SetScale(1f);
	}

	public void PropagateScale(Texture other)
	{
		if (other.ScaleMode == TextureScaleMode.Blacklisted || ScaleMode == TextureScaleMode.Blacklisted)
		{
			BlacklistScale();
			other.BlacklistScale();
			return;
		}
		float resScale = GraphicsConfig.ResScale;
		float scale = ((ScaleFactor == resScale || other.ScaleFactor == resScale) ? resScale : Math.Max(ScaleFactor, other.ScaleFactor));
		SetScale(scale);
		other.SetScale(scale);
	}

	private ITexture GetScaledHostTexture(float scale, bool copy, ITexture storage = null)
	{
		if (storage == null)
		{
			TextureCreateInfo createInfo = TextureCache.GetCreateInfo(Info, _context.Capabilities, scale);
			storage = _context.Renderer.CreateTexture(createInfo);
		}
		if (copy)
		{
			HostTexture.CopyTo(storage, new Extents2D(0, 0, HostTexture.Width, HostTexture.Height), new Extents2D(0, 0, storage.Width, storage.Height), linearFilter: true);
		}
		return storage;
	}

	public void SetScale(float scale)
	{
		TextureScaleMode textureScaleMode = ((ScaleMode != TextureScaleMode.Blacklisted && (ScaleMode != TextureScaleMode.Undesired || scale != 1f)) ? TextureScaleMode.Scaled : ScaleMode);
		if (_viewStorage != this)
		{
			_viewStorage.ScaleMode = textureScaleMode;
			_viewStorage.SetScale(scale);
			return;
		}
		if (ScaleFactor != scale)
		{
			Logger.Debug?.Print(LogClass.Gpu, $"Rescaling {Info.Width}x{Info.Height} {Info.FormatInfo.Format} to ({ScaleFactor} to {scale}). ", "SetScale");
			ScaleFactor = scale;
			ITexture scaledHostTexture = GetScaledHostTexture(ScaleFactor, copy: true);
			Logger.Debug?.Print(LogClass.Gpu, $"  Copy performed: {HostTexture.Width}x{HostTexture.Height} to {scaledHostTexture.Width}x{scaledHostTexture.Height}", "SetScale");
			ReplaceStorage(scaledHostTexture);
			foreach (Texture view in _views)
			{
				Logger.Debug?.Print(LogClass.Gpu, $"  Recreating view {Info.Width}x{Info.Height} {Info.FormatInfo.Format}.", "SetScale");
				view.ScaleFactor = scale;
				TextureCreateInfo createInfo = TextureCache.GetCreateInfo(view.Info, _context.Capabilities, scale);
				ITexture hostTexture = HostTexture.CreateView(createInfo, view.FirstLayer - FirstLayer, view.FirstLevel - FirstLevel);
				view.ReplaceStorage(hostTexture);
				view.ScaleMode = textureScaleMode;
			}
		}
		if (ScaleMode == textureScaleMode)
		{
			return;
		}
		ScaleMode = textureScaleMode;
		foreach (Texture view2 in _views)
		{
			view2.ScaleMode = textureScaleMode;
		}
	}

	public bool CheckModified(bool consume)
	{
		return Group.CheckDirty(this, consume);
	}

	public void DiscardData()
	{
		Group.DiscardData(this);
		_dirty = false;
	}

	public void SynchronizeMemory()
	{
		if (Target != Target.TextureBuffer && _dirty)
		{
			_dirty = false;
			if (_hasData)
			{
				Group.SynchronizeMemory(this);
				return;
			}
			Group.CheckDirty(this, consume: true);
			SynchronizeFull();
		}
	}

	public void SignalGroupDirty()
	{
		_dirty = true;
	}

	public void SignalModifiedDirty()
	{
		_modifiedStale = true;
	}

	public void SynchronizeFull()
	{
		using (RyuStutterTrace.Measure("texture.sync.full", (long)Size))
		{
			ReadOnlySpan<byte> span = _physicalMemory.GetSpan(Range);
			if (Info.FormatInfo.Format.IsAstc() && !_context.Capabilities.SupportsAstcCompression)
			{
				if (_updateCount < 4)
				{
					_updateCount++;
				}
				else
				{
					if (_currentData != null && span.SequenceEqual(_currentData))
					{
						return;
					}
					_currentData = span.ToArray();
				}
			}
			MemoryOwner<byte> data = ConvertToHostCompatibleFormat(span);
			if (ScaleFactor != 1f && AllowScaledSetData())
			{
				ITexture texture = (_setHostTexture = GetScaledHostTexture(1f, copy: false, _setHostTexture));
				texture.SetData(data);
				texture.CopyTo(HostTexture, new Extents2D(0, 0, texture.Width, texture.Height), new Extents2D(0, 0, HostTexture.Width, HostTexture.Height), linearFilter: true);
			}
			else
			{
				HostTexture.SetData(data);
			}
			_hasData = true;
		}
	}

	public void SetData(MemoryOwner<byte> data)
	{
		BlacklistScale();
		Group.CheckDirty(this, consume: true);
		AlwaysFlushOnOverlap = true;
		HostTexture.SetData(data);
		_hasData = true;
	}

	public void SetData(MemoryOwner<byte> data, int layer, int level)
	{
		BlacklistScale();
		HostTexture.SetData(data, layer, level);
		_currentData = null;
		_hasData = true;
	}

	public void SetData(MemoryOwner<byte> data, int layer, int level, Rectangle<int> region)
	{
		BlacklistScale();
		HostTexture.SetData(data, layer, level, region);
		_currentData = null;
		_hasData = true;
	}

	public MemoryOwner<byte> ConvertToHostCompatibleFormat(ReadOnlySpan<byte> data, int level = 0, bool single = false)
	{
		using (RyuStutterTrace.Measure("texture.convert", data.Length, RyuStutterTrace.Enabled ? $"{Format} {Info.Width}x{Info.Height}x{Info.DepthOrLayers} levels={Info.Levels}" : null))
		{
			int width = Info.Width;
			int height = Info.Height;
			int depth = _depth;
			int layers = (single ? 1 : _layers);
			int levels = (single ? 1 : (Info.Levels - level));
			width = Math.Max(width >> level, 1);
			height = Math.Max(height >> level, 1);
			depth = Math.Max(depth >> level, 1);
			int num = (single ? 1 : depth);
			MemoryOwner<byte> memoryOwner = ((!Info.IsLinear) ? LayoutConverter.ConvertBlockLinearToLinear(width, height, depth, num, levels, layers, Info.FormatInfo.BlockWidth, Info.FormatInfo.BlockHeight, Info.FormatInfo.BytesPerPixel, Info.GobBlocksInY, Info.GobBlocksInZ, Info.GobBlocksInTileX, _sizeInfo, data) : LayoutConverter.ConvertLinearStridedToLinear(width, height, Info.FormatInfo.BlockWidth, Info.FormatInfo.BlockHeight, Info.Stride, Info.Stride, Info.FormatInfo.BytesPerPixel, data));
			MemoryOwner<byte> memoryOwner2 = memoryOwner;
			if (!_context.Capabilities.SupportsAstcCompression && Format.IsAstc())
			{
				using (memoryOwner2)
				{
					if (!AstcDecoder.TryDecodeToRgba8P(memoryOwner2.Memory, Info.FormatInfo.BlockWidth, Info.FormatInfo.BlockHeight, width, height, num, levels, layers, out var decoded))
					{
						string value = $"{Info.Target} {Info.FormatInfo.Format} {Info.Width}x{Info.Height}x{Info.DepthOrLayers} levels {Info.Levels}";
						Logger.Debug?.Print(LogClass.Gpu, $"Invalid ASTC texture at 0x{Info.GpuAddress:X} ({value}).", "ConvertToHostCompatibleFormat");
					}
					if (GraphicsConfig.EnableTextureRecompression)
					{
						using (decoded)
						{
							return BCnEncoder.EncodeBC7(decoded.Memory, width, height, num, levels, layers);
						}
					}
					return decoded;
				}
			}
			if (!_context.Capabilities.SupportsEtc2Compression && Format.IsEtc2())
			{
				switch (Format)
				{
				case Format.Etc2RgbaUnorm:
				case Format.Etc2RgbaSrgb:
					using (memoryOwner2)
					{
						return ETC2Decoder.DecodeRgba(memoryOwner2.Span, width, height, num, levels, layers);
					}
				case Format.Etc2RgbPtaUnorm:
				case Format.Etc2RgbPtaSrgb:
					using (memoryOwner2)
					{
						return ETC2Decoder.DecodePta(memoryOwner2.Span, width, height, num, levels, layers);
					}
				case Format.Etc2RgbUnorm:
				case Format.Etc2RgbSrgb:
					using (memoryOwner2)
					{
						return ETC2Decoder.DecodeRgb(memoryOwner2.Span, width, height, num, levels, layers);
					}
				}
			}
			else if (!TextureCompatibility.HostSupportsBcFormat(Format, Target, _context.Capabilities))
			{
				switch (Format)
				{
				case Format.Bc1RgbaUnorm:
				case Format.Bc1RgbaSrgb:
					using (memoryOwner2)
					{
						return BCnDecoder.DecodeBC1(memoryOwner2.Span, width, height, num, levels, layers);
					}
				case Format.Bc2Unorm:
				case Format.Bc2Srgb:
					using (memoryOwner2)
					{
						return BCnDecoder.DecodeBC2(memoryOwner2.Span, width, height, num, levels, layers);
					}
				case Format.Bc3Unorm:
				case Format.Bc3Srgb:
					using (memoryOwner2)
					{
						return BCnDecoder.DecodeBC3(memoryOwner2.Span, width, height, num, levels, layers);
					}
				case Format.Bc4Unorm:
				case Format.Bc4Snorm:
					using (memoryOwner2)
					{
						return BCnDecoder.DecodeBC4(memoryOwner2.Span, width, height, num, levels, layers, Format == Format.Bc4Snorm);
					}
				case Format.Bc5Unorm:
				case Format.Bc5Snorm:
					using (memoryOwner2)
					{
						return BCnDecoder.DecodeBC5(memoryOwner2.Span, width, height, num, levels, layers, Format == Format.Bc5Snorm);
					}
				case Format.Bc6HSfloat:
				case Format.Bc6HUfloat:
					using (memoryOwner2)
					{
						return BCnDecoder.DecodeBC6(memoryOwner2.Span, width, height, num, levels, layers, Format == Format.Bc6HSfloat);
					}
				case Format.Bc7Unorm:
				case Format.Bc7Srgb:
					using (memoryOwner2)
					{
						return BCnDecoder.DecodeBC7(memoryOwner2.Span, width, height, num, levels, layers);
					}
				}
			}
			else
			{
				if (!_context.Capabilities.SupportsR4G4Format && Format == Format.R4G4Unorm)
				{
					using (memoryOwner2)
					{
						MemoryOwner<byte> memoryOwner3 = PixelConverter.ConvertR4G4ToR4G4B4A4(memoryOwner2.Span, width);
						if (!_context.Capabilities.SupportsR4G4B4A4Format)
						{
							using (memoryOwner3)
							{
								return PixelConverter.ConvertR4G4B4A4ToR8G8B8A8(memoryOwner3.Span, width);
							}
						}
						return memoryOwner3;
					}
				}
				if (Format == Format.R4G4B4A4Unorm)
				{
					if (!_context.Capabilities.SupportsR4G4B4A4Format)
					{
						using (memoryOwner2)
						{
							return PixelConverter.ConvertR4G4B4A4ToR8G8B8A8(memoryOwner2.Span, width);
						}
					}
				}
				else if (!_context.Capabilities.Supports5BitComponentFormat && Format.Is16BitPacked())
				{
					switch (Format)
					{
					case Format.R5G6B5Unorm:
					case Format.B5G6R5Unorm:
						using (memoryOwner2)
						{
							return PixelConverter.ConvertR5G6B5ToR8G8B8A8(memoryOwner2.Span, width);
						}
					case Format.R5G5B5X1Unorm:
					case Format.R5G5B5A1Unorm:
					case Format.B5G5R5A1Unorm:
						using (memoryOwner2)
						{
							return PixelConverter.ConvertR5G5B5ToR8G8B8A8(memoryOwner2.Span, width, Format == Format.R5G5B5X1Unorm);
						}
					case Format.A1B5G5R5Unorm:
						using (memoryOwner2)
						{
							return PixelConverter.ConvertA1B5G5R5ToR8G8B8A8(memoryOwner2.Span, width);
						}
					case Format.R4G4B4A4Unorm:
						using (memoryOwner2)
						{
							return PixelConverter.ConvertR4G4B4A4ToR8G8B8A8(memoryOwner2.Span, width);
						}
					}
				}
			}
			return memoryOwner2;
		}
	}

	public ReadOnlySpan<byte> ConvertFromHostCompatibleFormat(Span<byte> output, ReadOnlySpan<byte> data, int level = 0, bool single = false)
	{
		if (Target != Target.TextureBuffer)
		{
			int width = Info.Width;
			int height = Info.Height;
			int depth = _depth;
			int layers = (single ? 1 : _layers);
			int levels = (single ? 1 : (Info.Levels - level));
			width = Math.Max(width >> level, 1);
			height = Math.Max(height >> level, 1);
			depth = Math.Max(depth >> level, 1);
			data = ((!Info.IsLinear) ? LayoutConverter.ConvertLinearToBlockLinear(output, width, height, depth, single ? 1 : depth, levels, layers, Info.FormatInfo.BlockWidth, Info.FormatInfo.BlockHeight, Info.FormatInfo.BytesPerPixel, Info.GobBlocksInY, Info.GobBlocksInZ, Info.GobBlocksInTileX, _sizeInfo, data) : LayoutConverter.ConvertLinearToLinearStrided(output, Info.Width, Info.Height, Info.FormatInfo.BlockWidth, Info.FormatInfo.BlockHeight, Info.Stride, Info.FormatInfo.BytesPerPixel, data));
		}
		return data;
	}

	public bool FlushModified(bool tracked = true)
	{
		if (TextureCompatibility.CanTextureFlush(Info, _context.Capabilities))
		{
			return Group.FlushModified(this, tracked);
		}
		return false;
	}

	public void Flush(bool tracked)
	{
		if (TextureCompatibility.CanTextureFlush(Info, _context.Capabilities))
		{
			FlushTextureDataToGuest(tracked);
		}
	}

	public ITexture GetFlushTexture()
	{
		ITexture result = HostTexture;
		if (ScaleFactor != 1f)
		{
			result = (_flushHostTexture = GetScaledHostTexture(1f, copy: true, _flushHostTexture));
		}
		return result;
	}

	public void FlushTextureDataToGuest(bool tracked, ITexture texture = null)
	{
		using WritableRegion writableRegion = _physicalMemory.GetWritableRegion(Range, tracked);
		GetTextureDataFromGpu(writableRegion.Memory.Span, tracked, texture);
	}

	private void GetTextureDataFromGpu(Span<byte> output, bool blacklist, ITexture texture = null)
	{
		PinnedSpan<byte> data;
		if (texture != null)
		{
			data = texture.GetData();
		}
		else if (blacklist)
		{
			BlacklistScale();
			data = HostTexture.GetData();
		}
		else if (ScaleFactor != 1f)
		{
			float scaleFactor = ScaleFactor;
			SetScale(1f);
			data = HostTexture.GetData();
			SetScale(scaleFactor);
		}
		else
		{
			data = HostTexture.GetData();
		}
		ConvertFromHostCompatibleFormat(output, data.Get());
		data.Dispose();
	}

	public void GetTextureDataSliceFromGpu(Span<byte> output, int layer, int level, bool blacklist, ITexture texture = null)
	{
		PinnedSpan<byte> data;
		if (texture != null)
		{
			data = texture.GetData(layer, level);
		}
		else if (blacklist)
		{
			BlacklistScale();
			data = HostTexture.GetData(layer, level);
		}
		else if (ScaleFactor != 1f)
		{
			float scaleFactor = ScaleFactor;
			SetScale(1f);
			data = HostTexture.GetData(layer, level);
			SetScale(scaleFactor);
		}
		else
		{
			data = HostTexture.GetData(layer, level);
		}
		ConvertFromHostCompatibleFormat(output, data.Get(), level, single: true);
		data.Dispose();
	}

	public TextureMatchQuality IsExactMatch(TextureInfo info, TextureSearchFlags flags)
	{
		bool flag = (flags & TextureSearchFlags.ForSampler) != 0;
		TextureMatchQuality textureMatchQuality = TextureCompatibility.FormatMatches(Info, info, flag, (flags & TextureSearchFlags.DepthAlias) != 0);
		if (textureMatchQuality == TextureMatchQuality.NoMatch)
		{
			return textureMatchQuality;
		}
		if (!TextureCompatibility.LayoutMatches(Info, info))
		{
			return TextureMatchQuality.NoMatch;
		}
		if (!TextureCompatibility.SizeMatches(Info, info, flag))
		{
			return TextureMatchQuality.NoMatch;
		}
		if ((flags & TextureSearchFlags.ForSampler) != 0 && !TextureCompatibility.SamplerParamsMatches(Info, info))
		{
			return TextureMatchQuality.NoMatch;
		}
		if ((flags & TextureSearchFlags.ForCopy) != 0)
		{
			if ((Info.Target != Target.Texture2DMultisample || info.Target != Target.Texture2D) && !TextureCompatibility.TargetAndSamplesCompatible(Info, info))
			{
				return TextureMatchQuality.NoMatch;
			}
		}
		else if (!TextureCompatibility.TargetAndSamplesCompatible(Info, info))
		{
			return TextureMatchQuality.NoMatch;
		}
		if (Info.Levels != info.Levels)
		{
			return TextureMatchQuality.NoMatch;
		}
		return textureMatchQuality;
	}

	public TextureViewCompatibility IsViewCompatible(TextureInfo info, MultiRange range, bool exactSize, int layerSize, Capabilities caps, out int firstLayer, out int firstLevel, TextureSearchFlags flags = TextureSearchFlags.None)
	{
		TextureViewCompatibility first = TextureViewCompatibility.Full;
		first = TextureCompatibility.PropagateViewCompatibility(first, TextureCompatibility.ViewFormatCompatible(Info, info, caps, flags));
		if (first != TextureViewCompatibility.Incompatible)
		{
			first = TextureCompatibility.PropagateViewCompatibility(first, TextureCompatibility.ViewTargetCompatible(Info, info, ref caps));
			if (Info.Target.IsMultisample() && info.Target.IsMultisample() && (Info.SamplesInX != info.SamplesInX || Info.SamplesInY != info.SamplesInY))
			{
				first = TextureViewCompatibility.Incompatible;
			}
			if (first == TextureViewCompatibility.Full && Info.FormatInfo.Format != info.FormatInfo.Format && !_context.Capabilities.SupportsMismatchingViewFormat)
			{
				first = TextureViewCompatibility.CopyOnly;
			}
		}
		firstLayer = 0;
		firstLevel = 0;
		if (first == TextureViewCompatibility.Incompatible)
		{
			return TextureViewCompatibility.Incompatible;
		}
		int num = Range.FindOffset(range);
		if (num < 0 || !_sizeInfo.FindView(num, out firstLayer, out firstLevel))
		{
			return TextureViewCompatibility.LayoutIncompatible;
		}
		if (!TextureCompatibility.ViewLayoutCompatible(Info, info, firstLevel))
		{
			return TextureViewCompatibility.LayoutIncompatible;
		}
		if (info.GetSlices() > 1 && LayerSize != layerSize)
		{
			return TextureViewCompatibility.LayoutIncompatible;
		}
		first = TextureCompatibility.PropagateViewCompatibility(first, TextureCompatibility.ViewSizeMatches(Info, info, exactSize, firstLevel));
		return TextureCompatibility.PropagateViewCompatibility(first, TextureCompatibility.ViewSubImagesInBounds(Info, info, firstLayer, firstLevel));
	}

	public ITexture GetTargetTexture(Target target)
	{
		if (target == Target)
		{
			return HostTexture;
		}
		if (_arrayViewTexture == null && IsSameDimensionsTarget(target))
		{
			FormatInfo formatInfo = TextureCompatibility.ToHostCompatibleFormat(Info, _context.Capabilities);
			TextureCreateInfo info = new TextureCreateInfo(Info.Width, Info.Height, (target != Target.CubemapArray) ? 1 : 6, Info.Levels, Info.Samples, formatInfo.BlockWidth, formatInfo.BlockHeight, formatInfo.BytesPerPixel, formatInfo.Format, Info.DepthStencilMode, target, Info.SwizzleR, Info.SwizzleG, Info.SwizzleB, Info.SwizzleA);
			ITexture result = (_arrayViewTexture = HostTexture.CreateView(info, 0, 0));
			_arrayViewTarget = target;
			return result;
		}
		if (_arrayViewTarget == target)
		{
			return _arrayViewTexture;
		}
		return null;
	}

	private bool CanTextureForceAnisotropy()
	{
		if (Target != Target.Texture2D && Target != Target.Texture2DArray)
		{
			return false;
		}
		int val = BitOperations.Log2((uint)Math.Max(Info.Width, Info.Height)) + 1;
		return Info.Levels >= Math.Min(5, val);
	}

	private bool IsSameDimensionsTarget(Target target)
	{
		switch (Info.Target)
		{
		case Target.Texture1D:
		case Target.Texture1DArray:
			if (target != Target.Texture1D)
			{
				return target == Target.Texture1DArray;
			}
			return true;
		case Target.Texture2D:
		case Target.Texture2DArray:
			if (target != Target.Texture2D)
			{
				return target == Target.Texture2DArray;
			}
			return true;
		case Target.Cubemap:
		case Target.CubemapArray:
			if (target != Target.Cubemap)
			{
				return target == Target.CubemapArray;
			}
			return true;
		case Target.Texture2DMultisample:
		case Target.Texture2DMultisampleArray:
			if (target != Target.Texture2DMultisample)
			{
				return target == Target.Texture2DMultisampleArray;
			}
			return true;
		case Target.Texture3D:
			return target == Target.Texture3D;
		default:
			return false;
		}
	}

	public void ReplaceView(Texture parent, TextureInfo info, ITexture hostTexture, int firstLayer, int firstLevel)
	{
		IncrementReferenceCount();
		parent._viewStorage.SynchronizeMemory();
		if (_views.Count > 0)
		{
			Texture[] array = _views.ToArray();
			foreach (Texture texture in array)
			{
				TextureCreateInfo createInfo = TextureCache.GetCreateInfo(texture.Info, _context.Capabilities, ScaleFactor);
				ITexture hostTexture2 = parent.HostTexture.CreateView(createInfo, texture.FirstLayer + firstLayer, texture.FirstLevel + firstLevel);
				texture.ReplaceView(parent, texture.Info, hostTexture2, texture.FirstLayer + firstLayer, texture.FirstLevel + firstLevel);
			}
		}
		ReplaceStorage(hostTexture);
		if (_viewStorage != this)
		{
			_viewStorage.RemoveView(this);
		}
		FirstLayer = parent.FirstLayer + firstLayer;
		FirstLevel = parent.FirstLevel + firstLevel;
		parent._viewStorage.AddView(this);
		SetInfo(info);
		DecrementReferenceCount();
	}

	private void SetInfo(TextureInfo info)
	{
		Info = info;
		Target = info.Target;
		Width = info.Width;
		Height = info.Height;
		CanForceAnisotropy = CanTextureForceAnisotropy();
		_depth = info.GetDepth();
		_layers = info.GetLayers();
	}

	public void SignalModified()
	{
		_scaledSetScore = Math.Max(0, _scaledSetScore - 1);
		if (_modifiedStale || Group.HasCopyDependencies)
		{
			_modifiedStale = false;
			Group.SignalModified(this);
		}
		_physicalMemory.TextureCache.Lift(this);
	}

	public void SignalModifying(bool bound)
	{
		if (bound)
		{
			_scaledSetScore = Math.Max(0, _scaledSetScore - 1);
		}
		if (_modifiedStale || Group.HasCopyDependencies || Group.HasFlushBuffer)
		{
			_modifiedStale = false;
			Group.SignalModifying(this, bound);
		}
		_physicalMemory.TextureCache.Lift(this);
		if (bound)
		{
			IncrementReferenceCount();
		}
		else
		{
			DecrementReferenceCount();
		}
	}

	private void ReplaceStorage(ITexture hostTexture)
	{
		DisposeTextures();
		HostTexture = hostTexture;
	}

	public bool DataOverlaps(Texture texture, TextureViewCompatibility compatibility)
	{
		if (compatibility == TextureViewCompatibility.LayoutIncompatible && Info.GobBlocksInZ > 1 && Info.GobBlocksInZ == texture.Info.GobBlocksInZ)
		{
			return false;
		}
		if (texture._sizeInfo.AllOffsets.Length == 1 && _sizeInfo.AllOffsets.Length == 1)
		{
			return Range.OverlapsWith(texture.Range);
		}
		MultiRange otherRange = texture.Range;
		IEnumerable<MultiRange> enumerable = from region in _sizeInfo.AllRegions()
			select Range.Slice((ulong)region.Offset, (ulong)region.Size);
		IEnumerable<MultiRange> enumerable2 = from region in texture._sizeInfo.AllRegions()
			select otherRange.Slice((ulong)region.Offset, (ulong)region.Size);
		foreach (MultiRange item in enumerable)
		{
			foreach (MultiRange item2 in enumerable2)
			{
				if (item.OverlapsWith(item2))
				{
					return true;
				}
			}
		}
		return false;
	}

	public void IncrementReferenceCount()
	{
		_referenceCount++;
	}

	public void IncrementReferenceCount(TexturePool pool, int id, ulong gpuVa)
	{
		HadPoolOwner = true;
		lock (_poolOwners)
		{
			_poolOwners.Add(new TexturePoolOwner
			{
				Pool = pool,
				ID = id,
				GpuAddress = gpuVa
			});
		}
		_referenceCount++;
		if (ShortCacheEntry != null)
		{
			_physicalMemory.TextureCache.RemoveShortCache(this);
		}
	}

	public bool HasOneReference()
	{
		return _referenceCount == 1;
	}

	public bool DecrementReferenceCount()
	{
		int num = --_referenceCount;
		if (num == 0)
		{
			if (_viewStorage != this)
			{
				_viewStorage.RemoveView(this);
			}
			_physicalMemory.TextureCache.RemoveTextureFromCache(this);
		}
		DeleteIfNotUsed();
		return num <= 0;
	}

	public bool DecrementReferenceCount(TexturePool pool, int id = -1)
	{
		lock (_poolOwners)
		{
			if (_poolOwners.RemoveAll((TexturePoolOwner entry) => (entry.Pool == pool && entry.ID == id) || id == -1) == 0)
			{
				return _referenceCount <= 0;
			}
		}
		return DecrementReferenceCount();
	}

	public void RemoveFromPools(bool deferred)
	{
		lock (_poolOwners)
		{
			foreach (TexturePoolOwner poolOwner in _poolOwners)
			{
				poolOwner.Pool.ForceRemove(this, poolOwner.ID, deferred);
			}
			_poolOwners.Clear();
		}
		if (ShortCacheEntry != null && !ShortCacheEntry.IsAutoDelete && _context.IsGpuThread())
		{
			_physicalMemory.TextureCache.RemoveShortCache(this);
		}
		InvalidatedSequence++;
	}

	public void UpdatePoolMappings()
	{
		ChangedMapping = true;
		lock (_poolOwners)
		{
			ulong num = 0uL;
			foreach (TexturePoolOwner poolOwner in _poolOwners)
			{
				if (num == 0L || num == poolOwner.GpuAddress)
				{
					num = poolOwner.GpuAddress;
					poolOwner.Pool.QueueUpdateMapping(this, poolOwner.ID);
				}
				else
				{
					poolOwner.Pool.ForceRemove(this, poolOwner.ID, deferred: true);
				}
			}
			_poolOwners.Clear();
		}
		InvalidatedSequence++;
	}

	private void DeleteIfNotUsed()
	{
		if (_referenceCount == 0 && _views.Count == 0)
		{
			Dispose();
		}
	}

	private void DisposeTextures()
	{
		InvalidatedSequence++;
		_currentData = null;
		HostTexture.Release();
		_arrayViewTexture?.Release();
		_arrayViewTexture = null;
		_flushHostTexture?.Release();
		_flushHostTexture = null;
		_setHostTexture?.Release();
		_setHostTexture = null;
	}

	public void Unmapped(MultiRange unmapRange)
	{
		ChangedMapping = true;
		if (Group.Storage == this)
		{
			Group.Unmapped();
			Group.ClearModified(unmapRange);
		}
	}

	public void Dispose()
	{
		DisposeTextures();
		if (Group.Storage == this)
		{
			Group.Dispose();
		}
	}
}
