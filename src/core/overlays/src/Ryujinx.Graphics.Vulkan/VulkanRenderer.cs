using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;
using Ryujinx.Graphics.Vulkan.MoltenVK;
using Ryujinx.Graphics.Vulkan.Queries;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;

namespace Ryujinx.Graphics.Vulkan;

public sealed class VulkanRenderer : IRenderer, IDisposable
{
	private VulkanInstance _instance;

	private SurfaceKHR _surface;

	private VulkanPhysicalDevice _physicalDevice;

	private Device _device;

	private WindowBase _window;

	private bool _initialized;

	internal HardwareCapabilities Capabilities;

	private VulkanDebugMessenger _debugMessenger;

	private Counters _counters;

	private PipelineFull _pipeline;

	private readonly Func<Instance, Vk, SurfaceKHR> _getSurface;

	private readonly Func<string[]> _getRequiredExtensions;

	private readonly string _preferredGpuId;

	private int[] _pdReservedBindings;

	private static readonly int[] _pdReservedBindingsNvn = new int[5] { 3, 18, 21, 36, 30 };

	private static readonly int[] _pdReservedBindingsOgl = new int[5] { 17, 18, 34, 35, 36 };

	public object NxGraphicsDevice { get; private set; }

	public uint ProgramCount { get; set; }

	internal FormatCapabilities FormatCapabilities { get; private set; }

	internal Vk Api { get; private set; }

	internal KhrSurface SurfaceApi { get; private set; }

	internal KhrSwapchain SwapchainApi { get; private set; }

	internal ExtConditionalRendering ConditionalRenderingApi { get; private set; }

	internal ExtExtendedDynamicState ExtendedDynamicStateApi { get; private set; }

	internal KhrPushDescriptor PushDescriptorApi { get; private set; }

	internal ExtTransformFeedback TransformFeedbackApi { get; private set; }

	internal KhrDrawIndirectCount DrawIndirectCountApi { get; private set; }

	internal ExtAttachmentFeedbackLoopDynamicState DynamicFeedbackLoopApi { get; private set; }

	internal uint QueueFamilyIndex { get; private set; }

	internal Queue Queue { get; private set; }

	internal Queue BackgroundQueue { get; private set; }

	internal Lock BackgroundQueueLock { get; private set; }

	internal Lock QueueLock { get; private set; }

	internal MemoryAllocator MemoryAllocator { get; private set; }

	internal HostMemoryAllocator HostMemoryAllocator { get; private set; }

	internal CommandBufferPool CommandBufferPool { get; private set; }

	internal PipelineLayoutCache PipelineLayoutCache { get; private set; }

	internal BackgroundResources BackgroundResources { get; private set; }

	internal Action<Action> InterruptAction { get; private set; }

	internal SyncManager SyncManager { get; private set; }

	internal BufferManager BufferManager { get; private set; }

	internal RyuPipelineCompiler NxPipelineCompiler { get; } = new RyuPipelineCompiler();

	internal HashSet<ShaderCollection> Shaders { get; }

	internal HashSet<ITexture> Textures { get; }

	internal HashSet<SamplerHolder> Samplers { get; }

	internal HelperShader HelperShader { get; private set; }

	internal PipelineFull PipelineInternal => _pipeline;

	internal BarrierBatch Barriers { get; private set; }

	public IPipeline Pipeline => _pipeline;

	public IWindow Window => _window;

	internal Vendor Vendor { get; private set; }

	internal bool IsAmdWindows { get; private set; }

	internal bool IsIntelWindows { get; private set; }

	internal bool IsAmdGcn { get; private set; }

	internal bool IsAmdRdna3 { get; private set; }

	internal bool IsNvidiaPreTuring { get; private set; }

	internal bool IsIntelArc { get; private set; }

	internal bool IsQualcommProprietary { get; private set; }

	internal bool IsMoltenVk { get; private set; }

	internal bool IsTBDR { get; private set; }

	internal bool IsSharedMemory { get; private set; }

	public string GpuVendor { get; private set; }

	public string GpuDriver { get; private set; }

	public string GpuRenderer { get; private set; }

	public string GpuVersion { get; private set; }

	public bool PreferThreading => true;

	public event Action<bool> FramePresentationCompleted;

	public event EventHandler<ScreenCaptureImageInfo> ScreenCaptured;

	internal void NotifyFramePresentation(bool accepted)
	{
		FramePresentationCompleted?.Invoke(accepted);
	}

	internal unsafe RyuDriverPipelineCache CreateNxDriverCache()
	{
		Api.GetPhysicalDeviceProperties(_physicalDevice.PhysicalDevice, out var pProperties);
		byte[] uuid = new ReadOnlySpan<byte>(pProperties.PipelineCacheUuid, 16).ToArray();
		return new RyuDriverPipelineCache(RyuPipelineCacheSettings.Directory, pProperties.VendorID, pProperties.DeviceID, pProperties.DriverVersion, uuid, typeof(VulkanRenderer).Module.ModuleVersionId);
	}

	public VulkanRenderer(Vk api, Func<Instance, Vk, SurfaceKHR> surfaceFunc, Func<string[]> requiredExtensionsFunc, string preferredGpuId)
	{
		_getSurface = surfaceFunc;
		_getRequiredExtensions = requiredExtensionsFunc;
		_preferredGpuId = preferredGpuId;
		Api = api;
		Shaders = new HashSet<ShaderCollection>();
		Textures = new HashSet<ITexture>();
		Samplers = new HashSet<SamplerHolder>();
		if (OperatingSystem.IsMacOS())
		{
			MVKInitialization.Initialize();
			IsMoltenVk = true;
		}
	}

	private unsafe void LoadFeatures(uint maxQueueCount, uint queueFamilyIndex)
	{
		FormatCapabilities = new FormatCapabilities(Api, _physicalDevice.PhysicalDevice);
		if (Api.TryGetDeviceExtension<ExtConditionalRendering>(_instance.Instance, _device, out var ext))
		{
			ConditionalRenderingApi = ext;
		}
		if (Api.TryGetDeviceExtension<ExtExtendedDynamicState>(_instance.Instance, _device, out var ext2))
		{
			ExtendedDynamicStateApi = ext2;
		}
		if (Api.TryGetDeviceExtension<KhrPushDescriptor>(_instance.Instance, _device, out var ext3))
		{
			PushDescriptorApi = ext3;
		}
		if (Api.TryGetDeviceExtension<ExtTransformFeedback>(_instance.Instance, _device, out var ext4))
		{
			TransformFeedbackApi = ext4;
		}
		if (Api.TryGetDeviceExtension<KhrDrawIndirectCount>(_instance.Instance, _device, out var ext5))
		{
			DrawIndirectCountApi = ext5;
		}
		if (Api.TryGetDeviceExtension<ExtAttachmentFeedbackLoopDynamicState>(_instance.Instance, _device, out var ext6))
		{
			DynamicFeedbackLoopApi = ext6;
		}
		if (maxQueueCount >= 2)
		{
			Api.GetDeviceQueue(_device, queueFamilyIndex, 1u, out var pQueue);
			BackgroundQueue = pQueue;
			BackgroundQueueLock = new Lock();
		}
		PhysicalDeviceProperties2 physicalDeviceProperties = new PhysicalDeviceProperties2
		{
			SType = StructureType.PhysicalDeviceProperties2Khr
		};
		PhysicalDeviceSubgroupProperties physicalDeviceSubgroupProperties = new PhysicalDeviceSubgroupProperties
		{
			SType = StructureType.PhysicalDeviceSubgroupProperties,
			PNext = physicalDeviceProperties.PNext
		};
		physicalDeviceProperties.PNext = &physicalDeviceSubgroupProperties;
		PhysicalDeviceBlendOperationAdvancedPropertiesEXT physicalDeviceBlendOperationAdvancedPropertiesEXT = new PhysicalDeviceBlendOperationAdvancedPropertiesEXT
		{
			SType = StructureType.PhysicalDeviceBlendOperationAdvancedPropertiesExt
		};
		bool flag = _physicalDevice.IsDeviceExtensionPresent("VK_EXT_blend_operation_advanced");
		if (flag)
		{
			physicalDeviceBlendOperationAdvancedPropertiesEXT.PNext = physicalDeviceProperties.PNext;
			physicalDeviceProperties.PNext = &physicalDeviceBlendOperationAdvancedPropertiesEXT;
		}
		bool flag2 = _physicalDevice.IsDeviceExtensionPresent("VK_EXT_transform_feedback");
		PhysicalDeviceTransformFeedbackPropertiesEXT physicalDeviceTransformFeedbackPropertiesEXT = new PhysicalDeviceTransformFeedbackPropertiesEXT
		{
			SType = StructureType.PhysicalDeviceTransformFeedbackPropertiesExt
		};
		if (flag2)
		{
			physicalDeviceTransformFeedbackPropertiesEXT.PNext = physicalDeviceProperties.PNext;
			physicalDeviceProperties.PNext = &physicalDeviceTransformFeedbackPropertiesEXT;
		}
		PhysicalDevicePortabilitySubsetPropertiesKHR physicalDevicePortabilitySubsetPropertiesKHR = new PhysicalDevicePortabilitySubsetPropertiesKHR
		{
			SType = StructureType.PhysicalDevicePortabilitySubsetPropertiesKhr
		};
		bool flag3 = _physicalDevice.IsDeviceExtensionPresent("VK_KHR_push_descriptor");
		PhysicalDevicePushDescriptorPropertiesKHR physicalDevicePushDescriptorPropertiesKHR = new PhysicalDevicePushDescriptorPropertiesKHR
		{
			SType = StructureType.PhysicalDevicePushDescriptorPropertiesKhr
		};
		if (flag3)
		{
			physicalDevicePushDescriptorPropertiesKHR.PNext = physicalDeviceProperties.PNext;
			physicalDeviceProperties.PNext = &physicalDevicePushDescriptorPropertiesKHR;
		}
		PhysicalDeviceFeatures2 physicalDeviceFeatures = new PhysicalDeviceFeatures2
		{
			SType = StructureType.PhysicalDeviceFeatures2Khr
		};
		PhysicalDevicePrimitiveTopologyListRestartFeaturesEXT physicalDevicePrimitiveTopologyListRestartFeaturesEXT = new PhysicalDevicePrimitiveTopologyListRestartFeaturesEXT
		{
			SType = StructureType.PhysicalDevicePrimitiveTopologyListRestartFeaturesExt
		};
		PhysicalDeviceRobustness2FeaturesEXT physicalDeviceRobustness2FeaturesEXT = new PhysicalDeviceRobustness2FeaturesEXT
		{
			SType = StructureType.PhysicalDeviceRobustness2FeaturesExt
		};
		PhysicalDeviceShaderFloat16Int8FeaturesKHR physicalDeviceShaderFloat16Int8FeaturesKHR = new PhysicalDeviceShaderFloat16Int8FeaturesKHR
		{
			SType = StructureType.PhysicalDeviceShaderFloat16Int8FeaturesKhr
		};
		PhysicalDeviceCustomBorderColorFeaturesEXT physicalDeviceCustomBorderColorFeaturesEXT = new PhysicalDeviceCustomBorderColorFeaturesEXT
		{
			SType = StructureType.PhysicalDeviceCustomBorderColorFeaturesExt
		};
		PhysicalDeviceDepthClipControlFeaturesEXT physicalDeviceDepthClipControlFeaturesEXT = new PhysicalDeviceDepthClipControlFeaturesEXT
		{
			SType = StructureType.PhysicalDeviceDepthClipControlFeaturesExt
		};
		PhysicalDeviceAttachmentFeedbackLoopLayoutFeaturesEXT physicalDeviceAttachmentFeedbackLoopLayoutFeaturesEXT = new PhysicalDeviceAttachmentFeedbackLoopLayoutFeaturesEXT
		{
			SType = StructureType.PhysicalDeviceAttachmentFeedbackLoopLayoutFeaturesExt
		};
		PhysicalDeviceAttachmentFeedbackLoopDynamicStateFeaturesEXT physicalDeviceAttachmentFeedbackLoopDynamicStateFeaturesEXT = new PhysicalDeviceAttachmentFeedbackLoopDynamicStateFeaturesEXT
		{
			SType = StructureType.PhysicalDeviceAttachmentFeedbackLoopDynamicStateFeaturesExt
		};
		PhysicalDevicePortabilitySubsetFeaturesKHR physicalDevicePortabilitySubsetFeaturesKHR = new PhysicalDevicePortabilitySubsetFeaturesKHR
		{
			SType = StructureType.PhysicalDevicePortabilitySubsetFeaturesKhr
		};
		if (_physicalDevice.IsDeviceExtensionPresent("VK_EXT_primitive_topology_list_restart"))
		{
			physicalDeviceFeatures.PNext = &physicalDevicePrimitiveTopologyListRestartFeaturesEXT;
		}
		if (_physicalDevice.IsDeviceExtensionPresent("VK_EXT_robustness2"))
		{
			physicalDeviceRobustness2FeaturesEXT.PNext = physicalDeviceFeatures.PNext;
			physicalDeviceFeatures.PNext = &physicalDeviceRobustness2FeaturesEXT;
		}
		if (_physicalDevice.IsDeviceExtensionPresent("VK_KHR_shader_float16_int8"))
		{
			physicalDeviceShaderFloat16Int8FeaturesKHR.PNext = physicalDeviceFeatures.PNext;
			physicalDeviceFeatures.PNext = &physicalDeviceShaderFloat16Int8FeaturesKHR;
		}
		if (_physicalDevice.IsDeviceExtensionPresent("VK_EXT_custom_border_color"))
		{
			physicalDeviceCustomBorderColorFeaturesEXT.PNext = physicalDeviceFeatures.PNext;
			physicalDeviceFeatures.PNext = &physicalDeviceCustomBorderColorFeaturesEXT;
		}
		bool flag4 = _physicalDevice.IsDeviceExtensionPresent("VK_EXT_depth_clip_control");
		if (flag4)
		{
			physicalDeviceDepthClipControlFeaturesEXT.PNext = physicalDeviceFeatures.PNext;
			physicalDeviceFeatures.PNext = &physicalDeviceDepthClipControlFeaturesEXT;
		}
		bool flag5 = _physicalDevice.IsDeviceExtensionPresent("VK_EXT_attachment_feedback_loop_layout");
		if (flag5)
		{
			physicalDeviceAttachmentFeedbackLoopLayoutFeaturesEXT.PNext = physicalDeviceFeatures.PNext;
			physicalDeviceFeatures.PNext = &physicalDeviceAttachmentFeedbackLoopLayoutFeaturesEXT;
		}
		bool flag6 = _physicalDevice.IsDeviceExtensionPresent("VK_EXT_attachment_feedback_loop_dynamic_state");
		if (flag6)
		{
			physicalDeviceAttachmentFeedbackLoopDynamicStateFeaturesEXT.PNext = physicalDeviceFeatures.PNext;
			physicalDeviceFeatures.PNext = &physicalDeviceAttachmentFeedbackLoopDynamicStateFeaturesEXT;
		}
		bool flag7 = _physicalDevice.IsDeviceExtensionPresent("VK_KHR_portability_subset");
		if (flag7)
		{
			physicalDevicePortabilitySubsetPropertiesKHR.PNext = physicalDeviceProperties.PNext;
			physicalDeviceProperties.PNext = &physicalDevicePortabilitySubsetPropertiesKHR;
			physicalDevicePortabilitySubsetFeaturesKHR.PNext = physicalDeviceFeatures.PNext;
			physicalDeviceFeatures.PNext = &physicalDevicePortabilitySubsetFeaturesKHR;
		}
		Api.GetPhysicalDeviceProperties2(_physicalDevice.PhysicalDevice, &physicalDeviceProperties);
		Api.GetPhysicalDeviceFeatures2(_physicalDevice.PhysicalDevice, &physicalDeviceFeatures);
		PortabilitySubsetFlags portabilitySubsetFlags = PortabilitySubsetFlags.None;
		uint vertexBufferAlignment = 1u;
		if (flag7)
		{
			vertexBufferAlignment = physicalDevicePortabilitySubsetPropertiesKHR.MinVertexInputBindingStrideAlignment;
			portabilitySubsetFlags = (PortabilitySubsetFlags)((int)portabilitySubsetFlags | ((!physicalDevicePortabilitySubsetFeaturesKHR.TriangleFans) ? 1 : 0));
			portabilitySubsetFlags = (PortabilitySubsetFlags)((int)portabilitySubsetFlags | ((!physicalDevicePortabilitySubsetFeaturesKHR.PointPolygons) ? 2 : 0));
			portabilitySubsetFlags = (PortabilitySubsetFlags)((int)portabilitySubsetFlags | ((!physicalDevicePortabilitySubsetFeaturesKHR.ImageView2DOn3DImage) ? 4 : 0));
			portabilitySubsetFlags = (PortabilitySubsetFlags)((int)portabilitySubsetFlags | ((!physicalDevicePortabilitySubsetFeaturesKHR.SamplerMipLodBias) ? 8 : 0));
		}
		bool supportsCustomBorderColor = _physicalDevice.IsDeviceExtensionPresent("VK_EXT_custom_border_color") && (bool)physicalDeviceCustomBorderColorFeaturesEXT.CustomBorderColors && (bool)physicalDeviceCustomBorderColorFeaturesEXT.CustomBorderColorWithoutFormat;
		ref PhysicalDeviceProperties properties = ref physicalDeviceProperties.Properties;
		bool flag8 = _physicalDevice.TryGetPhysicalDeviceDriverPropertiesKHR(Api, out var res);
		Vendor = VendorUtils.FromId(properties.VendorID);
		IsAmdWindows = Vendor == Vendor.Amd && OperatingSystem.IsWindows();
		IsIntelWindows = Vendor == Vendor.Intel && OperatingSystem.IsWindows();
		IsTBDR = Vendor == Vendor.Apple || Vendor == Vendor.Qualcomm || Vendor == Vendor.ARM || Vendor == Vendor.Broadcom || Vendor == Vendor.ImgTec;
		GpuVendor = VendorUtils.GetNameFromId(properties.VendorID);
		GpuDriver = ((flag8 && !OperatingSystem.IsMacOS()) ? VendorUtils.GetFriendlyDriverName(res.DriverID) : GpuVendor);
		fixed (byte* deviceName = properties.DeviceName)
		{
			GpuRenderer = Marshal.PtrToStringAnsi((nint)deviceName);
		}
		GpuVersion = "Vulkan v" + ParseStandardVulkanVersion(properties.ApiVersion) + ", Driver v" + ParseDriverVersion(ref properties);
		IsAmdGcn = !IsMoltenVk && Vendor == Vendor.Amd && VendorUtils.AmdGcnRegex().IsMatch(GpuRenderer);
		bool flag9 = Vendor == Vendor.Amd;
		uint deviceID;
		if (flag9)
		{
			bool flag10 = VendorUtils.AmdRdna3Regex().IsMatch(GpuRenderer);
			if (!flag10)
			{
				deviceID = properties.DeviceID;
				bool flag11 = ((deviceID == 5567 || deviceID == 5576) ? true : false);
				flag10 = flag11;
			}
			flag9 = flag10;
		}
		IsAmdRdna3 = flag9;
		if (Vendor == Vendor.Nvidia)
		{
			Match match = VendorUtils.NvidiaConsumerClassRegex().Match(GpuRenderer);
			if (match != null && int.TryParse(match.Groups[2].Value, out var result))
			{
				IsNvidiaPreTuring = result < 2000;
			}
			else if (GpuRenderer.Contains("TITAN") && !GpuRenderer.Contains("RTX"))
			{
				IsNvidiaPreTuring = true;
			}
		}
		else if (Vendor == Vendor.Intel)
		{
			IsIntelArc = GpuRenderer.StartsWith("Intel(R) Arc(TM)");
		}
		IsQualcommProprietary = flag8 && res.DriverID == DriverId.DriverIDQualcommProprietary;
		ulong minResourceAlignment = Math.Max(Math.Max(properties.Limits.MinStorageBufferOffsetAlignment, properties.Limits.MinUniformBufferOffsetAlignment), properties.Limits.MinTexelBufferOffsetAlignment);
		SampleCountFlags supportedSampleCounts = properties.Limits.FramebufferColorSampleCounts & properties.Limits.FramebufferDepthSampleCounts & properties.Limits.FramebufferStencilSampleCounts;
		Capabilities = new HardwareCapabilities(_physicalDevice.IsDeviceExtensionPresent("VK_EXT_index_type_uint8"), supportsCustomBorderColor, flag, physicalDeviceBlendOperationAdvancedPropertiesEXT.AdvancedBlendCorrelatedOverlap, physicalDeviceBlendOperationAdvancedPropertiesEXT.AdvancedBlendNonPremultipliedSrcColor, physicalDeviceBlendOperationAdvancedPropertiesEXT.AdvancedBlendNonPremultipliedDstColor, _physicalDevice.IsDeviceExtensionPresent("VK_KHR_draw_indirect_count"), _physicalDevice.IsDeviceExtensionPresent("VK_EXT_fragment_shader_interlock"), _physicalDevice.IsDeviceExtensionPresent("VK_NV_geometry_shader_passthrough"), physicalDeviceFeatures.Features.ShaderFloat64, physicalDeviceShaderFloat16Int8FeaturesKHR.ShaderInt8, _physicalDevice.IsDeviceExtensionPresent("VK_EXT_shader_stencil_export"), physicalDeviceFeatures.Features.ShaderStorageImageMultisample, _physicalDevice.IsDeviceExtensionPresent("VK_EXT_conditional_rendering"), _physicalDevice.IsDeviceExtensionPresent("VK_EXT_extended_dynamic_state"), (bool)physicalDeviceFeatures.Features.MultiViewport && (!IsMoltenVk || Vendor != Vendor.Amd), (bool)physicalDeviceRobustness2FeaturesEXT.NullDescriptor || IsMoltenVk, flag3 && !IsMoltenVk, physicalDevicePushDescriptorPropertiesKHR.MaxPushDescriptors, physicalDevicePrimitiveTopologyListRestartFeaturesEXT.PrimitiveTopologyListRestart, physicalDevicePrimitiveTopologyListRestartFeaturesEXT.PrimitiveTopologyPatchListRestart, flag2, physicalDeviceTransformFeedbackPropertiesEXT.TransformFeedbackQueries, physicalDeviceFeatures.Features.OcclusionQueryPrecise, _physicalDevice.PhysicalDeviceFeatures.PipelineStatisticsQuery, _physicalDevice.PhysicalDeviceFeatures.GeometryShader, _physicalDevice.PhysicalDeviceFeatures.TessellationShader, _physicalDevice.IsDeviceExtensionPresent("VK_NV_viewport_array2"), _physicalDevice.IsDeviceExtensionPresent("VK_EXT_external_memory_host"), flag4 && (bool)physicalDeviceDepthClipControlFeaturesEXT.DepthClipControl, flag5 && (bool)physicalDeviceAttachmentFeedbackLoopLayoutFeaturesEXT.AttachmentFeedbackLoopLayout, flag6 && (bool)physicalDeviceAttachmentFeedbackLoopDynamicStateFeaturesEXT.AttachmentFeedbackLoopDynamicState, physicalDeviceSubgroupProperties.SubgroupSize, supportedSampleCounts, portabilitySubsetFlags, vertexBufferAlignment, properties.Limits.SubTexelPrecisionBits, minResourceAlignment);
		IsSharedMemory = MemoryAllocator.IsDeviceMemoryShared(_physicalDevice);
		deviceID = properties.VendorID;
		uint deviceID2 = properties.DeviceID;
		string gpuVendor = GpuVendor;
		string gpuRenderer = GpuRenderer;
		uint? driverId = (flag8 ? new uint?((uint)res.DriverID) : ((uint?)null));
		string gpuDriver = GpuDriver;
		uint driverVersion = properties.DriverVersion;
		uint apiVersion = properties.ApiVersion;
		string driverVersion2 = ParseDriverVersion(ref properties);
		string apiVersion2 = ParseStandardVulkanVersion(properties.ApiVersion);
		ulong totalGPUMemory = GetTotalGPUMemory();
		NxGraphicsDevice = new
		{
			schemaVersion = 1,
			source = "native_vulkan_selected_physical_device",
			api = "vulkan",
			vendorId = deviceID,
			deviceId = deviceID2,
			vendorName = gpuVendor,
			deviceName = gpuRenderer,
			driverId = driverId,
			driverName = gpuDriver,
			driverInfo = (string)null,
			driverVersionRaw = driverVersion,
			apiVersionRaw = apiVersion,
			driverVersion = driverVersion2,
			apiVersion = apiVersion2,
			deviceLocalHeapBytes = totalGPUMemory,
			deviceMemoryShared = properties.DeviceType switch
			{
				PhysicalDeviceType.IntegratedGpu => (bool?)true,
				PhysicalDeviceType.DiscreteGpu => false,
				_ => null,
			}
		};
		MemoryAllocator = new MemoryAllocator(Api, _physicalDevice, _device);
		Api.TryGetDeviceExtension<ExtExternalMemoryHost>(_instance.Instance, _device, out var ext7);
		HostMemoryAllocator = new HostMemoryAllocator(MemoryAllocator, Api, ext7, _device);
		CommandBufferPool = new CommandBufferPool(Api, _device, Queue, QueueLock, queueFamilyIndex, IsQualcommProprietary);
		PipelineLayoutCache = new PipelineLayoutCache();
		BackgroundResources = new BackgroundResources(this, _device);
		BufferManager = new BufferManager(this, _device);
		SyncManager = new SyncManager(this, _device);
		_pipeline = new PipelineFull(this, _device);
		_pipeline.Initialize();
		HelperShader = new HelperShader(this, _device);
		Barriers = new BarrierBatch(this);
		_counters = new Counters(this, _device, _pipeline);
	}

	private void SetupContext(GraphicsDebugLevel logLevel)
	{
		_instance = VulkanInitialization.CreateInstance(Api, logLevel, _getRequiredExtensions());
		_debugMessenger = new VulkanDebugMessenger(Api, _instance.Instance, logLevel);
		if (Api.TryGetInstanceExtension<KhrSurface>(_instance.Instance, out var ext))
		{
			SurfaceApi = ext;
		}
		_surface = _getSurface(_instance.Instance, Api);
		_physicalDevice = VulkanInitialization.FindSuitablePhysicalDevice(Api, _instance, _surface, _preferredGpuId);
		uint queueFamilyIndex = VulkanInitialization.FindSuitableQueueFamily(Api, _physicalDevice, _surface, out var queueCount);
		_device = VulkanInitialization.CreateDevice(Api, _physicalDevice, queueFamilyIndex, queueCount);
		if (Api.TryGetDeviceExtension<KhrSwapchain>(_instance.Instance, _device, out var ext2))
		{
			SwapchainApi = ext2;
		}
		Api.GetDeviceQueue(_device, queueFamilyIndex, 0u, out var pQueue);
		Queue = pQueue;
		QueueLock = new Lock();
		LoadFeatures(queueCount, queueFamilyIndex);
		QueueFamilyIndex = queueFamilyIndex;
		_window = new Window(this, _surface, _physicalDevice.PhysicalDevice, _device);
		_initialized = true;
	}

	internal int[] GetPushDescriptorReservedBindings(bool isOgl)
	{
		if (_pdReservedBindings == null)
		{
			if (Capabilities.MaxPushDescriptors <= 36)
			{
				_pdReservedBindings = (isOgl ? _pdReservedBindingsOgl : _pdReservedBindingsNvn);
			}
			else
			{
				_pdReservedBindings = Array.Empty<int>();
			}
		}
		return _pdReservedBindings;
	}

	public BufferHandle CreateBuffer(int size, BufferAccess access)
	{
		return BufferManager.CreateWithHandle(this, size, access.HasFlag(BufferAccess.SparseCompatible), access.Convert(), access.HasFlag(BufferAccess.Stream));
	}

	public BufferHandle CreateBuffer(nint pointer, int size)
	{
		return BufferManager.CreateHostImported(this, pointer, size);
	}

	public BufferHandle CreateBufferSparse(ReadOnlySpan<BufferRange> storageBuffers)
	{
		return BufferManager.CreateSparse(this, storageBuffers);
	}

	public IImageArray CreateImageArray(int size, bool isBuffer)
	{
		return new ImageArray(this, size, isBuffer);
	}

	public IProgram CreateProgram(ShaderSource[] sources, ShaderInfo info)
	{
		ProgramCount++;
		bool flag = sources.Length == 1 && sources[0].Stage == ShaderStage.Compute;
		if (info.State.HasValue | flag)
		{
			return new ShaderCollection(this, _device, sources, info.ResourceLayout, info.State.GetValueOrDefault(), info.FromCache);
		}
		return new ShaderCollection(this, _device, sources, info.ResourceLayout);
	}

	internal ShaderCollection CreateProgramWithMinimalLayout(ShaderSource[] sources, ResourceLayout resourceLayout, SpecDescription[] specDescription = null)
	{
		return new ShaderCollection(this, _device, sources, resourceLayout, specDescription, isMinimal: true);
	}

	public ISampler CreateSampler(Ryujinx.Graphics.GAL.SamplerCreateInfo info)
	{
		return new SamplerHolder(this, _device, info);
	}

	public ITexture CreateTexture(TextureCreateInfo info)
	{
		if (info.Target == Target.TextureBuffer)
		{
			return new TextureBuffer(this, info);
		}
		return CreateTextureView(info);
	}

	public ITextureArray CreateTextureArray(int size, bool isBuffer)
	{
		return new TextureArray(this, size, isBuffer);
	}

	internal TextureView CreateTextureView(TextureCreateInfo info)
	{
		return CreateTextureStorage(info).CreateView(info, 0, 0);
	}

	internal TextureStorage CreateTextureStorage(TextureCreateInfo info)
	{
		return new TextureStorage(this, _device, info);
	}

	public void DeleteBuffer(BufferHandle buffer)
	{
		BufferManager.Delete(buffer);
	}

	internal void FlushAllCommands()
	{
		_pipeline?.FlushCommandsImpl();
	}

	internal void RegisterFlush()
	{
		SyncManager.RegisterFlush();
		BufferManager.StagingBuffer.FreeCompleted();
	}

	public PinnedSpan<byte> GetBufferData(BufferHandle buffer, int offset, int size)
	{
		return BufferManager.GetData(buffer, offset, size);
	}

	public unsafe Capabilities GetCapabilities()
	{
		FormatFeatureFlags flags = FormatFeatureFlags.FormatFeatureSampledImageBit | FormatFeatureFlags.FormatFeatureBlitSrcBit | FormatFeatureFlags.FormatFeatureSampledImageFilterLinearBit | FormatFeatureFlags.FormatFeatureTransferSrcBitKhr | FormatFeatureFlags.FormatFeatureTransferDstBitKhr;
		bool supportsBc123Compression = FormatCapabilities.OptimalFormatsSupport(flags, Ryujinx.Graphics.GAL.Format.Bc1RgbaSrgb, Ryujinx.Graphics.GAL.Format.Bc1RgbaUnorm, Ryujinx.Graphics.GAL.Format.Bc2Srgb, Ryujinx.Graphics.GAL.Format.Bc2Unorm, Ryujinx.Graphics.GAL.Format.Bc3Srgb, Ryujinx.Graphics.GAL.Format.Bc3Unorm);
		bool supportsBc45Compression = FormatCapabilities.OptimalFormatsSupport(flags, Ryujinx.Graphics.GAL.Format.Bc4Snorm, Ryujinx.Graphics.GAL.Format.Bc4Unorm, Ryujinx.Graphics.GAL.Format.Bc5Snorm, Ryujinx.Graphics.GAL.Format.Bc5Unorm);
		bool supportsBc67Compression = FormatCapabilities.OptimalFormatsSupport(flags, Ryujinx.Graphics.GAL.Format.Bc6HSfloat, Ryujinx.Graphics.GAL.Format.Bc6HUfloat, Ryujinx.Graphics.GAL.Format.Bc7Srgb, Ryujinx.Graphics.GAL.Format.Bc7Unorm);
		bool supportsEtc2Compression = FormatCapabilities.OptimalFormatsSupport(flags, Ryujinx.Graphics.GAL.Format.Etc2RgbaSrgb, Ryujinx.Graphics.GAL.Format.Etc2RgbaUnorm, Ryujinx.Graphics.GAL.Format.Etc2RgbPtaSrgb, Ryujinx.Graphics.GAL.Format.Etc2RgbPtaUnorm, Ryujinx.Graphics.GAL.Format.Etc2RgbSrgb, Ryujinx.Graphics.GAL.Format.Etc2RgbUnorm);
		bool supports5BitComponentFormat = FormatCapabilities.OptimalFormatsSupport(flags, Ryujinx.Graphics.GAL.Format.R5G6B5Unorm, Ryujinx.Graphics.GAL.Format.R5G5B5A1Unorm, Ryujinx.Graphics.GAL.Format.R5G5B5X1Unorm, Ryujinx.Graphics.GAL.Format.B5G6R5Unorm, Ryujinx.Graphics.GAL.Format.B5G5R5A1Unorm, Ryujinx.Graphics.GAL.Format.A1B5G5R5Unorm);
		bool supportsR4G4B4A4Format = FormatCapabilities.OptimalFormatsSupport(flags, Ryujinx.Graphics.GAL.Format.R4G4B4A4Unorm);
		bool flag = FormatCapabilities.OptimalFormatsSupport(flags, Ryujinx.Graphics.GAL.Format.Astc4x4Unorm, Ryujinx.Graphics.GAL.Format.Astc5x4Unorm, Ryujinx.Graphics.GAL.Format.Astc5x5Unorm, Ryujinx.Graphics.GAL.Format.Astc6x5Unorm, Ryujinx.Graphics.GAL.Format.Astc6x6Unorm, Ryujinx.Graphics.GAL.Format.Astc8x5Unorm, Ryujinx.Graphics.GAL.Format.Astc8x6Unorm, Ryujinx.Graphics.GAL.Format.Astc8x8Unorm, Ryujinx.Graphics.GAL.Format.Astc10x5Unorm, Ryujinx.Graphics.GAL.Format.Astc10x6Unorm, Ryujinx.Graphics.GAL.Format.Astc10x8Unorm, Ryujinx.Graphics.GAL.Format.Astc10x10Unorm, Ryujinx.Graphics.GAL.Format.Astc12x10Unorm, Ryujinx.Graphics.GAL.Format.Astc12x12Unorm, Ryujinx.Graphics.GAL.Format.Astc4x4Srgb, Ryujinx.Graphics.GAL.Format.Astc5x4Srgb, Ryujinx.Graphics.GAL.Format.Astc5x5Srgb, Ryujinx.Graphics.GAL.Format.Astc6x5Srgb, Ryujinx.Graphics.GAL.Format.Astc6x6Srgb, Ryujinx.Graphics.GAL.Format.Astc8x5Srgb, Ryujinx.Graphics.GAL.Format.Astc8x6Srgb, Ryujinx.Graphics.GAL.Format.Astc8x8Srgb, Ryujinx.Graphics.GAL.Format.Astc10x5Srgb, Ryujinx.Graphics.GAL.Format.Astc10x6Srgb, Ryujinx.Graphics.GAL.Format.Astc10x8Srgb, Ryujinx.Graphics.GAL.Format.Astc10x10Srgb, Ryujinx.Graphics.GAL.Format.Astc12x10Srgb, Ryujinx.Graphics.GAL.Format.Astc12x12Srgb);
		PhysicalDeviceVulkan12Features physicalDeviceVulkan12Features = new PhysicalDeviceVulkan12Features
		{
			SType = StructureType.PhysicalDeviceVulkan12Features
		};
		PhysicalDeviceFeatures2 physicalDeviceFeatures = new PhysicalDeviceFeatures2
		{
			SType = StructureType.PhysicalDeviceFeatures2Khr,
			PNext = &physicalDeviceVulkan12Features
		};
		Api.GetPhysicalDeviceFeatures2(_physicalDevice.PhysicalDevice, &physicalDeviceFeatures);
		PhysicalDeviceLimits limits = _physicalDevice.PhysicalDeviceProperties.Limits;
		QueueFamilyProperties queueFamilyProperties = _physicalDevice.QueueFamilyProperties[QueueFamilyIndex];
		return new Capabilities(memoryType: IsSharedMemory ? SystemMemoryType.UnifiedMemory : ((Vendor == Vendor.Nvidia) ? SystemMemoryType.DedicatedMemorySlowStorage : SystemMemoryType.DedicatedMemory), api: TargetApi.Vulkan, vendorName: GpuVendor, hasFrontFacingBug: IsIntelWindows, hasVectorIndexingBug: IsQualcommProprietary, needsFragmentOutputSpecialization: IsMoltenVk, reduceShaderPrecision: IsMoltenVk, supportsAstcCompression: (bool)physicalDeviceFeatures.Features.TextureCompressionAstcLdr & flag, supportsBc123Compression: supportsBc123Compression, supportsBc45Compression: supportsBc45Compression, supportsBc67Compression: supportsBc67Compression, supportsEtc2Compression: supportsEtc2Compression, supports3DTextureCompression: true, supportsBgraFormat: true, supportsR4G4Format: false, supportsR4G4B4A4Format: supportsR4G4B4A4Format, supportsScaledVertexFormats: FormatCapabilities.SupportsScaledVertexFormats(), supportsSnormBufferTextureFormat: true, supports5BitComponentFormat: supports5BitComponentFormat, supportsSparseBuffer: (bool)physicalDeviceFeatures.Features.SparseBinding && queueFamilyProperties.QueueFlags.HasFlag(QueueFlags.QueueSparseBindingBit), supportsBlendEquationAdvanced: Capabilities.SupportsBlendEquationAdvanced, supportsFragmentShaderInterlock: Capabilities.SupportsFragmentShaderInterlock, supportsFragmentShaderOrderingIntel: false, supportsGeometryShader: Capabilities.SupportsGeometryShader, supportsGeometryShaderPassthrough: Capabilities.SupportsGeometryShaderPassthrough, supportsTransformFeedback: Capabilities.SupportsTransformFeedback, supportsImageLoadFormatted: physicalDeviceFeatures.Features.ShaderStorageImageReadWithoutFormat, supportsLayerVertexTessellation: physicalDeviceVulkan12Features.ShaderOutputLayer, supportsMismatchingViewFormat: true, supportsCubemapView: !IsAmdGcn, supportsNonConstantTextureOffset: false, supportsQuads: false, supportsSeparateSampler: true, supportsShaderBallot: false, supportsShaderBarrierDivergence: Vendor != Vendor.Intel, supportsShaderFloat64: Capabilities.SupportsShaderFloat64, supportsTextureGatherOffsets: (bool)physicalDeviceFeatures.Features.ShaderImageGatherExtended && !IsMoltenVk, supportsTextureShadowLod: false, supportsVertexStoreAndAtomics: physicalDeviceFeatures.Features.VertexPipelineStoresAndAtomics, supportsViewportIndexVertexTessellation: physicalDeviceVulkan12Features.ShaderOutputViewportIndex, supportsViewportMask: Capabilities.SupportsViewportArray2, supportsViewportSwizzle: false, supportsIndirectParameters: true, supportsDepthClipControl: Capabilities.SupportsDepthClipControl, uniformBufferSetIndex: 0, storageBufferSetIndex: 1, textureSetIndex: 2, imageSetIndex: 3, extraSetBaseIndex: 4, maximumExtraSets: Math.Max(0, (int)(limits.MaxBoundDescriptorSets - 4)), maximumUniformBuffersPerStage: 18u, maximumStorageBuffersPerStage: 16u, maximumTexturesPerStage: 64u, maximumImagesPerStage: 16u, maximumComputeSharedMemorySize: (int)limits.MaxComputeSharedMemorySize, maximumSupportedAnisotropy: (int)limits.MaxSamplerAnisotropy, shaderSubgroupSize: (int)Capabilities.SubgroupSize, storageBufferOffsetAlignment: (int)limits.MinStorageBufferOffsetAlignment, textureBufferOffsetAlignment: (int)limits.MinTexelBufferOffsetAlignment, gatherBiasPrecision: (int)((IsIntelWindows || IsAmdWindows) ? Capabilities.SubTexelPrecisionBits : 0), maximumGpuMemory: GetTotalGPUMemory());
	}

	private ulong GetTotalGPUMemory()
	{
		ulong num = 0uL;
		Api.GetPhysicalDeviceMemoryProperties(_physicalDevice.PhysicalDevice, out var pMemoryProperties);
		for (int i = 0; i < pMemoryProperties.MemoryHeapCount; i++)
		{
			MemoryHeap memoryHeap = pMemoryProperties.MemoryHeaps[i];
			if ((memoryHeap.Flags & MemoryHeapFlags.MemoryHeapDeviceLocalBit) == MemoryHeapFlags.MemoryHeapDeviceLocalBit)
			{
				num += memoryHeap.Size;
			}
		}
		return num;
	}

	public HardwareInfo GetHardwareInfo()
	{
		return new HardwareInfo(GpuVendor, GpuRenderer, GpuDriver);
	}

	public static DeviceInfo[] GetPhysicalDevices()
	{
		try
		{
			return VulkanInitialization.GetSuitablePhysicalDevices(Vk.GetApi());
		}
		catch (Exception ex)
		{
			Logger.Error?.PrintMsg(LogClass.Gpu, "Error querying Vulkan devices: " + ex.Message);
			return Array.Empty<DeviceInfo>();
		}
	}

	public static DeviceInfo[] GetPhysicalDevices(Vk api)
	{
		try
		{
			return VulkanInitialization.GetSuitablePhysicalDevices(api);
		}
		catch (Exception)
		{
			return Array.Empty<DeviceInfo>();
		}
	}

	private static string ParseStandardVulkanVersion(uint version)
	{
		return $"{version >> 22}.{(version >> 12) & 0x3FF}.{version & 0xFFF}";
	}

	private static string ParseDriverVersion(ref PhysicalDeviceProperties properties)
	{
		uint driverVersion = properties.DriverVersion;
		if (properties.VendorID == 4318)
		{
			return $"{(driverVersion >> 22) & 0x3FF}.{(driverVersion >> 14) & 0xFF}.{(driverVersion >> 6) & 0xFF}.{driverVersion & 0x3F}";
		}
		return ParseStandardVulkanVersion(driverVersion);
	}

	internal Ryujinx.Graphics.GAL.PrimitiveTopology TopologyRemap(Ryujinx.Graphics.GAL.PrimitiveTopology topology)
	{
		switch (topology)
		{
		case Ryujinx.Graphics.GAL.PrimitiveTopology.Quads:
			return Ryujinx.Graphics.GAL.PrimitiveTopology.Triangles;
		case Ryujinx.Graphics.GAL.PrimitiveTopology.QuadStrip:
			return Ryujinx.Graphics.GAL.PrimitiveTopology.TriangleStrip;
		case Ryujinx.Graphics.GAL.PrimitiveTopology.TriangleFan:
		case Ryujinx.Graphics.GAL.PrimitiveTopology.Polygon:
			return Capabilities.PortabilitySubset.HasFlag(PortabilitySubsetFlags.NoTriangleFans) ? Ryujinx.Graphics.GAL.PrimitiveTopology.Triangles : topology;
		default:
			return topology;
		}
	}

	internal bool TopologyUnsupported(Ryujinx.Graphics.GAL.PrimitiveTopology topology)
	{
		switch (topology)
		{
		case Ryujinx.Graphics.GAL.PrimitiveTopology.Quads:
			return true;
		case Ryujinx.Graphics.GAL.PrimitiveTopology.TriangleFan:
		case Ryujinx.Graphics.GAL.PrimitiveTopology.Polygon:
			return Capabilities.PortabilitySubset.HasFlag(PortabilitySubsetFlags.NoTriangleFans);
		default:
			return false;
		}
	}

	private void PrintGpuInformation()
	{
		Logger.Notice.Print(LogClass.Gpu, $"{GpuVendor} {GpuRenderer} ({GpuVersion})", "PrintGpuInformation");
		Logger.Notice.Print(LogClass.Gpu, $"GPU Memory: {GetTotalGPUMemory() / 1048576} MiB", "PrintGpuInformation");
	}

	public void Initialize(GraphicsDebugLevel logLevel)
	{
		SetupContext(logLevel);
		PrintGpuInformation();
	}

	internal bool NeedsVertexBufferAlignment(int attrScalarAlignment, out int alignment)
	{
		if (Capabilities.VertexBufferAlignment > 1)
		{
			alignment = (int)Capabilities.VertexBufferAlignment;
			return true;
		}
		if (Vendor != Vendor.Nvidia)
		{
			alignment = attrScalarAlignment;
			return true;
		}
		alignment = 1;
		return false;
	}

	public void PreFrame()
	{
		SyncManager.Cleanup();
	}

	public ICounterEvent ReportCounter(CounterType type, EventHandler<ulong> resultHandler, float divisor, bool hostReserved)
	{
		return _counters.QueueReport(type, resultHandler, divisor, hostReserved);
	}

	public void ResetCounter(CounterType type)
	{
		_counters.QueueReset(type);
	}

	public void SetBufferData(BufferHandle buffer, int offset, ReadOnlySpan<byte> data)
	{
		BufferManager.SetData(buffer, offset, data, _pipeline.CurrentCommandBuffer, _pipeline.EndRenderPassDelegate);
	}

	public void UpdateCounters()
	{
		_counters.Update();
	}

	public void ResetCounterPool()
	{
		_counters.ResetCounterPool();
	}

	public void ResetFutureCounters(CommandBuffer cmd, int count)
	{
		_counters?.ResetFutureCounters(cmd, count);
	}

	public void BackgroundContextAction(Action action, bool alwaysBackground = false)
	{
		action();
	}

	public void CreateSync(ulong id, bool strict)
	{
		SyncManager.Create(id, strict);
	}

	public IProgram LoadProgramBinary(byte[] programBinary, bool isFragment, ShaderInfo info)
	{
		throw new NotImplementedException();
	}

	public void WaitSync(ulong id)
	{
		SyncManager.Wait(id);
	}

	public ulong GetCurrentSync()
	{
		return SyncManager.GetCurrent();
	}

	public void SetInterruptAction(Action<Action> interruptAction)
	{
		InterruptAction = interruptAction;
	}

	public void Screenshot()
	{
		_window.ScreenCaptureRequested = true;
	}

	public void OnScreenCaptured(ScreenCaptureImageInfo bitmap)
	{
		ScreenCaptured?.Invoke(this, bitmap);
	}

	public bool SupportsRenderPassBarrier(PipelineStageFlags flags)
	{
		if (!IsMoltenVk)
		{
			return !IsQualcommProprietary;
		}
		return false;
	}

	public unsafe void Dispose()
	{
		NxPipelineCompiler.Dispose();
		if (!_initialized)
		{
			return;
		}
		CommandBufferPool.Dispose();
		BackgroundResources.Dispose();
		_counters.Dispose();
		_window.Dispose();
		HelperShader.Dispose();
		_pipeline.Dispose();
		BufferManager.Dispose();
		PipelineLayoutCache.Dispose();
		Barriers.Dispose();
		MemoryAllocator.Dispose();
		foreach (ShaderCollection shader in Shaders)
		{
			shader.Dispose();
		}
		foreach (ITexture texture in Textures)
		{
			texture.Release();
		}
		foreach (SamplerHolder sampler in Samplers)
		{
			sampler.Dispose();
		}
		SurfaceApi.DestroySurface(_instance.Instance, _surface, null);
		Api.DestroyDevice(_device, null);
		_debugMessenger.Dispose();
		_instance.Dispose();
	}

	public bool PrepareHostMapping(nint address, ulong size)
	{
		if (Capabilities.SupportsHostImportedMemory)
		{
			return HostMemoryAllocator.TryImport(BufferManager.HostImportedBufferMemoryRequirements, MemoryPropertyFlags.MemoryPropertyHostVisibleBit | MemoryPropertyFlags.MemoryPropertyHostCoherentBit | MemoryPropertyFlags.MemoryPropertyHostCachedBit, address, size);
		}
		return false;
	}
}
