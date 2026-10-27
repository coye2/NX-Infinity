using System;
using System.CodeDom.Compiler;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using ARMeilleure.CodeGen.Unwinding;

namespace ARMeilleure.Translation.Cache;

internal static partial class JitUnwindWindows
{
	private struct RuntimeFunction
	{
		public uint BeginAddress;

		public uint EndAddress;

		public uint UnwindData;
	}

	private struct UnwindInfo
	{
		public byte VersionAndFlags;

		public byte SizeOfProlog;

		public byte CountOfUnwindCodes;

		public byte FrameRegister;

		public unsafe fixed ushort UnwindCodes[32];
	}

	private enum UnwindOp
	{
		PushNonvol = 0,
		AllocLarge = 1,
		AllocSmall = 2,
		SetFpreg = 3,
		SaveNonvol = 4,
		SaveNonvolFar = 5,
		SaveXmm128 = 8,
		SaveXmm128Far = 9,
		PushMachframe = 10
	}

	private unsafe delegate RuntimeFunction* GetRuntimeFunctionCallback(ulong controlPc, nint context);

	private const int MaxUnwindCodesArraySize = 32;

	private unsafe static readonly GetRuntimeFunctionCallback _getRuntimeFunctionCallback = FunctionTableHandler;

	public unsafe static int FunctionTableSize => sizeof(RuntimeFunction) + sizeof(UnwindInfo);

	[LibraryImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool RtlInstallFunctionTableCallback(ulong tableIdentifier, ulong baseAddress, uint length, GetRuntimeFunctionCallback callback, nint context, [MarshalAs(UnmanagedType.LPWStr)] string outOfProcessCallbackDll);

	[LibraryImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool RtlDeleteFunctionTable(ulong tableIdentifier);

	public static void InstallFunctionTableHandler(nint codeCachePointer, uint codeCacheLength)
	{
		ulong num = (ulong)((IntPtr)codeCachePointer).ToInt64();
		if (!RtlInstallFunctionTableCallback(num | 3, num, codeCacheLength, _getRuntimeFunctionCallback, codeCachePointer, null))
		{
			throw new InvalidOperationException("Failure installing function table callback.");
		}
	}

	public static void RemoveFunctionTableHandler(nint codeCachePointer)
	{
		if (!RtlDeleteFunctionTable((ulong)(((IntPtr)codeCachePointer).ToInt64() | 3)))
		{
			throw new InvalidOperationException("Failure removing function table callback.");
		}
	}

	private unsafe static RuntimeFunction* FunctionTableHandler(ulong controlPc, nint context)
	{
		int offset = (int)((long)controlPc - ((IntPtr)context).ToInt64());
		if (!JitCache.TryFind(context, offset, out var entry, out var _))
		{
			return null;
		}
		return (RuntimeFunction*)(context + entry.Offset + JitCache.AlignCodeSize(entry.Size));
	}

	public unsafe static void WriteFunctionTable(nint workBuffer, int offset, int size, ARMeilleure.CodeGen.Unwinding.UnwindInfo unwindInfo)
	{
		UnwindInfo* ptr = (UnwindInfo*)(workBuffer + sizeof(RuntimeFunction));
		*(RuntimeFunction*)workBuffer = default;
		*ptr = default;
		int num = 0;
		for (int num2 = unwindInfo.PushEntries.Length - 1; num2 >= 0; num2--)
		{
			UnwindPushEntry unwindPushEntry = unwindInfo.PushEntries[num2];
			switch (unwindPushEntry.PseudoOp)
			{
			case UnwindPseudoOp.SaveXmm128:
			{
				int stackOffsetOrAllocSize2 = unwindPushEntry.StackOffsetOrAllocSize;
				if (stackOffsetOrAllocSize2 <= 1048560)
				{
					ptr->UnwindCodes[num++] = PackUnwindOp(UnwindOp.SaveXmm128, unwindPushEntry.PrologOffset, unwindPushEntry.RegIndex);
					ptr->UnwindCodes[num++] = (ushort)(stackOffsetOrAllocSize2 / 16);
				}
				else
				{
					ptr->UnwindCodes[num++] = PackUnwindOp(UnwindOp.SaveXmm128Far, unwindPushEntry.PrologOffset, unwindPushEntry.RegIndex);
					ptr->UnwindCodes[num++] = (ushort)stackOffsetOrAllocSize2;
					ptr->UnwindCodes[num++] = (ushort)(stackOffsetOrAllocSize2 >> 16);
				}
				break;
			}
			case UnwindPseudoOp.AllocStack:
			{
				int stackOffsetOrAllocSize = unwindPushEntry.StackOffsetOrAllocSize;
				if (stackOffsetOrAllocSize <= 128)
				{
					ptr->UnwindCodes[num++] = PackUnwindOp(UnwindOp.AllocSmall, unwindPushEntry.PrologOffset, stackOffsetOrAllocSize / 8 - 1);
				}
				else if (stackOffsetOrAllocSize <= 524280)
				{
					ptr->UnwindCodes[num++] = PackUnwindOp(UnwindOp.AllocLarge, unwindPushEntry.PrologOffset, 0);
					ptr->UnwindCodes[num++] = (ushort)(stackOffsetOrAllocSize / 8);
				}
				else
				{
					ptr->UnwindCodes[num++] = PackUnwindOp(UnwindOp.AllocLarge, unwindPushEntry.PrologOffset, 1);
					ptr->UnwindCodes[num++] = (ushort)stackOffsetOrAllocSize;
					ptr->UnwindCodes[num++] = (ushort)(stackOffsetOrAllocSize >> 16);
				}
				break;
			}
			case UnwindPseudoOp.PushReg:
				ptr->UnwindCodes[num++] = PackUnwindOp(UnwindOp.PushNonvol, unwindPushEntry.PrologOffset, unwindPushEntry.RegIndex);
				break;
			default:
				throw new NotImplementedException($"({"PseudoOp"} = {unwindPushEntry.PseudoOp})");
			}
		}
		ptr->VersionAndFlags = 1;
		ptr->SizeOfProlog = (byte)unwindInfo.PrologSize;
		ptr->CountOfUnwindCodes = (byte)num;
		ptr->FrameRegister = 0;
		((RuntimeFunction*)workBuffer)->BeginAddress = (uint)offset;
		((RuntimeFunction*)workBuffer)->EndAddress = (uint)(offset + size);
		((RuntimeFunction*)workBuffer)->UnwindData = (uint)(offset + JitCache.AlignCodeSize(size) + sizeof(RuntimeFunction));
	}

	private static ushort PackUnwindOp(UnwindOp op, int prologOffset, int opInfo)
	{
		return (ushort)(prologOffset | ((int)op << 8) | (opInfo << 12));
	}
}
