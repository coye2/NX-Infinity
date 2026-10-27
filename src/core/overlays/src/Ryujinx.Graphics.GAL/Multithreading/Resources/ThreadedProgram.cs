using System;
using System.Threading;
using Ryujinx.Graphics.GAL.Multithreading.Commands.Program;
using Ryujinx.Graphics.GAL.Multithreading.Model;

namespace Ryujinx.Graphics.GAL.Multithreading.Resources;

internal class ThreadedProgram : IProgram, IDisposable
{
	private readonly ThreadedRenderer _renderer;

	public IProgram Base;

	internal bool Compiled;

	private int _linkStatus;

	private int _linkPollPending;

	public ThreadedProgram(ThreadedRenderer renderer)
	{
		_renderer = renderer;
	}

	private TableRef<T> Ref<T>(T reference)
	{
		return new TableRef<T>(_renderer, reference);
	}

	public void Dispose()
	{
		_renderer.New<ProgramDisposeCommand>().Set(Ref(this));
		_renderer.QueueCommand();
	}

	public byte[] GetBinary()
	{
		ResultBox<byte[]> resultBox = new ResultBox<byte[]>();
		_renderer.New<ProgramGetBinaryCommand>().Set(Ref(this), Ref(resultBox));
		_renderer.InvokeCommand();
		return resultBox.Result;
	}

	public ProgramLinkStatus CheckProgramLink(bool blocking)
	{
		if (!blocking)
		{
			int num = Volatile.Read(in _linkStatus);
			if (num == 0 && Interlocked.Exchange(ref _linkPollPending, 1) == 0)
			{
				_renderer.New<ProgramCheckLinkCommand>().Set(Ref(this), blocking: false, Ref<ResultBox<ProgramLinkStatus>>(null));
				_renderer.QueueCommand();
			}
			return (ProgramLinkStatus)num;
		}
		ResultBox<ProgramLinkStatus> resultBox = new ResultBox<ProgramLinkStatus>();
		_renderer.New<ProgramCheckLinkCommand>().Set(Ref(this), blocking, Ref(resultBox));
		_renderer.InvokeCommand();
		return resultBox.Result;
	}

	internal void CompleteLinkPoll(ProgramLinkStatus status)
	{
		Volatile.Write(ref _linkStatus, (int)status);
		Volatile.Write(ref _linkPollPending, 0);
	}
}
