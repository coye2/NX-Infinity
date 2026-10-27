using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Ryujinx.Common.Memory;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using Silk.NET.Core;
using Silk.NET.Vulkan;

namespace Ryujinx.Graphics.Vulkan;

internal class PipelineBase : IDisposable
{
	public const int DescriptorSetLayouts = 4;

	public const int UniformSetIndex = 0;

	public const int StorageSetIndex = 1;

	public const int TextureSetIndex = 2;

	public const int ImageSetIndex = 3;

	protected readonly VulkanRenderer Gd;

	protected readonly Device Device;

	public readonly PipelineCache PipelineCache;

	private readonly RyuDriverPipelineCache _nxDriverCache;

	public readonly AutoFlushCounter AutoFlush;

	public readonly Action EndRenderPassDelegate;

	protected PipelineDynamicState DynamicState;

	protected bool IsMainPipeline;

	private PipelineState _newState;

	private bool _graphicsStateDirty;

	private bool _computeStateDirty;

	private bool _bindingBarriersDirty;

	private Ryujinx.Graphics.GAL.PrimitiveTopology _topology;

	private ulong _currentPipelineHandle;

	protected Auto<DisposablePipeline> Pipeline;

	protected PipelineBindPoint Pbp;

	protected CommandBufferScoped Cbs;

	protected CommandBufferScoped? PreloadCbs;

	protected CommandBuffer CommandBuffer;

	private ShaderCollection _program;

	protected FramebufferParams FramebufferParams;

	private Auto<DisposableFramebuffer> _framebuffer;

	private RenderPassHolder _rpHolder;

	private Auto<DisposableRenderPass> _renderPass;

	private RenderPassHolder _nullRenderPass;

	private int _writtenAttachmentCount;

	private bool _framebufferUsingColorWriteMask;

	private ITexture[] _preMaskColors;

	private ITexture _preMaskDepthStencil;

	private readonly DescriptorSetUpdater _descriptorSetUpdater;

	private IndexBufferState _indexBuffer;

	private IndexBufferPattern _indexBufferPattern;

	private readonly BufferState[] _transformFeedbackBuffers;

	private readonly VertexBufferState[] _vertexBuffers;

	private ulong _vertexBuffersDirty;

	protected Rectangle<int> ClearScissor;

	private readonly VertexBufferUpdater _vertexBufferUpdater;

	public IndexBufferPattern QuadsToTrisPattern;

	public IndexBufferPattern TriFanToTrisPattern;

	private bool _needsIndexBufferRebind;

	private bool _needsTransformFeedbackBuffersRebind;

	private bool _tfEnabled;

	private bool _tfActive;

	private FeedbackLoopAspects _feedbackLoop;

	private bool _passWritesDepthStencil;

	private readonly PipelineColorBlendAttachmentState[] _storedBlend;

	public CommandBufferScoped CurrentCommandBuffer => Cbs;

	public ulong DrawCount { get; private set; }

	public bool RenderPassActive { get; private set; }

	public unsafe PipelineBase(VulkanRenderer gd, Device device, bool nxPersistentCache = false)
	{
		Gd = gd;
		Device = device;
		AutoFlush = new AutoFlushCounter(gd);
		EndRenderPassDelegate = EndRenderPass;
		PipelineCacheCreateInfo pCreateInfo = new PipelineCacheCreateInfo
		{
			SType = StructureType.PipelineCacheCreateInfo
		};
		if (nxPersistentCache)
		{
			_nxDriverCache = gd.CreateNxDriverCache();
			PipelineCache = _nxDriverCache.Create(gd.Api, device);
		}
		else
		{
			gd.Api.CreatePipelineCache(device, in pCreateInfo, null, out PipelineCache).ThrowOnError();
		}
		_descriptorSetUpdater = new DescriptorSetUpdater(gd, device);
		_vertexBufferUpdater = new VertexBufferUpdater(gd);
		_transformFeedbackBuffers = new BufferState[4];
		_vertexBuffers = new VertexBufferState[33];
		using BufferHolder bufferHolder = gd.BufferManager.Create(gd, 16);
		bufferHolder.SetData(0, new byte[16]);
		_vertexBuffers[0] = new VertexBufferState(bufferHolder.GetBuffer(), 0, 0, 16);
		_vertexBuffersDirty = ulong.MaxValue >> 64 - _vertexBuffers.Length;
		ClearScissor = new Rectangle<int>(0, 0, 65535, 65535);
		_storedBlend = new PipelineColorBlendAttachmentState[8];
		_newState.Initialize();
	}

	public void Initialize()
	{
		_descriptorSetUpdater.Initialize(IsMainPipeline);
		QuadsToTrisPattern = new IndexBufferPattern(Gd, 4, 6, 0, new int[6] { 0, 1, 2, 0, 2, 3 }, 4, repeatStart: false);
		TriFanToTrisPattern = new IndexBufferPattern(Gd, 3, 3, 2, new int[3] { -2147483648, -1, 0 }, 1, repeatStart: true);
	}

	public void Barrier()
	{
		Gd.Barriers.QueueMemoryBarrier();
	}

	public void ComputeBarrier()
	{
		MemoryBarrier reference = new MemoryBarrier
		{
			SType = StructureType.MemoryBarrier,
			SrcAccessMask = (AccessFlags.AccessMemoryReadBit | AccessFlags.AccessMemoryWriteBit),
			DstAccessMask = (AccessFlags.AccessMemoryReadBit | AccessFlags.AccessMemoryWriteBit)
		};
		Gd.Api.CmdPipelineBarrier(CommandBuffer, PipelineStageFlags.PipelineStageComputeShaderBit, PipelineStageFlags.PipelineStageAllCommandsBit, DependencyFlags.None, 1u, new ReadOnlySpan<MemoryBarrier>(in reference), 0u, ReadOnlySpan<BufferMemoryBarrier>.Empty, 0u, ReadOnlySpan<ImageMemoryBarrier>.Empty);
	}

	public void BeginTransformFeedback(Ryujinx.Graphics.GAL.PrimitiveTopology topology)
	{
		Gd.Barriers.EnableTfbBarriers(enable: true);
		_tfEnabled = true;
	}

	public void ClearBuffer(BufferHandle destination, int offset, int size, uint value)
	{
		EndRenderPass();
		Silk.NET.Vulkan.Buffer value2 = Gd.BufferManager.GetBuffer(CommandBuffer, destination, offset, size, isWrite: true).Get(Cbs, offset, size, write: true).Value;
		BufferHolder.InsertBufferBarrier(Gd, Cbs.CommandBuffer, value2, AccessFlags.AccessIndirectCommandReadBit | AccessFlags.AccessUniformReadBit | AccessFlags.AccessShaderReadBit | AccessFlags.AccessShaderWriteBit | AccessFlags.AccessTransferReadBit | AccessFlags.AccessTransferWriteBit, AccessFlags.AccessTransferWriteBit, PipelineStageFlags.PipelineStageAllCommandsBit, PipelineStageFlags.PipelineStageTransferBit, offset, size);
		Gd.Api.CmdFillBuffer(CommandBuffer, value2, (ulong)offset, (ulong)size, value);
		BufferHolder.InsertBufferBarrier(Gd, Cbs.CommandBuffer, value2, AccessFlags.AccessTransferWriteBit, AccessFlags.AccessIndirectCommandReadBit | AccessFlags.AccessUniformReadBit | AccessFlags.AccessShaderReadBit | AccessFlags.AccessShaderWriteBit | AccessFlags.AccessTransferReadBit | AccessFlags.AccessTransferWriteBit, PipelineStageFlags.PipelineStageTransferBit, PipelineStageFlags.PipelineStageAllCommandsBit, offset, size);
	}

	public unsafe void ClearRenderTargetColor(int index, int layer, int layerCount, ColorF color)
	{
		if (FramebufferParams != null && FramebufferParams.IsValidColorAttachment(index))
		{
			if (_renderPass == null)
			{
				CreateRenderPass();
			}
			Gd.Barriers.Flush(Cbs, RenderPassActive, _rpHolder, EndRenderPassDelegate);
			BeginRenderPass();
			ClearValue value = new ClearValue(new ClearColorValue(color.Red, color.Green, color.Blue, color.Alpha));
			ClearAttachment clearAttachment = new ClearAttachment(ImageAspectFlags.ImageAspectColorBit, (uint)index, value);
			ClearRect clearRect = FramebufferParams.GetClearRect(ClearScissor, layer, layerCount);
			Gd.Api.CmdClearAttachments(CommandBuffer, 1u, &clearAttachment, 1u, &clearRect);
		}
	}

	public unsafe void ClearRenderTargetDepthStencil(int layer, int layerCount, float depthValue, bool depthMask, int stencilValue, bool stencilMask)
	{
		if (FramebufferParams == null || !FramebufferParams.HasDepthStencil)
		{
			return;
		}
		ClearValue value = new ClearValue(null, new ClearDepthStencilValue(depthValue, (uint)stencilValue));
		ImageAspectFlags imageAspectFlags = (depthMask ? ImageAspectFlags.ImageAspectDepthBit : ImageAspectFlags.ImageAspectNoneKhr);
		if (stencilMask)
		{
			imageAspectFlags |= ImageAspectFlags.ImageAspectStencilBit;
		}
		imageAspectFlags &= FramebufferParams.GetDepthStencilAspectFlags();
		if (imageAspectFlags != ImageAspectFlags.ImageAspectNoneKhr)
		{
			if (_renderPass == null)
			{
				CreateRenderPass();
			}
			Gd.Barriers.Flush(Cbs, RenderPassActive, _rpHolder, EndRenderPassDelegate);
			BeginRenderPass();
			ClearAttachment clearAttachment = new ClearAttachment(imageAspectFlags, 0u, value);
			ClearRect clearRect = FramebufferParams.GetClearRect(ClearScissor, layer, layerCount);
			Gd.Api.CmdClearAttachments(CommandBuffer, 1u, &clearAttachment, 1u, &clearRect);
		}
	}

	public void CommandBufferBarrier()
	{
		Gd.Barriers.QueueCommandBufferBarrier();
	}

	public void CopyBuffer(BufferHandle source, BufferHandle destination, int srcOffset, int dstOffset, int size)
	{
		EndRenderPass();
		Auto<DisposableBuffer> buffer = Gd.BufferManager.GetBuffer(CommandBuffer, source, srcOffset, size, isWrite: false);
		Auto<DisposableBuffer> buffer2 = Gd.BufferManager.GetBuffer(CommandBuffer, destination, dstOffset, size, isWrite: true);
		BufferHolder.Copy(Gd, Cbs, buffer, buffer2, srcOffset, dstOffset, size);
	}

	public void DirtyVertexBuffer(Auto<DisposableBuffer> buffer)
	{
		for (int i = 0; i < _vertexBuffers.Length; i++)
		{
			if (_vertexBuffers[i].BoundEquals(buffer))
			{
				_vertexBuffersDirty |= (ulong)(1L << i);
			}
		}
	}

	public void DirtyIndexBuffer(Auto<DisposableBuffer> buffer)
	{
		if (_indexBuffer.BoundEquals(buffer))
		{
			_needsIndexBufferRebind = true;
		}
	}

	public void DispatchCompute(int groupsX, int groupsY, int groupsZ)
	{
		if (_program.IsLinked)
		{
			EndRenderPass();
			RecreateComputePipelineIfNeeded();
			Gd.Api.CmdDispatch(CommandBuffer, (uint)groupsX, (uint)groupsY, (uint)groupsZ);
		}
	}

	public void DispatchComputeIndirect(Auto<DisposableBuffer> indirectBuffer, int indirectBufferOffset)
	{
		if (_program.IsLinked)
		{
			EndRenderPass();
			RecreateComputePipelineIfNeeded();
			Gd.Api.CmdDispatchIndirect(CommandBuffer, indirectBuffer.Get(Cbs, indirectBufferOffset, 12).Value, (ulong)indirectBufferOffset);
		}
	}

	public void Draw(int vertexCount, int instanceCount, int firstVertex, int firstInstance)
	{
		if (vertexCount == 0 || !RecreateGraphicsPipelineIfNeeded())
		{
			return;
		}
		BeginRenderPass();
		DrawCount++;
		if (Gd.TopologyUnsupported(_topology))
		{
			_needsIndexBufferRebind = true;
			IndexBufferPattern indexBufferPattern;
			switch (_topology)
			{
			case Ryujinx.Graphics.GAL.PrimitiveTopology.Quads:
				indexBufferPattern = QuadsToTrisPattern;
				break;
			case Ryujinx.Graphics.GAL.PrimitiveTopology.TriangleFan:
			case Ryujinx.Graphics.GAL.PrimitiveTopology.Polygon:
				indexBufferPattern = TriFanToTrisPattern;
				break;
			default:
				throw new NotSupportedException($"Unsupported topology: {_topology}");
			}
			BufferHandle repeatingBuffer = indexBufferPattern.GetRepeatingBuffer(vertexCount, out var indexCount);
			Auto<DisposableBuffer> buffer = Gd.BufferManager.GetBuffer(CommandBuffer, repeatingBuffer, isWrite: false);
			Gd.Api.CmdBindIndexBuffer(CommandBuffer, buffer.Get(Cbs, 0, indexCount * 4).Value, 0uL, Silk.NET.Vulkan.IndexType.Uint32);
			BeginRenderPass();
			ResumeTransformFeedbackInternal();
			Gd.Api.CmdDrawIndexed(CommandBuffer, (uint)indexCount, (uint)instanceCount, 0u, firstVertex, (uint)firstInstance);
		}
		else
		{
			ResumeTransformFeedbackInternal();
			Gd.Api.CmdDraw(CommandBuffer, (uint)vertexCount, (uint)instanceCount, (uint)firstVertex, (uint)firstInstance);
		}
	}

	private void UpdateIndexBufferPattern()
	{
		IndexBufferPattern indexBufferPattern = null;
		if (Gd.TopologyUnsupported(_topology))
		{
			IndexBufferPattern indexBufferPattern2;
			switch (_topology)
			{
			case Ryujinx.Graphics.GAL.PrimitiveTopology.Quads:
				indexBufferPattern2 = QuadsToTrisPattern;
				break;
			case Ryujinx.Graphics.GAL.PrimitiveTopology.TriangleFan:
			case Ryujinx.Graphics.GAL.PrimitiveTopology.Polygon:
				indexBufferPattern2 = TriFanToTrisPattern;
				break;
			default:
				throw new NotSupportedException($"Unsupported topology: {_topology}");
			}
			indexBufferPattern = indexBufferPattern2;
		}
		if (_indexBufferPattern != indexBufferPattern)
		{
			_indexBufferPattern = indexBufferPattern;
			_needsIndexBufferRebind = true;
		}
	}

	public void DrawIndexed(int indexCount, int instanceCount, int firstIndex, int firstVertex, int firstInstance)
	{
		if (indexCount == 0)
		{
			return;
		}
		UpdateIndexBufferPattern();
		if (!RecreateGraphicsPipelineIfNeeded())
		{
			return;
		}
		BeginRenderPass();
		DrawCount++;
		if (_indexBufferPattern != null)
		{
			IndexBufferPattern indexBufferPattern = _indexBufferPattern;
			int convertedCount = indexBufferPattern.GetConvertedCount(indexCount);
			if (_needsIndexBufferRebind)
			{
				_indexBuffer.BindConvertedIndexBuffer(Gd, Cbs, firstIndex, indexCount, convertedCount, indexBufferPattern);
				_needsIndexBufferRebind = false;
			}
			BeginRenderPass();
			ResumeTransformFeedbackInternal();
			Gd.Api.CmdDrawIndexed(CommandBuffer, (uint)convertedCount, (uint)instanceCount, 0u, firstVertex, (uint)firstInstance);
		}
		else
		{
			ResumeTransformFeedbackInternal();
			Gd.Api.CmdDrawIndexed(CommandBuffer, (uint)indexCount, (uint)instanceCount, (uint)firstIndex, firstVertex, (uint)firstInstance);
		}
	}

	public void DrawIndexedIndirect(BufferRange indirectBuffer)
	{
		Silk.NET.Vulkan.Buffer value = Gd.BufferManager.GetBuffer(CommandBuffer, indirectBuffer.Handle, indirectBuffer.Offset, indirectBuffer.Size, isWrite: false).Get(Cbs, indirectBuffer.Offset, indirectBuffer.Size).Value;
		UpdateIndexBufferPattern();
		if (RecreateGraphicsPipelineIfNeeded())
		{
			BeginRenderPass();
			DrawCount++;
			if (_indexBufferPattern != null)
			{
				IndexBufferPattern indexBufferPattern = _indexBufferPattern;
				Auto<DisposableBuffer> auto = _indexBuffer.BindConvertedIndexBufferIndirect(Gd, Cbs, indirectBuffer, BufferRange.Empty, indexBufferPattern, hasDrawCount: false, 1, indirectBuffer.Size);
				_needsIndexBufferRebind = false;
				BeginRenderPass();
				ResumeTransformFeedbackInternal();
				Gd.Api.CmdDrawIndexedIndirect(CommandBuffer, auto.Get(Cbs, 0, indirectBuffer.Size).Value, 0uL, 1u, (uint)indirectBuffer.Size);
			}
			else
			{
				ResumeTransformFeedbackInternal();
				Gd.Api.CmdDrawIndexedIndirect(CommandBuffer, value, (ulong)indirectBuffer.Offset, 1u, (uint)indirectBuffer.Size);
			}
		}
	}

	public void DrawIndexedIndirectCount(BufferRange indirectBuffer, BufferRange parameterBuffer, int maxDrawCount, int stride)
	{
		Silk.NET.Vulkan.Buffer value = Gd.BufferManager.GetBuffer(CommandBuffer, parameterBuffer.Handle, parameterBuffer.Offset, parameterBuffer.Size, isWrite: false).Get(Cbs, parameterBuffer.Offset, parameterBuffer.Size).Value;
		Silk.NET.Vulkan.Buffer value2 = Gd.BufferManager.GetBuffer(CommandBuffer, indirectBuffer.Handle, indirectBuffer.Offset, indirectBuffer.Size, isWrite: false).Get(Cbs, indirectBuffer.Offset, indirectBuffer.Size).Value;
		UpdateIndexBufferPattern();
		if (!RecreateGraphicsPipelineIfNeeded())
		{
			return;
		}
		BeginRenderPass();
		DrawCount++;
		if (_indexBufferPattern != null)
		{
			IndexBufferPattern indexBufferPattern = _indexBufferPattern;
			Auto<DisposableBuffer> auto = _indexBuffer.BindConvertedIndexBufferIndirect(Gd, Cbs, indirectBuffer, parameterBuffer, indexBufferPattern, hasDrawCount: true, maxDrawCount, stride);
			_needsIndexBufferRebind = false;
			BeginRenderPass();
			ResumeTransformFeedbackInternal();
			if (Gd.Capabilities.SupportsIndirectParameters)
			{
				Gd.DrawIndirectCountApi.CmdDrawIndexedIndirectCount(CommandBuffer, auto.Get(Cbs, 0, indirectBuffer.Size).Value, 0uL, value, (ulong)parameterBuffer.Offset, (uint)maxDrawCount, (uint)stride);
			}
			else
			{
				Gd.Api.CmdDrawIndexedIndirect(CommandBuffer, auto.Get(Cbs, 0, indirectBuffer.Size).Value, 0uL, (uint)maxDrawCount, (uint)stride);
			}
		}
		else
		{
			ResumeTransformFeedbackInternal();
			if (Gd.Capabilities.SupportsIndirectParameters)
			{
				Gd.DrawIndirectCountApi.CmdDrawIndexedIndirectCount(CommandBuffer, value2, (ulong)indirectBuffer.Offset, value, (ulong)parameterBuffer.Offset, (uint)maxDrawCount, (uint)stride);
			}
			else
			{
				Gd.Api.CmdDrawIndexedIndirect(CommandBuffer, value2, (ulong)indirectBuffer.Offset, (uint)maxDrawCount, (uint)stride);
			}
		}
	}

	public void DrawIndirect(BufferRange indirectBuffer)
	{
		Silk.NET.Vulkan.Buffer value = Gd.BufferManager.GetBuffer(CommandBuffer, indirectBuffer.Handle, indirectBuffer.Offset, indirectBuffer.Size, isWrite: false).Get(Cbs, indirectBuffer.Offset, indirectBuffer.Size).Value;
		if (RecreateGraphicsPipelineIfNeeded())
		{
			BeginRenderPass();
			ResumeTransformFeedbackInternal();
			DrawCount++;
			Gd.Api.CmdDrawIndirect(CommandBuffer, value, (ulong)indirectBuffer.Offset, 1u, (uint)indirectBuffer.Size);
		}
	}

	public void DrawIndirectCount(BufferRange indirectBuffer, BufferRange parameterBuffer, int maxDrawCount, int stride)
	{
		if (!Gd.Capabilities.SupportsIndirectParameters)
		{
			throw new NotSupportedException();
		}
		Silk.NET.Vulkan.Buffer value = Gd.BufferManager.GetBuffer(CommandBuffer, indirectBuffer.Handle, indirectBuffer.Offset, indirectBuffer.Size, isWrite: false).Get(Cbs, indirectBuffer.Offset, indirectBuffer.Size).Value;
		Silk.NET.Vulkan.Buffer value2 = Gd.BufferManager.GetBuffer(CommandBuffer, parameterBuffer.Handle, parameterBuffer.Offset, parameterBuffer.Size, isWrite: false).Get(Cbs, parameterBuffer.Offset, parameterBuffer.Size).Value;
		if (RecreateGraphicsPipelineIfNeeded())
		{
			BeginRenderPass();
			ResumeTransformFeedbackInternal();
			DrawCount++;
			Gd.DrawIndirectCountApi.CmdDrawIndirectCount(CommandBuffer, value, (ulong)indirectBuffer.Offset, value2, (ulong)parameterBuffer.Offset, (uint)maxDrawCount, (uint)stride);
		}
	}

	public void DrawTexture(ITexture texture, ISampler sampler, Extents2DF srcRegion, Extents2DF dstRegion)
	{
		if (texture is TextureView src)
		{
			CullModeFlags cullMode = _newState.CullMode;
			bool stencilTestEnable = _newState.StencilTestEnable;
			bool depthTestEnable = _newState.DepthTestEnable;
			bool depthWriteEnable = _newState.DepthWriteEnable;
			Array16<Silk.NET.Vulkan.Viewport> viewports = DynamicState.Viewports;
			uint viewportsCount = _newState.ViewportsCount;
			Ryujinx.Graphics.GAL.PrimitiveTopology topology = _topology;
			_newState.CullMode = CullModeFlags.CullModeNone;
			_newState.StencilTestEnable = false;
			_newState.DepthTestEnable = false;
			_newState.DepthWriteEnable = false;
			SignalStateChange();
			Gd.HelperShader.DrawTexture(Gd, this, src, sampler, srcRegion, dstRegion);
			_newState.CullMode = cullMode;
			_newState.StencilTestEnable = stencilTestEnable;
			_newState.DepthTestEnable = depthTestEnable;
			_newState.DepthWriteEnable = depthWriteEnable;
			SetPrimitiveTopology(topology);
			DynamicState.SetViewports(ref viewports, viewportsCount);
			_newState.ViewportsCount = viewportsCount;
			SignalStateChange();
		}
	}

	public void EndTransformFeedback()
	{
		Gd.Barriers.EnableTfbBarriers(enable: false);
		PauseTransformFeedbackInternal();
		_tfEnabled = false;
	}

	public bool IsCommandBufferActive(CommandBuffer cb)
	{
		return CommandBuffer.Handle == cb.Handle;
	}

	internal void Rebind(Auto<DisposableBuffer> buffer, int offset, int size)
	{
		_descriptorSetUpdater.Rebind(buffer, offset, size);
		if (_indexBuffer.Overlaps(buffer, offset, size))
		{
			_indexBuffer.BindIndexBuffer(Gd, Cbs);
		}
		for (int i = 0; i < _vertexBuffers.Length; i++)
		{
			if (_vertexBuffers[i].Overlaps(buffer, offset, size))
			{
				_vertexBuffers[i].BindVertexBuffer(Gd, Cbs, (uint)i, ref _newState, _vertexBufferUpdater);
			}
		}
		_vertexBufferUpdater.Commit(Cbs);
	}

	public void SetAlphaTest(bool enable, float reference, Ryujinx.Graphics.GAL.CompareOp op)
	{
	}

	public void SetBlendState(AdvancedBlendDescriptor blend)
	{
		for (int i = 0; i < 8; i++)
		{
			ref PipelineColorBlendAttachmentState reference = ref _newState.Internal.ColorBlendAttachmentState[i];
			if (i == 0)
			{
				Silk.NET.Vulkan.BlendOp value = blend.Op.Convert();
				Bool32? blendEnable = true;
				Silk.NET.Vulkan.BlendOp? colorBlendOp = value;
				Silk.NET.Vulkan.BlendOp? alphaBlendOp = value;
				ColorComponentFlags? colorWriteMask = reference.ColorWriteMask;
				reference = new PipelineColorBlendAttachmentState(blendEnable, null, null, colorBlendOp, null, null, alphaBlendOp, colorWriteMask);
				if (Gd.Capabilities.SupportsBlendEquationAdvancedNonPreMultipliedSrcColor)
				{
					_newState.AdvancedBlendSrcPreMultiplied = blend.SrcPreMultiplied;
				}
				if (Gd.Capabilities.SupportsBlendEquationAdvancedCorrelatedOverlap)
				{
					_newState.AdvancedBlendOverlap = blend.Overlap.Convert();
				}
			}
			else
			{
				ColorComponentFlags? colorWriteMask = reference.ColorWriteMask;
				reference = new PipelineColorBlendAttachmentState(null, null, null, null, null, null, null, colorWriteMask);
			}
			if (reference.ColorWriteMask == ColorComponentFlags.None)
			{
				_storedBlend[i] = reference;
				reference = default;
			}
		}
		SignalStateChange();
	}

	public void SetBlendState(int index, BlendDescriptor blend)
	{
		ref PipelineColorBlendAttachmentState reference = ref _newState.Internal.ColorBlendAttachmentState[index];
		if (blend.Enable)
		{
			reference.BlendEnable = blend.Enable;
			reference.SrcColorBlendFactor = blend.ColorSrcFactor.Convert();
			reference.DstColorBlendFactor = blend.ColorDstFactor.Convert();
			reference.ColorBlendOp = blend.ColorOp.Convert();
			reference.SrcAlphaBlendFactor = blend.AlphaSrcFactor.Convert();
			reference.DstAlphaBlendFactor = blend.AlphaDstFactor.Convert();
			reference.AlphaBlendOp = blend.AlphaOp.Convert();
		}
		else
		{
			ColorComponentFlags? colorWriteMask = reference.ColorWriteMask;
			reference = new PipelineColorBlendAttachmentState(null, null, null, null, null, null, null, colorWriteMask);
		}
		if (reference.ColorWriteMask == ColorComponentFlags.None)
		{
			_storedBlend[index] = reference;
			reference = default;
		}
		DynamicState.SetBlendConstants(blend.BlendConstant.Red, blend.BlendConstant.Green, blend.BlendConstant.Blue, blend.BlendConstant.Alpha);
		_newState.AdvancedBlendSrcPreMultiplied = true;
		_newState.AdvancedBlendDstPreMultiplied = true;
		_newState.AdvancedBlendOverlap = BlendOverlapEXT.BlendOverlapUncorrelatedExt;
		SignalStateChange();
	}

	public void SetDepthBias(PolygonModeMask enables, float factor, float units, float clamp)
	{
		DynamicState.SetDepthBias(factor, units, clamp);
		_newState.DepthBiasEnable = enables != 0;
		SignalStateChange();
	}

	public void SetDepthClamp(bool clamp)
	{
		_newState.DepthClampEnable = clamp;
		SignalStateChange();
	}

	public void SetDepthMode(DepthMode mode)
	{
		bool depthMode = _newState.DepthMode;
		_newState.DepthMode = mode == DepthMode.MinusOneToOne;
		if (_newState.DepthMode != depthMode)
		{
			SignalStateChange();
		}
	}

	public void SetDepthTest(DepthTestDescriptor depthTest)
	{
		_newState.DepthTestEnable = depthTest.TestEnable;
		_newState.DepthWriteEnable = depthTest.WriteEnable;
		_newState.DepthCompareOp = depthTest.Func.Convert();
		UpdatePassDepthStencil();
		SignalStateChange();
	}

	public void SetFaceCulling(bool enable, Face face)
	{
		_newState.CullMode = (enable ? face.Convert() : CullModeFlags.CullModeNone);
		SignalStateChange();
	}

	public void SetFrontFace(Ryujinx.Graphics.GAL.FrontFace frontFace)
	{
		_newState.FrontFace = frontFace.Convert();
		SignalStateChange();
	}

	public void SetImage(ShaderStage stage, int binding, ITexture image)
	{
		_descriptorSetUpdater.SetImage(Cbs, stage, binding, image);
	}

	public void SetImage(int binding, Auto<DisposableImageView> image)
	{
		_descriptorSetUpdater.SetImage(binding, image);
	}

	public void SetImageArray(ShaderStage stage, int binding, IImageArray array)
	{
		_descriptorSetUpdater.SetImageArray(Cbs, stage, binding, array);
	}

	public void SetImageArraySeparate(ShaderStage stage, int setIndex, IImageArray array)
	{
		_descriptorSetUpdater.SetImageArraySeparate(Cbs, stage, setIndex, array);
	}

	public void SetIndexBuffer(BufferRange buffer, Ryujinx.Graphics.GAL.IndexType type)
	{
		if (buffer.Handle != BufferHandle.Null)
		{
			_indexBuffer = new IndexBufferState(buffer.Handle, buffer.Offset, buffer.Size, type.Convert());
		}
		else
		{
			_indexBuffer = IndexBufferState.Null;
		}
		_needsIndexBufferRebind = true;
	}

	public void SetLineParameters(float width, bool smooth)
	{
		_newState.LineWidth = width;
		SignalStateChange();
	}

	public void SetLogicOpState(bool enable, LogicalOp op)
	{
		_newState.LogicOpEnable = enable;
		_newState.LogicOp = op.Convert();
		SignalStateChange();
	}

	public void SetMultisampleState(MultisampleDescriptor multisample)
	{
		_newState.AlphaToCoverageEnable = multisample.AlphaToCoverageEnable;
		_newState.AlphaToOneEnable = multisample.AlphaToOneEnable;
		SignalStateChange();
	}

	public void SetPatchParameters(int vertices, ReadOnlySpan<float> defaultOuterLevel, ReadOnlySpan<float> defaultInnerLevel)
	{
		_newState.PatchControlPoints = (uint)vertices;
		SignalStateChange();
	}

	public void SetPointParameters(float size, bool isProgramPointSize, bool enablePointSprite, Origin origin)
	{
	}

	public void SetPolygonMode(Ryujinx.Graphics.GAL.PolygonMode frontMode, Ryujinx.Graphics.GAL.PolygonMode backMode)
	{
	}

	public void SetPrimitiveRestart(bool enable, int index)
	{
		_newState.PrimitiveRestartEnable = enable;
		SignalStateChange();
	}

	public void SetPrimitiveTopology(Ryujinx.Graphics.GAL.PrimitiveTopology topology)
	{
		_topology = topology;
		Silk.NET.Vulkan.PrimitiveTopology topology2 = Gd.TopologyRemap(topology).Convert();
		_newState.Topology = topology2;
		SignalStateChange();
	}

	public void SetProgram(IProgram program)
	{
		ShaderCollection shaderCollection = (ShaderCollection)program;
		PipelineShaderStageCreateInfo[] infos = shaderCollection.GetInfos();
		_program = shaderCollection;
		_descriptorSetUpdater.SetProgram(Cbs, shaderCollection, _currentPipelineHandle != 0);
		_bindingBarriersDirty = true;
		_newState.PipelineLayout = shaderCollection.PipelineLayout;
		_newState.HasTessellationControlShader = shaderCollection.HasTessellationControlShader;
		_newState.StagesCount = (uint)infos.Length;
		infos.CopyTo(_newState.Stages.AsSpan().Slice(0, infos.Length));
		SignalStateChange();
		if (shaderCollection.IsCompute)
		{
			EndRenderPass();
		}
	}

	public void Specialize<T>(in T data) where T : unmanaged
	{
		ReadOnlySpan<byte> readOnlySpan = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in Unsafe.AsRef(in data), 1));
		if (!readOnlySpan.SequenceEqual(_newState.SpecializationData.Span))
		{
			_newState.SpecializationData = new SpecData(readOnlySpan);
			SignalStateChange();
		}
	}

	protected virtual void SignalAttachmentChange()
	{
	}

	public void SetRasterizerDiscard(bool discard)
	{
		_newState.RasterizerDiscardEnable = discard;
		SignalStateChange();
		if (!discard && Gd.IsQualcommProprietary)
		{
			DynamicState.ForceAllDirty();
		}
	}

	public void SetRenderTargetColorMasks(ReadOnlySpan<uint> componentMask)
	{
		int num = Math.Min(8, componentMask.Length);
		int num2 = 0;
		for (int i = 0; i < num; i++)
		{
			ref PipelineColorBlendAttachmentState reference = ref _newState.Internal.ColorBlendAttachmentState[i];
			ColorComponentFlags colorComponentFlags = (ColorComponentFlags)componentMask[i];
			if (reference.ColorWriteMask != colorComponentFlags)
			{
				if (colorComponentFlags == ColorComponentFlags.None)
				{
					_storedBlend[i] = reference;
					reference = default;
				}
				else if (reference.ColorWriteMask == ColorComponentFlags.None)
				{
					reference = _storedBlend[i];
				}
			}
			reference.ColorWriteMask = colorComponentFlags;
			if (componentMask[i] != 0)
			{
				num2++;
			}
		}
		if (_framebufferUsingColorWriteMask)
		{
			SetRenderTargetsInternal(_preMaskColors, _preMaskDepthStencil, filterWriteMasked: true);
			return;
		}
		SignalStateChange();
		if (num2 != _writtenAttachmentCount)
		{
			SignalAttachmentChange();
			_writtenAttachmentCount = num2;
		}
	}

	private void SetRenderTargetsInternal(ITexture[] colors, ITexture depthStencil, bool filterWriteMasked)
	{
		CreateFramebuffer(colors, depthStencil, filterWriteMasked);
		CreateRenderPass();
		SignalStateChange();
		SignalAttachmentChange();
	}

	public void SetRenderTargets(ITexture[] colors, ITexture depthStencil)
	{
		_framebufferUsingColorWriteMask = false;
		SetRenderTargetsInternal(colors, depthStencil, Gd.IsTBDR);
	}

	public void SetScissors(ReadOnlySpan<Rectangle<int>> regions)
	{
		int num = Math.Min((!Gd.Capabilities.SupportsMultiView) ? 1 : 16, regions.Length);
		if (num > 0)
		{
			ClearScissor = regions[0];
		}
		for (int i = 0; i < num; i++)
		{
			Rectangle<int> rectangle = regions[i];
			Offset2D value = new Offset2D(rectangle.X, rectangle.Y);
			Extent2D value2 = new Extent2D((uint)rectangle.Width, (uint)rectangle.Height);
			DynamicState.SetScissor(i, new Rect2D(value, value2));
		}
		DynamicState.ScissorsCount = num;
		_newState.ScissorsCount = (uint)num;
		SignalStateChange();
	}

	public void SetStencilTest(StencilTestDescriptor stencilTest)
	{
		DynamicState.SetStencilMasks((uint)stencilTest.BackFuncMask, (uint)stencilTest.BackMask, (uint)stencilTest.BackFuncRef, (uint)stencilTest.FrontFuncMask, (uint)stencilTest.FrontMask, (uint)stencilTest.FrontFuncRef);
		_newState.StencilTestEnable = stencilTest.TestEnable;
		_newState.StencilBackFailOp = stencilTest.BackSFail.Convert();
		_newState.StencilBackPassOp = stencilTest.BackDpPass.Convert();
		_newState.StencilBackDepthFailOp = stencilTest.BackDpFail.Convert();
		_newState.StencilBackCompareOp = stencilTest.BackFunc.Convert();
		_newState.StencilFrontFailOp = stencilTest.FrontSFail.Convert();
		_newState.StencilFrontPassOp = stencilTest.FrontDpPass.Convert();
		_newState.StencilFrontDepthFailOp = stencilTest.FrontDpFail.Convert();
		_newState.StencilFrontCompareOp = stencilTest.FrontFunc.Convert();
		UpdatePassDepthStencil();
		SignalStateChange();
	}

	public void SetStorageBuffers(ReadOnlySpan<BufferAssignment> buffers)
	{
		_descriptorSetUpdater.SetStorageBuffers(CommandBuffer, buffers);
	}

	public void SetStorageBuffers(int first, ReadOnlySpan<Auto<DisposableBuffer>> buffers)
	{
		_descriptorSetUpdater.SetStorageBuffers(CommandBuffer, first, buffers);
	}

	public void SetTextureAndSampler(ShaderStage stage, int binding, ITexture texture, ISampler sampler)
	{
		_descriptorSetUpdater.SetTextureAndSampler(Cbs, stage, binding, texture, sampler);
	}

	public void SetTextureAndSamplerIdentitySwizzle(ShaderStage stage, int binding, ITexture texture, ISampler sampler)
	{
		_descriptorSetUpdater.SetTextureAndSamplerIdentitySwizzle(Cbs, stage, binding, texture, sampler);
	}

	public void SetTextureArray(ShaderStage stage, int binding, ITextureArray array)
	{
		_descriptorSetUpdater.SetTextureArray(Cbs, stage, binding, array);
	}

	public void SetTextureArraySeparate(ShaderStage stage, int setIndex, ITextureArray array)
	{
		_descriptorSetUpdater.SetTextureArraySeparate(Cbs, stage, setIndex, array);
	}

	public void SetTransformFeedbackBuffers(ReadOnlySpan<BufferRange> buffers)
	{
		PauseTransformFeedbackInternal();
		int num = Math.Min(4, buffers.Length);
		for (int i = 0; i < num; i++)
		{
			BufferRange bufferRange = buffers[i];
			_transformFeedbackBuffers[i].Dispose();
			if (bufferRange.Handle != BufferHandle.Null)
			{
				_transformFeedbackBuffers[i] = new BufferState(Gd.BufferManager.GetBuffer(CommandBuffer, bufferRange.Handle, bufferRange.Offset, bufferRange.Size, isWrite: true), bufferRange.Offset, bufferRange.Size);
				_transformFeedbackBuffers[i].BindTransformFeedbackBuffer(Gd, Cbs, (uint)i);
			}
			else
			{
				_transformFeedbackBuffers[i] = BufferState.Null;
			}
		}
	}

	public void SetUniformBuffers(ReadOnlySpan<BufferAssignment> buffers)
	{
		_descriptorSetUpdater.SetUniformBuffers(CommandBuffer, buffers);
	}

	public void SetUserClipDistance(int index, bool enableClip)
	{
	}

	public void SetVertexAttribs(ReadOnlySpan<VertexAttribDescriptor> vertexAttribs)
	{
		FormatCapabilities formatCapabilities = Gd.FormatCapabilities;
		Span<int> span = stackalloc int[32];
		int num = Math.Min(32, vertexAttribs.Length);
		uint num2 = 0u;
		for (int i = 0; i < num; i++)
		{
			VertexAttribDescriptor vertexAttribDescriptor = vertexAttribs[i];
			int bufferIndex = vertexAttribDescriptor.BufferIndex;
			int value = ((!vertexAttribDescriptor.IsZero) ? (bufferIndex + 1) : 0);
			if (!vertexAttribDescriptor.IsZero)
			{
				span[bufferIndex] = Math.Max(span[bufferIndex], vertexAttribDescriptor.Format.GetScalarSize());
				num2 |= (uint)(1 << bufferIndex);
			}
			_newState.Internal.VertexAttributeDescriptions[i] = new VertexInputAttributeDescription((uint)i, (uint)value, formatCapabilities.ConvertToVertexVkFormat(vertexAttribDescriptor.Format), (uint)vertexAttribDescriptor.Offset);
		}
		while (num2 != 0)
		{
			int num3 = BitOperations.TrailingZeroCount(num2);
			ref VertexBufferState reference = ref _vertexBuffers[num3 + 1];
			if (reference.AttributeScalarAlignment != span[num3])
			{
				_vertexBuffersDirty |= (ulong)(1L << num3 + 1);
				reference.AttributeScalarAlignment = span[num3];
			}
			num2 &= (uint)(~(1 << num3));
		}
		_newState.VertexAttributeDescriptionsCount = (uint)num;
		SignalStateChange();
	}

	public void SetVertexBuffers(ReadOnlySpan<VertexBufferDescriptor> vertexBuffers)
	{
		int num = Math.Min(32, vertexBuffers.Length);
		_newState.Internal.VertexBindingDescriptions[0] = new VertexInputBindingDescription(0u, 0u, VertexInputRate.Vertex);
		int vertexBindingDescriptionsCount = 1;
		BufferHandle bufferHandle = default;
		Auto<DisposableBuffer> auto = null;
		for (int i = 0; i < num; i++)
		{
			VertexBufferDescriptor vertexBufferDescriptor = vertexBuffers[i];
			VertexInputRate value = ((vertexBufferDescriptor.Divisor != 0) ? VertexInputRate.Instance : VertexInputRate.Vertex);
			if (!(vertexBufferDescriptor.Buffer.Handle != BufferHandle.Null))
			{
				continue;
			}
			Auto<DisposableBuffer> auto2 = ((vertexBufferDescriptor.Buffer.Handle == bufferHandle) ? auto : Gd.BufferManager.GetBuffer(CommandBuffer, vertexBufferDescriptor.Buffer.Handle, isWrite: false));
			bufferHandle = vertexBufferDescriptor.Buffer.Handle;
			auto = auto2;
			if (auto2 == null)
			{
				continue;
			}
			int num2 = i + 1;
			int num3 = vertexBindingDescriptionsCount++;
			_newState.Internal.VertexBindingDescriptions[num3] = new VertexInputBindingDescription((uint)num2, (uint)vertexBufferDescriptor.Stride, value);
			int num4 = vertexBufferDescriptor.Buffer.Size;
			if (Gd.Vendor == Vendor.Amd && !Gd.IsMoltenVk && vertexBufferDescriptor.Stride > 0)
			{
				int num5 = num4 % vertexBufferDescriptor.Stride;
				if (num5 != 0)
				{
					num4 += vertexBufferDescriptor.Stride - num5;
				}
			}
			ref VertexBufferState reference = ref _vertexBuffers[num2];
			int attributeScalarAlignment = reference.AttributeScalarAlignment;
			if (Gd.Capabilities.VertexBufferAlignment < 2 && vertexBufferDescriptor.Stride % 4 == 0)
			{
				if (!reference.Matches(auto2, num3, vertexBufferDescriptor.Buffer.Offset, num4, vertexBufferDescriptor.Stride))
				{
					reference.Dispose();
					reference = new VertexBufferState(auto2, num3, vertexBufferDescriptor.Buffer.Offset, num4, vertexBufferDescriptor.Stride);
					reference.BindVertexBuffer(Gd, Cbs, (uint)num2, ref _newState, _vertexBufferUpdater);
				}
			}
			else
			{
				reference.Dispose();
				reference = new VertexBufferState(vertexBufferDescriptor.Buffer.Handle, num3, vertexBufferDescriptor.Buffer.Offset, num4, vertexBufferDescriptor.Stride);
				_vertexBuffersDirty |= (ulong)(1L << num2);
			}
			reference.AttributeScalarAlignment = attributeScalarAlignment;
		}
		_vertexBufferUpdater.Commit(Cbs);
		_newState.VertexBindingDescriptionsCount = (uint)vertexBindingDescriptionsCount;
		SignalStateChange();
	}

	public void SetViewports(ReadOnlySpan<Ryujinx.Graphics.GAL.Viewport> viewports)
	{
		int num = Math.Min((!Gd.Capabilities.SupportsMultiView) ? 1 : 16, viewports.Length);
		DynamicState.ViewportsCount = (uint)num;
		for (int i = 0; i < num; i++)
		{
			Ryujinx.Graphics.GAL.Viewport viewport = viewports[i];
			DynamicState.SetViewport(i, new Silk.NET.Vulkan.Viewport(viewport.Region.X, viewport.Region.Y, (viewport.Region.Width == 0f) ? 1f : viewport.Region.Width, (viewport.Region.Height == 0f) ? 1f : viewport.Region.Height, Clamp(viewport.DepthNear), Clamp(viewport.DepthFar)));
		}
		_newState.ViewportsCount = (uint)num;
		SignalStateChange();
		static float Clamp(float value)
		{
			return Math.Clamp(value, 0f, 1f);
		}
	}

	public void SwapBuffer(Auto<DisposableBuffer> from, Auto<DisposableBuffer> to)
	{
		_indexBuffer.Swap(from, to);
		for (int i = 0; i < _vertexBuffers.Length; i++)
		{
			_vertexBuffers[i].Swap(from, to);
		}
		for (int j = 0; j < _transformFeedbackBuffers.Length; j++)
		{
			_transformFeedbackBuffers[j].Swap(from, to);
		}
		_descriptorSetUpdater.SwapBuffer(from, to);
		SignalCommandBufferChange();
	}

	public void ForceTextureDirty()
	{
		_descriptorSetUpdater.ForceTextureDirty();
	}

	public void ForceImageDirty()
	{
		_descriptorSetUpdater.ForceImageDirty();
	}

	public void TextureBarrier()
	{
		Gd.Barriers.QueueTextureBarrier();
	}

	public void TextureBarrierTiled()
	{
		TextureBarrier();
	}

	protected void SignalCommandBufferChange()
	{
		_needsIndexBufferRebind = true;
		_needsTransformFeedbackBuffersRebind = true;
		_vertexBuffersDirty = ulong.MaxValue >> 64 - _vertexBuffers.Length;
		_descriptorSetUpdater.SignalCommandBufferChange();
		DynamicState.ForceAllDirty();
		_currentPipelineHandle = 0uL;
	}

	private void CreateFramebuffer(ITexture[] colors, ITexture depthStencil, bool filterWriteMasked)
	{
		if (filterWriteMasked)
		{
			for (int i = 0; i < colors.Length; i++)
			{
				if (colors[i] == null)
				{
					continue;
				}
				ref PipelineColorBlendAttachmentState reference = ref _newState.Internal.ColorBlendAttachmentState[i];
				for (int j = 0; j < i; j++)
				{
					if (colors[i] == colors[j])
					{
						ref PipelineColorBlendAttachmentState reference2 = ref _newState.Internal.ColorBlendAttachmentState[j];
						if (reference.ColorWriteMask == ColorComponentFlags.None)
						{
							colors[i] = null;
							MaskOut();
						}
						else if (reference2.ColorWriteMask == ColorComponentFlags.None)
						{
							colors[j] = null;
							MaskOut();
						}
					}
				}
			}
		}
		if (IsMainPipeline)
		{
			FramebufferParams?.ClearBindings();
		}
		FramebufferParams = new FramebufferParams(Device, colors, depthStencil);
		if (IsMainPipeline)
		{
			FramebufferParams.AddBindings();
			_newState.FeedbackLoopAspects = FeedbackLoopAspects.None;
			_bindingBarriersDirty = true;
		}
		_passWritesDepthStencil = false;
		UpdatePassDepthStencil();
		UpdatePipelineAttachmentFormats();
		void MaskOut()
		{
			if (!_framebufferUsingColorWriteMask)
			{
				_preMaskColors = colors.ToArray();
				_preMaskDepthStencil = depthStencil;
			}
			_framebufferUsingColorWriteMask = true;
		}
	}

	protected void UpdatePipelineAttachmentFormats()
	{
		Span<Silk.NET.Vulkan.Format> destination = _newState.Internal.AttachmentFormats.AsSpan();
		FramebufferParams.AttachmentFormats.CopyTo(destination);
		_newState.Internal.AttachmentIntegerFormatMask = FramebufferParams.AttachmentIntegerFormatMask;
		_newState.Internal.LogicOpsAllowed = FramebufferParams.LogicOpsAllowed;
		for (int i = FramebufferParams.AttachmentFormats.Length; i < destination.Length; i++)
		{
			destination[i] = Silk.NET.Vulkan.Format.Undefined;
		}
		_newState.ColorBlendAttachmentStateCount = (uint)(FramebufferParams.MaxColorAttachmentIndex + 1);
		_newState.HasDepthStencil = FramebufferParams.HasDepthStencil;
		_newState.SamplesCount = ((FramebufferParams.AttachmentSamples.Length == 0) ? 1u : FramebufferParams.AttachmentSamples[0]);
	}

	protected void CreateRenderPass()
	{
		bool flag = FramebufferParams != null;
		EndRenderPass();
		if (!flag || FramebufferParams.AttachmentsCount == 0)
		{
			if (_nullRenderPass == null)
			{
				_nullRenderPass = new RenderPassHolder(Gd, Device, default, FramebufferParams);
			}
			_rpHolder = _nullRenderPass;
			_renderPass = _nullRenderPass.GetRenderPass();
			_framebuffer = _nullRenderPass.GetFramebuffer(Gd, Cbs, FramebufferParams);
		}
		else
		{
			(RenderPassHolder, Auto<DisposableFramebuffer>) passAndFramebuffer = FramebufferParams.GetPassAndFramebuffer(Gd, Device, Cbs);
			_rpHolder = passAndFramebuffer.Item1;
			_framebuffer = passAndFramebuffer.Item2;
			_renderPass = _rpHolder.GetRenderPass();
		}
	}

	protected void SignalStateChange()
	{
		_graphicsStateDirty = true;
		_computeStateDirty = true;
	}

	private void RecreateComputePipelineIfNeeded()
	{
		if (_computeStateDirty || Pbp != PipelineBindPoint.Compute)
		{
			CreatePipeline(PipelineBindPoint.Compute);
			_computeStateDirty = false;
			Pbp = PipelineBindPoint.Compute;
			if (_bindingBarriersDirty)
			{
				_descriptorSetUpdater.InsertBindingBarriers(Cbs);
				_bindingBarriersDirty = false;
			}
		}
		Gd.Barriers.Flush(Cbs, _program, _feedbackLoop != FeedbackLoopAspects.None, RenderPassActive, _rpHolder, EndRenderPassDelegate);
		_descriptorSetUpdater.UpdateAndBindDescriptorSets(Cbs, PipelineBindPoint.Compute);
	}

	private bool ChangeFeedbackLoop(FeedbackLoopAspects aspects)
	{
		if ((Gd.IsAmdRdna3 || Gd.Vendor == Vendor.Qualcomm) && _feedbackLoop != aspects)
		{
			if (Gd.Capabilities.SupportsDynamicAttachmentFeedbackLoop)
			{
				DynamicState.SetFeedbackLoop(aspects);
			}
			else
			{
				_newState.FeedbackLoopAspects = aspects;
			}
			_feedbackLoop = aspects;
			return true;
		}
		return false;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private bool UpdateFeedbackLoop()
	{
		List<TextureView> feedbackLoopHazards = _descriptorSetUpdater.FeedbackLoopHazards;
		if ((feedbackLoopHazards?.Count ?? 0) > 0)
		{
			FeedbackLoopAspects feedbackLoopAspects = FeedbackLoopAspects.None;
			foreach (TextureView item in feedbackLoopHazards)
			{
				if (item.Info.Format.IsDepthOrStencil())
				{
					if (_passWritesDepthStencil)
					{
						feedbackLoopAspects |= FeedbackLoopAspects.Depth;
					}
				}
				else
				{
					feedbackLoopAspects |= FeedbackLoopAspects.Color;
				}
			}
			return ChangeFeedbackLoop(feedbackLoopAspects);
		}
		if (_feedbackLoop != FeedbackLoopAspects.None)
		{
			return ChangeFeedbackLoop(FeedbackLoopAspects.None);
		}
		return false;
	}

	private void UpdatePassDepthStencil()
	{
		if (!RenderPassActive)
		{
			_passWritesDepthStencil = false;
		}
		_passWritesDepthStencil |= (_newState.DepthTestEnable && _newState.DepthWriteEnable) || _newState.StencilTestEnable;
	}

	private bool RecreateGraphicsPipelineIfNeeded()
	{
		if (AutoFlush.ShouldFlushDraw(DrawCount))
		{
			Gd.FlushAllCommands();
		}
		DynamicState.ReplayIfDirty(Gd, CommandBuffer);
		if (_needsIndexBufferRebind && _indexBufferPattern == null)
		{
			_indexBuffer.BindIndexBuffer(Gd, Cbs);
			_needsIndexBufferRebind = false;
		}
		if (_needsTransformFeedbackBuffersRebind)
		{
			PauseTransformFeedbackInternal();
			for (int i = 0; i < 4; i++)
			{
				_transformFeedbackBuffers[i].BindTransformFeedbackBuffer(Gd, Cbs, (uint)i);
			}
			_needsTransformFeedbackBuffersRebind = false;
		}
		if (_vertexBuffersDirty != 0L)
		{
			while (_vertexBuffersDirty != 0L)
			{
				int num = BitOperations.TrailingZeroCount(_vertexBuffersDirty);
				_vertexBuffers[num].BindVertexBuffer(Gd, Cbs, (uint)num, ref _newState, _vertexBufferUpdater);
				_vertexBuffersDirty &= (ulong)(~(1L << num));
			}
			_vertexBufferUpdater.Commit(Cbs);
		}
		if (_bindingBarriersDirty)
		{
			_descriptorSetUpdater.InsertBindingBarriers(Cbs);
			_bindingBarriersDirty = false;
		}
		if (UpdateFeedbackLoop() || _graphicsStateDirty || Pbp != PipelineBindPoint.Graphics)
		{
			if (!CreatePipeline(PipelineBindPoint.Graphics))
			{
				return false;
			}
			_graphicsStateDirty = false;
			Pbp = PipelineBindPoint.Graphics;
		}
		Gd.Barriers.Flush(Cbs, _program, _feedbackLoop != FeedbackLoopAspects.None, RenderPassActive, _rpHolder, EndRenderPassDelegate);
		_descriptorSetUpdater.UpdateAndBindDescriptorSets(Cbs, PipelineBindPoint.Graphics);
		return true;
	}

	private bool CreatePipeline(PipelineBindPoint pbp)
	{
		if (_newState.Stages != null)
		{
			if (pbp == PipelineBindPoint.Graphics && _renderPass == null)
			{
				CreateRenderPass();
			}
			if (!_program.IsLinked)
			{
				return false;
			}
			Auto<DisposablePipeline> auto = ((pbp == PipelineBindPoint.Compute) ? _newState.CreateComputePipeline(Gd, Device, _program, PipelineCache) : _newState.CreateGraphicsPipeline(Gd, Device, _program, PipelineCache, _renderPass.Get(Cbs).Value));
			if (auto == null)
			{
				return false;
			}
			ulong handle = auto.GetUnsafe().Value.Handle;
			if (_currentPipelineHandle != handle)
			{
				_currentPipelineHandle = handle;
				Pipeline = auto;
				PauseTransformFeedbackInternal();
				Gd.Api.CmdBindPipeline(CommandBuffer, pbp, Pipeline.Get(Cbs).Value);
			}
		}
		return true;
	}

	private unsafe void BeginRenderPass()
	{
		if (!RenderPassActive)
		{
			FramebufferParams.InsertLoadOpBarriers(Gd, Cbs);
			Rect2D renderArea = new Rect2D(null, new Extent2D(FramebufferParams.Width, FramebufferParams.Height));
			ClearValue clearValue = default;
			RenderPassBeginInfo pRenderPassBegin = new RenderPassBeginInfo
			{
				SType = StructureType.RenderPassBeginInfo,
				RenderPass = _renderPass.Get(Cbs).Value,
				Framebuffer = _framebuffer.Get(Cbs).Value,
				RenderArea = renderArea,
				PClearValues = &clearValue,
				ClearValueCount = 1u
			};
			Gd.Api.CmdBeginRenderPass(CommandBuffer, in pRenderPassBegin, SubpassContents.Inline);
			RenderPassActive = true;
		}
	}

	public void EndRenderPass()
	{
		if (RenderPassActive)
		{
			FramebufferParams.AddStoreOpUsage();
			PauseTransformFeedbackInternal();
			Gd.Api.CmdEndRenderPass(CommandBuffer);
			SignalRenderPassEnd();
			RenderPassActive = false;
		}
	}

	protected virtual void SignalRenderPassEnd()
	{
	}

	private void PauseTransformFeedbackInternal()
	{
		if (_tfEnabled && _tfActive)
		{
			EndTransformFeedbackInternal();
			_tfActive = false;
		}
	}

	private void ResumeTransformFeedbackInternal()
	{
		if (_tfEnabled && !_tfActive)
		{
			BeginTransformFeedbackInternal();
			_tfActive = true;
		}
	}

	private unsafe void BeginTransformFeedbackInternal()
	{
		Gd.TransformFeedbackApi.CmdBeginTransformFeedback(CommandBuffer, 0u, 0u, null, null);
	}

	private unsafe void EndTransformFeedbackInternal()
	{
		Gd.TransformFeedbackApi.CmdEndTransformFeedback(CommandBuffer, 0u, 0u, null, null);
	}

	protected unsafe virtual void Dispose(bool disposing)
	{
		if (disposing)
		{
			_nullRenderPass?.Dispose();
			_newState.Dispose();
			_descriptorSetUpdater.Dispose();
			_vertexBufferUpdater.Dispose();
			for (int i = 0; i < _vertexBuffers.Length; i++)
			{
				_vertexBuffers[i].Dispose();
			}
			for (int j = 0; j < _transformFeedbackBuffers.Length; j++)
			{
				_transformFeedbackBuffers[j].Dispose();
			}
			Pipeline?.Dispose();
			_nxDriverCache?.Capture(Gd.Api, Device, PipelineCache);
			Gd.Api.DestroyPipelineCache(Device, PipelineCache, null);
		}
	}

	public void Dispose()
	{
		Dispose(disposing: true);
	}
}
