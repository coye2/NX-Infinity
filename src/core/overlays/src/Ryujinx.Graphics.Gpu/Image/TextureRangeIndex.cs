using System;
using System.Collections.Generic;
using Ryujinx.Memory.Range;

namespace Ryujinx.Graphics.Gpu.Image;

internal sealed class TextureRangeIndex
{
	private readonly MultiRange _range;

	private readonly ulong[] _ends;

	public TextureRangeIndex(MultiRange range)
	{
		_range = range;
		_ends = new ulong[range.Count];
		ulong num = 0uL;
		for (int i = 0; i < _ends.Length; i++)
		{
			num = (_ends[i] = checked(num + range.GetSubRange(i).Size));
		}
	}

	private int Find(ulong offset)
	{
		int num = 0;
		int num2 = _ends.Length;
		while (num < num2)
		{
			int num3 = num + (num2 - num) / 2;
			if (_ends[num3] <= offset)
			{
				num = num3 + 1;
			}
			else
			{
				num2 = num3;
			}
		}
		return num;
	}

	private MemoryRange At(int index, ulong offset, ulong size)
	{
		MemoryRange subRange = _range.GetSubRange(index);
		ulong num = ((index == 0) ? 0 : _ends[index - 1]);
		ulong num2 = offset - num;
		return new MemoryRange((subRange.Address == ulong.MaxValue) ? ulong.MaxValue : (subRange.Address + num2), Math.Min(size, subRange.Size - num2));
	}

	public IEnumerable<MemoryRange> Slice(ulong offset, ulong size)
	{
		int index = Find(offset);
		while (size != 0L && index < _ends.Length)
		{
			MemoryRange range = At(index++, offset, size);
			if (range.Size != 0L)
			{
				yield return range;
				offset += range.Size;
				size -= range.Size;
			}
		}
		if (size != 0L)
		{
			throw new ArgumentOutOfRangeException("size");
		}
	}

	public bool Matches(TextureRangeIndex other, ulong offset, ulong size)
	{
		int i = Find(offset);
		int j = other.Find(offset);
		while (size != 0L)
		{
			if (i >= _ends.Length || j >= other._ends.Length)
			{
				return false;
			}
			MemoryRange memoryRange = At(i, offset, size);
			MemoryRange memoryRange2 = other.At(j, offset, size);
			if (memoryRange.Address != memoryRange2.Address)
			{
				return false;
			}
			ulong num = Math.Min(memoryRange.Size, memoryRange2.Size);
			if (num == 0L)
			{
				return false;
			}
			offset += num;
			size -= num;
			for (; i < _ends.Length && _ends[i] <= offset; i++)
			{
			}
			for (; j < other._ends.Length && other._ends[j] <= offset; j++)
			{
			}
		}
		return true;
	}
}
