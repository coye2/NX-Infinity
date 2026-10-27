using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Ryujinx.Common;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Common.PreciseSleep;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu;
using Ryujinx.Graphics.Gpu.Synchronization;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvMap;
using Ryujinx.HLE.HOS.Services.Vi;

namespace Ryujinx.HLE.HOS.Services.SurfaceFlinger;

internal class SurfaceFlinger : IConsumerListener, IDisposable
{
	private class Layer
	{
		public long Id;

		public int ProducerBinderId;

		public IGraphicBufferProducer Producer;

		public BufferItemConsumer Consumer;

		public BufferQueueCore Core;

		public ulong Owner;

		public LayerState State;
	}

	private class TextureCallbackInformation
	{
		public Layer Layer;

		public BufferItem Item;

		public long MeasurementSegment;
	}

	private sealed class LayerListener(SurfaceFlinger owner, long layerId, ulong processId) : IConsumerListener
	{
		public void OnFrameAvailable(ref BufferItem item)
		{
			owner._device.FramePolicy.RecordQueued(layerId, processId, item.FrameNumber, replaced: false);
			owner.OnFrameAvailable(ref item);
		}

		public void OnFrameReplaced(ref BufferItem item)
		{
			owner._device.FramePolicy.RecordQueued(layerId, processId, item.FrameNumber, replaced: true);
			owner.OnFrameReplaced(ref item);
		}

		public void OnBuffersReleased()
		{
		}
	}

	private readonly Switch _device;

	private readonly Dictionary<long, Layer> _layers;

	private bool _isRunning;

	private readonly Thread _composerThread;

	private readonly AutoResetEvent _event = new AutoResetEvent(initialState: false);

	private readonly AutoResetEvent _nextFrameEvent = new AutoResetEvent(initialState: true);

	private long _ticks;

	private long _ticksPerFrame;

	private readonly long _spinTicks;

	private readonly long _1msTicks;

	private Ryujinx.Common.Configuration.VSyncMode _vSyncMode;

	private long _targetVSyncInterval;

	private int _swapInterval;

	private int _swapIntervalDelay;

	private readonly Lock _lock = new Lock();

	public long RenderLayerId { get; private set; }

	public SurfaceFlinger(Switch device)
	{
		_device = device;
		_layers = new Dictionary<long, Layer>();
		RenderLayerId = 0L;
		_composerThread = new Thread(HandleComposition)
		{
			Name = "SurfaceFlinger.Composer",
			Priority = ThreadPriority.AboveNormal
		};
		_ticks = 0L;
		_spinTicks = Stopwatch.Frequency / 500;
		_1msTicks = Stopwatch.Frequency / 1000;
		UpdateSwapInterval(1);
		_composerThread.Start();
	}

	private void UpdateSwapInterval(int swapInterval)
	{
		_swapInterval = swapInterval;
		if (_swapInterval == 0)
		{
			_nextFrameEvent.Set();
			_ticksPerFrame = 1L;
		}
		else
		{
			_ticksPerFrame = Stopwatch.Frequency / _device.TargetVSyncInterval;
			_targetVSyncInterval = _device.TargetVSyncInterval;
		}
	}

	public IGraphicBufferProducer CreateLayer(out long layerId, ulong pid, LayerState initialState = LayerState.ManagedClosed)
	{
		layerId = 1L;
		using (_lock.EnterScope())
		{
			foreach (KeyValuePair<long, Layer> layer in _layers)
			{
				if (layer.Key >= layerId)
				{
					layerId = layer.Key + 1;
				}
			}
		}
		CreateLayerFromId(pid, layerId, initialState);
		return GetProducerByLayerId(layerId);
	}

	private void CreateLayerFromId(ulong pid, long layerId, LayerState initialState)
	{
		using (_lock.EnterScope())
		{
			Logger.Info?.Print(LogClass.SurfaceFlinger, $"Creating layer {layerId}", "CreateLayerFromId");
			BufferQueueCore bufferQueueCore = BufferQueue.CreateBufferQueue(_device, pid, out var producer, out var consumer);
			bufferQueueCore.BufferQueued += () =>
			{
				_nextFrameEvent.Set();
			};
			_layers.Add(layerId, new Layer
			{
				Id = layerId,
				ProducerBinderId = HOSBinderDriverServer.RegisterBinderObject(producer),
				Producer = producer,
				Consumer = new BufferItemConsumer(_device, consumer, 0u, -1, controlledByApp: false, new LayerListener(this, layerId, pid)),
				Core = bufferQueueCore,
				Owner = pid,
				State = initialState
			});
		}
	}

	public Ryujinx.HLE.HOS.Services.Vi.ResultCode OpenLayer(ulong pid, long layerId, out IBinder producer)
	{
		Layer layerByIdLocked = GetLayerByIdLocked(layerId);
		if (layerByIdLocked == null || layerByIdLocked.State != LayerState.ManagedClosed)
		{
			producer = null;
			return Ryujinx.HLE.HOS.Services.Vi.ResultCode.InvalidArguments;
		}
		layerByIdLocked.State = LayerState.ManagedOpened;
		producer = layerByIdLocked.Producer;
		return Ryujinx.HLE.HOS.Services.Vi.ResultCode.Success;
	}

	public Ryujinx.HLE.HOS.Services.Vi.ResultCode CloseLayer(long layerId)
	{
		using (_lock.EnterScope())
		{
			Layer layerByIdLocked = GetLayerByIdLocked(layerId);
			if (layerByIdLocked == null)
			{
				Logger.Error?.Print(LogClass.SurfaceFlinger, $"Failed to close layer {layerId}", "CloseLayer");
				return Ryujinx.HLE.HOS.Services.Vi.ResultCode.InvalidValue;
			}
			CloseLayer(layerId, layerByIdLocked);
			return Ryujinx.HLE.HOS.Services.Vi.ResultCode.Success;
		}
	}

	public Ryujinx.HLE.HOS.Services.Vi.ResultCode DestroyManagedLayer(long layerId)
	{
		using (_lock.EnterScope())
		{
			Layer layerByIdLocked = GetLayerByIdLocked(layerId);
			if (layerByIdLocked == null)
			{
				Logger.Error?.Print(LogClass.SurfaceFlinger, $"Failed to destroy managed layer {layerId} (not found)", "DestroyManagedLayer");
				return Ryujinx.HLE.HOS.Services.Vi.ResultCode.InvalidValue;
			}
			if (layerByIdLocked.State != LayerState.ManagedClosed && layerByIdLocked.State != LayerState.ManagedOpened)
			{
				Logger.Error?.Print(LogClass.SurfaceFlinger, $"Failed to destroy managed layer {layerId} (permission denied)", "DestroyManagedLayer");
				return Ryujinx.HLE.HOS.Services.Vi.ResultCode.PermissionDenied;
			}
			HOSBinderDriverServer.UnregisterBinderObject(layerByIdLocked.ProducerBinderId);
			if (_layers.Remove(layerId) && layerByIdLocked.State == LayerState.ManagedOpened)
			{
				CloseLayer(layerId, layerByIdLocked);
			}
			return Ryujinx.HLE.HOS.Services.Vi.ResultCode.Success;
		}
	}

	public Ryujinx.HLE.HOS.Services.Vi.ResultCode DestroyStrayLayer(long layerId)
	{
		using (_lock.EnterScope())
		{
			Layer layerByIdLocked = GetLayerByIdLocked(layerId);
			if (layerByIdLocked == null)
			{
				Logger.Error?.Print(LogClass.SurfaceFlinger, $"Failed to destroy stray layer {layerId} (not found)", "DestroyStrayLayer");
				return Ryujinx.HLE.HOS.Services.Vi.ResultCode.InvalidValue;
			}
			if (layerByIdLocked.State != LayerState.Stray)
			{
				Logger.Error?.Print(LogClass.SurfaceFlinger, $"Failed to destroy stray layer {layerId} (permission denied)", "DestroyStrayLayer");
				return Ryujinx.HLE.HOS.Services.Vi.ResultCode.PermissionDenied;
			}
			HOSBinderDriverServer.UnregisterBinderObject(layerByIdLocked.ProducerBinderId);
			if (_layers.Remove(layerId))
			{
				CloseLayer(layerId, layerByIdLocked);
			}
			return Ryujinx.HLE.HOS.Services.Vi.ResultCode.Success;
		}
	}

	private void CloseLayer(long layerId, Layer layer)
	{
		if (RenderLayerId == layerId)
		{
			if (_layers.Count == 0)
			{
				SetRenderLayer(0L);
			}
			else
			{
				SetRenderLayer(_layers.Last().Key);
			}
		}
		if (layer.State == LayerState.ManagedOpened)
		{
			layer.State = LayerState.ManagedClosed;
		}
	}

	public void SetRenderLayer(long layerId)
	{
		using (_lock.EnterScope())
		{
			RenderLayerId = layerId;
			_device.FramePolicy.SetRenderLayer(layerId);
		}
	}

	private Layer GetLayerByIdLocked(long layerId)
	{
		foreach (KeyValuePair<long, Layer> layer in _layers)
		{
			if (layer.Key == layerId)
			{
				return layer.Value;
			}
		}
		return null;
	}

	public IGraphicBufferProducer GetProducerByLayerId(long layerId)
	{
		using (_lock.EnterScope())
		{
			Layer layerByIdLocked = GetLayerByIdLocked(layerId);
			if (layerByIdLocked != null)
			{
				return layerByIdLocked.Producer;
			}
		}
		return null;
	}

	private void HandleComposition()
	{
		_isRunning = true;
		long num = PerformanceCounter.ElapsedTicks;
		while (_isRunning)
		{
			long elapsedTicks = PerformanceCounter.ElapsedTicks;
			if (_swapInterval == 0)
			{
				Compose();
				_device.System?.SignalVsync();
				_nextFrameEvent.WaitOne(17);
				num = elapsedTicks;
				continue;
			}
			_ticks += elapsedTicks - num;
			num = elapsedTicks;
			if (_ticks >= _ticksPerFrame)
			{
				if (_swapIntervalDelay > 0)
				{
					_swapIntervalDelay--;
				}
				else if (Compose())
				{
					_swapIntervalDelay = Math.Max(0, _swapInterval - 1);
				}
				_device.System?.SignalVsync();
				_ticks = Math.Min(_ticks - _ticksPerFrame, _ticksPerFrame * 3);
			}
			long num2 = _ticksPerFrame - (_ticks + PerformanceCounter.ElapsedTicks - elapsedTicks);
			if (num2 > 0)
			{
				PreciseSleepHelper.SleepUntilTimePoint(_event, PerformanceCounter.ElapsedTicks + num2);
				num2 = _ticksPerFrame - (_ticks + PerformanceCounter.ElapsedTicks - elapsedTicks);
				if (num2 < _spinTicks)
				{
					PreciseSleepHelper.SpinWaitUntilTimePoint(PerformanceCounter.ElapsedTicks + num2);
				}
				else
				{
					_event.WaitOne((int)(num2 / _1msTicks));
				}
			}
		}
	}

	public bool Compose()
	{
		using (_lock.EnterScope())
		{
			if (RenderLayerId == 0L)
			{
				return false;
			}
			Layer layerByIdLocked = GetLayerByIdLocked(RenderLayerId);
			if (layerByIdLocked == null)
			{
				return false;
			}
			BufferItem bufferItem;
			switch (layerByIdLocked.Consumer.AcquireBuffer(out bufferItem, 0uL))
			{
			case Status.Success:
			{
				_device.FramePolicy.RecordAcquired(layerByIdLocked.Id, layerByIdLocked.Owner, bufferItem.FrameNumber);
				int num = _device.FramePolicy.ResolveSwapInterval(layerByIdLocked.Id, layerByIdLocked.Owner, bufferItem.SwapInterval, _device.VSyncMode == Ryujinx.Common.Configuration.VSyncMode.Switch && _device.TargetVSyncInterval == 60);
				if (_device.VSyncMode == Ryujinx.Common.Configuration.VSyncMode.Unbounded)
				{
					if (_swapInterval != 0)
					{
						UpdateSwapInterval(0);
						_vSyncMode = _device.VSyncMode;
					}
				}
				else if (_device.VSyncMode != _vSyncMode)
				{
					UpdateSwapInterval(num);
					_vSyncMode = _device.VSyncMode;
				}
				else if (num != _swapInterval || _device.TargetVSyncInterval != _targetVSyncInterval)
				{
					UpdateSwapInterval(num);
				}
				return PostFrameBuffer(layerByIdLocked, bufferItem);
			}
			default:
				throw new InvalidOperationException();
			case Status.InvalidOperation:
			case Status.ReleaseAllBuffers:
				return false;
			}
		}
	}

	private bool PostFrameBuffer(Layer layer, BufferItem item)
	{
		int width = item.GraphicBuffer.Object.Width;
		int height = item.GraphicBuffer.Object.Height;
		int num = item.GraphicBuffer.Object.Buffer.Surfaces[0].NvMapHandle;
		if (num == 0)
		{
			num = item.GraphicBuffer.Object.Buffer.NvMapId;
		}
		ulong num2 = (ulong)item.GraphicBuffer.Object.Buffer.Surfaces[0].Offset;
		ulong address = NvMapDeviceFile.GetMapFromHandle(layer.Owner, num).Address + num2;
		Format format = ConvertColorFormat(item.GraphicBuffer.Object.Buffer.Surfaces[0].ColorFormat);
		byte bytesPerPixel = (byte)((format == Format.B5G6R5Unorm || format == Format.R4G4B4A4Unorm) ? 2 : 4);
		int gobBlocksInY = 1 << item.GraphicBuffer.Object.Buffer.Surfaces[0].BlockHeightLog2;
		Rect crop = item.Crop;
		bool flipX = item.Transform.HasFlag(NativeWindowTransform.FlipX);
		bool flipY = item.Transform.HasFlag(NativeWindowTransform.FlipY);
		AspectRatio aspectRatio = _device.Configuration.AspectRatio;
		bool isStretched = aspectRatio == AspectRatio.Stretched;
		ImageCrop crop2 = new ImageCrop(crop.Left, crop.Right, crop.Top, crop.Bottom, flipX, flipY, isStretched, aspectRatio.ToFloatX(), aspectRatio.ToFloatY());
		TextureCallbackInformation textureCallbackInformation = new TextureCallbackInformation
		{
			Layer = layer,
			Item = item,
			MeasurementSegment = _device.FramePolicy.CaptureMeasurementSegment()
		};
		if (_device.Gpu.Window.EnqueueFrameThreadSafe(layer.Owner, address, width, height, 0, isLinear: false, gobBlocksInY, format, bytesPerPixel, crop2, AcquireBuffer, ReleaseBuffer, textureCallbackInformation))
		{
			_device.FramePolicy.RecordComposited(layer.Id, layer.Owner, item.FrameNumber);
			if (item.Fence.FenceCount == 0)
			{
				_device.Gpu.Window.SignalFrameReady();
				_device.Gpu.GPFifo.Interrupt();
			}
			else
			{
				item.Fence.RegisterCallback(_device.Gpu, (SyncpointWaiterHandle x) =>
				{
					_device.Gpu.Window.SignalFrameReady();
					_device.Gpu.GPFifo.Interrupt();
				});
			}
			return true;
		}
		ReleaseBuffer(textureCallbackInformation);
		return false;
	}

	private void ReleaseBuffer(object obj)
	{
		ReleaseBuffer((TextureCallbackInformation)obj);
	}

	private void ReleaseBuffer(TextureCallbackInformation information)
	{
		AndroidFence fence = AndroidFence.NoFence;
		information.Layer.Consumer.ReleaseBuffer(information.Item, ref fence);
	}

	private void AcquireBuffer(GpuContext ignored, object obj)
	{
		AcquireBuffer((TextureCallbackInformation)obj);
	}

	private void AcquireBuffer(TextureCallbackInformation information)
	{
		information.Item.Fence.WaitForever(_device.Gpu);
		_device.FramePolicy.RecordReadyToPresent(information.Layer.Id, information.Layer.Owner, information.Item.FrameNumber, information.MeasurementSegment);
	}

	public static Format ConvertColorFormat(ColorFormat colorFormat)
	{
		return colorFormat switch
		{
			ColorFormat.A8B8G8R8 => Format.R8G8B8A8Unorm,
			ColorFormat.X8B8G8R8 => Format.R8G8B8A8Unorm,
			ColorFormat.R5G6B5 => Format.B5G6R5Unorm,
			ColorFormat.A8R8G8B8 => Format.B8G8R8A8Unorm,
			ColorFormat.A4B4G4R4 => Format.R4G4B4A4Unorm,
			_ => throw new NotImplementedException($"Color Format \"{colorFormat}\" not implemented!"),
		};
	}

	public void Dispose()
	{
		_isRunning = false;
		foreach (Layer value in _layers.Values)
		{
			value.Core.PrepareForExit();
		}
	}

	public void OnFrameAvailable(ref BufferItem item)
	{
		_device.Statistics.RecordGameFrameTime();
	}

	public void OnFrameReplaced(ref BufferItem item)
	{
		_device.Statistics.RecordGameFrameTime();
	}

	public void OnBuffersReleased()
	{
	}
}
