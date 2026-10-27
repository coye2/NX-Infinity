using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Ryujinx.Common;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu.Engine.Threed;
using Ryujinx.Graphics.Gpu.Engine.Types;
using Ryujinx.Graphics.Gpu.Image;
using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.Graphics.Gpu.Shader.DiskCache;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;

namespace Ryujinx.Graphics.Gpu.Shader;

internal class ShaderCache : IDisposable
{
	private readonly struct TranslatedShader(CachedShaderStage shader, ShaderProgram program)
	{
		public readonly CachedShaderStage Shader = shader;

		public readonly ShaderProgram Program = program;
	}

	private readonly struct TranslatedShaderVertexPair(CachedShaderStage vertexA, CachedShaderStage vertexB, ShaderProgram program)
	{
		public readonly CachedShaderStage VertexA = vertexA;

		public readonly CachedShaderStage VertexB = vertexB;

		public readonly ShaderProgram Program = program;
	}

	private readonly struct ProgramToSave(CachedShaderProgram cachedProgram, IProgram hostProgram, byte[] binaryCode)
	{
		public readonly CachedShaderProgram CachedProgram = cachedProgram;

		public readonly IProgram HostProgram = hostProgram;

		public readonly byte[] BinaryCode = binaryCode;
	}

	public const TranslationFlags DefaultFlags = TranslationFlags.DebugMode;

	private readonly GpuContext _context;

	private readonly ShaderDumper _dumper;

	private readonly Dictionary<ulong, CachedShaderProgram> _cpPrograms;

	private readonly Dictionary<ShaderAddresses, CachedShaderProgram> _gpPrograms;

	private readonly Queue<ProgramToSave> _programsToSaveQueue;

	private readonly ComputeShaderCacheHashTable _computeShaderCache;

	private readonly ShaderCacheHashTable _graphicsShaderCache;

	private readonly DiskCacheHostStorage _diskCacheHostStorage;

	private readonly BackgroundDiskCacheWriter _cacheWriter;

	public event Action<ShaderCacheState, int, int> ShaderCacheStateChanged;

	public ShaderCache(GpuContext context)
	{
		_context = context;
		_dumper = new ShaderDumper();
		_cpPrograms = new Dictionary<ulong, CachedShaderProgram>();
		_gpPrograms = new Dictionary<ShaderAddresses, CachedShaderProgram>();
		_programsToSaveQueue = new Queue<ProgramToSave>();
		string diskCachePath = GetDiskCachePath();
		_computeShaderCache = new ComputeShaderCacheHashTable();
		_graphicsShaderCache = new ShaderCacheHashTable();
		_diskCacheHostStorage = new DiskCacheHostStorage(diskCachePath);
		if (_diskCacheHostStorage.CacheEnabled)
		{
			_cacheWriter = new BackgroundDiskCacheWriter(context, _diskCacheHostStorage);
		}
	}

	private static string GetDiskCachePath()
	{
		if (!GraphicsConfig.EnableShaderCache || GraphicsConfig.TitleId == null)
		{
			return null;
		}
		return Path.Combine(AppDataManager.GamesDirPath, GraphicsConfig.TitleId.ToLower(), "cache", "shader");
	}

	public void ProcessShaderCacheQueue()
	{
		ProgramToSave result;
		while (_programsToSaveQueue.TryPeek(out result))
		{
			switch (result.HostProgram.CheckProgramLink(blocking: false))
			{
			case ProgramLinkStatus.Success:
				_cacheWriter.AddShader(result.CachedProgram, result.BinaryCode ?? result.HostProgram.GetBinary());
				break;
			case ProgramLinkStatus.Incomplete:
				return;
			}
			_programsToSaveQueue.Dequeue();
		}
	}

	internal void Initialize(CancellationToken cancellationToken)
	{
		if (_diskCacheHostStorage.CacheEnabled)
		{
			ParallelDiskCacheLoader parallelDiskCacheLoader = new ParallelDiskCacheLoader(_context, _graphicsShaderCache, _computeShaderCache, _diskCacheHostStorage, ShaderCacheStateUpdate, cancellationToken);
			parallelDiskCacheLoader.LoadShaders();
			int errorCount = parallelDiskCacheLoader.ErrorCount;
			if (errorCount != 0)
			{
				Logger.Warning?.Print(LogClass.Gpu, $"Failed to load {errorCount} shaders from the disk cache.", "Initialize");
			}
		}
	}

	private void ShaderCacheStateUpdate(ShaderCacheState state, int current, int total)
	{
		ShaderCacheStateChanged?.Invoke(state, current, total);
	}

	public CachedShaderProgram GetComputeShader(GpuChannel channel, int samplerPoolMaximumId, GpuChannelPoolState poolState, GpuChannelComputeState computeState, ulong gpuVa)
	{
		if (_cpPrograms.TryGetValue(gpuVa, out var value) && IsShaderEqual(channel, poolState, computeState, value, gpuVa))
		{
			return value;
		}
		if (_computeShaderCache.TryFind(channel, poolState, computeState, gpuVa, out value, out var cachedGuestCode))
		{
			_cpPrograms[gpuVa] = value;
			return value;
		}
		ShaderSpecializationState specializationState = new ShaderSpecializationState(ref computeState);
		GpuAccessorState state = new GpuAccessorState(samplerPoolMaximumId, poolState, computeState, default, specializationState);
		GpuAccessor gpuAccessor = new GpuAccessor(_context, channel, state);
		gpuAccessor.InitializeReservedCounts(tfEnabled: false, vertexAsCompute: false);
		TranslatorContext context = DecodeComputeShader(gpuAccessor, _context.Capabilities.Api, gpuVa);
		TranslatedShader translatedShader = TranslateShader(_dumper, channel, context, cachedGuestCode, asCompute: false);
		ShaderSource[] array = new ShaderSource[1] { CreateShaderSource(translatedShader.Program) };
		ShaderInfo info = ShaderInfoBuilder.BuildForCompute(_context, translatedShader.Program.Info);
		IProgram hostProgram = _context.Renderer.CreateProgram(array, info);
		value = new CachedShaderProgram(hostProgram, specializationState, translatedShader.Shader);
		_computeShaderCache.Add(value);
		EnqueueProgramToSave(value, hostProgram, array);
		_cpPrograms[gpuVa] = value;
		return value;
	}

	private static void UpdatePipelineInfo(ref ThreedClassState state, ref ProgramPipelineState pipeline, GpuChannelGraphicsState graphicsState, GpuChannel channel)
	{
		channel.TextureManager.UpdateRenderTargets();
		RtControl rtControl = state.RtControl;
		TextureMsaaMode rtMsaaMode = state.RtMsaaMode;
		pipeline.SamplesCount = rtMsaaMode.SamplesInX() * rtMsaaMode.SamplesInY();
		int num = rtControl.UnpackCount();
		for (int i = 0; i < 8; i++)
		{
			int index = rtControl.UnpackPermutationIndex(i);
			RtColorState rtColorState = state.RtColorState[index];
			if (i >= num || rtColorState.Format == 0 || rtColorState.WidthOrStride == 0)
			{
				pipeline.AttachmentEnable[i] = false;
				pipeline.AttachmentFormats[i] = Format.R8G8B8A8Unorm;
			}
			else
			{
				pipeline.AttachmentEnable[i] = true;
				pipeline.AttachmentFormats[i] = rtColorState.Format.Convert().Format;
			}
		}
		pipeline.DepthStencilEnable = state.RtDepthStencilEnable;
		pipeline.DepthStencilFormat = (pipeline.DepthStencilEnable ? state.RtDepthStencilState.Format.Convert().Format : Format.D24UnormS8Uint);
		pipeline.VertexBufferCount = 16;
		pipeline.Topology = graphicsState.Topology;
	}

	public CachedShaderProgram GetGraphicsShader(ref ThreedClassState state, ref ProgramPipelineState pipeline, GpuChannel channel, int samplerPoolMaximumId, ref GpuChannelPoolState poolState, ref GpuChannelGraphicsState graphicsState, ShaderAddresses addresses)
	{
		if (_gpPrograms.TryGetValue(addresses, out var value) && IsShaderEqual(channel, ref poolState, ref graphicsState, value, addresses))
		{
			return value;
		}
		if (_graphicsShaderCache.TryFind(channel, ref poolState, ref graphicsState, addresses, out value, out var guestCode))
		{
			_gpPrograms[addresses] = value;
			return value;
		}
		TransformFeedbackDescriptor[] transformFeedbackDescriptors = GetTransformFeedbackDescriptors(ref state);
		UpdatePipelineInfo(ref state, ref pipeline, graphicsState, channel);
		ShaderSpecializationState specializationState = new ShaderSpecializationState(ref graphicsState, ref pipeline, transformFeedbackDescriptors);
		GpuAccessorState state2 = new GpuAccessorState(samplerPoolMaximumId, poolState, default, graphicsState, specializationState, transformFeedbackDescriptors);
		ReadOnlySpan<ulong> readOnlySpan = addresses.AsSpan();
		GpuAccessor[] array = new GpuAccessor[5];
		TranslatorContext[] array2 = new TranslatorContext[6];
		TranslatorContext translatorContext = null;
		TargetApi api = _context.Capabilities.Api;
		for (int num = 4; num >= 0; num--)
		{
			ulong num2 = readOnlySpan[num + 1];
			if (num2 != 0L)
			{
				GpuAccessor gpuAccessor = new GpuAccessor(_context, channel, state2, num, addresses.Geometry != 0);
				TranslatorContext translatorContext2 = DecodeGraphicsShader(gpuAccessor, api, TranslationFlags.DebugMode, num2);
				if (translatorContext != null)
				{
					translatorContext2.SetNextStage(translatorContext);
				}
				if (num == 0 && addresses.VertexA != 0L)
				{
					array2[0] = DecodeGraphicsShader(gpuAccessor, api, TranslationFlags.VertexA | TranslationFlags.DebugMode, addresses.VertexA);
				}
				array[num] = gpuAccessor;
				array2[num + 1] = translatorContext2;
				translatorContext = translatorContext2;
			}
		}
		bool flag = array2[4] != null;
		bool vertexHasStore = array2[1] != null && array2[1].HasStore;
		bool geometryHasStore = flag && array2[4].HasStore;
		bool flag2 = ShouldConvertVertexToCompute(_context, vertexHasStore, geometryHasStore, flag);
		bool flag3 = ShouldConvertGeometryToCompute(_context, geometryHasStore);
		CachedShaderStage[] array3 = new CachedShaderStage[6];
		List<ShaderSource> list = new List<ShaderSource>();
		TranslatorContext translatorContext3 = null;
		ShaderInfoBuilder shaderInfoBuilder = new ShaderInfoBuilder(_context, transformFeedbackDescriptors != null, flag2);
		if (flag3 && array2[4] != null)
		{
			array2[4].SetVertexOutputMapForGeometryAsCompute(array2[1]);
		}
		ShaderAsCompute shaderAsCompute = null;
		ShaderAsCompute geometryAsCompute = null;
		for (int i = 0; i < 5; i++)
		{
			TranslatorContext translatorContext4 = array2[i + 1];
			if (translatorContext4 != null)
			{
				array[i].InitializeReservedCounts(transformFeedbackDescriptors != null, flag2);
				bool flag4 = ((i == 0) & flag2) || ((i == 3) & flag3);
				ShaderProgram shaderProgram;
				if (i == 0 && array2[0] != null)
				{
					TranslatedShaderVertexPair translatedShaderVertexPair = TranslateShader(_dumper, channel, translatorContext4, array2[0], guestCode.VertexACode, guestCode.VertexBCode, flag4);
					array3[0] = translatedShaderVertexPair.VertexA;
					array3[1] = translatedShaderVertexPair.VertexB;
					shaderProgram = translatedShaderVertexPair.Program;
				}
				else
				{
					byte[] byIndex = guestCode.GetByIndex(i);
					TranslatedShader translatedShader = TranslateShader(_dumper, channel, translatorContext4, byIndex, flag4);
					array3[i + 1] = translatedShader.Shader;
					shaderProgram = translatedShader.Program;
				}
				if (flag4)
				{
					bool tfEnabled = transformFeedbackDescriptors != null;
					if (i == 0)
					{
						shaderAsCompute = CreateHostVertexAsComputeProgram(shaderProgram, translatorContext4, tfEnabled);
						shaderProgram = (flag3 ? (array2[4] ?? translatorContext4) : translatorContext4).GenerateVertexPassthroughForCompute();
					}
					else
					{
						geometryAsCompute = CreateHostVertexAsComputeProgram(shaderProgram, translatorContext4, tfEnabled);
						shaderProgram = null;
					}
				}
				if (shaderProgram != null)
				{
					list.Add(CreateShaderSource(shaderProgram));
					shaderInfoBuilder.AddStageInfo(shaderProgram.Info);
				}
				translatorContext3 = translatorContext4;
			}
			else if (translatorContext3 != null && translatorContext3.LayerOutputWritten && i == 3 && !_context.Capabilities.SupportsLayerVertexTessellation)
			{
				list.Add(CreateShaderSource(translatorContext3.GenerateGeometryPassthrough()));
			}
		}
		ShaderSource[] array4 = list.ToArray();
		ShaderInfo info = shaderInfoBuilder.Build(pipeline);
		IProgram hostProgram = _context.Renderer.CreateProgram(array4, info);
		value = new CachedShaderProgram(hostProgram, shaderAsCompute, geometryAsCompute, specializationState, array3);
		_graphicsShaderCache.Add(value);
		if (shaderAsCompute == null)
		{
			EnqueueProgramToSave(value, hostProgram, array4);
		}
		_gpPrograms[addresses] = value;
		return value;
	}

	public static bool ShouldConvertVertexToCompute(GpuContext context, bool vertexHasStore, bool geometryHasStore, bool hasGeometryShader)
	{
		if (!context.Capabilities.SupportsVertexStoreAndAtomics & vertexHasStore)
		{
			return true;
		}
		if (hasGeometryShader)
		{
			return ShouldConvertGeometryToCompute(context, geometryHasStore);
		}
		return false;
	}

	public static bool ShouldConvertGeometryToCompute(GpuContext context, bool geometryHasStore)
	{
		if (!(!context.Capabilities.SupportsVertexStoreAndAtomics & geometryHasStore))
		{
			return !context.Capabilities.SupportsGeometryShader;
		}
		return true;
	}

	public static bool MayConvertVtgToCompute(ref Capabilities capabilities)
	{
		if (capabilities.SupportsVertexStoreAndAtomics)
		{
			return !capabilities.SupportsGeometryShader;
		}
		return true;
	}

	private ShaderAsCompute CreateHostVertexAsComputeProgram(ShaderProgram program, TranslatorContext context, bool tfEnabled)
	{
		ShaderSource shaderSource = new ShaderSource(program.Code, program.BinaryCode, ShaderStage.Compute, program.Language);
		ShaderInfo info = ShaderInfoBuilder.BuildForVertexAsCompute(_context, program.Info, tfEnabled);
		return new ShaderAsCompute(_context.Renderer.CreateProgram(new ShaderSource[1] { shaderSource }, info), program.Info, context.GetResourceReservations());
	}

	public static ShaderSource CreateShaderSource(ShaderProgram program)
	{
		return new ShaderSource(program.Code, program.BinaryCode, program.Info.Stage, program.Language);
	}

	private void EnqueueProgramToSave(CachedShaderProgram program, IProgram hostProgram, ShaderSource[] sources)
	{
		if (_diskCacheHostStorage.CacheEnabled)
		{
			byte[] binaryCode = ((_context.Capabilities.Api == TargetApi.Vulkan) ? ShaderBinarySerializer.Pack(sources) : null);
			ProgramToSave item = new ProgramToSave(program, hostProgram, binaryCode);
			_programsToSaveQueue.Enqueue(item);
		}
	}

	private static TransformFeedbackDescriptor[] GetTransformFeedbackDescriptors(ref ThreedClassState state)
	{
		if (!state.TfEnable)
		{
			return null;
		}
		TransformFeedbackDescriptor[] array = new TransformFeedbackDescriptor[4];
		for (int i = 0; i < 4; i++)
		{
			TfState tfState = state.TfState[i];
			array[i] = new TransformFeedbackDescriptor(tfState.BufferIndex, tfState.Stride, tfState.VaryingsCount, ref state.TfVaryingLocations[i]);
		}
		return array;
	}

	private static bool IsShaderEqual(GpuChannel channel, GpuChannelPoolState poolState, GpuChannelComputeState computeState, CachedShaderProgram cpShader, ulong gpuVa)
	{
		if (IsShaderEqual(channel.MemoryManager, cpShader.Shaders[0], gpuVa))
		{
			return cpShader.SpecializationState.MatchesCompute(channel, ref poolState, computeState, checkTextures: true);
		}
		return false;
	}

	private static bool IsShaderEqual(GpuChannel channel, ref GpuChannelPoolState poolState, ref GpuChannelGraphicsState graphicsState, CachedShaderProgram gpShaders, ShaderAddresses addresses)
	{
		ReadOnlySpan<ulong> readOnlySpan = addresses.AsSpan();
		for (int i = 0; i < gpShaders.Shaders.Length; i++)
		{
			CachedShaderStage shader = gpShaders.Shaders[i];
			ulong gpuVa = readOnlySpan[i];
			if (!IsShaderEqual(channel.MemoryManager, shader, gpuVa))
			{
				return false;
			}
		}
		bool vertexAsCompute = gpShaders.VertexAsCompute != null;
		bool usesDrawParameters = gpShaders.Shaders[1]?.Info.UsesDrawParameters ?? false;
		return gpShaders.SpecializationState.MatchesGraphics(channel, ref poolState, ref graphicsState, vertexAsCompute, usesDrawParameters, checkTextures: true);
	}

	private static bool IsShaderEqual(MemoryManager memoryManager, CachedShaderStage shader, ulong gpuVa)
	{
		if (shader == null)
		{
			return true;
		}
		return memoryManager.GetSpanMapped(gpuVa, shader.Code.Length).SequenceEqual(shader.Code);
	}

	public static TranslatorContext DecodeComputeShader(IGpuAccessor gpuAccessor, TargetApi api, ulong gpuVa)
	{
		TranslationOptions options = CreateTranslationOptions(api, TranslationFlags.Compute | TranslationFlags.DebugMode);
		return Translator.CreateContext(gpuVa, gpuAccessor, options);
	}

	public static TranslatorContext DecodeGraphicsShader(IGpuAccessor gpuAccessor, TargetApi api, TranslationFlags flags, ulong gpuVa)
	{
		TranslationOptions options = CreateTranslationOptions(api, flags);
		return Translator.CreateContext(gpuVa, gpuAccessor, options);
	}

	private static TranslatedShaderVertexPair TranslateShader(ShaderDumper dumper, GpuChannel channel, TranslatorContext currentStage, TranslatorContext vertexA, byte[] codeA, byte[] codeB, bool asCompute)
	{
		using (RyuStutterTrace.Measure("shader.translate.vertex_pair", (long)currentStage.Address))
		{
			ulong graphicsUniformBufferAddress = channel.BufferManager.GetGraphicsUniformBufferAddress(0, 1);
			MemoryManager memoryManager = channel.MemoryManager;
			if (codeA == null)
			{
				codeA = memoryManager.GetSpan(vertexA.Address, vertexA.Size).ToArray();
			}
			if (codeB == null)
			{
				codeB = memoryManager.GetSpan(currentStage.Address, currentStage.Size).ToArray();
			}
			byte[] cb1Data = ReadArray(memoryManager, graphicsUniformBufferAddress, vertexA.Cb1DataSize);
			byte[] cb1Data2 = ReadArray(memoryManager, graphicsUniformBufferAddress, currentStage.Cb1DataSize);
			ShaderDumpPaths shaderDumpPaths = default;
			ShaderDumpPaths shaderDumpPaths2 = default;
			if (dumper != null)
			{
				shaderDumpPaths = dumper.Dump(codeA, compute: false);
				shaderDumpPaths2 = dumper.Dump(codeB, compute: false);
			}
			ShaderProgram shaderProgram = currentStage.Translate(vertexA, asCompute);
			shaderDumpPaths2.Prepend(shaderProgram);
			shaderDumpPaths.Prepend(shaderProgram);
			CachedShaderStage vertexA2 = new CachedShaderStage(null, codeA, cb1Data);
			CachedShaderStage vertexB = new CachedShaderStage(shaderProgram.Info, codeB, cb1Data2);
			return new TranslatedShaderVertexPair(vertexA2, vertexB, shaderProgram);
		}
	}

	private static TranslatedShader TranslateShader(ShaderDumper dumper, GpuChannel channel, TranslatorContext context, byte[] code, bool asCompute)
	{
		using (RyuStutterTrace.Measure("shader.translate", (long)context.Address))
		{
			MemoryManager memoryManager = channel.MemoryManager;
			ulong address = ((context.Stage == ShaderStage.Compute) ? channel.BufferManager.GetComputeUniformBufferAddress(1) : channel.BufferManager.GetGraphicsUniformBufferAddress(StageToStageIndex(context.Stage), 1));
			byte[] cb1Data = ReadArray(memoryManager, address, context.Cb1DataSize);
			if (code == null)
			{
				code = memoryManager.GetSpan(context.Address, context.Size).ToArray();
			}
			ShaderDumpPaths shaderDumpPaths = dumper?.Dump(code, context.Stage == ShaderStage.Compute) ?? default(ShaderDumpPaths);
			ShaderProgram shaderProgram = context.Translate(asCompute);
			shaderDumpPaths.Prepend(shaderProgram);
			return new TranslatedShader(new CachedShaderStage(shaderProgram.Info, code, cb1Data), shaderProgram);
		}
	}

	private static byte[] ReadArray(MemoryManager memoryManager, ulong address, int size)
	{
		if (address == ulong.MaxValue || size == 0)
		{
			return Array.Empty<byte>();
		}
		return memoryManager.Physical.GetSpan(address, size).ToArray();
	}

	private static int StageToStageIndex(ShaderStage stage)
	{
		return stage switch
		{
			ShaderStage.TessellationControl => 1,
			ShaderStage.TessellationEvaluation => 2,
			ShaderStage.Geometry => 3,
			ShaderStage.Fragment => 4,
			_ => 0,
		};
	}

	private static TranslationOptions CreateTranslationOptions(TargetApi api, TranslationFlags flags)
	{
		return new TranslationOptions((GraphicsConfig.EnableSpirvCompilationOnVulkan && api == TargetApi.Vulkan) ? TargetLanguage.Spirv : TargetLanguage.Glsl, api, flags);
	}

	public void Dispose()
	{
		foreach (CachedShaderProgram program in _graphicsShaderCache.GetPrograms())
		{
			program.Dispose();
		}
		foreach (CachedShaderProgram program2 in _computeShaderCache.GetPrograms())
		{
			program2.Dispose();
		}
		_cacheWriter?.Dispose();
	}
}
