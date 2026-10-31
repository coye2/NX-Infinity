using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Silk.NET.Vulkan;

namespace Ryujinx.Graphics.Vulkan;

internal class ShaderCollection : IProgram, IDisposable
{
	private readonly PipelineShaderStageCreateInfo[] _infos;

	private readonly Shader[] _shaders;

	private readonly PipelineLayoutCacheEntry _plce;

	public readonly SpecDescription[] SpecDescriptions;

	private HashTableSlim<PipelineUid, Auto<DisposablePipeline>> _graphicsPipelineCache;

	private HashTableSlim<SpecData, Auto<DisposablePipeline>> _computePipelineCache;

	private readonly VulkanRenderer _gd;

	private Device _device;

	private bool _initialized;

	private ProgramPipelineState _state;

	private DisposableRenderPass _dummyRenderPass;

	private static long _nxTraceSequence;

	internal readonly long NxTraceId = Interlocked.Increment(ref _nxTraceSequence);

	private readonly Task _compileTask;

	private bool _firstBackgroundUse;

	public PipelineLayout PipelineLayout => _plce.PipelineLayout;

	public bool HasMinimalLayout { get; }

	public bool UsePushDescriptors { get; }

	public bool IsCompute { get; }

	public bool HasTessellationControlShader => (Stages & 8) != 0;

	public bool UpdateTexturesWithoutTemplate { get; }

	public uint Stages { get; }

	public PipelineStageFlags IncoherentBufferWriteStages { get; }

	public PipelineStageFlags IncoherentTextureWriteStages { get; }

	public ResourceBindingSegment[][] ClearSegments { get; }

	public ResourceBindingSegment[][] BindingSegments { get; }

	public DescriptorSetTemplate[] Templates { get; }

	public ProgramLinkStatus LinkStatus { get; private set; }

	public bool IsLinked
	{
		get
		{
			if (LinkStatus == ProgramLinkStatus.Incomplete)
			{
				CheckProgramLink(blocking: true);
			}
			return LinkStatus == ProgramLinkStatus.Success;
		}
	}

	public ShaderCollection(VulkanRenderer gd, Device device, ShaderSource[] shaders, ResourceLayout resourceLayout, SpecDescription[] specDescription = null, bool isMinimal = false)
	{
		_gd = gd;
		_device = device;
		if (specDescription != null && specDescription.Length != shaders.Length)
		{
			throw new ArgumentException("specDescription array length must match shaders array if provided");
		}
		gd.Shaders.Add(this);
		Shader[] array = new Shader[shaders.Length];
		_infos = new PipelineShaderStageCreateInfo[shaders.Length];
		SpecDescriptions = specDescription;
		LinkStatus = ProgramLinkStatus.Incomplete;
		uint num = 0u;
		for (int i = 0; i < shaders.Length; i++)
		{
			Shader shader = new Shader(gd.Api, device, shaders[i]);
			uint num2 = num;
			num = num2 | (uint)(1 << shader.StageFlags switch
			{
				ShaderStageFlags.ShaderStageFragmentBit => 1,
				ShaderStageFlags.ShaderStageGeometryBit => 2,
				ShaderStageFlags.ShaderStageTessellationControlBit => 3,
				ShaderStageFlags.ShaderStageTessellationEvaluationBit => 4,
				_ => 0,
			});
			if (shader.StageFlags == ShaderStageFlags.ShaderStageComputeBit)
			{
				IsCompute = true;
			}
			array[i] = shader;
		}
		_shaders = array;
		bool flag = !isMinimal && _gd.Capabilities.SupportsPushDescriptors && !IsCompute && !HasPushDescriptorsBug(gd) && CanUsePushDescriptors(gd, resourceLayout, IsCompute);
		ReadOnlyCollection<ResourceDescriptorCollection> readOnlyCollection = (flag ? BuildPushDescriptorSets(gd, resourceLayout.Sets) : resourceLayout.Sets);
		_plce = gd.PipelineLayoutCache.GetOrCreate(gd, device, readOnlyCollection, flag);
		HasMinimalLayout = isMinimal;
		UsePushDescriptors = flag;
		Stages = num;
		ClearSegments = BuildClearSegments(readOnlyCollection);
		BindingSegments = BuildBindingSegments(resourceLayout.SetUsages, out var usesBufferTextures);
		Templates = BuildTemplates(flag);
		(PipelineStageFlags, PipelineStageFlags) tuple = BuildIncoherentStages(resourceLayout.SetUsages);
		IncoherentBufferWriteStages = tuple.Item1;
		IncoherentTextureWriteStages = tuple.Item2;
		UpdateTexturesWithoutTemplate = gd.IsQualcommProprietary & usesBufferTextures;
		_compileTask = Task.CompletedTask;
		_firstBackgroundUse = false;
	}

	public ShaderCollection(VulkanRenderer gd, Device device, ShaderSource[] sources, ResourceLayout resourceLayout, ProgramPipelineState state, bool fromCache)
		: this(gd, device, sources, resourceLayout)
	{
		_state = state;
		_compileTask = gd.NxPipelineCompiler.Enqueue(BackgroundCompilation, NxTraceId);
		_firstBackgroundUse = !fromCache;
	}

	private static bool HasPushDescriptorsBug(VulkanRenderer gd)
	{
		if (!gd.IsNvidiaPreTuring)
		{
			if (gd.IsIntelArc)
			{
				return gd.IsIntelWindows;
			}
			return false;
		}
		return true;
	}

	private static bool CanUsePushDescriptors(VulkanRenderer gd, ResourceLayout layout, bool isCompute)
	{
		ReadOnlyCollection<ResourceUsage> usages = layout.SetUsages[0].Usages;
		bool isOgl = usages.Any((ResourceUsage x) => x.Binding == 3);
		int[] source = (isCompute ? Array.Empty<int>() : gd.GetPushDescriptorReservedBindings(isOgl));
		for (int num = 0; num < usages.Count; num++)
		{
			int binding = usages[num].Binding;
			if (Enumerable.Contains(source, binding) || binding >= 64 || binding >= gd.Capabilities.MaxPushDescriptors + source.Count((int id) => id < binding))
			{
				return false;
			}
		}
		int num2 = 0;
		foreach (ResourceDescriptor descriptor in layout.Sets.First().Descriptors)
		{
			if (!Enumerable.Contains(source, descriptor.Binding))
			{
				num2 += descriptor.Count;
			}
		}
		if (num2 > gd.Capabilities.MaxPushDescriptors)
		{
			return false;
		}
		return true;
	}

	private static ReadOnlyCollection<ResourceDescriptorCollection> BuildPushDescriptorSets(VulkanRenderer gd, ReadOnlyCollection<ResourceDescriptorCollection> sets)
	{
		int[] pushDescriptorReservedBindings = gd.GetPushDescriptorReservedBindings(isOgl: false);
		ResourceDescriptorCollection[] array = new ResourceDescriptorCollection[sets.Count];
		for (int i = 0; i < sets.Count; i++)
		{
			if (i == 0)
			{
				ResourceDescriptorCollection resourceDescriptorCollection = sets[i];
				ResourceDescriptor[] array2 = new ResourceDescriptor[resourceDescriptorCollection.Descriptors.Count];
				int num = 0;
				foreach (ResourceDescriptor descriptor in resourceDescriptorCollection.Descriptors)
				{
					if (Enumerable.Contains(pushDescriptorReservedBindings, descriptor.Binding))
					{
						array2[num++] = new ResourceDescriptor(descriptor.Binding, 0, descriptor.Type, descriptor.Stages);
					}
					else
					{
						array2[num++] = descriptor;
					}
				}
				array[i] = new ResourceDescriptorCollection(new ReadOnlyCollection<ResourceDescriptor>(array2));
			}
			else
			{
				array[i] = sets[i];
			}
		}
		return new ReadOnlyCollection<ResourceDescriptorCollection>(array);
	}

	private static ResourceBindingSegment[][] BuildClearSegments(ReadOnlyCollection<ResourceDescriptorCollection> sets)
	{
		ResourceBindingSegment[][] array = new ResourceBindingSegment[sets.Count][];
		for (int i = 0; i < sets.Count; i++)
		{
			List<ResourceBindingSegment> list = new List<ResourceBindingSegment>();
			ResourceDescriptor resourceDescriptor = default;
			int num = 0;
			for (int j = 0; j < sets[i].Descriptors.Count; j++)
			{
				ResourceDescriptor resourceDescriptor2 = sets[i].Descriptors[j];
				if (resourceDescriptor.Binding + num != resourceDescriptor2.Binding || resourceDescriptor.Type != resourceDescriptor2.Type || resourceDescriptor.Stages != resourceDescriptor2.Stages || resourceDescriptor.Count > 1 || resourceDescriptor2.Count > 1)
				{
					if (num != 0)
					{
						list.Add(new ResourceBindingSegment(resourceDescriptor.Binding, num, resourceDescriptor.Type, resourceDescriptor.Stages, resourceDescriptor.Count > 1));
					}
					resourceDescriptor = resourceDescriptor2;
					num = resourceDescriptor2.Count;
				}
				else
				{
					num += resourceDescriptor2.Count;
				}
			}
			if (num != 0)
			{
				list.Add(new ResourceBindingSegment(resourceDescriptor.Binding, num, resourceDescriptor.Type, resourceDescriptor.Stages, resourceDescriptor.Count > 1));
			}
			array[i] = list.ToArray();
		}
		return array;
	}

	private static ResourceBindingSegment[][] BuildBindingSegments(ReadOnlyCollection<ResourceUsageCollection> setUsages, out bool usesBufferTextures)
	{
		usesBufferTextures = false;
		ResourceBindingSegment[][] array = new ResourceBindingSegment[setUsages.Count][];
		for (int i = 0; i < setUsages.Count; i++)
		{
			List<ResourceBindingSegment> list = new List<ResourceBindingSegment>();
			ResourceUsage resourceUsage = default;
			int num = 0;
			for (int j = 0; j < setUsages[i].Usages.Count; j++)
			{
				ResourceUsage resourceUsage2 = setUsages[i].Usages[j];
				if (resourceUsage2.Type == ResourceType.BufferTexture)
				{
					usesBufferTextures = true;
				}
				if (resourceUsage.Binding + num != resourceUsage2.Binding || resourceUsage.Type != resourceUsage2.Type || resourceUsage.Stages != resourceUsage2.Stages || resourceUsage.ArrayLength > 1 || resourceUsage2.ArrayLength > 1)
				{
					if (num != 0)
					{
						list.Add(new ResourceBindingSegment(resourceUsage.Binding, num, resourceUsage.Type, resourceUsage.Stages, resourceUsage.ArrayLength > 1));
					}
					resourceUsage = resourceUsage2;
					num = resourceUsage2.ArrayLength;
				}
				else
				{
					num++;
				}
			}
			if (num != 0)
			{
				list.Add(new ResourceBindingSegment(resourceUsage.Binding, num, resourceUsage.Type, resourceUsage.Stages, resourceUsage.ArrayLength > 1));
			}
			array[i] = list.ToArray();
		}
		return array;
	}

	private DescriptorSetTemplate[] BuildTemplates(bool usePushDescriptors)
	{
		DescriptorSetTemplate[] array = new DescriptorSetTemplate[BindingSegments.Length];
		for (int i = 0; i < BindingSegments.Length; i++)
		{
			if (!usePushDescriptors || i != 0)
			{
				ResourceBindingSegment[] array2 = BindingSegments[i];
				if (array2 != null && array2.Length != 0)
				{
					array[i] = new DescriptorSetTemplate(_gd, _device, array2, _plce, IsCompute ? PipelineBindPoint.Compute : PipelineBindPoint.Graphics, i);
				}
			}
		}
		return array;
	}

	private PipelineStageFlags GetPipelineStages(ResourceStages stages)
	{
		PipelineStageFlags pipelineStageFlags = PipelineStageFlags.PipelineStageNoneKhr;
		if ((stages & ResourceStages.Compute) != 0)
		{
			pipelineStageFlags |= PipelineStageFlags.PipelineStageComputeShaderBit;
		}
		if ((stages & ResourceStages.Vertex) != 0)
		{
			pipelineStageFlags |= PipelineStageFlags.PipelineStageVertexShaderBit;
		}
		if ((stages & ResourceStages.Fragment) != 0)
		{
			pipelineStageFlags |= PipelineStageFlags.PipelineStageFragmentShaderBit;
		}
		if ((stages & ResourceStages.Geometry) != 0)
		{
			pipelineStageFlags |= PipelineStageFlags.PipelineStageGeometryShaderBit;
		}
		if ((stages & ResourceStages.TessellationControl) != 0)
		{
			pipelineStageFlags |= PipelineStageFlags.PipelineStageTessellationControlShaderBit;
		}
		if ((stages & ResourceStages.TessellationEvaluation) != 0)
		{
			pipelineStageFlags |= PipelineStageFlags.PipelineStageTessellationEvaluationShaderBit;
		}
		return pipelineStageFlags;
	}

	private (PipelineStageFlags Buffer, PipelineStageFlags Texture) BuildIncoherentStages(ReadOnlyCollection<ResourceUsageCollection> setUsages)
	{
		PipelineStageFlags pipelineStageFlags = PipelineStageFlags.PipelineStageNoneKhr;
		PipelineStageFlags pipelineStageFlags2 = PipelineStageFlags.PipelineStageNoneKhr;
		foreach (ResourceUsageCollection setUsage in setUsages)
		{
			foreach (ResourceUsage usage in setUsage.Usages)
			{
				if (usage.Write)
				{
					PipelineStageFlags pipelineStages = GetPipelineStages(usage.Stages);
					switch (usage.Type)
					{
					case ResourceType.Image:
						pipelineStageFlags2 |= pipelineStages;
						break;
					case ResourceType.StorageBuffer:
					case ResourceType.BufferImage:
						pipelineStageFlags |= pipelineStages;
						break;
					}
				}
			}
		}
		return (Buffer: pipelineStageFlags, Texture: pipelineStageFlags2);
	}

	private async Task BackgroundCompilation()
	{
		long readyStart = RyuStutterTrace.Timestamp();
		await Task.WhenAll(_shaders.Select((Shader shader) => shader.CompileTask)).ConfigureAwait(continueOnCapturedContext: false);
		RyuStutterTrace.Interval("shader.modules.ready", readyStart, NxTraceId);
		using (RyuStutterTrace.Measure("pipeline.background.execute", NxTraceId))
		{
			if (Array.Exists(_shaders, (Shader shader) => shader.CompileStatus == ProgramLinkStatus.Failure))
			{
				LinkStatus = ProgramLinkStatus.Failure;
				return;
			}
			try
			{
				if (IsCompute)
				{
					CreateBackgroundComputePipeline();
				}
				else
				{
					CreateBackgroundGraphicsPipeline();
				}
			}
			catch (VulkanException ex)
			{
				Logger.Error?.PrintMsg(LogClass.Gpu, "Background Compilation failed: " + ex.Message);
				LinkStatus = ProgramLinkStatus.Failure;
			}
		}
	}

	private void EnsureShadersReady()
	{
		if (_initialized)
		{
			return;
		}
		CheckProgramLink(blocking: true);
		ProgramLinkStatus linkStatus = ProgramLinkStatus.Success;
		for (int i = 0; i < _shaders.Length; i++)
		{
			Shader shader = _shaders[i];
			if (shader.CompileStatus != ProgramLinkStatus.Success)
			{
				linkStatus = ProgramLinkStatus.Failure;
			}
			_infos[i] = shader.GetInfo();
		}
		if (LinkStatus != ProgramLinkStatus.Failure)
		{
			LinkStatus = linkStatus;
		}
		_initialized = true;
	}

	public PipelineShaderStageCreateInfo[] GetInfos()
	{
		EnsureShadersReady();
		return _infos;
	}

	protected DisposableRenderPass CreateDummyRenderPass()
	{
		if (_dummyRenderPass.Value.Handle != 0L)
		{
			return _dummyRenderPass;
		}
		return _dummyRenderPass = _state.ToRenderPass(_gd, _device);
	}

	public void CreateBackgroundComputePipeline()
	{
		PipelineState pipelineState = default;
		pipelineState.Initialize();
		pipelineState.Stages[0] = _shaders[0].GetInfo();
		pipelineState.StagesCount = 1u;
		pipelineState.PipelineLayout = PipelineLayout;
		pipelineState.CreateComputePipeline(_gd, _device, this, (_gd.Pipeline as PipelineBase).PipelineCache);
		pipelineState.Dispose();
	}

	public void CreateBackgroundGraphicsPipeline()
	{
		DisposableRenderPass disposableRenderPass = CreateDummyRenderPass();
		PipelineState pipelineState = _state.ToVulkanPipelineState(_gd);
		Span<PipelineShaderStageCreateInfo> span = pipelineState.Stages.AsSpan();
		for (int i = 0; i < _shaders.Length; i++)
		{
			span[i] = _shaders[i].GetInfo();
		}
		pipelineState.HasTessellationControlShader = HasTessellationControlShader;
		pipelineState.StagesCount = (uint)_shaders.Length;
		pipelineState.PipelineLayout = PipelineLayout;
		pipelineState.CreateGraphicsPipeline(_gd, _device, this, (_gd.Pipeline as PipelineBase).PipelineCache, disposableRenderPass.Value, throwOnError: true);
		pipelineState.Dispose();
	}

	public ProgramLinkStatus CheckProgramLink(bool blocking)
	{
		if (LinkStatus == ProgramLinkStatus.Incomplete)
		{
			ProgramLinkStatus result = ProgramLinkStatus.Success;
			Shader[] shaders = _shaders;
			foreach (Shader shader in shaders)
			{
				if (shader.CompileStatus == ProgramLinkStatus.Incomplete)
				{
					if (!blocking)
					{
						return ProgramLinkStatus.Incomplete;
					}
					using (RyuStutterTrace.Measure("shader.module.consumer_wait", NxTraceId))
					{
						shader.WaitForCompile();
					}
					if (shader.CompileStatus != ProgramLinkStatus.Success)
					{
						result = ProgramLinkStatus.Failure;
					}
				}
			}
			if (!_compileTask.IsCompleted)
			{
				if (!blocking)
				{
					return ProgramLinkStatus.Incomplete;
				}
				using (RyuStutterTrace.Measure("pipeline.consumer_wait", NxTraceId))
				{
					_compileTask.Wait();
				}
				if (LinkStatus == ProgramLinkStatus.Failure)
				{
					return ProgramLinkStatus.Failure;
				}
			}
			if (LinkStatus == ProgramLinkStatus.Failure || _compileTask.IsFaulted || Array.Exists(_shaders, (Shader shader2) => shader2.CompileStatus == ProgramLinkStatus.Failure || shader2.CompileTask.IsFaulted))
			{
				return ProgramLinkStatus.Failure;
			}
			return result;
		}
		return LinkStatus;
	}

	public byte[] GetBinary()
	{
		return null;
	}

	public DescriptorSetTemplate GetPushDescriptorTemplate(long updateMask)
	{
		return _plce.GetPushDescriptorTemplate(IsCompute ? PipelineBindPoint.Compute : PipelineBindPoint.Graphics, updateMask);
	}

	public void AddComputePipeline(ref SpecData key, Auto<DisposablePipeline> pipeline)
	{
		(_computePipelineCache ?? (_computePipelineCache = new HashTableSlim<SpecData, Auto<DisposablePipeline>>())).Add(ref key, pipeline);
	}

	public void AddGraphicsPipeline(ref PipelineUid key, Auto<DisposablePipeline> pipeline)
	{
		(_graphicsPipelineCache ?? (_graphicsPipelineCache = new HashTableSlim<PipelineUid, Auto<DisposablePipeline>>())).Add(ref key, pipeline);
	}

	public bool TryGetComputePipeline(ref SpecData key, out Auto<DisposablePipeline> pipeline)
	{
		if (_computePipelineCache == null)
		{
			pipeline = null;
			return false;
		}
		if (_computePipelineCache.TryGetValue(ref key, out pipeline))
		{
			return true;
		}
		return false;
	}

	public bool TryGetGraphicsPipeline(ref PipelineUid key, out Auto<DisposablePipeline> pipeline)
	{
		if (_graphicsPipelineCache == null)
		{
			pipeline = null;
			return false;
		}
		if (!_graphicsPipelineCache.TryGetValue(ref key, out pipeline))
		{
			if (_firstBackgroundUse)
			{
				Logger.Warning?.Print(LogClass.Gpu, "Background pipeline compile missed on draw - incorrect pipeline state?", "TryGetGraphicsPipeline");
				_firstBackgroundUse = false;
			}
			return false;
		}
		_firstBackgroundUse = false;
		return true;
	}

	public void UpdateDescriptorCacheCommandBufferIndex(int commandBufferIndex)
	{
		_plce.UpdateCommandBufferIndex(commandBufferIndex);
	}

	public Auto<DescriptorSetCollection> GetNewDescriptorSetCollection(int setIndex, out bool isNew)
	{
		return _plce.GetNewDescriptorSetCollection(setIndex, out isNew);
	}

	public Auto<DescriptorSetCollection> GetNewManualDescriptorSetCollection(CommandBufferScoped cbs, int setIndex, out int cacheIndex)
	{
		return _plce.GetNewManualDescriptorSetCollection(cbs, setIndex, out cacheIndex);
	}

	public void UpdateManualDescriptorSetCollectionOwnership(CommandBufferScoped cbs, int setIndex, int cacheIndex)
	{
		_plce.UpdateManualDescriptorSetCollectionOwnership(cbs, setIndex, cacheIndex);
	}

	public void ReleaseManualDescriptorSetCollection(int setIndex, int cacheIndex)
	{
		_plce.ReleaseManualDescriptorSetCollection(setIndex, cacheIndex);
	}

	public bool HasSameLayout(ShaderCollection other)
	{
		if (other != null)
		{
			return _plce == other._plce;
		}
		return false;
	}

	protected virtual void Dispose(bool disposing)
	{
		if (!disposing || !_gd.Shaders.Remove(this))
		{
			return;
		}
		try
		{
			_compileTask.GetAwaiter().GetResult();
		}
		catch (Exception ex)
		{
			Logger.Error?.PrintMsg(LogClass.Gpu, ex.Message);
		}
		try
		{
			Task.WhenAll(_shaders.Select((Shader shader) => shader.CompileTask)).GetAwaiter().GetResult();
		}
		catch (Exception ex2)
		{
			Logger.Error?.PrintMsg(LogClass.Gpu, ex2.Message);
		}
		for (int num = 0; num < _shaders.Length; num++)
		{
			_shaders[num].Dispose();
		}
		if (_graphicsPipelineCache != null)
		{
			foreach (Auto<DisposablePipeline> value in _graphicsPipelineCache.Values)
			{
				value?.Dispose();
			}
		}
		if (_computePipelineCache != null)
		{
			foreach (Auto<DisposablePipeline> value2 in _computePipelineCache.Values)
			{
				value2.Dispose();
			}
		}
		for (int num2 = 0; num2 < Templates.Length; num2++)
		{
			Templates[num2]?.Dispose();
		}
		if (_dummyRenderPass.Value.Handle != 0L)
		{
			_dummyRenderPass.Dispose();
		}
	}

	public void Dispose()
	{
		Dispose(disposing: true);
	}
}
