using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using ARMeilleure.CodeGen;
using ARMeilleure.CodeGen.Unwinding;
using ARMeilleure.Memory;
using ARMeilleure.Native;
using Humanizer;
using Ryujinx.Common.Logging;
using Ryujinx.Memory;

namespace ARMeilleure.Translation.Cache;

internal static partial class JitCache
{
	private static readonly int _pageSize = (int)MemoryBlock.GetPageSize();

	private static readonly int _pageMask = _pageSize - 1;

	private const int CodeAlignment = 4;

	private const int CacheSize = 268435456;

	private static JitCacheInvalidation _jitCacheInvalidator;

	private static List<CacheMemoryAllocator> _cacheAllocators = new List<CacheMemoryAllocator>();

	private static readonly List<List<CacheEntry>> _cacheEntries = new List<List<CacheEntry>>();

	private static readonly Lock _lock = new Lock();

	private static bool _initialized;

	private static readonly List<ReservedRegion> _jitRegions = new List<ReservedRegion>();

	private static int _activeRegionIndex = 0;

	[SupportedOSPlatform("windows")]
	[LibraryImport("kernel32.dll", SetLastError = true)]
	public static partial IntPtr FlushInstructionCache(IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize);

	public static void Initialize(IJitMemoryAllocator allocator)
	{
		using (_lock.EnterScope())
		{
			if (_initialized)
			{
				if (OperatingSystem.IsWindows())
				{
					foreach (ReservedRegion jitRegion in _jitRegions)
					{
						JitUnwindWindows.RemoveFunctionTableHandler(jitRegion.Pointer);
					}
				}
				for (int i = 0; i < _jitRegions.Count; i++)
				{
					_jitRegions[i].Dispose();
				}
				_jitRegions.Clear();
				_cacheAllocators.Clear();
				_cacheEntries.Clear();
			}
			else
			{
				_initialized = true;
			}
			_activeRegionIndex = 0;
			ReservedRegion reservedRegion = new ReservedRegion(allocator, 268435456uL, 0uL);
			_jitRegions.Add(reservedRegion);
			CacheMemoryAllocator item = new CacheMemoryAllocator(268435456);
			_cacheAllocators.Add(item);
			_cacheEntries.Add(new List<CacheEntry>());
			if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
			{
				_jitCacheInvalidator = new JitCacheInvalidation(allocator);
			}
			if (OperatingSystem.IsWindows())
			{
				JitUnwindWindows.InstallFunctionTableHandler(reservedRegion.Pointer, 268435456u);
			}
		}
	}

	public unsafe static nint Map(CompiledFunction func)
	{
		byte[] code = func.Code;
		using (_lock.EnterScope())
		{
			int num = AllocationSize(code.Length);
			int num2 = Allocate(num);
			ReservedRegion reservedRegion = _jitRegions[_activeRegionIndex];
			nint num3 = reservedRegion.Pointer + num2;
			if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
			{
				fixed (byte* src = code)
				{
					JitSupportDarwin.Copy(num3, (nint)src, (ulong)code.Length);
				}
			}
			else
			{
				ReprotectAsWritable(reservedRegion, num2, num);
				Marshal.Copy(code, 0, num3, code.Length);
				if (OperatingSystem.IsWindows())
				{
					JitUnwindWindows.WriteFunctionTable(num3 + AlignCodeSize(code.Length), num2, code.Length, func.UnwindInfo);
				}
				ReprotectAsExecutable(reservedRegion, num2, num);
				if (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
				{
					FlushInstructionCache(Process.GetCurrentProcess().Handle, num3, (nuint)code.Length);
				}
				else
				{
					_jitCacheInvalidator?.Invalidate(num3, (ulong)code.Length);
				}
			}
			Add(num2, code.Length, func.UnwindInfo);
			return num3;
		}
	}

	public static void Unmap(nint pointer)
	{
		using (_lock.EnterScope())
		{
			for (int i = 0; i < _jitRegions.Count; i++)
			{
				ReservedRegion reservedRegion = _jitRegions[i];
				if (((IntPtr)pointer).ToInt64() >= ((IntPtr)reservedRegion.Pointer).ToInt64() && ((IntPtr)pointer).ToInt64() < ((IntPtr)(reservedRegion.Pointer + 268435456)).ToInt64())
				{
					int num = (int)(((IntPtr)pointer).ToInt64() - ((IntPtr)reservedRegion.Pointer).ToInt64());
					if (TryFind(reservedRegion.Pointer, num, out var entry, out var entryIndex) && entry.Offset == num)
					{
						_cacheAllocators[i].Free(num, AllocationSize(entry.Size));
						_cacheEntries[i].RemoveAt(entryIndex);
					}
					break;
				}
			}
		}
	}

	private static void ReprotectAsWritable(ReservedRegion region, int offset, int size)
	{
		int num = offset + size;
		int num2 = offset & ~_pageMask;
		int num3 = (num + _pageMask) & ~_pageMask;
		region.Block.MapAsRwx((ulong)num2, (ulong)(num3 - num2));
	}

	private static void ReprotectAsExecutable(ReservedRegion region, int offset, int size)
	{
		int num = offset + size;
		int num2 = offset & ~_pageMask;
		int num3 = (num + _pageMask) & ~_pageMask;
		region.Block.MapAsRx((ulong)num2, (ulong)(num3 - num2));
	}

	private static int Allocate(int codeSize)
	{
		codeSize = AlignCodeSize(codeSize);
		int num = _cacheAllocators[_activeRegionIndex].Allocate(codeSize);
		if (num >= 0)
		{
			_jitRegions[_activeRegionIndex].ExpandIfNeeded((ulong)num + (ulong)codeSize);
			return num;
		}
		int activeRegionIndex = _activeRegionIndex;
		ReservedRegion reservedRegion = new ReservedRegion(_jitRegions[0].Allocator, 268435456uL, 0uL);
		_jitRegions.Add(reservedRegion);
		_activeRegionIndex = _jitRegions.Count - 1;
		Logger.Warning?.Print(LogClass.Cpu, $"JIT Cache Region {activeRegionIndex} exhausted, creating new Cache Region {_activeRegionIndex} ({((long)(_activeRegionIndex + 1) * 268435456L).Bytes()} Total Allocation).", "Allocate");
		_cacheAllocators.Add(new CacheMemoryAllocator(268435456));
		_cacheEntries.Add(new List<CacheEntry>());
		if (OperatingSystem.IsWindows())
		{
			JitUnwindWindows.InstallFunctionTableHandler(reservedRegion.Pointer, 268435456u);
		}
		int num2 = _cacheAllocators[_activeRegionIndex].Allocate(codeSize);
		if (num2 < 0)
		{
			throw new OutOfMemoryException("Failed to allocate in new Cache Region!");
		}
		reservedRegion.ExpandIfNeeded((ulong)num2 + (ulong)codeSize);
		return num2;
	}

	private static int AllocationSize(int codeSize)
	{
		return checked(AlignCodeSize(codeSize) + (OperatingSystem.IsWindows() ? JitUnwindWindows.FunctionTableSize : 0));
	}

	internal static int AlignCodeSize(int codeSize)
	{
		return checked(codeSize + 3) & -4;
	}

	private static void Add(int offset, int size, UnwindInfo unwindInfo)
	{
		CacheEntry item = new CacheEntry(offset, size, unwindInfo);
		List<CacheEntry> list = _cacheEntries[_activeRegionIndex];
		int num = list.BinarySearch(item);
		if (num < 0)
		{
			num = ~num;
		}
		list.Insert(num, item);
	}

	public static bool TryFind(nint regionPointer, int offset, out CacheEntry entry, out int entryIndex)
	{
		using (_lock.EnterScope())
		{
			for (int i = 0; i < _jitRegions.Count; i++)
			{
				if (_jitRegions[i].Pointer == regionPointer)
				{
					List<CacheEntry> list = _cacheEntries[i];
					int num = list.BinarySearch(new CacheEntry(offset, 0, default));
					if (num < 0)
					{
						num = ~num - 1;
					}
					if (num >= 0 && (uint)(offset - list[num].Offset) < (uint)list[num].Size)
					{
						entry = list[num];
						entryIndex = num;
						return true;
					}
					break;
				}
			}
		}
		entry = default;
		entryIndex = 0;
		return false;
	}
}
