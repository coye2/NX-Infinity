using Ryujinx.Graphics.GAL.Multithreading.Model;
using Ryujinx.Graphics.GAL.Multithreading.Resources;

namespace Ryujinx.Graphics.GAL.Multithreading.Commands.Program;

internal struct ProgramCheckLinkCommand : IGALCommand, IGALCommand<ProgramCheckLinkCommand>
{
	private TableRef<ThreadedProgram> _program;

	private bool _blocking;

	private TableRef<ResultBox<ProgramLinkStatus>> _result;

	public readonly CommandType CommandType => CommandType.ProgramCheckLink;

	public void Set(TableRef<ThreadedProgram> program, bool blocking, TableRef<ResultBox<ProgramLinkStatus>> result)
	{
		_program = program;
		_blocking = blocking;
		_result = result;
	}

	public static void Run(ref ProgramCheckLinkCommand command, ThreadedRenderer threaded, IRenderer renderer)
	{
		ThreadedProgram threadedProgram = command._program.Get(threaded);
		ProgramLinkStatus programLinkStatus = threadedProgram.Base.CheckProgramLink(command._blocking);
		ResultBox<ProgramLinkStatus> resultBox = command._result.Get(threaded);
		if (resultBox != null)
		{
			resultBox.Result = programLinkStatus;
		}
		else
		{
			threadedProgram.CompleteLinkPoll(programLinkStatus);
		}
	}
}
