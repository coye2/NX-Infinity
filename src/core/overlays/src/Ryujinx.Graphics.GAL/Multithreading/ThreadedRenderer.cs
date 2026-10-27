#nullable enable annotations

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Ryujinx.Common;
using Ryujinx.Common.Configuration;
using Ryujinx.Graphics.GAL.Multithreading.Commands;
using Ryujinx.Graphics.GAL.Multithreading.Commands.Buffer;
using Ryujinx.Graphics.GAL.Multithreading.Commands.Renderer;
using Ryujinx.Graphics.GAL.Multithreading.Model;
using Ryujinx.Graphics.GAL.Multithreading.Resources;
using Ryujinx.Graphics.GAL.Multithreading.Resources.Programs;

namespace Ryujinx.Graphics.GAL.Multithreading;

public class ThreadedRenderer : IRenderer, IDisposable
{
	private const int SpanPoolBytes = 4194304;

	private const int MaxRefsPerCommand = 2;

	private const int QueueCount = 10000;

	private readonly int _elementSize;

	private readonly IRenderer _baseRenderer;

	private Thread _gpuThread;

	private Thread _backendThread;

	private bool _running;

	private readonly AutoResetEvent _frameComplete = new AutoResetEvent(initialState: true);

	private readonly ManualResetEventSlim _galWorkAvailable;

	private readonly CircularSpanPool _spanPool;

	private readonly ManualResetEventSlim _invokeRun;

	private readonly AutoResetEvent _interruptRun;

	private bool _lastSampleCounterClear = true;

	private readonly byte[] _commandQueue;

	private readonly object[] _refQueue;

	private int _consumerPtr;

	private int _commandCount;

	private int _diagnosticCommand = -1;

	private int _producerPtr;

	private int _lastProducedPtr;

	private int _invokePtr;

	private int _refProducerPtr;

	private int _refConsumerPtr;

	private Action _interruptAction;

	private readonly Lock _interruptLock = new Lock();

	public uint ProgramCount { get; set; }

	internal BufferMap Buffers { get; }

	internal SyncMap Sync { get; }

	internal CircularSpanPool SpanPool { get; }

	internal ProgramQueue Programs { get; }

	public IPipeline Pipeline { get; }

	public IWindow Window { get; }

	public IRenderer BaseRenderer => _baseRenderer;

	public bool PreferThreading => _baseRenderer.PreferThreading;

	public event EventHandler<ScreenCaptureImageInfo> ScreenCaptured;

	public object CommandQueueSnapshot()
	{
		int num = Volatile.Read(in _diagnosticCommand);
		return new
		{
			enabled = RyuStutterTrace.Enabled,
			pending = Volatile.Read(in _commandCount),
			consumer = ((num < 0) ? null : ((CommandType)num/*cast due to constrained. prefix*/).ToString())
		};
	}

	public ThreadedRenderer(IRenderer renderer)
	{
		_baseRenderer = renderer;
		renderer.ScreenCaptured += (object? sender, ScreenCaptureImageInfo info) =>
		{
			ScreenCaptured?.Invoke(this, info);
		};
		renderer.SetInterruptAction(Interrupt);
		Pipeline = new ThreadedPipeline(this);
		Window = new ThreadedWindow(this, renderer);
		Buffers = new BufferMap();
		Sync = new SyncMap();
		Programs = new ProgramQueue(renderer);
		_galWorkAvailable = new ManualResetEventSlim(initialState: false);
		_invokeRun = new ManualResetEventSlim();
		_interruptRun = new AutoResetEvent(initialState: false);
		_spanPool = new CircularSpanPool(this, 4194304);
		SpanPool = _spanPool;
		_elementSize = BitUtils.AlignUp(CommandHelper.GetMaxCommandSize(), 4);
		_commandQueue = new byte[_elementSize * 10000];
		_refQueue = new object[20000];
	}

	public void RunLoop(ThreadStart gpuLoop)
	{
		_running = true;
		_backendThread = Thread.CurrentThread;
		_gpuThread = new Thread(gpuLoop)
		{
			Name = "GPU.MainThread"
		};
		_gpuThread.Start();
		RenderLoop();
	}

	public void RenderLoop()
	{
		while (_running)
		{
			_galWorkAvailable.Wait();
			_galWorkAvailable.Reset();
			if (Volatile.Read(in _interruptAction) != null)
			{
				_interruptAction?.Invoke();
				_interruptRun.Set();
				Interlocked.Exchange(ref _interruptAction, null);
			}
			while (Volatile.Read(in _commandCount) > 0 && Volatile.Read(in _interruptAction) == null)
			{
				int consumerPtr = _consumerPtr;
				Span<byte> memory = new Span<byte>(_commandQueue, consumerPtr * _elementSize, _elementSize);
				if (RyuStutterTrace.Enabled)
				{
					Volatile.Write(ref _diagnosticCommand, memory[memory.Length - 1]);
				}
				CommandHelper.RunCommand(memory, this, _baseRenderer);
				if (RyuStutterTrace.Enabled)
				{
					Volatile.Write(ref _diagnosticCommand, -1);
				}
				if (Interlocked.CompareExchange(ref _invokePtr, -1, consumerPtr) == consumerPtr)
				{
					_invokeRun.Set();
				}
				_consumerPtr = (_consumerPtr + 1) % 10000;
				Interlocked.Decrement(ref _commandCount);
			}
		}
	}

	internal SpanRef<T> CopySpan<T>(ReadOnlySpan<T> data) where T : unmanaged
	{
		return _spanPool.Insert(data);
	}

	private TableRef<T> Ref<T>(T reference)
	{
		return new TableRef<T>(this, reference);
	}

	internal ref T New<T>() where T : struct
	{
		while (_producerPtr == (Volatile.Read(in _consumerPtr) + 10000 - 1) % 10000)
		{
			Thread.Sleep(1);
		}
		int num = (_lastProducedPtr = _producerPtr);
		_producerPtr = (_producerPtr + 1) % 10000;
		Span<byte> span = new Span<byte>(_commandQueue, num * _elementSize, _elementSize);
		ref T reference = ref Unsafe.As<byte, T>(ref MemoryMarshal.GetReference(span));
		span[span.Length - 1] = (byte)((IGALCommand)(object)reference).CommandType;
		return ref reference;
	}

	internal int AddTableRef(object obj)
	{
		int refProducerPtr = _refProducerPtr;
		_refQueue[refProducerPtr] = obj;
		_refProducerPtr = (_refProducerPtr + 1) % _refQueue.Length;
		return refProducerPtr;
	}

	internal object RemoveTableRef(int index)
	{
		object result = _refQueue[_refConsumerPtr];
		_refQueue[_refConsumerPtr] = null;
		_refConsumerPtr = (_refConsumerPtr + 1) % _refQueue.Length;
		return result;
	}

	internal void QueueCommand()
	{
		if (Interlocked.Increment(ref _commandCount) == 1)
		{
			_galWorkAvailable.Set();
		}
	}

	internal void InvokeCommand()
	{
		_invokeRun.Reset();
		_invokePtr = _lastProducedPtr;
		QueueCommand();
		_invokeRun.Wait();
	}

	internal void WaitForFrame()
	{
		_frameComplete.WaitOne();
	}

	internal void SignalFrame()
	{
		_frameComplete.Set();
	}

	internal bool IsGpuThread()
	{
		return Thread.CurrentThread == _gpuThread;
	}

	public void BackgroundContextAction(Action action, bool alwaysBackground = false)
	{
		if (IsGpuThread() && !alwaysBackground)
		{
			New<ActionCommand>().Set(Ref(action));
			InvokeCommand();
		}
		else
		{
			_baseRenderer.BackgroundContextAction(action, alwaysBackground: true);
		}
	}

	public BufferHandle CreateBuffer(int size, BufferAccess access)
	{
		BufferHandle bufferHandle = Buffers.CreateBufferHandle();
		New<CreateBufferAccessCommand>().Set(bufferHandle, size, access);
		QueueCommand();
		return bufferHandle;
	}

	public BufferHandle CreateBuffer(nint pointer, int size)
	{
		BufferHandle bufferHandle = Buffers.CreateBufferHandle();
		New<CreateHostBufferCommand>().Set(bufferHandle, pointer, size);
		QueueCommand();
		return bufferHandle;
	}

	public BufferHandle CreateBufferSparse(ReadOnlySpan<BufferRange> storageBuffers)
	{
		BufferHandle bufferHandle = Buffers.CreateBufferHandle();
		New<CreateBufferSparseCommand>().Set(bufferHandle, CopySpan(storageBuffers));
		QueueCommand();
		return bufferHandle;
	}

	public IImageArray CreateImageArray(int size, bool isBuffer)
	{
		ThreadedImageArray threadedImageArray = new ThreadedImageArray(this);
		New<CreateImageArrayCommand>().Set(Ref(threadedImageArray), size, isBuffer);
		QueueCommand();
		return threadedImageArray;
	}

	public IProgram CreateProgram(ShaderSource[] shaders, ShaderInfo info)
	{
		ThreadedProgram threadedProgram = new ThreadedProgram(this);
		SourceProgramRequest sourceProgramRequest = new SourceProgramRequest(threadedProgram, shaders, info);
		Programs.Add(sourceProgramRequest);
		ProgramCount++;
		New<CreateProgramCommand>().Set(Ref((IProgramRequest)sourceProgramRequest));
		QueueCommand();
		return threadedProgram;
	}

	public ISampler CreateSampler(SamplerCreateInfo info)
	{
		ThreadedSampler threadedSampler = new ThreadedSampler(this);
		New<CreateSamplerCommand>().Set(Ref(threadedSampler), info);
		QueueCommand();
		return threadedSampler;
	}

	public void CreateSync(ulong id, bool strict)
	{
		Sync.CreateSyncHandle(id);
		New<CreateSyncCommand>().Set(id, strict);
		QueueCommand();
	}

	public ITexture CreateTexture(TextureCreateInfo info)
	{
		if (IsGpuThread())
		{
			ThreadedTexture threadedTexture = new ThreadedTexture(this, info);
			New<CreateTextureCommand>().Set(Ref(threadedTexture), info);
			QueueCommand();
			return threadedTexture;
		}
		return new ThreadedTexture(this, info)
		{
			Base = _baseRenderer.CreateTexture(info)
		};
	}

	public ITextureArray CreateTextureArray(int size, bool isBuffer)
	{
		ThreadedTextureArray threadedTextureArray = new ThreadedTextureArray(this);
		New<CreateTextureArrayCommand>().Set(Ref(threadedTextureArray), size, isBuffer);
		QueueCommand();
		return threadedTextureArray;
	}

	public void DeleteBuffer(BufferHandle buffer)
	{
		New<BufferDisposeCommand>().Set(buffer);
		QueueCommand();
	}

	public PinnedSpan<byte> GetBufferData(BufferHandle buffer, int offset, int size)
	{
		if (IsGpuThread())
		{
			ResultBox<PinnedSpan<byte>> resultBox = new ResultBox<PinnedSpan<byte>>();
			New<BufferGetDataCommand>().Set(buffer, offset, size, Ref(resultBox));
			InvokeCommand();
			return resultBox.Result;
		}
		return _baseRenderer.GetBufferData(Buffers.MapBufferBlocking(buffer), offset, size);
	}

	public Capabilities GetCapabilities()
	{
		ResultBox<Capabilities> resultBox = new ResultBox<Capabilities>();
		New<GetCapabilitiesCommand>().Set(Ref(resultBox));
		InvokeCommand();
		return resultBox.Result;
	}

	public ulong GetCurrentSync()
	{
		return _baseRenderer.GetCurrentSync();
	}

	public HardwareInfo GetHardwareInfo()
	{
		return _baseRenderer.GetHardwareInfo();
	}

	public void Initialize(GraphicsDebugLevel logLevel)
	{
		_baseRenderer.Initialize(logLevel);
	}

	public IProgram LoadProgramBinary(byte[] programBinary, bool hasFragmentShader, ShaderInfo info)
	{
		ThreadedProgram threadedProgram = new ThreadedProgram(this);
		BinaryProgramRequest binaryProgramRequest = new BinaryProgramRequest(threadedProgram, programBinary, hasFragmentShader, info);
		Programs.Add(binaryProgramRequest);
		New<CreateProgramCommand>().Set(Ref((IProgramRequest)binaryProgramRequest));
		QueueCommand();
		return threadedProgram;
	}

	public void PreFrame()
	{
		New<PreFrameCommand>();
		QueueCommand();
	}

	public ICounterEvent ReportCounter(CounterType type, EventHandler<ulong> resultHandler, float divisor, bool hostReserved)
	{
		ThreadedCounterEvent threadedCounterEvent = new ThreadedCounterEvent(this, type, _lastSampleCounterClear);
		New<ReportCounterCommand>().Set(Ref(threadedCounterEvent), type, Ref(resultHandler), divisor, hostReserved);
		QueueCommand();
		if (type == CounterType.SamplesPassed)
		{
			_lastSampleCounterClear = false;
		}
		return threadedCounterEvent;
	}

	public void ResetCounter(CounterType type)
	{
		New<ResetCounterCommand>().Set(type);
		QueueCommand();
		_lastSampleCounterClear = true;
	}

	public void Screenshot()
	{
		_baseRenderer.Screenshot();
	}

	public void SetBufferData(BufferHandle buffer, int offset, ReadOnlySpan<byte> data)
	{
		New<BufferSetDataCommand>().Set(buffer, offset, CopySpan(data));
		QueueCommand();
	}

	public void UpdateCounters()
	{
		New<UpdateCountersCommand>();
		QueueCommand();
	}

	public void WaitSync(ulong id)
	{
		Sync.WaitSyncAvailability(id);
		_baseRenderer.WaitSync(id);
	}

	private void Interrupt(Action action)
	{
		if (Thread.CurrentThread == _backendThread)
		{
			action();
			return;
		}
		using (_interruptLock.EnterScope())
		{
			while (Interlocked.CompareExchange(ref _interruptAction, action, null) != null)
			{
			}
			_galWorkAvailable.Set();
			_interruptRun.WaitOne();
		}
	}

	public void SetInterruptAction(Action<Action> interruptAction)
	{
	}

	public bool PrepareHostMapping(nint address, ulong size)
	{
		return _baseRenderer.PrepareHostMapping(address, size);
	}

	public void FlushThreadedCommands()
	{
		SpinWait spinWait = default;
		while (Volatile.Read(in _commandCount) > 0)
		{
			spinWait.SpinOnce();
		}
	}

	public void Dispose()
	{
		GC.SuppressFinalize(this);
		_running = false;
		_galWorkAvailable.Set();
		if (_gpuThread != null && _gpuThread.IsAlive)
		{
			_gpuThread.Join();
		}
		_baseRenderer.Dispose();
		_frameComplete.Dispose();
		_galWorkAvailable.Dispose();
		_invokeRun.Dispose();
		_interruptRun.Dispose();
		Sync.Dispose();
	}
}
