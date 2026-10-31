using System;
using System.Diagnostics;
using System.Threading;

namespace Ryujinx.Common;

public static class RyuStutterTrace
{
	private struct ScopeState
	{
		public long Id;

		public long Start;

		public long Children;
	}

	public readonly record struct Sample(long Id, long ParentId, long StartTicks, long EndTicks, long? ExclusiveTicks, int ThreadId, long StartFrame, long EndFrame, string Operation, long Identity, string Key);

	public readonly struct Scope : IDisposable
	{
		private readonly long _id;

		private readonly long _parent;

		private readonly long _start;

		private readonly long _frame;

		private readonly long _identity;

		private readonly string _operation;

		private readonly string _key;

		private readonly int _depth;

		internal Scope(string operation, long identity, string key)
		{
			if (_stack == null)
			{
				_stack = new ScopeState[64];
			}
			if (RyuStutterTrace._depth >= _stack.Length)
			{
				this = default;
				return;
			}
			_id = Interlocked.Increment(ref _sequence);
			_parent = ((RyuStutterTrace._depth == 0) ? 0 : _stack[RyuStutterTrace._depth - 1].Id);
			_start = Stopwatch.GetTimestamp();
			_frame = Interlocked.Read(in RyuStutterTrace._frame);
			_identity = identity;
			_operation = operation;
			_key = key;
			_depth = RyuStutterTrace._depth++;
			_stack[_depth] = new ScopeState
			{
				Id = _id,
				Start = _start
			};
		}

		public void Dispose()
		{
			if (_id == 0L)
			{
				return;
			}
			long timestamp = Stopwatch.GetTimestamp();
			if (RyuStutterTrace._depth == _depth + 1 && _stack[_depth].Id == _id)
			{
				long num = timestamp - _start;
				long value = Math.Max(0L, num - _stack[_depth].Children);
				RyuStutterTrace._depth--;
				if (_depth > 0)
				{
					_stack[_depth - 1].Children += num;
				}
				Record(new Sample(_id, _parent, _start, timestamp, value, Environment.CurrentManagedThreadId, _frame, Interlocked.Read(in RyuStutterTrace._frame), _operation, _identity, _key));
			}
		}
	}

	public static readonly bool Enabled = Environment.GetEnvironmentVariable("NX_STUTTER_TRACE") == "1";

	private const int Capacity = 2048;

	private static readonly object Gate = new object();

	private static readonly Sample[] Ring = (Enabled ? new Sample[2048] : Array.Empty<Sample>());

	private static long _sequence;

	private static long _frame;

	private static long _overwritten;

	private static int _next;

	private static int _count;

	[ThreadStatic]
	private static ScopeState[] _stack;

	[ThreadStatic]
	private static int _depth;

	public static Scope Measure(string operation, long identity = 0L, string key = null)
	{
		if (!Enabled)
		{
			return default;
		}
		return new Scope(operation, identity, key);
	}

	public static long Timestamp()
	{
		if (!Enabled)
		{
			return 0L;
		}
		return Stopwatch.GetTimestamp();
	}

	public static void Interval(string operation, long start, long identity = 0L)
	{
		if (Enabled && start != 0L)
		{
			long timestamp = Stopwatch.GetTimestamp();
			long endFrame = Interlocked.Read(in _frame);
			Record(new Sample(Interlocked.Increment(ref _sequence), 0L, start, timestamp, null, Environment.CurrentManagedThreadId, -1L, endFrame, operation, identity, null));
		}
	}

	public static void Present()
	{
		if (Enabled)
		{
			long num = Interlocked.Increment(ref _frame);
			long timestamp = Stopwatch.GetTimestamp();
			Record(new Sample(Interlocked.Increment(ref _sequence), 0L, timestamp, timestamp, 0L, Environment.CurrentManagedThreadId, num, num, "present.return", 0L, null));
		}
	}

	private static void Record(Sample sample)
	{
		lock (Gate)
		{
			Ring[_next] = sample;
			_next = (_next + 1) % 2048;
			if (_count == 2048)
			{
				_overwritten++;
			}
			else
			{
				_count++;
			}
		}
	}

	public static object Snapshot()
	{
		lock (Gate)
		{
			Sample[] array = new Sample[_count];
			for (int i = 0; i < array.Length; i++)
			{
				array[i] = Ring[(_next - _count + i + 2048) % 2048];
			}
			return new
			{
				schemaVersion = 1,
				enabled = Enabled,
				frequency = Stopwatch.Frequency,
				source = "cpu_monotonic_intervals_not_gpu_or_scanout",
				capacity = 2048,
				overwritten = _overwritten,
				samples = array
			};
		}
	}
}
