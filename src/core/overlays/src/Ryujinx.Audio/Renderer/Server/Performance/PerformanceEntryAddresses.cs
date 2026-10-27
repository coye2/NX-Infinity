using System;
using Ryujinx.Common;

namespace Ryujinx.Audio.Renderer.Server.Performance;

public class PerformanceEntryAddresses
{
	public static readonly ObjectPool<PerformanceEntryAddresses> PerformanceEntryAddressesPool = new ObjectPool<PerformanceEntryAddresses>(() => new PerformanceEntryAddresses(), 256);

	public Memory<int> BaseMemory;

	public uint StartTimeOffset;

	public uint EntryCountOffset;

	public uint ProcessingTimeOffset;

	public void IncrementEntryCount()
	{
		BaseMemory.Span[(int)EntryCountOffset / 4]++;
	}

	public void SetStartTime(ulong startTimeNano)
	{
		BaseMemory.Span[(int)StartTimeOffset / 4] = (int)(startTimeNano / 1000);
	}

	public void SetProcessingTime(ulong endTimeNano)
	{
		BaseMemory.Span[(int)ProcessingTimeOffset / 4] = (int)(endTimeNano / 1000) - BaseMemory.Span[(int)StartTimeOffset / 4];
	}

	public void Clear()
	{
	}
}
