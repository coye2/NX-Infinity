using System;

namespace Ryujinx.Graphics.Gpu.Image;

internal static class TextureCacheBudget
{
	private const ulong GiB = 1073741824uL;

	internal static ulong Calculate(ulong gpuMemory, ulong hostMemory)
	{
		ulong num = hostMemory / 1073741824;
		ulong num2;
		switch (num)
		{
		default:
		{
			ulong num3 = num - 6;
			if (num3 > 4)
			{
				goto IL_0085;
			}
			switch ((uint)num3)
			{
			case 0u:
				break;
			case 1u:
			case 2u:
				goto IL_006d;
			case 3u:
			case 4u:
				goto IL_0079;
			default:
				goto IL_0085;
			}
			num2 = 4294967296uL;
			break;
		}
		case 0uL:
			num2 = 1073741824uL;
			break;
		case 1uL:
		case 2uL:
		case 3uL:
		case 4uL:
		case 5uL:
			{
				num2 = ((gpuMemory < 6442450944L) ? 1073741824 : 2147483648u);
				break;
			}
			IL_0085:
			num2 = 12884901888uL;
			break;
			IL_0079:
			num2 = 10737418240uL;
			break;
			IL_006d:
			num2 = 6442450944uL;
			break;
		}
		ulong val = num2;
		if (gpuMemory != 0L)
		{
			return Math.Min(gpuMemory / 2, val);
		}
		return Math.Min(1073741824uL, val);
	}
}
