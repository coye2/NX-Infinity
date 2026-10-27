using System;
using System.Diagnostics;
using ARMeilleure.State;

namespace Ryujinx.Cpu;

public class TickSource : ITickSource, ICounter
{
	private readonly RyuGuestClock _clock = new RyuGuestClock();

	public ulong Frequency { get; }

	public ulong Counter => (ulong)(ElapsedSeconds * (double)Frequency);

	public TimeSpan ElapsedTime => TimeSpan.FromSeconds(ElapsedSeconds);

	public double ElapsedSeconds => _clock.ElapsedTicks / (double)Stopwatch.Frequency;

	public TickSource(ulong frequency)
	{
		Frequency = frequency;
	}

	public void Suspend()
	{
		_clock.Suspend();
	}

	public void Resume()
	{
		_clock.Resume();
	}
}
