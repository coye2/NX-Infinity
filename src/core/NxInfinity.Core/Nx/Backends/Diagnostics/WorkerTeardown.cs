using System;
using System.Runtime.ExceptionServices;

namespace Nx.Backends.Diagnostics;

internal sealed class WorkerTeardown
{
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;

    private bool _entered;

    private bool _completed;

    private ExceptionDispatchInfo? _failure;

    public bool Failed => _failure != null;

    public bool Completed => _completed;

    public void Run(Action release)
    {
        if (Environment.CurrentManagedThreadId != _ownerThread)
        {
            throw new InvalidOperationException("Native teardown must run on the session owner thread.");
        }
        _failure?.Throw();
        if (_completed)
        {
            return;
        }
        if (_entered)
        {
            throw new InvalidOperationException("Native teardown was reentered before completion.");
        }
        _entered = true;
        try
        {
            release();
            _completed = true;
        }
        catch (Exception source)
        {
            _failure = ExceptionDispatchInfo.Capture(source);
            throw;
        }
    }
}
