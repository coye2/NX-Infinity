using System;
using System.Collections.Generic;
using System.Threading;
using Ryujinx.Common;
using Ryujinx.Graphics.GAL.Multithreading.Resources.Programs;

namespace Ryujinx.Graphics.GAL.Multithreading.Resources;

internal class ProgramQueue
{
	private const int MaxConcurrentCompilations = 8;

	private readonly IRenderer _renderer;

	private readonly Queue<IProgramRequest> _toCompile;

	private readonly List<ThreadedProgram> _inProgress;

	public ProgramQueue(IRenderer renderer)
	{
		_renderer = renderer;
		_toCompile = new Queue<IProgramRequest>();
		_inProgress = new List<ThreadedProgram>();
	}

	public void Add(IProgramRequest request)
	{
		lock (_toCompile)
		{
			_toCompile.Enqueue(request);
		}
	}

	public void ProcessQueue()
	{
		for (int i = 0; i < _inProgress.Count; i++)
		{
			ThreadedProgram threadedProgram = _inProgress[i];
			if (threadedProgram.Base.CheckProgramLink(blocking: false) != ProgramLinkStatus.Incomplete)
			{
				threadedProgram.Compiled = true;
				_inProgress.RemoveAt(i--);
			}
		}
		int num = 8 - _inProgress.Count;
		for (int j = 0; j < num; j++)
		{
			IProgramRequest result;
			lock (_toCompile)
			{
				if (!_toCompile.TryDequeue(out result))
				{
					break;
				}
			}
			if (result.Threaded.Base != null)
			{
				if (result.Threaded.Base.CheckProgramLink(blocking: false) != ProgramLinkStatus.Incomplete)
				{
					result.Threaded.Compiled = true;
					j--;
					continue;
				}
			}
			else
			{
				result.Threaded.Base = result.Create(_renderer);
			}
			_inProgress.Add(result.Threaded);
		}
	}

	public void WaitForProgram(ThreadedProgram program)
	{
		if (program.Compiled)
		{
			return;
		}
		using (RyuStutterTrace.Measure("pipeline.threaded.consumer_wait", 0L))
		{
			Span<SpinWait> span = stackalloc SpinWait[1];
			while (!program.Compiled)
			{
				ProcessQueue();
				if (!program.Compiled)
				{
					span[0].SpinOnce(-1);
				}
			}
		}
	}
}
