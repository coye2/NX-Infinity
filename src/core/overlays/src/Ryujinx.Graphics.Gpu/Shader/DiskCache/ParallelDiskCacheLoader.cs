using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;

namespace Ryujinx.Graphics.Gpu.Shader.DiskCache;

internal class ParallelDiskCacheLoader
{
	private readonly struct ProgramEntry(CachedShaderProgram cachedProgram, byte[] binaryCode, int programIndex, bool isCompute, bool isBinary)
	{
		public readonly CachedShaderProgram CachedProgram = cachedProgram;

		public readonly byte[] BinaryCode = binaryCode;

		public readonly int ProgramIndex = programIndex;

		public readonly bool IsCompute = isCompute;

		public readonly bool IsBinary = isBinary;
	}

	private readonly struct ProgramCompilation(ShaderProgram[] translatedStages, CachedShaderStage[] shaders, ShaderSpecializationState specState, int programIndex, bool isCompute)
	{
		public readonly ShaderProgram[] TranslatedStages = translatedStages;

		public readonly CachedShaderStage[] Shaders = shaders;

		public readonly ShaderSpecializationState SpecializationState = specState;

		public readonly int ProgramIndex = programIndex;

		public readonly bool IsCompute = isCompute;
	}

	private readonly struct AsyncProgramTranslation(GuestCodeAndCbData?[] guestShaders, ShaderSpecializationState specState, int programIndex, bool isCompute)
	{
		public readonly GuestCodeAndCbData?[] GuestShaders = guestShaders;

		public readonly ShaderSpecializationState SpecializationState = specState;

		public readonly int ProgramIndex = programIndex;

		public readonly bool IsCompute = isCompute;
	}

	private const int ThreadCount = 8;

	private readonly GpuContext _context;

	private readonly ShaderCacheHashTable _graphicsCache;

	private readonly ComputeShaderCacheHashTable _computeCache;

	private readonly DiskCacheHostStorage _hostStorage;

	private readonly CancellationToken _cancellationToken;

	private readonly Action<ShaderCacheState, int, int> _stateChangeCallback;

	private bool _needsHostRegen;

	private int _errorCount;

	private readonly Queue<ProgramEntry> _validationQueue;

	private readonly ConcurrentQueue<ProgramCompilation> _compilationQueue;

	private readonly BlockingCollection<AsyncProgramTranslation> _asyncTranslationQueue;

	private readonly SortedList<int, (CachedShaderProgram, byte[])> _programList;

	private readonly int _backendParallelCompileThreads;

	private readonly CacheLoadProgress _progress;

	public bool Active => !_cancellationToken.IsCancellationRequested;

	public int ErrorCount => Volatile.Read(in _errorCount);

	public ParallelDiskCacheLoader(GpuContext context, ShaderCacheHashTable graphicsCache, ComputeShaderCacheHashTable computeCache, DiskCacheHostStorage hostStorage, Action<ShaderCacheState, int, int> stateChangeCallback, CancellationToken cancellationToken)
	{
		_context = context;
		_graphicsCache = graphicsCache;
		_computeCache = computeCache;
		_hostStorage = hostStorage;
		_stateChangeCallback = stateChangeCallback;
		_progress = new CacheLoadProgress(stateChangeCallback);
		_cancellationToken = cancellationToken;
		_validationQueue = new Queue<ProgramEntry>();
		_compilationQueue = new ConcurrentQueue<ProgramCompilation>();
		_asyncTranslationQueue = new BlockingCollection<AsyncProgramTranslation>(8);
		_programList = new SortedList<int, (CachedShaderProgram, byte[])>();
		_backendParallelCompileThreads = Math.Min(Environment.ProcessorCount, 8);
	}

	public void LoadShaders()
	{
		Thread[] array = new Thread[8];
		for (int i = 0; i < 8; i++)
		{
			array[i] = new Thread(ProcessAsyncQueue)
			{
				Name = $"GPU.AsyncTranslationThread.{i}"
			};
		}
		int programCount = _hostStorage.GetProgramCount();
		_progress.Start(programCount);
		Logger.Info?.Print(LogClass.Gpu, $"Loading {programCount} shaders from the cache...", "LoadShaders");
		int num = 0;
		try
		{
			for (int j = 0; j < 8; j++)
			{
				array[j].Start(_cancellationToken);
				num++;
			}
			_hostStorage.LoadShaders(_context, this);
		}
		catch (DiskCacheLoadException ex)
		{
			Logger.Warning?.Print(LogClass.Gpu, "Error loading the shader cache. " + ex.Message, "LoadShaders");
			if (ex.Result != DiskCacheLoadResult.NoAccess)
			{
				_needsHostRegen = true;
			}
		}
		catch (InvalidDataException ex2)
		{
			Logger.Warning?.Print(LogClass.Gpu, "Error decompressing the shader cache file. " + ex2.Message, "LoadShaders");
			_needsHostRegen = true;
		}
		catch (IOException ex3)
		{
			Logger.Warning?.Print(LogClass.Gpu, "Error reading the shader cache file. " + ex3.Message, "LoadShaders");
			_needsHostRegen = true;
		}
		finally
		{
			_asyncTranslationQueue.CompleteAdding();
			for (int k = 0; k < num; k++)
			{
				array[k].Join();
			}
		}
		CheckCompilationBlocking();
		if (_needsHostRegen && Active)
		{
			try
			{
				_hostStorage.ClearSharedCache();
				_hostStorage.ClearHostCache(_context);
				if (_programList.Count != 0)
				{
					_stateChangeCallback(ShaderCacheState.Packaging, 0, _programList.Count);
					Logger.Info?.Print(LogClass.Gpu, $"Rebuilding {_programList.Count} shaders...", "LoadShaders");
					using DiskCacheOutputStreams streams = _hostStorage.GetOutputStreams(_context);
					int num2 = 0;
					foreach (KeyValuePair<int, (CachedShaderProgram, byte[])> program2 in _programList)
					{
						if (!Active)
						{
							break;
						}
						var (program, array2) = program2.Value;
						_hostStorage.AddShader(_context, program, array2, streams);
						_stateChangeCallback(ShaderCacheState.Packaging, ++num2, _programList.Count);
					}
					Logger.Info?.Print(LogClass.Gpu, $"Rebuilt {_programList.Count} shaders successfully.", "LoadShaders");
				}
				else
				{
					_hostStorage.ClearGuestCache();
					Logger.Info?.Print(LogClass.Gpu, "Shader cache deleted due to corruption.", "LoadShaders");
				}
			}
			catch (DiskCacheLoadException ex4)
			{
				Logger.Warning?.Print(LogClass.Gpu, "Error deleting the shader cache. " + ex4.Message, "LoadShaders");
			}
			catch (IOException ex5)
			{
				Logger.Warning?.Print(LogClass.Gpu, "Error deleting the shader cache file. " + ex5.Message, "LoadShaders");
			}
		}
		Logger.Info?.Print(LogClass.Gpu, "Shader cache loaded.", "LoadShaders");
		_progress.Finish();
	}

	public void QueueHostProgram(CachedShaderProgram cachedProgram, byte[] binaryCode, int programIndex, bool isCompute)
	{
		EnqueueForValidation(new ProgramEntry(cachedProgram, binaryCode, programIndex, isCompute, isBinary: true));
	}

	public void QueueGuestProgram(GuestCodeAndCbData?[] guestShaders, ShaderSpecializationState specState, int programIndex, bool isCompute)
	{
		try
		{
			AsyncProgramTranslation item = new AsyncProgramTranslation(guestShaders, specState, programIndex, isCompute);
			_asyncTranslationQueue.Add(item, _cancellationToken);
		}
		catch (OperationCanceledException)
		{
		}
	}

	public void CheckCompilation()
	{
		ProcessCompilationQueue();
		ProgramEntry result;
		while (_validationQueue.TryPeek(out result))
		{
			ProgramLinkStatus programLinkStatus = result.CachedProgram.HostProgram.CheckProgramLink(blocking: false);
			if (programLinkStatus != ProgramLinkStatus.Incomplete)
			{
				ProcessCompiledProgram(ref result, programLinkStatus);
				_validationQueue.Dequeue();
				continue;
			}
			break;
		}
	}

	private void CheckCompilationBlocking()
	{
		ProcessCompilationQueue();
		ProgramEntry result;
		while (_validationQueue.TryDequeue(out result) && Active)
		{
			ProcessCompiledProgram(ref result, result.CachedProgram.HostProgram.CheckProgramLink(blocking: true), asyncCompile: false);
		}
	}

	private void ProcessCompiledProgram(ref ProgramEntry entry, ProgramLinkStatus result, bool asyncCompile = true)
	{
		if (result == ProgramLinkStatus.Success)
		{
			if (entry.IsCompute)
			{
				_computeCache.Add(entry.CachedProgram);
			}
			else
			{
				_graphicsCache.Add(entry.CachedProgram);
			}
			if (!entry.IsBinary)
			{
				_needsHostRegen = true;
			}
			byte[] item = entry.BinaryCode ?? entry.CachedProgram.HostProgram.GetBinary();
			_programList.Add(entry.ProgramIndex, (entry.CachedProgram, item));
			SignalCompiled();
		}
		else if (entry.IsBinary)
		{
			CachedShaderProgram cachedProgram = entry.CachedProgram;
			GuestCodeAndCbData?[] array = new GuestCodeAndCbData?[cachedProgram.Shaders.Length];
			for (int i = 0; i < cachedProgram.Shaders.Length; i++)
			{
				CachedShaderStage cachedShaderStage = cachedProgram.Shaders[i];
				if (cachedShaderStage != null)
				{
					array[i] = new GuestCodeAndCbData(cachedShaderStage.Code, cachedShaderStage.Cb1Data);
				}
			}
			if (asyncCompile)
			{
				QueueGuestProgram(array, cachedProgram.SpecializationState, entry.ProgramIndex, entry.IsCompute);
				return;
			}
			RecompileFromGuestCode(array, cachedProgram.SpecializationState, entry.ProgramIndex, entry.IsCompute);
			ProcessCompilationQueue();
		}
		else
		{
			Interlocked.Increment(ref _errorCount);
			SignalCompiled();
		}
	}

	private void ProcessCompilationQueue()
	{
		ProgramCompilation result;
		while (_compilationQueue.TryDequeue(out result) && Active)
		{
			ShaderSource[] array = new ShaderSource[result.TranslatedStages.Length];
			ShaderInfoBuilder shaderInfoBuilder = new ShaderInfoBuilder(_context, result.SpecializationState.TransformFeedbackDescriptors != null);
			for (int i = 0; i < result.TranslatedStages.Length; i++)
			{
				ShaderProgram shaderProgram = result.TranslatedStages[i];
				array[i] = ShaderCache.CreateShaderSource(shaderProgram);
				shaderInfoBuilder.AddStageInfo(shaderProgram.Info);
			}
			ShaderInfo info = shaderInfoBuilder.Build(result.SpecializationState.PipelineState, fromCache: true);
			CachedShaderProgram cachedProgram = new CachedShaderProgram(_context.Renderer.CreateProgram(array, info), result.SpecializationState, result.Shaders);
			byte[] binaryCode = ((_context.Capabilities.Api == TargetApi.Vulkan) ? ShaderBinarySerializer.Pack(array) : null);
			EnqueueForValidation(new ProgramEntry(cachedProgram, binaryCode, result.ProgramIndex, result.IsCompute, isBinary: false));
		}
	}

	private void EnqueueForValidation(ProgramEntry newEntry)
	{
		_validationQueue.Enqueue(newEntry);
		if (_validationQueue.Count >= _backendParallelCompileThreads && _validationQueue.TryDequeue(out var result))
		{
			ProcessCompiledProgram(ref result, result.CachedProgram.HostProgram.CheckProgramLink(blocking: true), asyncCompile: false);
		}
	}

	private void ProcessAsyncQueue(object state)
	{
		CancellationToken cancellationToken = (CancellationToken)state;
		try
		{
			foreach (AsyncProgramTranslation item in _asyncTranslationQueue.GetConsumingEnumerable(cancellationToken))
			{
				RecompileFromGuestCode(item.GuestShaders, item.SpecializationState, item.ProgramIndex, item.IsCompute);
			}
		}
		catch (OperationCanceledException)
		{
		}
	}

	private void RecompileFromGuestCode(GuestCodeAndCbData?[] guestShaders, ShaderSpecializationState specState, int programIndex, bool isCompute)
	{
		try
		{
			if (isCompute)
			{
				RecompileComputeFromGuestCode(guestShaders, specState, programIndex);
			}
			else
			{
				RecompileGraphicsFromGuestCode(guestShaders, specState, programIndex);
			}
		}
		catch (Exception ex)
		{
			Logger.Error?.Print(LogClass.Gpu, "Error translating guest shader. " + ex.Message, "RecompileFromGuestCode");
			Interlocked.Increment(ref _errorCount);
			SignalCompiled();
		}
	}

	private void RecompileGraphicsFromGuestCode(GuestCodeAndCbData?[] guestShaders, ShaderSpecializationState specState, int programIndex)
	{
		ShaderSpecializationState shaderSpecializationState = new ShaderSpecializationState(ref specState.GraphicsState, specState.PipelineState, specState.TransformFeedbackDescriptors);
		ResourceCounts counts = new ResourceCounts();
		DiskCacheGpuAccessor[] array = new DiskCacheGpuAccessor[5];
		TranslatorContext[] array2 = new TranslatorContext[6];
		TranslatorContext translatorContext = null;
		TargetApi api = _context.Capabilities.Api;
		bool hasValue = guestShaders[4].HasValue;
		for (int num = 4; num >= 0; num--)
		{
			if (guestShaders[num + 1].HasValue)
			{
				GuestCodeAndCbData value = guestShaders[num + 1].Value;
				byte[] code = value.Code;
				byte[] cb1Data = value.Cb1Data;
				DiskCacheGpuAccessor diskCacheGpuAccessor = new DiskCacheGpuAccessor(_context, code, cb1Data, specState, shaderSpecializationState, counts, num, hasValue);
				TranslatorContext translatorContext2 = ShaderCache.DecodeGraphicsShader(diskCacheGpuAccessor, api, TranslationFlags.DebugMode, 0uL);
				if (translatorContext != null)
				{
					translatorContext2.SetNextStage(translatorContext);
				}
				if (num == 0 && guestShaders[0].HasValue)
				{
					byte[] code2 = guestShaders[0].Value.Code;
					byte[] cb1Data2 = guestShaders[0].Value.Cb1Data;
					DiskCacheGpuAccessor gpuAccessor = new DiskCacheGpuAccessor(_context, code2, cb1Data2, specState, shaderSpecializationState, counts, 0, hasValue);
					array2[0] = ShaderCache.DecodeGraphicsShader(gpuAccessor, api, TranslationFlags.VertexA | TranslationFlags.DebugMode, 0uL);
				}
				array[num] = diskCacheGpuAccessor;
				array2[num + 1] = translatorContext2;
				translatorContext = translatorContext2;
			}
		}
		bool flag = array2[4] != null;
		bool vertexHasStore = array2[1] != null && array2[1].HasStore;
		bool geometryHasStore = flag && array2[4].HasStore;
		bool flag2 = ShaderCache.ShouldConvertVertexToCompute(_context, vertexHasStore, geometryHasStore, flag);
		if (flag2)
		{
			return;
		}
		CachedShaderStage[] array3 = new CachedShaderStage[guestShaders.Length];
		List<ShaderProgram> list = new List<ShaderProgram>();
		TranslatorContext translatorContext3 = null;
		for (int i = 0; i < 5; i++)
		{
			TranslatorContext translatorContext4 = array2[i + 1];
			if (translatorContext4 != null)
			{
				array[i].InitializeReservedCounts(specState.TransformFeedbackDescriptors != null, flag2);
				byte[] code3 = guestShaders[i + 1].Value.Code;
				byte[] cb1Data3 = guestShaders[i + 1].Value.Cb1Data;
				ShaderProgram shaderProgram;
				if (i == 0 && guestShaders[0].HasValue)
				{
					shaderProgram = translatorContext4.Translate(array2[0]);
					byte[] code4 = guestShaders[0].Value.Code;
					byte[] cb1Data4 = guestShaders[0].Value.Cb1Data;
					array3[0] = new CachedShaderStage(null, code4, cb1Data4);
					array3[1] = new CachedShaderStage(shaderProgram.Info, code3, cb1Data3);
				}
				else
				{
					shaderProgram = translatorContext4.Translate();
					array3[i + 1] = new CachedShaderStage(shaderProgram.Info, code3, cb1Data3);
				}
				if (shaderProgram != null)
				{
					list.Add(shaderProgram);
				}
				translatorContext3 = translatorContext4;
			}
			else if (translatorContext3 != null && translatorContext3.LayerOutputWritten && i == 3 && !_context.Capabilities.SupportsLayerVertexTessellation)
			{
				list.Add(translatorContext3.GenerateGeometryPassthrough());
			}
		}
		_compilationQueue.Enqueue(new ProgramCompilation(list.ToArray(), array3, shaderSpecializationState, programIndex, isCompute: false));
	}

	private void RecompileComputeFromGuestCode(GuestCodeAndCbData?[] guestShaders, ShaderSpecializationState specState, int programIndex)
	{
		GuestCodeAndCbData value = guestShaders[0].Value;
		ResourceCounts counts = new ResourceCounts();
		ShaderSpecializationState shaderSpecializationState = new ShaderSpecializationState(ref specState.ComputeState);
		DiskCacheGpuAccessor diskCacheGpuAccessor = new DiskCacheGpuAccessor(_context, value.Code, value.Cb1Data, specState, shaderSpecializationState, counts, 0, hasGeometryShader: false);
		diskCacheGpuAccessor.InitializeReservedCounts(tfEnabled: false, vertexAsCompute: false);
		ShaderProgram shaderProgram = ShaderCache.DecodeComputeShader(diskCacheGpuAccessor, _context.Capabilities.Api, 0uL).Translate();
		CachedShaderStage[] shaders = new CachedShaderStage[1]
		{
			new CachedShaderStage(shaderProgram.Info, value.Code, value.Cb1Data)
		};
		_compilationQueue.Enqueue(new ProgramCompilation(new ShaderProgram[1] { shaderProgram }, shaders, shaderSpecializationState, programIndex, isCompute: true));
	}

	private void SignalCompiled()
	{
		_progress.CompleteOne();
	}
}
