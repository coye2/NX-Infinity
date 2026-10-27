using System;
using System.Collections.Generic;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Vulkan.Queries;
using Silk.NET.Vulkan;

namespace Ryujinx.Graphics.Vulkan;

internal class PipelineFull : PipelineBase, IPipeline
{
	private const ulong MinByteWeightForFlush = 268435456uL;

	private readonly List<(QueryPool, bool)> _activeQueries;

	private CounterQueueEvent _activeConditionalRender;

	private readonly List<BufferedQuery> _pendingQueryCopies;

	private readonly List<BufferHolder> _activeBufferMirrors;

	private ulong _byteWeight;

	private readonly List<BufferHolder> _backingSwaps;

	public PipelineFull(VulkanRenderer gd, Device device)
		: base(gd, device, nxPersistentCache: true)
	{
		_activeQueries = new List<(QueryPool, bool)>();
		_pendingQueryCopies = new List<BufferedQuery>();
		_backingSwaps = new List<BufferHolder>();
		_activeBufferMirrors = new List<BufferHolder>();
		CommandBuffer = ((CommandBufferScoped)(Cbs = gd.CommandBufferPool.Rent())).CommandBuffer;
		IsMainPipeline = true;
	}

	private void CopyPendingQuery()
	{
		foreach (BufferedQuery pendingQueryCopy in _pendingQueryCopies)
		{
			pendingQueryCopy.PoolCopy(Cbs);
		}
		_pendingQueryCopies.Clear();
	}

	public void ClearRenderTargetColor(int index, int layer, int layerCount, uint componentMask, ColorF color)
	{
		if (FramebufferParams == null)
		{
			return;
		}
		if (componentMask != 15 || Gd.IsQualcommProprietary)
		{
			TextureView colorView = FramebufferParams.GetColorView(index);
			if (colorView != null)
			{
				Span<float> span = stackalloc float[4];
				span[0] = color.Red;
				span[1] = color.Green;
				span[2] = color.Blue;
				span[3] = color.Alpha;
				Gd.HelperShader.Clear(Gd, colorView, span, componentMask, (int)FramebufferParams.Width, (int)FramebufferParams.Height, FramebufferParams.GetAttachmentComponentType(index), ClearScissor);
			}
		}
		else
		{
			ClearRenderTargetColor(index, layer, layerCount, color);
		}
	}

	public void ClearRenderTargetDepthStencil(int layer, int layerCount, float depthValue, bool depthMask, int stencilValue, int stencilMask)
	{
		if (FramebufferParams == null)
		{
			return;
		}
		if ((stencilMask != 0 && stencilMask != 255) || Gd.IsQualcommProprietary)
		{
			TextureView depthStencilView = FramebufferParams.GetDepthStencilView();
			if (depthStencilView != null)
			{
				Gd.HelperShader.Clear(Gd, depthStencilView, depthValue, depthMask, stencilValue, stencilMask, (int)FramebufferParams.Width, (int)FramebufferParams.Height, FramebufferParams.AttachmentFormats[FramebufferParams.AttachmentsCount - 1], ClearScissor);
			}
		}
		else
		{
			ClearRenderTargetDepthStencil(layer, layerCount, depthValue, depthMask, stencilValue, stencilMask != 0);
		}
	}

	public void EndHostConditionalRendering()
	{
		_ = Gd.Capabilities.SupportsConditionalRendering;
		_activeConditionalRender?.ReleaseHostAccess();
		_activeConditionalRender = null;
	}

	public bool TryHostConditionalRendering(ICounterEvent value, ulong compare, bool isEqual)
	{
		if (value is CounterQueueEvent counterQueueEvent && compare == 0L && counterQueueEvent.Type == CounterType.SamplesPassed && counterQueueEvent.ClearCounter)
		{
			if (!value.ReserveForHostAccess())
			{
				return false;
			}
			_ = Gd.Capabilities.SupportsConditionalRendering;
			_activeConditionalRender = counterQueueEvent;
			return true;
		}
		FlushPendingQuery();
		return false;
	}

	public bool TryHostConditionalRendering(ICounterEvent value, ICounterEvent compare, bool isEqual)
	{
		FlushPendingQuery();
		return false;
	}

	private void FlushPendingQuery()
	{
		if (AutoFlush.ShouldFlushQuery())
		{
			FlushCommandsImpl();
		}
	}

	public CommandBufferScoped GetPreloadCommandBuffer()
	{
		CommandBufferScoped valueOrDefault = PreloadCbs.GetValueOrDefault();
		if (!PreloadCbs.HasValue)
		{
			valueOrDefault = Gd.CommandBufferPool.Rent();
			PreloadCbs = valueOrDefault;
		}
		return PreloadCbs.Value;
	}

	public void FlushCommandsIfWeightExceeding(IAuto disposedResource, ulong byteWeight)
	{
		bool flag = disposedResource.HasCommandBufferDependency(Cbs);
		if (PreloadCbs.HasValue && !flag)
		{
			flag = disposedResource.HasCommandBufferDependency(PreloadCbs.Value);
		}
		if (flag)
		{
			_byteWeight += byteWeight;
			if (_byteWeight >= 268435456)
			{
				FlushCommandsImpl();
			}
		}
	}

	public void Restore()
	{
		if (Pipeline != null)
		{
			Gd.Api.CmdBindPipeline(CommandBuffer, Pbp, Pipeline.Get(Cbs).Value);
		}
		SignalCommandBufferChange();
		if (Pipeline != null && Pbp == PipelineBindPoint.Graphics)
		{
			DynamicState.ReplayIfDirty(Gd, CommandBuffer);
		}
	}

	public void FlushCommandsImpl()
	{
		AutoFlush.RegisterFlush(DrawCount);
		EndRenderPass();
		foreach (var activeQuery in _activeQueries)
		{
			QueryPool item = activeQuery.Item1;
			Gd.Api.CmdEndQuery(CommandBuffer, item, 0u);
		}
		_byteWeight = 0uL;
		if (PreloadCbs.HasValue)
		{
			PreloadCbs.Value.Dispose();
			PreloadCbs = null;
		}
		Gd.Barriers.Flush(Cbs, inRenderPass: false, null, null);
		CommandBufferScoped commandBufferScoped = (Cbs = Gd.CommandBufferPool.ReturnAndRent(Cbs));
		CommandBuffer = commandBufferScoped.CommandBuffer;
		Gd.RegisterFlush();
		foreach (BufferHolder activeBufferMirror in _activeBufferMirrors)
		{
			activeBufferMirror.ClearMirrors();
		}
		_activeBufferMirrors.Clear();
		foreach (var activeQuery2 in _activeQueries)
		{
			QueryPool item2 = activeQuery2.Item1;
			bool item3 = activeQuery2.Item2;
			bool flag = Gd.Capabilities.SupportsPreciseOcclusionQueries & item3;
			Gd.Api.CmdResetQueryPool(CommandBuffer, item2, 0u, 1u);
			Gd.Api.CmdBeginQuery(CommandBuffer, item2, 0u, flag ? QueryControlFlags.QueryControlPreciseBit : QueryControlFlags.None);
		}
		Gd.ResetCounterPool();
		Restore();
	}

	public void RegisterActiveMirror(BufferHolder buffer)
	{
		_activeBufferMirrors.Add(buffer);
	}

	public void BeginQuery(BufferedQuery query, QueryPool pool, bool needsReset, bool isOcclusion, bool fromSamplePool)
	{
		if (needsReset)
		{
			EndRenderPass();
			Gd.Api.CmdResetQueryPool(CommandBuffer, pool, 0u, 1u);
			if (fromSamplePool)
			{
				Gd.ResetFutureCounters(CommandBuffer, AutoFlush.GetRemainingQueries());
			}
		}
		bool flag = Gd.Capabilities.SupportsPreciseOcclusionQueries & isOcclusion;
		Gd.Api.CmdBeginQuery(CommandBuffer, pool, 0u, flag ? QueryControlFlags.QueryControlPreciseBit : QueryControlFlags.None);
		_activeQueries.Add((pool, isOcclusion));
	}

	public void EndQuery(QueryPool pool)
	{
		Gd.Api.CmdEndQuery(CommandBuffer, pool, 0u);
		for (int i = 0; i < _activeQueries.Count; i++)
		{
			if (_activeQueries[i].Item1.Handle == pool.Handle)
			{
				_activeQueries.RemoveAt(i);
				break;
			}
		}
	}

	public void CopyQueryResults(BufferedQuery query)
	{
		_pendingQueryCopies.Add(query);
		if (AutoFlush.RegisterPendingQuery())
		{
			FlushCommandsImpl();
		}
	}

	protected override void SignalAttachmentChange()
	{
		if (AutoFlush.ShouldFlushAttachmentChange(DrawCount))
		{
			FlushCommandsImpl();
		}
	}

	protected override void SignalRenderPassEnd()
	{
		CopyPendingQuery();
	}
}
