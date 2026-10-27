using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Nx.Backends.Diagnostics;
using Ryujinx.Common.Logging;

namespace NxInfinity.Core;

internal static class Program
{
    private static int _diagnosticDescriptor = -1;

    [DllImport("libc", EntryPoint = "dup", SetLastError = true)]
    private static extern int DuplicateDescriptor(int descriptor);

    [DllImport("libc", EntryPoint = "dup2", SetLastError = true)]
    private static extern int RestoreDescriptor(int descriptor, int target);

    public static int Main()
    {
        Protocol protocol = new Protocol();
        try
        {
            using TextWriter textWriter = OpenDiagnostics();
            WorkerDiagnostics evidence = new WorkerDiagnostics(textWriter, "nxi");
            try
            {
                TextWriter textWriter2 = Console.Out;
                TextWriter error = Console.Error;
                CoreSession session = null;
                int result = 0;
                bool flag = false;
                EventHandler<LogEventArgs> value = (object? _, LogEventArgs e) =>
                {
                    evidence.CoreLog(e.Level.ToString(), e.Message, e.ThreadName, e.Data, e.Time);
                };
                UnhandledExceptionEventHandler value2 = (object _, UnhandledExceptionEventArgs e) =>
                {
                    if (e.ExceptionObject is Exception error6)
                    {
                        evidence.Exception("host", "unhandled", session?.DiagnosticState ?? "created", error6);
                    }
                };
                EventHandler<UnobservedTaskExceptionEventArgs> value3 = (object? _, UnobservedTaskExceptionEventArgs e) =>
                {
                    evidence.Exception("host", "task.unobserved", session?.DiagnosticState ?? "created", e.Exception);
                };
                AppDomain.CurrentDomain.UnhandledException += value2;
                TaskScheduler.UnobservedTaskException += value3;
                try
                {
                    Console.SetOut(textWriter);
                    Logger.RemoveTarget("console");
                    Logger.SetEnable(LogLevel.Guest, enabled: true);
                    Logger.SetEnable(LogLevel.Trace, enabled: false);
                    if (_diagnosticDescriptor >= 0 && RestoreDescriptor(_diagnosticDescriptor, 2) < 0)
                    {
                        throw new IOException("Unable to restore native stderr.");
                    }
                    Console.SetError(textWriter);
                    Logger.Updated += value;
                    flag = true;
                    session = new CoreSession(protocol, evidence);
                    protocol.ImmediateRequest = (Request request) =>
                    {
                        try
                        {
                            protocol.Success(request.Id, session.Interactions.Respond(request.Params));
                        }
                        catch (NxError nxError2)
                        {
                            protocol.Failure(request.Id, nxError2.Code, nxError2.Message);
                        }
                        catch (Exception error6)
                        {
                            evidence.Exception("protocol", "interaction.respond", session.DiagnosticState, error6);
                            protocol.Failure(request.Id, "core_error", "Interaction response failed. See local stderr.");
                        }
                    };
                    protocol.InputClosed = () =>
                    {
                        try
                        {
                            session.Interactions.CancelAll();
                        }
                        catch (Exception error6)
                        {
                            evidence.Exception("protocol", "input.closed", session.DiagnosticState, error6);
                        }
                    };
                    protocol.StartReader();
                    while (!session.Disposed)
                    {
                        if (protocol.Requests.TryTake(out var item, (!session.PendingWindowEvents) ? 2 : 0))
                        {
                            try
                            {
                                protocol.Success(item.Id, session.Dispatch(item.Method, item.Params));
                            }
                            catch (NxError nxError)
                            {
                                protocol.Failure(item.Id, nxError.Code, nxError.Message);
                            }
                            catch (DllNotFoundException error2)
                            {
                                evidence.Exception("host", "dispatch", session.DiagnosticState, error2);
                                protocol.Failure(item.Id, "missing_dependency", "A required native core, SDL3, Vulkan, or audio library is unavailable. See the local dependency manifest.");
                            }
                            catch (Exception ex)
                            {
                                evidence.Exception("host", "dispatch", session.DiagnosticState, ex);
                                protocol.Failure(item.Id, "core_error", "Core operation failed (" + ex.GetType().Name + "). See local stderr.");
                            }
                        }
                        if (session.RequiresProcessExit)
                        {
                            result = 1;
                            break;
                        }
                        session.Pump();
                        if (!protocol.EndOfInput && session.ProgramTransitionPending)
                        {
                            session = session.CreateProgramSuccessor();
                            session.BootProgramSuccessor();
                        }
                        if (protocol.EndOfInput && protocol.Requests.Count == 0)
                        {
                            break;
                        }
                    }
                }
                catch (Exception error3)
                {
                    evidence.Exception("host", "run.failed", session?.DiagnosticState ?? "created", error3);
                    result = 1;
                }
                finally
                {
                    if (session != null && !session.RequiresProcessExit)
                    {
                        try
                        {
                            session.Dispose();
                        }
                        catch (Exception error4)
                        {
                            evidence.Exception("host", "dispose.failed", session.DiagnosticState, error4);
                            result = 1;
                        }
                    }
                    CoreSession coreSession = session;
                    if (coreSession != null && coreSession.RequiresProcessExit)
                    {
                        evidence.Trace("host", "exit.required", session.DiagnosticState, "exitCode=1 profileLease=retained");
                        Environment.Exit(1);
                    }
                    try
                    {
                        protocol.Dispose();
                    }
                    catch (Exception error5)
                    {
                        evidence.Exception("protocol", "dispose.failed", session?.DiagnosticState ?? "created", error5);
                        result = 1;
                    }
                    if (flag)
                    {
                        Logger.Updated -= value;
                    }
                    AppDomain.CurrentDomain.UnhandledException -= value2;
                    TaskScheduler.UnobservedTaskException -= value3;
                    evidence.Dispose();
                    Console.SetOut(textWriter2);
                    Console.SetError(error);
                }
                return result;
            }
            finally
            {
                if (evidence != null)
                {
                    ((IDisposable)evidence).Dispose();
                }
            }
        }
        finally
        {
            if (protocol != null)
            {
                ((IDisposable)protocol).Dispose();
            }
        }
    }

    private static TextWriter OpenDiagnostics()
    {
        Stream stream;
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            int num = DuplicateDescriptor(2);
            if (num < 0)
            {
                throw new IOException("Unable to duplicate stderr.");
            }
            _diagnosticDescriptor = num;
            stream = new FileStream(new SafeFileHandle(num, ownsHandle: true), FileAccess.Write);
        }
        else
        {
            stream = Console.OpenStandardError();
        }
        return TextWriter.Synchronized(new StreamWriter(stream)
        {
            AutoFlush = true
        });
    }
}
