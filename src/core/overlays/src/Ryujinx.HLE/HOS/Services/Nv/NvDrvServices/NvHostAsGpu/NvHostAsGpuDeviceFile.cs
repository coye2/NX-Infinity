using System;
using System.Runtime.CompilerServices;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostAsGpu.Types;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostChannel;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvMap;
using Ryujinx.Memory;

namespace Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostAsGpu;

internal class NvHostAsGpuDeviceFile : NvDeviceFile
{
	private readonly struct VmRegion
	{
		public ulong Start { get; }

		public ulong Limit { get; }

		public VmRegion(ulong start, ulong limit)
		{
			Start = start;
			Limit = limit;
		}
	}

	private const uint SmallPageSize = 4096u;

	private const uint BigPageSize = 65536u;

	private static readonly uint[] _pageSizes = new uint[2] { 4096u, 65536u };

	private const ulong SmallRegionLimit = 17179869184uL;

	private const ulong DefaultUserSize = 137438953472uL;

	private static readonly VmRegion[] _vmRegions = new VmRegion[2]
	{
		new VmRegion(4294967296uL, 17179869184uL),
		new VmRegion(17179869184uL, 137438953472uL)
	};

	private readonly AddressSpaceContext _asContext;

	private readonly NvMemoryAllocator _memoryAllocator;

	public NvHostAsGpuDeviceFile(ServiceCtx context, IVirtualMemoryManager memory, ulong owner)
		: base(context, owner)
	{
		long totalAvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
		ulong cpuMemorySize = ((totalAvailableMemoryBytes > 0) ? ((ulong)totalAvailableMemoryBytes) : context.Device.Memory.Size);
		_asContext = new AddressSpaceContext(context.Device.Gpu.CreateMemoryManager(owner, cpuMemorySize));
		_memoryAllocator = new NvMemoryAllocator();
	}

	public override NvInternalResult Ioctl(NvIoctl command, Span<byte> arguments)
	{
		NvInternalResult result = NvInternalResult.NotImplemented;
		if (command.Type == 65)
		{
			switch (command.Number)
			{
			case 1u:
				result = NvDeviceFile.CallIoctlMethod<BindChannelArguments>(BindChannel, arguments);
				break;
			case 2u:
				result = NvDeviceFile.CallIoctlMethod<AllocSpaceArguments>(AllocSpace, arguments);
				break;
			case 3u:
				result = NvDeviceFile.CallIoctlMethod<FreeSpaceArguments>(FreeSpace, arguments);
				break;
			case 5u:
				result = NvDeviceFile.CallIoctlMethod<UnmapBufferArguments>(UnmapBuffer, arguments);
				break;
			case 6u:
				result = NvDeviceFile.CallIoctlMethod<MapBufferExArguments>(MapBufferEx, arguments);
				break;
			case 8u:
				result = NvDeviceFile.CallIoctlMethod<GetVaRegionsArguments>(GetVaRegions, arguments);
				break;
			case 9u:
				result = NvDeviceFile.CallIoctlMethod<InitializeExArguments>(InitializeEx, arguments);
				break;
			case 20u:
				result = NvDeviceFile.CallIoctlMethod<RemapArguments>(Remap, arguments);
				break;
			}
		}
		return result;
	}

	public override NvInternalResult Ioctl3(NvIoctl command, Span<byte> arguments, Span<byte> inlineOutBuffer)
	{
		NvInternalResult result = NvInternalResult.NotImplemented;
		if (command.Type == 65 && command.Number == 8)
		{
			result = NvDeviceFile.CallIoctlMethod<GetVaRegionsArguments>(GetVaRegions, arguments);
		}
		return result;
	}

	private NvInternalResult BindChannel(ref BindChannelArguments arguments)
	{
		INvDrvServices.DeviceFileIdRegistry.GetData<NvHostChannelDeviceFile>(arguments.Fd).Channel.BindMemory(_asContext.Gmm);
		return NvInternalResult.Success;
	}

	private NvInternalResult AllocSpace(ref AllocSpaceArguments arguments)
	{
		ulong num = (ulong)arguments.Pages * (ulong)arguments.PageSize;
		NvInternalResult result = NvInternalResult.Success;
		lock (_asContext)
		{
			if ((arguments.Flags & AddressSpaceFlags.FixedOffset) != 0)
			{
				ulong offset;
				if (!_memoryAllocator.IsRegionInUse(arguments.Offset, num, out var freeAddressStartPosition))
				{
					_memoryAllocator.AllocateRange(arguments.Offset, num, freeAddressStartPosition);
					offset = freeAddressStartPosition;
				}
				else
				{
					offset = ulong.MaxValue;
				}
				arguments.Offset = offset;
			}
			else
			{
				ulong freeAddress = _memoryAllocator.GetFreeAddress(num, out var freeAddressStartPosition2, arguments.Offset);
				if (freeAddress != ulong.MaxValue)
				{
					_memoryAllocator.AllocateRange(freeAddress, num, freeAddressStartPosition2);
				}
				arguments.Offset = freeAddress;
			}
			if (arguments.Offset == ulong.MaxValue)
			{
				arguments.Offset = 0uL;
				Logger.Warning?.Print(LogClass.ServiceNv, $"Failed to allocate size {num:x16}!", "AllocSpace");
				result = NvInternalResult.OutOfMemory;
			}
			else
			{
				_asContext.AddReservation(arguments.Offset, num);
			}
		}
		return result;
	}

	private NvInternalResult FreeSpace(ref FreeSpaceArguments arguments)
	{
		ulong num = (ulong)arguments.Pages * (ulong)arguments.PageSize;
		NvInternalResult result = NvInternalResult.Success;
		lock (_asContext)
		{
			if (_asContext.RemoveReservation(arguments.Offset))
			{
				_memoryAllocator.DeallocateRange(arguments.Offset, num);
				_asContext.Gmm.Unmap(arguments.Offset, num);
			}
			else
			{
				Logger.Warning?.Print(LogClass.ServiceNv, $"Failed to free offset 0x{arguments.Offset:x16} size 0x{num:x16}!", "FreeSpace");
				result = NvInternalResult.InvalidInput;
			}
		}
		return result;
	}

	private NvInternalResult UnmapBuffer(ref UnmapBufferArguments arguments)
	{
		lock (_asContext)
		{
			if (_asContext.RemoveMap(arguments.Offset, out var size))
			{
				if (size != 0L)
				{
					_memoryAllocator.DeallocateRange(arguments.Offset, size);
					_asContext.Gmm.Unmap(arguments.Offset, size);
				}
			}
			else
			{
				Logger.Warning?.Print(LogClass.ServiceNv, $"Invalid buffer offset {arguments.Offset:x16}!", "UnmapBuffer");
			}
		}
		return NvInternalResult.Success;
	}

	private NvInternalResult MapBufferEx(ref MapBufferExArguments arguments)
	{
		ulong physicalAddress;
		if ((arguments.Flags & AddressSpaceFlags.RemapSubRange) != 0)
		{
			lock (_asContext)
			{
				if (_asContext.TryGetMapPhysicalAddress(arguments.Offset, out physicalAddress))
				{
					ulong va = arguments.Offset + arguments.BufferOffset;
					physicalAddress += arguments.BufferOffset;
					_asContext.Gmm.Map(physicalAddress, va, arguments.MappingSize, (PteKind)arguments.Kind);
					return NvInternalResult.Success;
				}
				Logger.Warning?.Print(LogClass.ServiceNv, $"Address 0x{arguments.Offset:x16} not mapped!", "MapBufferEx");
				return NvInternalResult.InvalidInput;
			}
		}
		NvMapHandle mapFromHandle = NvMapDeviceFile.GetMapFromHandle(Owner, arguments.NvMapHandle);
		if (mapFromHandle == null)
		{
			Logger.Warning?.Print(LogClass.ServiceNv, $"Invalid NvMap handle 0x{arguments.NvMapHandle:x8}!", "MapBufferEx");
			return NvInternalResult.InvalidInput;
		}
		ulong num = (ulong)arguments.PageSize;
		if (num == 0L)
		{
			num = (ulong)mapFromHandle.Align;
		}
		physicalAddress = mapFromHandle.Address + arguments.BufferOffset;
		ulong num2 = arguments.MappingSize;
		if (num2 == 0L)
		{
			num2 = mapFromHandle.Size;
		}
		NvInternalResult result = NvInternalResult.Success;
		lock (_asContext)
		{
			bool flag = (arguments.Flags & AddressSpaceFlags.FixedOffset) == 0;
			if (!flag)
			{
				if (_asContext.ValidateFixedBuffer(arguments.Offset, num2, num))
				{
					_asContext.Gmm.Map(physicalAddress, arguments.Offset, num2, (PteKind)arguments.Kind);
				}
				else
				{
					string message = $"Failed to map fixed buffer with offset 0x{arguments.Offset:x16}, size 0x{num2:x16} and alignment 0x{num:x16}!";
					Logger.Warning?.Print(LogClass.ServiceNv, message, "MapBufferEx");
					result = NvInternalResult.InvalidInput;
				}
			}
			else
			{
				ulong freeAddress = _memoryAllocator.GetFreeAddress(num2, out var freeAddressStartPosition, num);
				if (freeAddress != ulong.MaxValue)
				{
					_memoryAllocator.AllocateRange(freeAddress, num2, freeAddressStartPosition);
				}
				_asContext.Gmm.Map(physicalAddress, freeAddress, num2, (PteKind)arguments.Kind);
				arguments.Offset = freeAddress;
			}
			if (arguments.Offset == ulong.MaxValue)
			{
				arguments.Offset = 0uL;
				Logger.Warning?.Print(LogClass.ServiceNv, $"Failed to map size 0x{num2:x16}!", "MapBufferEx");
				result = NvInternalResult.InvalidInput;
			}
			else
			{
				_asContext.AddMap(arguments.Offset, num2, physicalAddress, flag);
			}
		}
		return result;
	}

	private NvInternalResult GetVaRegions(ref GetVaRegionsArguments arguments)
	{
		int num = Unsafe.SizeOf<VaRegion>();
		uint num2 = (uint)(arguments.BufferSize / num);
		if (num2 > _pageSizes.Length)
		{
			num2 = (uint)_pageSizes.Length;
		}
		for (uint num3 = 0u; num3 < num2; num3++)
		{
			ref VaRegion reference = ref arguments.Regions[(int)num3];
			VmRegion vmRegion = _vmRegions[num3];
			uint num4 = (reference.PageSize = _pageSizes[num3]);
			reference.Offset = vmRegion.Start;
			reference.Pages = (vmRegion.Limit - vmRegion.Start) / num4;
			reference.Padding = 0u;
		}
		arguments.BufferSize = (uint)(_pageSizes.Length * num);
		return NvInternalResult.Success;
	}

	private NvInternalResult InitializeEx(ref InitializeExArguments arguments)
	{
		Logger.Stub?.PrintStub(LogClass.ServiceNv, "", "InitializeEx");
		return NvInternalResult.Success;
	}

	private NvInternalResult Remap(Span<RemapArguments> arguments)
	{
		MemoryManager gmm = _asContext.Gmm;
		for (int i = 0; i < arguments.Length; i++)
		{
			ref RemapArguments reference = ref arguments[i];
			ulong va = (ulong)reference.GpuOffset << 16;
			ulong size = (ulong)reference.Pages << 16;
			int nvMapHandle = reference.NvMapHandle;
			if (nvMapHandle == 0)
			{
				gmm.Unmap(va, size);
				continue;
			}
			ulong num = (ulong)reference.MapOffset << 16;
			PteKind kind = (PteKind)reference.Kind;
			NvMapHandle mapFromHandle = NvMapDeviceFile.GetMapFromHandle(Owner, nvMapHandle);
			if (mapFromHandle == null)
			{
				Logger.Warning?.Print(LogClass.ServiceNv, $"Invalid NvMap handle 0x{nvMapHandle:x8}!", "Remap");
				return NvInternalResult.InvalidInput;
			}
			gmm.Map(num + mapFromHandle.Address, va, size, kind);
		}
		return NvInternalResult.Success;
	}

	public override void Close()
	{
	}
}
