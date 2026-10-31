using System.Collections.Generic;
using Ryujinx.Graphics.GAL;

namespace Ryujinx.Graphics.Gpu.Memory;

internal class CounterCache
{
	private readonly struct CounterEntry
	{
		public ulong Address { get; }

		public ICounterEvent Event { get; }

		public CounterEntry(ulong address, ICounterEvent evt)
		{
			Address = address;
			Event = evt;
		}
	}

	private readonly List<CounterEntry> _items;

	public CounterCache()
	{
		_items = new List<CounterEntry>();
	}

	public void AddOrUpdate(ulong gpuVa, ICounterEvent evt)
	{
		int num = BinarySearch(gpuVa);
		CounterEntry counterEntry = new CounterEntry(gpuVa, evt);
		if (num < 0)
		{
			_items.Insert(~num, counterEntry);
		}
		else
		{
			_items[num] = counterEntry;
		}
	}

	public void MemoryUnmappedHandler(object sender, UnmapEventArgs e)
	{
		RemoveRange(e.Address, e.Size);
	}

	private void RemoveRange(ulong gpuVa, ulong size)
	{
		if (size == 0L)
		{
			return;
		}
		int num = BinarySearch(gpuVa);
		if (num < 0)
		{
			num = ~num;
		}
		if (num >= _items.Count || !InRange(gpuVa, size, _items[num].Address))
		{
			return;
		}
		int i;
		for (i = 1; num + i < _items.Count && InRange(gpuVa, size, _items[num + i].Address); i++)
		{
		}
		for (int j = 0; j < i; j++)
		{
			ICounterEvent counterEvent = _items[num + j].Event;
			if (counterEvent != null)
			{
				counterEvent.Invalid = true;
			}
		}
		_items.RemoveRange(num, i);
	}

	private static bool InRange(ulong startVa, ulong size, ulong gpuVa)
	{
		if (gpuVa >= startVa)
		{
			return gpuVa - startVa < size;
		}
		return false;
	}

	public bool Contains(ulong gpuVa)
	{
		return BinarySearch(gpuVa) >= 0;
	}

	public bool FindAndFlush(ulong gpuVa)
	{
		int num = BinarySearch(gpuVa);
		if (num >= 0)
		{
			_items[num].Event?.Flush();
			return true;
		}
		return false;
	}

	public ICounterEvent FindEvent(ulong gpuVa)
	{
		int num = BinarySearch(gpuVa);
		if (num >= 0)
		{
			return _items[num].Event;
		}
		return null;
	}

	private int BinarySearch(ulong address)
	{
		int num = 0;
		int num2 = _items.Count - 1;
		while (num <= num2)
		{
			int num3 = num2 - num;
			int num4 = num + (num3 >> 1);
			CounterEntry counterEntry = _items[num4];
			if (counterEntry.Address == address)
			{
				return num4;
			}
			if (address < counterEntry.Address)
			{
				num2 = num4 - 1;
			}
			else
			{
				num = num4 + 1;
			}
		}
		return ~num;
	}
}
