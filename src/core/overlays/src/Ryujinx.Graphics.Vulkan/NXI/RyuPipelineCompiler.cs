using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ryujinx.Common;

namespace Ryujinx.Graphics.Vulkan;

internal sealed class RyuPipelineCompiler : IDisposable
{
	private sealed record Job(Func<Task> Compile, TaskCompletionSource<bool> Completion, long Submitted, long Identity);

	private readonly Queue<Job> _queue = new Queue<Job>();

	private readonly int _capacity;

	private readonly Thread[] _threads;

	private int _disposed;

	public RyuPipelineCompiler(int workers = 0, int capacity = 128)
	{
		if (workers == 0)
		{
			workers = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
		}
		if (workers < 1 || capacity < 1)
		{
			throw new ArgumentOutOfRangeException();
		}
		_capacity = capacity;
		_threads = new Thread[workers];
		for (int i = 0; i < workers; i++)
		{
			_threads[i] = new Thread(Run)
			{
				IsBackground = true,
				Name = "NX Vulkan compiler " + i
			};
			_threads[i].Start();
		}
	}

	public Task Enqueue(Func<Task> compile, long identity)
	{
		ArgumentNullException.ThrowIfNull(compile, "compile");
		ObjectDisposedException.ThrowIf(Volatile.Read(in _disposed) != 0, this);
		TaskCompletionSource<bool> taskCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		using (RyuStutterTrace.Measure("pipeline.enqueue.backpressure", identity))
		{
			lock (_queue)
			{
				while (_queue.Count >= _capacity && _disposed == 0)
				{
					Monitor.Wait(_queue);
				}
				ObjectDisposedException.ThrowIf(_disposed != 0, this);
				_queue.Enqueue(new Job(compile, taskCompletionSource, RyuStutterTrace.Timestamp(), identity));
				Monitor.PulseAll(_queue);
			}
		}
		return taskCompletionSource.Task;
	}

	private void Run()
	{
		while (true)
		{
			Job job;
			lock (_queue)
			{
				while (_queue.Count == 0 && _disposed == 0)
				{
					Monitor.Wait(_queue);
				}
				if (_queue.Count == 0)
				{
					break;
				}
				job = _queue.Dequeue();
				Monitor.PulseAll(_queue);
			}
			RyuStutterTrace.Interval("pipeline.queue", job.Submitted, job.Identity);
			try
			{
				job.Compile().GetAwaiter().GetResult();
				job.Completion.SetResult(result: true);
			}
			catch (Exception exception)
			{
				job.Completion.SetException(exception);
			}
		}
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) == 0)
		{
			lock (_queue)
			{
				Monitor.PulseAll(_queue);
			}
			Thread[] threads = _threads;
			for (int i = 0; i < threads.Length; i++)
			{
				threads[i].Join();
			}
		}
	}
}
