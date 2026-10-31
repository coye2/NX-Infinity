using System;
using System.Linq;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Vulkan.Effects;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace Ryujinx.Graphics.Vulkan;

internal class Window : WindowBase, IDisposable
{
	private const int SurfaceWidth = 1280;

	private const int SurfaceHeight = 720;

	private readonly VulkanRenderer _gd;

	private readonly SurfaceKHR _surface;

	private readonly PhysicalDevice _physicalDevice;

	private readonly Device _device;

	private SwapchainKHR _swapchain;

	private Image[] _swapchainImages;

	private TextureView[] _swapchainImageViews;

	private Semaphore[] _imageAvailableSemaphores;

	private Semaphore[] _renderFinishedSemaphores;

	private int _frameIndex;

	private int _width;

	private int _height;

	private VSyncMode _vSyncMode;

	private bool _swapchainIsDirty;

	private Silk.NET.Vulkan.Format _format;

	private AntiAliasing _currentAntiAliasing;

	private bool _updateEffect;

	private IPostProcessingEffect _effect;

	private IScalingFilter _scalingFilter;

	private bool _isLinear;

	private float _scalingFilterLevel;

	private bool _updateScalingFilter;

	private ScalingFilter _currentScalingFilter;

	private bool _colorSpacePassthroughEnabled;

	public Window(VulkanRenderer gd, SurfaceKHR surface, PhysicalDevice physicalDevice, Device device)
	{
		_gd = gd;
		_physicalDevice = physicalDevice;
		_device = device;
		_surface = surface;
		CreateSwapchain();
	}

	private unsafe void RecreateSwapchain()
	{
		SwapchainKHR swapchain = _swapchain;
		_swapchainIsDirty = false;
		for (int i = 0; i < _swapchainImageViews.Length; i++)
		{
			_swapchainImageViews[i].Dispose();
		}
		_gd.Api.DeviceWaitIdle(_device);
		for (int j = 0; j < _imageAvailableSemaphores.Length; j++)
		{
			_gd.Api.DestroySemaphore(_device, _imageAvailableSemaphores[j], null);
		}
		for (int k = 0; k < _renderFinishedSemaphores.Length; k++)
		{
			_gd.Api.DestroySemaphore(_device, _renderFinishedSemaphores[k], null);
		}
		_gd.SwapchainApi.DestroySwapchain(_device, swapchain, Span<AllocationCallbacks>.Empty);
		CreateSwapchain();
	}

	private unsafe void CreateSwapchain()
	{
		_gd.SurfaceApi.GetPhysicalDeviceSurfaceCapabilities(_physicalDevice, _surface, out var pSurfaceCapabilities);
		uint num = default;
		_gd.SurfaceApi.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, &num, null);
		SurfaceFormatKHR[] array = new SurfaceFormatKHR[num];
		fixed (SurfaceFormatKHR* pSurfaceFormats = array)
		{
			_gd.SurfaceApi.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, &num, pSurfaceFormats);
		}
		uint num2 = default;
		_gd.SurfaceApi.GetPhysicalDeviceSurfacePresentModes(_physicalDevice, _surface, &num2, null);
		PresentModeKHR[] array2 = new PresentModeKHR[num2];
		fixed (PresentModeKHR* pPresentModes = array2)
		{
			_gd.SurfaceApi.GetPhysicalDeviceSurfacePresentModes(_physicalDevice, _surface, &num2, pPresentModes);
		}
		uint num3 = pSurfaceCapabilities.MinImageCount + 1;
		if (pSurfaceCapabilities.MaxImageCount != 0 && num3 > pSurfaceCapabilities.MaxImageCount)
		{
			num3 = pSurfaceCapabilities.MaxImageCount;
		}
		SurfaceFormatKHR surfaceFormatKHR = ChooseSwapSurfaceFormat(array, _colorSpacePassthroughEnabled);
		Extent2D imageExtent = ChooseSwapExtent(pSurfaceCapabilities);
		_width = (int)imageExtent.Width;
		_height = (int)imageExtent.Height;
		_format = surfaceFormatKHR.Format;
		_ = _swapchain;
		SwapchainCreateInfoKHR pCreateInfo = new SwapchainCreateInfoKHR
		{
			SType = StructureType.SwapchainCreateInfoKhr,
			Surface = _surface,
			MinImageCount = num3,
			ImageFormat = surfaceFormatKHR.Format,
			ImageColorSpace = surfaceFormatKHR.ColorSpace,
			ImageExtent = imageExtent,
			ImageUsage = (ImageUsageFlags.ImageUsageTransferDstBit | ImageUsageFlags.ImageUsageStorageBit | ImageUsageFlags.ImageUsageColorAttachmentBit),
			ImageSharingMode = SharingMode.Exclusive,
			ImageArrayLayers = 1u,
			PreTransform = pSurfaceCapabilities.CurrentTransform,
			CompositeAlpha = ChooseCompositeAlpha(pSurfaceCapabilities.SupportedCompositeAlpha),
			PresentMode = ChooseSwapPresentMode(array2, _vSyncMode),
			Clipped = true
		};
		TextureCreateInfo info = new TextureCreateInfo(_width, _height, 1, 1, 1, 1, 1, 1, FormatTable.GetFormat(surfaceFormatKHR.Format), DepthStencilMode.Depth, Target.Texture2D, SwizzleComponent.Red, SwizzleComponent.Green, SwizzleComponent.Blue, SwizzleComponent.Alpha);
		_gd.SwapchainApi.CreateSwapchain(_device, in pCreateInfo, null, out _swapchain).ThrowOnError();
		_gd.SwapchainApi.GetSwapchainImages(_device, _swapchain, &num3, null);
		_swapchainImages = new Image[num3];
		fixed (Image* swapchainImages = _swapchainImages)
		{
			_gd.SwapchainApi.GetSwapchainImages(_device, _swapchain, &num3, swapchainImages);
		}
		_swapchainImageViews = new TextureView[num3];
		for (int i = 0; i < _swapchainImageViews.Length; i++)
		{
			_swapchainImageViews[i] = CreateSwapchainImageView(_swapchainImages[i], surfaceFormatKHR.Format, info);
		}
		SemaphoreCreateInfo pCreateInfo2 = new SemaphoreCreateInfo
		{
			SType = StructureType.SemaphoreCreateInfo
		};
		_imageAvailableSemaphores = new Semaphore[num3];
		for (int j = 0; j < _imageAvailableSemaphores.Length; j++)
		{
			_gd.Api.CreateSemaphore(_device, in pCreateInfo2, null, out _imageAvailableSemaphores[j]).ThrowOnError();
		}
		_renderFinishedSemaphores = new Semaphore[num3];
		for (int k = 0; k < _renderFinishedSemaphores.Length; k++)
		{
			_gd.Api.CreateSemaphore(_device, in pCreateInfo2, null, out _renderFinishedSemaphores[k]).ThrowOnError();
		}
	}

	private unsafe TextureView CreateSwapchainImageView(Image swapchainImage, Silk.NET.Vulkan.Format format, TextureCreateInfo info)
	{
		ComponentMapping components = new ComponentMapping(ComponentSwizzle.R, ComponentSwizzle.G, ComponentSwizzle.B, ComponentSwizzle.A);
		ImageAspectFlags value = ImageAspectFlags.ImageAspectColorBit;
		ImageSubresourceRange subresourceRange = new ImageSubresourceRange(value, 0u, 1u, 0u, 1u);
		ImageViewCreateInfo pCreateInfo = new ImageViewCreateInfo
		{
			SType = StructureType.ImageViewCreateInfo,
			Image = swapchainImage,
			ViewType = ImageViewType.ImageViewType2D,
			Format = format,
			Components = components,
			SubresourceRange = subresourceRange
		};
		_gd.Api.CreateImageView(_device, in pCreateInfo, null, out var pView).ThrowOnError();
		return new TextureView(_gd, _device, new DisposableImageView(_gd.Api, _device, pView), info, format);
	}

	private static SurfaceFormatKHR ChooseSwapSurfaceFormat(SurfaceFormatKHR[] availableFormats, bool colorSpacePassthroughEnabled)
	{
		if (availableFormats.Length == 1 && availableFormats[0].Format == Silk.NET.Vulkan.Format.Undefined)
		{
			return new SurfaceFormatKHR(Silk.NET.Vulkan.Format.B8G8R8A8Unorm, ColorSpaceKHR.ColorSpaceSrgbNonlinearKhr);
		}
		SurfaceFormatKHR result = availableFormats[0];
		if (colorSpacePassthroughEnabled)
		{
			SurfaceFormatKHR[] array = availableFormats;
			for (int i = 0; i < array.Length; i++)
			{
				SurfaceFormatKHR surfaceFormatKHR = array[i];
				if (surfaceFormatKHR.Format == Silk.NET.Vulkan.Format.B8G8R8A8Unorm && surfaceFormatKHR.ColorSpace == ColorSpaceKHR.ColorSpacePassThroughExt)
				{
					result = surfaceFormatKHR;
					break;
				}
				if (surfaceFormatKHR.Format == Silk.NET.Vulkan.Format.B8G8R8A8Unorm && surfaceFormatKHR.ColorSpace == ColorSpaceKHR.ColorSpaceSrgbNonlinearKhr)
				{
					result = surfaceFormatKHR;
				}
			}
		}
		else
		{
			SurfaceFormatKHR[] array = availableFormats;
			for (int i = 0; i < array.Length; i++)
			{
				SurfaceFormatKHR surfaceFormatKHR2 = array[i];
				if (surfaceFormatKHR2.Format == Silk.NET.Vulkan.Format.B8G8R8A8Unorm && surfaceFormatKHR2.ColorSpace == ColorSpaceKHR.ColorSpaceSrgbNonlinearKhr)
				{
					result = surfaceFormatKHR2;
					break;
				}
			}
		}
		return result;
	}

	private static CompositeAlphaFlagsKHR ChooseCompositeAlpha(CompositeAlphaFlagsKHR supportedFlags)
	{
		if (supportedFlags.HasFlag(CompositeAlphaFlagsKHR.CompositeAlphaOpaqueBitKhr))
		{
			return CompositeAlphaFlagsKHR.CompositeAlphaOpaqueBitKhr;
		}
		if (supportedFlags.HasFlag(CompositeAlphaFlagsKHR.CompositeAlphaPreMultipliedBitKhr))
		{
			return CompositeAlphaFlagsKHR.CompositeAlphaPreMultipliedBitKhr;
		}
		return CompositeAlphaFlagsKHR.CompositeAlphaInheritBitKhr;
	}

	private static PresentModeKHR ChooseSwapPresentMode(PresentModeKHR[] availablePresentModes, VSyncMode vSyncMode)
	{
		if (vSyncMode == VSyncMode.Unbounded && Enumerable.Contains(availablePresentModes, PresentModeKHR.PresentModeImmediateKhr))
		{
			return PresentModeKHR.PresentModeImmediateKhr;
		}
		if (Enumerable.Contains(availablePresentModes, PresentModeKHR.PresentModeMailboxKhr))
		{
			return PresentModeKHR.PresentModeMailboxKhr;
		}
		return PresentModeKHR.PresentModeFifoKhr;
	}

	public static Extent2D ChooseSwapExtent(SurfaceCapabilitiesKHR capabilities)
	{
		if (capabilities.CurrentExtent.Width != uint.MaxValue)
		{
			return capabilities.CurrentExtent;
		}
		uint value = Math.Max(capabilities.MinImageExtent.Width, Math.Min(capabilities.MaxImageExtent.Width, 1280u));
		return new Extent2D(height: Math.Max(capabilities.MinImageExtent.Height, Math.Min(capabilities.MaxImageExtent.Height, 720u)), width: value);
	}

	public unsafe override void Present(ITexture texture, ImageCrop crop, Action swapBuffersCallback)
	{
		_gd.PipelineInternal.AutoFlush.Present();
		uint pImageIndex = 0u;
		int num = _frameIndex++ % _imageAvailableSemaphores.Length;
		Result result;
		while (true)
		{
			result = _gd.SwapchainApi.AcquireNextImage(_device, _swapchain, ulong.MaxValue, _imageAvailableSemaphores[num], default, ref pImageIndex);
			if (result != Result.ErrorOutOfDateKhr && result != Result.SuboptimalKhr && !_swapchainIsDirty)
			{
				break;
			}
			RecreateSwapchain();
			num = (_frameIndex - 1) % _imageAvailableSemaphores.Length;
		}
		result.ThrowOnError();
		Image image = _swapchainImages[pImageIndex];
		_gd.FlushAllCommands();
		CommandBufferScoped cbs = _gd.CommandBufferPool.Rent();
		Transition(cbs.CommandBuffer, image, AccessFlags.AccessNoneKhr, AccessFlags.AccessTransferWriteBit, ImageLayout.Undefined, ImageLayout.General);
		TextureView textureView = (TextureView)texture;
		UpdateEffect();
		if (_effect != null)
		{
			textureView = _effect.Run(textureView, cbs, _width, _height);
		}
		int num2;
		int num3;
		if (crop.Left == 0 && crop.Right == 0)
		{
			num2 = 0;
			num3 = textureView.Width;
		}
		else
		{
			num2 = crop.Left;
			num3 = crop.Right;
		}
		int num4;
		int num5;
		if (crop.Top == 0 && crop.Bottom == 0)
		{
			num4 = 0;
			num5 = textureView.Height;
		}
		else
		{
			num4 = crop.Top;
			num5 = crop.Bottom;
		}
		if (ScreenCaptureRequested)
		{
			if (_effect != null)
			{
				_gd.CommandBufferPool.Return(cbs, null, new PipelineStageFlags[1] { PipelineStageFlags.PipelineStageColorAttachmentOutputBit }, null);
				_gd.FlushAllCommands();
				cbs.GetFence().Wait();
				cbs = _gd.CommandBufferPool.Rent();
			}
			CaptureFrame(textureView, num2, num4, num3 - num2, num5 - num4, textureView.Info.Format.IsBgr(), crop.FlipX, crop.FlipY);
			ScreenCaptureRequested = false;
		}
		float num6 = (crop.IsStretched ? 1f : MathF.Min(1f, (float)_height * crop.AspectRatioX / ((float)_width * crop.AspectRatioY)));
		float num7 = (crop.IsStretched ? 1f : MathF.Min(1f, (float)_width * crop.AspectRatioY / ((float)_height * crop.AspectRatioX)));
		int num8 = (int)((float)_width * num6);
		int num9 = (int)((float)_height * num7);
		int num10 = (_width - num8) / 2;
		int num11 = (_height - num9) / 2;
		int x = (crop.FlipX ? (_width - num10) : num10);
		int x2 = (crop.FlipX ? num10 : (_width - num10));
		int num12 = (crop.FlipY ? num11 : (_height - num11));
		int num13 = (crop.FlipY ? (_height - num11) : num11);
		if (_scalingFilter != null)
		{
			_scalingFilter.Run(textureView, cbs, _swapchainImageViews[pImageIndex].GetImageViewForAttachment(), _format, _width, _height, new Extents2D(num2, num4, num3, num5), new Extents2D(x, num12, x2, num13));
		}
		else
		{
			_gd.HelperShader.BlitColor(_gd, cbs, textureView, _swapchainImageViews[pImageIndex], new Extents2D(num2, num4, num3, num5), new Extents2D(x, num13, x2, num12), _isLinear, clearAlpha: true);
		}
		Transition(cbs.CommandBuffer, image, AccessFlags.AccessNoneKhr, AccessFlags.AccessNoneKhr, ImageLayout.General, ImageLayout.PresentSrcKhr);
		_gd.CommandBufferPool.Return(cbs, new ReadOnlySpan<Semaphore>((Semaphore)_imageAvailableSemaphores[num]), new PipelineStageFlags[1] { PipelineStageFlags.PipelineStageColorAttachmentOutputBit }, new ReadOnlySpan<Semaphore>((Semaphore)_renderFinishedSemaphores[num]));
		Semaphore semaphore = _renderFinishedSemaphores[num];
		SwapchainKHR swapchain = _swapchain;
		Result result2 = Result.ErrorUnknown;
		PresentInfoKHR pPresentInfo = new PresentInfoKHR
		{
			SType = StructureType.PresentInfoKhr,
			WaitSemaphoreCount = 1u,
			PWaitSemaphores = &semaphore,
			SwapchainCount = 1u,
			PSwapchains = &swapchain,
			PImageIndices = &pImageIndex,
			PResults = &result2
		};
		Result result3;
		using (_gd.QueueLock.EnterScope())
		{
			result3 = _gd.SwapchainApi.QueuePresent(_gd.Queue, in pPresentInfo);
		}
		VulkanRenderer gd = _gd;
		bool flag = ((result3 == Result.Success || result3 == Result.SuboptimalKhr) ? true : false);
		bool flag2 = flag;
		if (flag2)
		{
			bool flag3 = ((result2 == Result.Success || result2 == Result.SuboptimalKhr) ? true : false);
			flag2 = flag3;
		}
		gd.NotifyFramePresentation(flag2);
	}

	public override void SetAntiAliasing(AntiAliasing effect)
	{
		if (_currentAntiAliasing != effect || _effect == null)
		{
			_currentAntiAliasing = effect;
			_updateEffect = true;
		}
	}

	public override void SetScalingFilter(ScalingFilter type)
	{
		if (_currentScalingFilter != type || _effect == null)
		{
			_currentScalingFilter = type;
			_updateScalingFilter = true;
		}
	}

	public override void SetColorSpacePassthrough(bool colorSpacePassthroughEnabled)
	{
		_colorSpacePassthroughEnabled = colorSpacePassthroughEnabled;
		_swapchainIsDirty = true;
	}

	private void UpdateEffect()
	{
		if (_updateEffect)
		{
			_updateEffect = false;
			switch (_currentAntiAliasing)
			{
			case AntiAliasing.Fxaa:
				_effect?.Dispose();
				_effect = new FxaaPostProcessingEffect(_gd, _device);
				break;
			case AntiAliasing.None:
				_effect?.Dispose();
				_effect = null;
				break;
			case AntiAliasing.SmaaLow:
			case AntiAliasing.SmaaMedium:
			case AntiAliasing.SmaaHigh:
			case AntiAliasing.SmaaUltra:
			{
				int quality = (int)(_currentAntiAliasing - 2);
				if (_effect is SmaaPostProcessingEffect smaaPostProcessingEffect)
				{
					smaaPostProcessingEffect.Quality = quality;
					break;
				}
				_effect?.Dispose();
				_effect = new SmaaPostProcessingEffect(_gd, _device, quality);
				break;
			}
			}
		}
		if (!_updateScalingFilter)
		{
			return;
		}
		_updateScalingFilter = false;
		switch (_currentScalingFilter)
		{
		case ScalingFilter.Bilinear:
		case ScalingFilter.Nearest:
			_scalingFilter?.Dispose();
			_scalingFilter = null;
			_isLinear = _currentScalingFilter == ScalingFilter.Bilinear;
			break;
		case ScalingFilter.Fsr:
			if (!(_scalingFilter is FsrScalingFilter))
			{
				_scalingFilter?.Dispose();
				_scalingFilter = new FsrScalingFilter(_gd, _device);
			}
			_scalingFilter.Level = _scalingFilterLevel;
			break;
		case ScalingFilter.Area:
			if (!(_scalingFilter is AreaScalingFilter))
			{
				_scalingFilter?.Dispose();
				_scalingFilter = new AreaScalingFilter(_gd, _device);
			}
			break;
		}
	}

	public override void SetScalingFilterLevel(float level)
	{
		_scalingFilterLevel = level;
		_updateScalingFilter = true;
	}

	private unsafe void Transition(CommandBuffer commandBuffer, Image image, AccessFlags srcAccess, AccessFlags dstAccess, ImageLayout srcLayout, ImageLayout dstLayout)
	{
		ImageSubresourceRange subresourceRange = new ImageSubresourceRange(ImageAspectFlags.ImageAspectColorBit, 0u, 1u, 0u, 1u);
		ImageMemoryBarrier pImageMemoryBarriers = new ImageMemoryBarrier
		{
			SType = StructureType.ImageMemoryBarrier,
			SrcAccessMask = srcAccess,
			DstAccessMask = dstAccess,
			OldLayout = srcLayout,
			NewLayout = dstLayout,
			SrcQueueFamilyIndex = uint.MaxValue,
			DstQueueFamilyIndex = uint.MaxValue,
			Image = image,
			SubresourceRange = subresourceRange
		};
		_gd.Api.CmdPipelineBarrier(commandBuffer, PipelineStageFlags.PipelineStageTopOfPipeBit, PipelineStageFlags.PipelineStageAllCommandsBit, DependencyFlags.None, 0u, null, 0u, null, 1u, in pImageMemoryBarriers);
	}

	private void CaptureFrame(TextureView texture, int x, int y, int width, int height, bool isBgra, bool flipX, bool flipY)
	{
		byte[] data = texture.GetData(x, y, width, height);
		_gd.OnScreenCaptured(new ScreenCaptureImageInfo(width, height, isBgra, data, flipX, flipY));
	}

	public override void SetSize(int width, int height)
	{
		_swapchainIsDirty = true;
	}

	public override void ChangeVSyncMode(VSyncMode vSyncMode)
	{
		_vSyncMode = vSyncMode;
		_swapchainIsDirty = true;
	}

	protected unsafe virtual void Dispose(bool disposing)
	{
		if (disposing)
		{
			for (int i = 0; i < _swapchainImageViews.Length; i++)
			{
				_swapchainImageViews[i].Dispose();
			}
			for (int j = 0; j < _imageAvailableSemaphores.Length; j++)
			{
				_gd.Api.DestroySemaphore(_device, _imageAvailableSemaphores[j], null);
			}
			for (int k = 0; k < _renderFinishedSemaphores.Length; k++)
			{
				_gd.Api.DestroySemaphore(_device, _renderFinishedSemaphores[k], null);
			}
			_gd.SwapchainApi.DestroySwapchain(_device, _swapchain, null);
			_effect?.Dispose();
			_scalingFilter?.Dispose();
		}
	}

	public override void Dispose()
	{
		Dispose(disposing: true);
	}
}
