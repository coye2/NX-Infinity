using System;
using System.Numerics;
using Ryujinx.Common;
using Ryujinx.Common.Memory;
using Silk.NET.Vulkan;

namespace Ryujinx.Graphics.Vulkan;

internal struct PipelineState : IDisposable
{
	private const int MaxDynamicStatesCount = 9;

	public PipelineUid Internal;

	public bool HasTessellationControlShader;

	public NativeArray<PipelineShaderStageCreateInfo> Stages;

	public PipelineLayout PipelineLayout;

	public SpecData SpecializationData;

	private Array32<VertexInputAttributeDescription> _vertexAttributeDescriptions2;

	public float LineWidth
	{
		readonly get
		{
			return BitConverter.Int32BitsToSingle((int)(Internal.Id0 & 0xFFFFFFFFu));
		}
		set
		{
			Internal.Id0 = (Internal.Id0 & 0xFFFFFFFF00000000uL) | (uint)BitConverter.SingleToInt32Bits(value);
		}
	}

	public float DepthBiasClamp
	{
		readonly get
		{
			return BitConverter.Int32BitsToSingle((int)((Internal.Id0 >> 32) & 0xFFFFFFFFu));
		}
		set
		{
			Internal.Id0 = (Internal.Id0 & 0xFFFFFFFFu) | ((ulong)(uint)BitConverter.SingleToInt32Bits(value) << 32);
		}
	}

	public float DepthBiasConstantFactor
	{
		readonly get
		{
			return BitConverter.Int32BitsToSingle((int)(Internal.Id1 & 0xFFFFFFFFu));
		}
		set
		{
			Internal.Id1 = (Internal.Id1 & 0xFFFFFFFF00000000uL) | (uint)BitConverter.SingleToInt32Bits(value);
		}
	}

	public float DepthBiasSlopeFactor
	{
		readonly get
		{
			return BitConverter.Int32BitsToSingle((int)((Internal.Id1 >> 32) & 0xFFFFFFFFu));
		}
		set
		{
			Internal.Id1 = (Internal.Id1 & 0xFFFFFFFFu) | ((ulong)(uint)BitConverter.SingleToInt32Bits(value) << 32);
		}
	}

	public uint StencilFrontCompareMask
	{
		readonly get
		{
			return (uint)(Internal.Id2 & 0xFFFFFFFFu);
		}
		set
		{
			Internal.Id2 = (Internal.Id2 & 0xFFFFFFFF00000000uL) | value;
		}
	}

	public uint StencilFrontWriteMask
	{
		readonly get
		{
			return (uint)((Internal.Id2 >> 32) & 0xFFFFFFFFu);
		}
		set
		{
			Internal.Id2 = (Internal.Id2 & 0xFFFFFFFFu) | ((ulong)value << 32);
		}
	}

	public uint StencilFrontReference
	{
		readonly get
		{
			return (uint)(Internal.Id3 & 0xFFFFFFFFu);
		}
		set
		{
			Internal.Id3 = (Internal.Id3 & 0xFFFFFFFF00000000uL) | value;
		}
	}

	public uint StencilBackCompareMask
	{
		readonly get
		{
			return (uint)((Internal.Id3 >> 32) & 0xFFFFFFFFu);
		}
		set
		{
			Internal.Id3 = (Internal.Id3 & 0xFFFFFFFFu) | ((ulong)value << 32);
		}
	}

	public uint StencilBackWriteMask
	{
		readonly get
		{
			return (uint)(Internal.Id4 & 0xFFFFFFFFu);
		}
		set
		{
			Internal.Id4 = (Internal.Id4 & 0xFFFFFFFF00000000uL) | value;
		}
	}

	public uint StencilBackReference
	{
		readonly get
		{
			return (uint)((Internal.Id4 >> 32) & 0xFFFFFFFFu);
		}
		set
		{
			Internal.Id4 = (Internal.Id4 & 0xFFFFFFFFu) | ((ulong)value << 32);
		}
	}

	public PolygonMode PolygonMode
	{
		readonly get
		{
			return (PolygonMode)(Internal.Id5 & 0x3FFFFFFF);
		}
		set
		{
			Internal.Id5 = (Internal.Id5 & 0xFFFFFFFFC0000000uL) | (ulong)value;
		}
	}

	public uint StagesCount
	{
		readonly get
		{
			return (byte)((Internal.Id5 >> 30) & 0xFF);
		}
		set
		{
			Internal.Id5 = (Internal.Id5 & 0xFFFFFFC03FFFFFFFuL) | ((ulong)value << 30);
		}
	}

	public uint VertexAttributeDescriptionsCount
	{
		readonly get
		{
			return (byte)((Internal.Id5 >> 38) & 0xFF);
		}
		set
		{
			Internal.Id5 = (Internal.Id5 & 0xFFFFC03FFFFFFFFFuL) | ((ulong)value << 38);
		}
	}

	public uint VertexBindingDescriptionsCount
	{
		readonly get
		{
			return (byte)((Internal.Id5 >> 46) & 0xFF);
		}
		set
		{
			Internal.Id5 = (Internal.Id5 & 0xFFC03FFFFFFFFFFFuL) | ((ulong)value << 46);
		}
	}

	public uint ViewportsCount
	{
		readonly get
		{
			return (byte)((Internal.Id5 >> 54) & 0xFF);
		}
		set
		{
			Internal.Id5 = (Internal.Id5 & 0xC03FFFFFFFFFFFFFuL) | ((ulong)value << 54);
		}
	}

	public uint ScissorsCount
	{
		readonly get
		{
			return (byte)(Internal.Id6 & 0xFF);
		}
		set
		{
			Internal.Id6 = (Internal.Id6 & 0xFFFFFFFFFFFFFF00uL) | value;
		}
	}

	public uint ColorBlendAttachmentStateCount
	{
		readonly get
		{
			return (byte)((Internal.Id6 >> 8) & 0xFF);
		}
		set
		{
			Internal.Id6 = (Internal.Id6 & 0xFFFFFFFFFFFF00FFuL) | ((ulong)value << 8);
		}
	}

	public PrimitiveTopology Topology
	{
		readonly get
		{
			return (PrimitiveTopology)((Internal.Id6 >> 16) & 0xF);
		}
		set
		{
			Internal.Id6 = (Internal.Id6 & 0xFFFFFFFFFFF0FFFFuL) | (ulong)((long)value << 16);
		}
	}

	public LogicOp LogicOp
	{
		readonly get
		{
			return (LogicOp)((Internal.Id6 >> 20) & 0xF);
		}
		set
		{
			Internal.Id6 = (Internal.Id6 & 0xFFFFFFFFFF0FFFFFuL) | (ulong)((long)value << 20);
		}
	}

	public CompareOp DepthCompareOp
	{
		readonly get
		{
			return (CompareOp)((Internal.Id6 >> 24) & 7);
		}
		set
		{
			Internal.Id6 = (Internal.Id6 & 0xFFFFFFFFF8FFFFFFuL) | (ulong)((long)value << 24);
		}
	}

	public StencilOp StencilFrontFailOp
	{
		readonly get
		{
			return (StencilOp)((Internal.Id6 >> 27) & 7);
		}
		set
		{
			Internal.Id6 = (Internal.Id6 & 0xFFFFFFFFC7FFFFFFuL) | (ulong)((long)value << 27);
		}
	}

	public StencilOp StencilFrontPassOp
	{
		readonly get
		{
			return (StencilOp)((Internal.Id6 >> 30) & 7);
		}
		set
		{
			Internal.Id6 = (Internal.Id6 & 0xFFFFFFFE3FFFFFFFuL) | (ulong)((long)value << 30);
		}
	}

	public StencilOp StencilFrontDepthFailOp
	{
		readonly get
		{
			return (StencilOp)((Internal.Id6 >> 33) & 7);
		}
		set
		{
			Internal.Id6 = (Internal.Id6 & 0xFFFFFFF1FFFFFFFFuL) | (ulong)((long)value << 33);
		}
	}

	public CompareOp StencilFrontCompareOp
	{
		readonly get
		{
			return (CompareOp)((Internal.Id6 >> 36) & 7);
		}
		set
		{
			Internal.Id6 = (Internal.Id6 & 0xFFFFFF8FFFFFFFFFuL) | (ulong)((long)value << 36);
		}
	}

	public StencilOp StencilBackFailOp
	{
		readonly get
		{
			return (StencilOp)((Internal.Id6 >> 39) & 7);
		}
		set
		{
			Internal.Id6 = (Internal.Id6 & 0xFFFFFC7FFFFFFFFFuL) | (ulong)((long)value << 39);
		}
	}

	public StencilOp StencilBackPassOp
	{
		readonly get
		{
			return (StencilOp)((Internal.Id6 >> 42) & 7);
		}
		set
		{
			Internal.Id6 = (Internal.Id6 & 0xFFFFE3FFFFFFFFFFuL) | (ulong)((long)value << 42);
		}
	}

	public StencilOp StencilBackDepthFailOp
	{
		readonly get
		{
			return (StencilOp)((Internal.Id6 >> 45) & 7);
		}
		set
		{
			Internal.Id6 = (Internal.Id6 & 0xFFFF1FFFFFFFFFFFuL) | (ulong)((long)value << 45);
		}
	}

	public CompareOp StencilBackCompareOp
	{
		readonly get
		{
			return (CompareOp)((Internal.Id6 >> 48) & 7);
		}
		set
		{
			Internal.Id6 = (Internal.Id6 & 0xFFF8FFFFFFFFFFFFuL) | (ulong)((long)value << 48);
		}
	}

	public CullModeFlags CullMode
	{
		readonly get
		{
			return (CullModeFlags)((Internal.Id6 >> 51) & 3);
		}
		set
		{
			Internal.Id6 = (Internal.Id6 & 0xFFE7FFFFFFFFFFFFuL) | (ulong)((long)value << 51);
		}
	}

	public bool PrimitiveRestartEnable
	{
		readonly get
		{
			return ((Internal.Id6 >> 53) & 1) != 0;
		}
		set
		{
			Internal.Id6 = (Internal.Id6 & 0xFFDFFFFFFFFFFFFFuL) | (ulong)((long)(value ? 1 : 0) << 53);
		}
	}

	public bool DepthClampEnable
	{
		readonly get
		{
			return ((Internal.Id6 >> 54) & 1) != 0;
		}
		set
		{
			Internal.Id6 = (Internal.Id6 & 0xFFBFFFFFFFFFFFFFuL) | (ulong)((long)(value ? 1 : 0) << 54);
		}
	}

	public bool RasterizerDiscardEnable
	{
		readonly get
		{
			return ((Internal.Id6 >> 55) & 1) != 0;
		}
		set
		{
			Internal.Id6 = (Internal.Id6 & 0xFF7FFFFFFFFFFFFFuL) | (ulong)((long)(value ? 1 : 0) << 55);
		}
	}

	public FrontFace FrontFace
	{
		readonly get
		{
			return (FrontFace)((Internal.Id6 >> 56) & 1);
		}
		set
		{
			Internal.Id6 = (Internal.Id6 & 0xFEFFFFFFFFFFFFFFuL) | (ulong)((long)value << 56);
		}
	}

	public bool DepthBiasEnable
	{
		readonly get
		{
			return ((Internal.Id6 >> 57) & 1) != 0;
		}
		set
		{
			Internal.Id6 = (Internal.Id6 & 0xFDFFFFFFFFFFFFFFuL) | (ulong)((long)(value ? 1 : 0) << 57);
		}
	}

	public bool DepthTestEnable
	{
		readonly get
		{
			return ((Internal.Id6 >> 58) & 1) != 0;
		}
		set
		{
			Internal.Id6 = (Internal.Id6 & 0xFBFFFFFFFFFFFFFFuL) | (ulong)((long)(value ? 1 : 0) << 58);
		}
	}

	public bool DepthWriteEnable
	{
		readonly get
		{
			return ((Internal.Id6 >> 59) & 1) != 0;
		}
		set
		{
			Internal.Id6 = (Internal.Id6 & 0xF7FFFFFFFFFFFFFFuL) | (ulong)((long)(value ? 1 : 0) << 59);
		}
	}

	public bool DepthBoundsTestEnable
	{
		readonly get
		{
			return ((Internal.Id6 >> 60) & 1) != 0;
		}
		set
		{
			Internal.Id6 = (Internal.Id6 & 0xEFFFFFFFFFFFFFFFuL) | (ulong)((long)(value ? 1 : 0) << 60);
		}
	}

	public bool StencilTestEnable
	{
		readonly get
		{
			return ((Internal.Id6 >> 61) & 1) != 0;
		}
		set
		{
			Internal.Id6 = (Internal.Id6 & 0xDFFFFFFFFFFFFFFFuL) | (ulong)((long)(value ? 1 : 0) << 61);
		}
	}

	public bool LogicOpEnable
	{
		readonly get
		{
			return ((Internal.Id6 >> 62) & 1) != 0;
		}
		set
		{
			Internal.Id6 = (Internal.Id6 & 0xBFFFFFFFFFFFFFFFuL) | (ulong)((long)(value ? 1 : 0) << 62);
		}
	}

	public bool HasDepthStencil
	{
		readonly get
		{
			return ((Internal.Id6 >> 63) & 1) != 0;
		}
		set
		{
			Internal.Id6 = (Internal.Id6 & 0x7FFFFFFFFFFFFFFFL) | (ulong)((long)(value ? 1 : 0) << 63);
		}
	}

	public uint PatchControlPoints
	{
		readonly get
		{
			return (uint)(Internal.Id7 & 0xFFFFFFFFu);
		}
		set
		{
			Internal.Id7 = (Internal.Id7 & 0xFFFFFFFF00000000uL) | value;
		}
	}

	public uint SamplesCount
	{
		readonly get
		{
			return (uint)((Internal.Id7 >> 32) & 0xFFFFFFFFu);
		}
		set
		{
			Internal.Id7 = (Internal.Id7 & 0xFFFFFFFFu) | ((ulong)value << 32);
		}
	}

	public bool AlphaToCoverageEnable
	{
		readonly get
		{
			return (Internal.Id8 & 1) != 0;
		}
		set
		{
			Internal.Id8 = (Internal.Id8 & 0xFFFFFFFFFFFFFFFEuL) | (ulong)(value ? 1 : 0);
		}
	}

	public bool AlphaToOneEnable
	{
		readonly get
		{
			return ((Internal.Id8 >> 1) & 1) != 0;
		}
		set
		{
			Internal.Id8 = (Internal.Id8 & 0xFFFFFFFFFFFFFFFDuL) | (ulong)((long)(value ? 1 : 0) << 1);
		}
	}

	public bool AdvancedBlendSrcPreMultiplied
	{
		readonly get
		{
			return ((Internal.Id8 >> 2) & 1) != 0;
		}
		set
		{
			Internal.Id8 = (Internal.Id8 & 0xFFFFFFFFFFFFFFFBuL) | (ulong)((long)(value ? 1 : 0) << 2);
		}
	}

	public bool AdvancedBlendDstPreMultiplied
	{
		readonly get
		{
			return ((Internal.Id8 >> 3) & 1) != 0;
		}
		set
		{
			Internal.Id8 = (Internal.Id8 & 0xFFFFFFFFFFFFFFF7uL) | (ulong)((long)(value ? 1 : 0) << 3);
		}
	}

	public BlendOverlapEXT AdvancedBlendOverlap
	{
		readonly get
		{
			return (BlendOverlapEXT)((Internal.Id8 >> 4) & 3);
		}
		set
		{
			Internal.Id8 = (Internal.Id8 & 0xFFFFFFFFFFFFFFCFuL) | (ulong)((long)value << 4);
		}
	}

	public bool DepthMode
	{
		readonly get
		{
			return ((Internal.Id8 >> 6) & 1) != 0;
		}
		set
		{
			Internal.Id8 = (Internal.Id8 & 0xFFFFFFFFFFFFFFBFuL) | (ulong)((long)(value ? 1 : 0) << 6);
		}
	}

	public FeedbackLoopAspects FeedbackLoopAspects
	{
		readonly get
		{
			return (FeedbackLoopAspects)((Internal.Id8 >> 7) & 3);
		}
		set
		{
			Internal.Id8 = (Internal.Id8 & 0xFFFFFFFFFFFFFE7FuL) | (ulong)((long)value << 7);
		}
	}

	public void Initialize()
	{
		HasTessellationControlShader = false;
		Stages = new NativeArray<PipelineShaderStageCreateInfo>(5);
		AdvancedBlendSrcPreMultiplied = true;
		AdvancedBlendDstPreMultiplied = true;
		AdvancedBlendOverlap = BlendOverlapEXT.BlendOverlapUncorrelatedExt;
		LineWidth = 1f;
		SamplesCount = 1u;
		DepthMode = true;
	}

	public unsafe Auto<DisposablePipeline> CreateComputePipeline(VulkanRenderer gd, Device device, ShaderCollection program, PipelineCache cache)
	{
		if (program.TryGetComputePipeline(ref SpecializationData, out var pipeline))
		{
			return pipeline;
		}
		using (RyuStutterTrace.Measure("pipeline.compute.miss", program.NxTraceId))
		{
			ComputePipelineCreateInfo computePipelineCreateInfo = new ComputePipelineCreateInfo
			{
				SType = StructureType.ComputePipelineCreateInfo,
				Stage = Stages[0],
				BasePipelineIndex = -1,
				Layout = PipelineLayout
			};
			Pipeline pipeline2 = default;
			bool flag = program.SpecDescriptions != null;
			SpecDescription specDescription = (flag ? program.SpecDescriptions[0] : SpecDescription.Empty);
			if (flag && SpecializationData.Length < (int)specDescription.Info.DataSize)
			{
				throw new InvalidOperationException("Specialization data size does not match description");
			}
			fixed (SpecializationInfo* info = &specDescription.Info)
			{
				fixed (SpecializationMapEntry* map = specDescription.Map)
				{
					fixed (byte* pData = SpecializationData.Span)
					{
						if (flag)
						{
							info->PMapEntries = map;
							info->PData = pData;
							computePipelineCreateInfo.Stage.PSpecializationInfo = info;
						}
						using (RyuStutterTrace.Measure("pipeline.compute.driver", program.NxTraceId))
						{
							gd.Api.CreateComputePipelines(device, cache, 1u, &computePipelineCreateInfo, null, &pipeline2).ThrowOnError();
						}
					}
				}
			}
			pipeline = new Auto<DisposablePipeline>(new DisposablePipeline(gd.Api, device, pipeline2));
			program.AddComputePipeline(ref SpecializationData, pipeline);
			return pipeline;
		}
	}

	public unsafe Auto<DisposablePipeline> CreateGraphicsPipeline(VulkanRenderer gd, Device device, ShaderCollection program, PipelineCache cache, RenderPass renderPass, bool throwOnError = false)
	{
		if (program.TryGetGraphicsPipeline(ref Internal, out var pipeline))
		{
			return pipeline;
		}
		Pipeline pipeline2 = default;
		using (RyuStutterTrace.Measure("pipeline.graphics.miss", program.NxTraceId, RyuStutterTrace.Enabled ? Internal.GetHashCode().ToString("X8") : null))
		{
			bool isMoltenVk = gd.IsMoltenVk;
			if (isMoltenVk)
			{
				UpdateVertexAttributeDescriptions(gd);
			}
			fixed (VertexInputAttributeDescription* ptr = &Internal.VertexAttributeDescriptions[0])
			{
				fixed (VertexInputAttributeDescription* ptr2 = &_vertexAttributeDescriptions2[0])
				{
					fixed (VertexInputBindingDescription* pVertexBindingDescriptions = &Internal.VertexBindingDescriptions[0])
					{
						fixed (PipelineColorBlendAttachmentState* pAttachments = &Internal.ColorBlendAttachmentState[0])
						{
							PipelineVertexInputStateCreateInfo pipelineVertexInputStateCreateInfo = new PipelineVertexInputStateCreateInfo
							{
								SType = StructureType.PipelineVertexInputStateCreateInfo,
								VertexAttributeDescriptionCount = VertexAttributeDescriptionsCount,
								PVertexAttributeDescriptions = (isMoltenVk ? ptr2 : ptr),
								VertexBindingDescriptionCount = VertexBindingDescriptionsCount,
								PVertexBindingDescriptions = pVertexBindingDescriptions
							};
							if (Topology == PrimitiveTopology.PatchList && !HasTessellationControlShader)
							{
								program.AddGraphicsPipeline(ref Internal, null);
								return null;
							}
							bool primitiveRestartEnable = PrimitiveRestartEnable;
							bool flag = ((!gd.Capabilities.SupportsPrimitiveTopologyListRestart) ? (Topology == PrimitiveTopology.LineStrip || Topology == PrimitiveTopology.TriangleStrip || Topology == PrimitiveTopology.TriangleFan || Topology == PrimitiveTopology.LineStripWithAdjacency || Topology == PrimitiveTopology.TriangleStripWithAdjacency) : (gd.Capabilities.SupportsPrimitiveTopologyPatchListRestart || Topology != PrimitiveTopology.PatchList));
							primitiveRestartEnable &= flag;
							PipelineInputAssemblyStateCreateInfo pipelineInputAssemblyStateCreateInfo = new PipelineInputAssemblyStateCreateInfo
							{
								SType = StructureType.PipelineInputAssemblyStateCreateInfo,
								PrimitiveRestartEnable = primitiveRestartEnable,
								Topology = (HasTessellationControlShader ? PrimitiveTopology.PatchList : Topology)
							};
							PipelineTessellationStateCreateInfo pipelineTessellationStateCreateInfo = new PipelineTessellationStateCreateInfo
							{
								SType = StructureType.PipelineTessellationStateCreateInfo,
								PatchControlPoints = PatchControlPoints
							};
							PipelineRasterizationStateCreateInfo pipelineRasterizationStateCreateInfo = new PipelineRasterizationStateCreateInfo
							{
								SType = StructureType.PipelineRasterizationStateCreateInfo,
								DepthClampEnable = DepthClampEnable,
								RasterizerDiscardEnable = RasterizerDiscardEnable,
								PolygonMode = PolygonMode,
								LineWidth = LineWidth,
								CullMode = CullMode,
								FrontFace = FrontFace,
								DepthBiasEnable = DepthBiasEnable
							};
							PipelineViewportStateCreateInfo pipelineViewportStateCreateInfo = new PipelineViewportStateCreateInfo
							{
								SType = StructureType.PipelineViewportStateCreateInfo,
								ViewportCount = ViewportsCount,
								ScissorCount = ScissorsCount
							};
							if (gd.Capabilities.SupportsDepthClipControl)
							{
								PipelineViewportDepthClipControlCreateInfoEXT pipelineViewportDepthClipControlCreateInfoEXT = new PipelineViewportDepthClipControlCreateInfoEXT
								{
									SType = StructureType.PipelineViewportDepthClipControlCreateInfoExt,
									NegativeOneToOne = DepthMode
								};
								pipelineViewportStateCreateInfo.PNext = &pipelineViewportDepthClipControlCreateInfoEXT;
							}
							PipelineMultisampleStateCreateInfo pipelineMultisampleStateCreateInfo = new PipelineMultisampleStateCreateInfo
							{
								SType = StructureType.PipelineMultisampleStateCreateInfo,
								SampleShadingEnable = false,
								RasterizationSamples = TextureStorage.ConvertToSampleCountFlags(gd.Capabilities.SupportedSampleCounts, SamplesCount),
								MinSampleShading = 1f,
								AlphaToCoverageEnable = AlphaToCoverageEnable,
								AlphaToOneEnable = AlphaToOneEnable
							};
							StencilOpState front = new StencilOpState(StencilFrontFailOp, StencilFrontPassOp, StencilFrontDepthFailOp, StencilFrontCompareOp);
							StencilOpState back = new StencilOpState(StencilBackFailOp, StencilBackPassOp, StencilBackDepthFailOp, StencilBackCompareOp);
							PipelineDepthStencilStateCreateInfo pipelineDepthStencilStateCreateInfo = new PipelineDepthStencilStateCreateInfo
							{
								SType = StructureType.PipelineDepthStencilStateCreateInfo,
								DepthTestEnable = DepthTestEnable,
								DepthWriteEnable = DepthWriteEnable,
								DepthCompareOp = DepthCompareOp,
								DepthBoundsTestEnable = false,
								StencilTestEnable = StencilTestEnable,
								Front = front,
								Back = back
							};
							uint num = 0u;
							if (gd.IsMoltenVk && Internal.AttachmentIntegerFormatMask != 0)
							{
								uint num2 = Internal.AttachmentIntegerFormatMask;
								while (num2 != 0)
								{
									int num3 = BitOperations.TrailingZeroCount(num2);
									if ((bool)Internal.ColorBlendAttachmentState[num3].BlendEnable)
									{
										num |= (uint)(1 << num3);
									}
									Internal.ColorBlendAttachmentState[num3].BlendEnable = false;
									num2 &= (uint)(~(1 << num3));
								}
							}
							bool flag2 = LogicOpEnable && (gd.Vendor == Vendor.Nvidia || Internal.LogicOpsAllowed);
							PipelineColorBlendStateCreateInfo pipelineColorBlendStateCreateInfo = new PipelineColorBlendStateCreateInfo
							{
								SType = StructureType.PipelineColorBlendStateCreateInfo,
								LogicOpEnable = flag2,
								LogicOp = LogicOp,
								AttachmentCount = ColorBlendAttachmentStateCount,
								PAttachments = pAttachments
							};
							if (!AdvancedBlendSrcPreMultiplied || !AdvancedBlendDstPreMultiplied || AdvancedBlendOverlap != BlendOverlapEXT.BlendOverlapUncorrelatedExt)
							{
								PipelineColorBlendAdvancedStateCreateInfoEXT pipelineColorBlendAdvancedStateCreateInfoEXT = new PipelineColorBlendAdvancedStateCreateInfoEXT
								{
									SType = StructureType.PipelineColorBlendAdvancedStateCreateInfoExt,
									SrcPremultiplied = AdvancedBlendSrcPreMultiplied,
									DstPremultiplied = AdvancedBlendDstPreMultiplied,
									BlendOverlap = AdvancedBlendOverlap
								};
								pipelineColorBlendStateCreateInfo.PNext = &pipelineColorBlendAdvancedStateCreateInfoEXT;
							}
							bool supportsExtendedDynamicState = gd.Capabilities.SupportsExtendedDynamicState;
							bool supportsDynamicAttachmentFeedbackLoop = gd.Capabilities.SupportsDynamicAttachmentFeedbackLoop;
							DynamicState* ptr3 = stackalloc DynamicState[9];
							int dynamicStateCount = 7;
							*ptr3 = DynamicState.Viewport;
							ptr3[1] = DynamicState.Scissor;
							ptr3[2] = DynamicState.DepthBias;
							ptr3[3] = DynamicState.StencilCompareMask;
							ptr3[4] = DynamicState.StencilWriteMask;
							ptr3[5] = DynamicState.StencilReference;
							ptr3[6] = DynamicState.BlendConstants;
							if (supportsExtendedDynamicState)
							{
								ptr3[dynamicStateCount++] = DynamicState.VertexInputBindingStrideExt;
							}
							if (supportsDynamicAttachmentFeedbackLoop)
							{
								ptr3[dynamicStateCount++] = DynamicState.AttachmentFeedbackLoopEnableExt;
							}
							PipelineDynamicStateCreateInfo pipelineDynamicStateCreateInfo = new PipelineDynamicStateCreateInfo
							{
								SType = StructureType.PipelineDynamicStateCreateInfo,
								DynamicStateCount = (uint)dynamicStateCount,
								PDynamicStates = ptr3
							};
							PipelineCreateFlags pipelineCreateFlags = PipelineCreateFlags.None;
							if (gd.Capabilities.SupportsAttachmentFeedbackLoop)
							{
								FeedbackLoopAspects feedbackLoopAspects = FeedbackLoopAspects;
								if ((feedbackLoopAspects & FeedbackLoopAspects.Color) != 0)
								{
									pipelineCreateFlags |= PipelineCreateFlags.PipelineCreateColorAttachmentFeedbackLoopBitExt;
								}
								if ((feedbackLoopAspects & FeedbackLoopAspects.Depth) != 0)
								{
									pipelineCreateFlags |= PipelineCreateFlags.PipelineCreateDepthStencilAttachmentFeedbackLoopBitExt;
								}
							}
							GraphicsPipelineCreateInfo graphicsPipelineCreateInfo = new GraphicsPipelineCreateInfo
							{
								SType = StructureType.GraphicsPipelineCreateInfo,
								Flags = (PipelineCreateFlags)((int)pipelineCreateFlags | (RyuPipelineCacheSettings.FastGraphicsCompilation ? 1 : 0)),
								StageCount = StagesCount,
								PStages = Stages.Pointer,
								PVertexInputState = &pipelineVertexInputStateCreateInfo,
								PInputAssemblyState = &pipelineInputAssemblyStateCreateInfo,
								PTessellationState = &pipelineTessellationStateCreateInfo,
								PViewportState = &pipelineViewportStateCreateInfo,
								PRasterizationState = &pipelineRasterizationStateCreateInfo,
								PMultisampleState = &pipelineMultisampleStateCreateInfo,
								PDepthStencilState = &pipelineDepthStencilStateCreateInfo,
								PColorBlendState = &pipelineColorBlendStateCreateInfo,
								PDynamicState = &pipelineDynamicStateCreateInfo,
								Layout = PipelineLayout,
								RenderPass = renderPass
							};
							Result result;
							using (RyuStutterTrace.Measure("pipeline.graphics.driver", program.NxTraceId))
							{
								result = gd.Api.CreateGraphicsPipelines(device, cache, 1u, &graphicsPipelineCreateInfo, null, &pipeline2);
							}
							if (throwOnError)
							{
								result.ThrowOnError();
							}
							else if (result.IsError())
							{
								program.AddGraphicsPipeline(ref Internal, null);
								return null;
							}
							while (num != 0)
							{
								int num4 = BitOperations.TrailingZeroCount(num);
								Internal.ColorBlendAttachmentState[num4].BlendEnable = true;
								num &= (uint)(~(1 << num4));
							}
						}
					}
				}
			}
			pipeline = new Auto<DisposablePipeline>(new DisposablePipeline(gd.Api, device, pipeline2));
			program.AddGraphicsPipeline(ref Internal, pipeline);
			return pipeline;
		}
	}

	private void UpdateVertexAttributeDescriptions(VulkanRenderer gd)
	{
		for (int i = 0; i < VertexAttributeDescriptionsCount; i++)
		{
			VertexInputAttributeDescription vertexInputAttributeDescription = Internal.VertexAttributeDescriptions[i];
			int vertexBufferIndex = GetVertexBufferIndex(vertexInputAttributeDescription.Binding);
			if (vertexBufferIndex >= 0)
			{
				ref VertexInputBindingDescription reference = ref Internal.VertexBindingDescriptions[vertexBufferIndex];
				Format format = vertexInputAttributeDescription.Format;
				while (reference.Stride != 0 && vertexInputAttributeDescription.Offset + FormatTable.GetAttributeFormatSize(format) > reference.Stride)
				{
					Format format2 = FormatTable.DropLastComponent(format);
					if (format2 == format)
					{
						format = vertexInputAttributeDescription.Format;
						break;
					}
					format = format2;
				}
				if (vertexInputAttributeDescription.Format != format && gd.FormatCapabilities.BufferFormatSupports(FormatFeatureFlags.FormatFeatureVertexBufferBit, format))
				{
					vertexInputAttributeDescription.Format = format;
				}
			}
			_vertexAttributeDescriptions2[i] = vertexInputAttributeDescription;
		}
	}

	private int GetVertexBufferIndex(uint binding)
	{
		for (int i = 0; i < VertexBindingDescriptionsCount; i++)
		{
			if (Internal.VertexBindingDescriptions[i].Binding == binding)
			{
				return i;
			}
		}
		return -1;
	}

	public readonly void Dispose()
	{
		Stages.Dispose();
	}
}
