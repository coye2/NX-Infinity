using System;
using System.Diagnostics;
using System.Threading;

namespace Ryujinx.Cpu;

internal sealed class RyuGuestClock
{
	private sealed record State(long Origin, double Elapsed, long Scale, bool Running);

	private readonly object _gate = new object();

	private readonly Func<long> _timestamp;

	private State _state;

	private int _version;

	public long Scale
	{
		get
		{
			return Volatile.Read(in _state).Scale;
		}
		set
		{
			ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, "value");
			lock (_gate)
			{
				State state = _state;
				if (state.Scale != value)
				{
					Change(state, value, state.Running);
				}
			}
		}
	}

	public double ElapsedTicks
	{
		get
		{
			SpinWait spinWait = default;
			double result;
			while (true)
			{
				int num = Volatile.Read(in _version);
				if ((num & 1) != 0)
				{
					spinWait.SpinOnce();
					continue;
				}
				result = At(Volatile.Read(in _state), _timestamp());
				if (num == Volatile.Read(in _version))
				{
					break;
				}
			}
			return result;
		}
	}

	public RyuGuestClock()
		: this(Stopwatch.GetTimestamp)
	{
	}

	internal RyuGuestClock(Func<long> timestamp)
	{
		_timestamp = timestamp;
		_state = new State(timestamp(), 0.0, 100L, Running: true);
	}

	public void Suspend()
	{
		SetRunning(running: false);
	}

	public void Resume()
	{
		SetRunning(running: true);
	}

	private void SetRunning(bool running)
	{
		lock (_gate)
		{
			State state = _state;
			if (state.Running != running)
			{
				Change(state, state.Scale, running);
			}
		}
	}

	private void Change(State state, long scale, bool running)
	{
		int num = Interlocked.Increment(ref _version);
		try
		{
			long num2 = _timestamp();
			Volatile.Write(ref _state, new State(num2, At(state, num2), scale, running));
		}
		finally
		{
			Volatile.Write(ref _version, num + 1);
		}
	}

	private static double At(State state, long now)
	{
		return state.Elapsed + (state.Running ? ((double)(now - state.Origin) * ((double)state.Scale / 100.0)) : 0.0);
	}
}
