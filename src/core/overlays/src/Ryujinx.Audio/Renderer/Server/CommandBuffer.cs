using System;
using Ryujinx.Audio.Renderer.Common;
using Ryujinx.Audio.Renderer.Dsp.Command;
using Ryujinx.Audio.Renderer.Dsp.State;
using Ryujinx.Audio.Renderer.Parameter;
using Ryujinx.Audio.Renderer.Parameter.Effect;
using Ryujinx.Audio.Renderer.Server.Performance;
using Ryujinx.Audio.Renderer.Server.Sink;
using Ryujinx.Audio.Renderer.Server.Splitter;
using Ryujinx.Audio.Renderer.Server.Upsampler;
using Ryujinx.Audio.Renderer.Server.Voice;
using Ryujinx.Common;

namespace Ryujinx.Audio.Renderer.Server;

public class CommandBuffer
{
	private readonly ICommandProcessingTimeEstimator _commandProcessingTimeEstimator;

	private static readonly ObjectPool<PcmInt16DataSourceCommandVersion1> _pcmInt16DataSourceCommandVersion1Pool = new ObjectPool<PcmInt16DataSourceCommandVersion1>(() => new PcmInt16DataSourceCommandVersion1(), 256);

	private static readonly ObjectPool<PcmFloatDataSourceCommandVersion1> _pcmFloatDataSourceCommandVersion1Pool = new ObjectPool<PcmFloatDataSourceCommandVersion1>(() => new PcmFloatDataSourceCommandVersion1(), 256);

	private static readonly ObjectPool<AdpcmDataSourceCommandVersion1> _adpcmDataSourceCommandVersion1Pool = new ObjectPool<AdpcmDataSourceCommandVersion1>(() => new AdpcmDataSourceCommandVersion1(), 256);

	private static readonly ObjectPool<DataSourceVersion2Command> _dataSourceVersion2CommandPool = new ObjectPool<DataSourceVersion2Command>(() => new DataSourceVersion2Command(), 256);

	private static readonly ObjectPool<VolumeCommand> _volumeCommandPool = new ObjectPool<VolumeCommand>(() => new VolumeCommand(), 256);

	private static readonly ObjectPool<VolumeRampCommand> _volumeRampCommandPool = new ObjectPool<VolumeRampCommand>(() => new VolumeRampCommand(), 256);

	private static readonly ObjectPool<BiquadFilterCommand> _biquadFilterCommandPool = new ObjectPool<BiquadFilterCommand>(() => new BiquadFilterCommand(), 256);

	private static readonly ObjectPool<MixCommand> _mixCommandPool = new ObjectPool<MixCommand>(() => new MixCommand(), 256);

	private static readonly ObjectPool<MixRampCommand> _mixRampCommandPool = new ObjectPool<MixRampCommand>(() => new MixRampCommand(), 256);

	private static readonly ObjectPool<MixRampGroupedCommand> _mixRampGroupedCommandPool = new ObjectPool<MixRampGroupedCommand>(() => new MixRampGroupedCommand(), 256);

	private static readonly ObjectPool<DepopPrepareCommand> _depopPrepareCommandPool = new ObjectPool<DepopPrepareCommand>(() => new DepopPrepareCommand(), 256);

	private static readonly ObjectPool<DepopForMixBuffersCommand> _depopForMixBuffersCommandPool = new ObjectPool<DepopForMixBuffersCommand>(() => new DepopForMixBuffersCommand(), 256);

	private static readonly ObjectPool<DelayCommand> _delayCommandPool = new ObjectPool<DelayCommand>(() => new DelayCommand(), 256);

	private static readonly ObjectPool<UpsampleCommand> _upsampleCommandPool = new ObjectPool<UpsampleCommand>(() => new UpsampleCommand(), 256);

	private static readonly ObjectPool<DownMixSurroundToStereoCommand> _downMixSurroundToStereoCommandPool = new ObjectPool<DownMixSurroundToStereoCommand>(() => new DownMixSurroundToStereoCommand(), 256);

	private static readonly ObjectPool<AuxiliaryBufferCommand> _auxiliaryBufferCommandPool = new ObjectPool<AuxiliaryBufferCommand>(() => new AuxiliaryBufferCommand(), 256);

	private static readonly ObjectPool<DeviceSinkCommand> _deviceSinkCommandPool = new ObjectPool<DeviceSinkCommand>(() => new DeviceSinkCommand(), 256);

	private static readonly ObjectPool<CircularBufferSinkCommand> _circularBufferSinkCommandPool = new ObjectPool<CircularBufferSinkCommand>(() => new CircularBufferSinkCommand(), 256);

	private static readonly ObjectPool<ReverbCommand> _reverbCommandPool = new ObjectPool<ReverbCommand>(() => new ReverbCommand(), 256);

	private static readonly ObjectPool<Reverb3dCommand> _reverb3dCommandPool = new ObjectPool<Reverb3dCommand>(() => new Reverb3dCommand(), 256);

	private static readonly ObjectPool<PerformanceCommand> _performanceCommandPool = new ObjectPool<PerformanceCommand>(() => new PerformanceCommand(), 256);

	private static readonly ObjectPool<ClearMixBufferCommand> _clearMixBufferCommandPool = new ObjectPool<ClearMixBufferCommand>(() => new ClearMixBufferCommand(), 256);

	private static readonly ObjectPool<CopyMixBufferCommand> _copyMixBufferCommandPool = new ObjectPool<CopyMixBufferCommand>(() => new CopyMixBufferCommand(), 256);

	private static readonly ObjectPool<LimiterCommandVersion1> _limiterCommandVersion1Pool = new ObjectPool<LimiterCommandVersion1>(() => new LimiterCommandVersion1(), 256);

	private static readonly ObjectPool<LimiterCommandVersion2> _limiterCommandVersion2Pool = new ObjectPool<LimiterCommandVersion2>(() => new LimiterCommandVersion2(), 256);

	private static readonly ObjectPool<MultiTapBiquadFilterCommand> _multiTapBiquadFilterCommandPool = new ObjectPool<MultiTapBiquadFilterCommand>(() => new MultiTapBiquadFilterCommand(), 256);

	private static readonly ObjectPool<CaptureBufferCommand> _captureBufferCommandPool = new ObjectPool<CaptureBufferCommand>(() => new CaptureBufferCommand(), 256);

	private static readonly ObjectPool<CompressorCommand> _compressorCommandPool = new ObjectPool<CompressorCommand>(() => new CompressorCommand(), 256);

	private static readonly ObjectPool<BiquadFilterAndMixCommand> _biquadFilterAndMixCommandPool = new ObjectPool<BiquadFilterAndMixCommand>(() => new BiquadFilterAndMixCommand(), 256);

	private static readonly ObjectPool<MultiTapBiquadFilterAndMixCommand> _multiTapBiquadFilterAndMixCommandPool = new ObjectPool<MultiTapBiquadFilterAndMixCommand>(() => new MultiTapBiquadFilterAndMixCommand(), 256);

	private static readonly ObjectPool<FillBufferCommand> _fillBufferCommandPool = new ObjectPool<FillBufferCommand>(() => new FillBufferCommand(), 256);

	public uint EstimatedProcessingTime { get; set; }

	public CommandList CommandList { get; }

	public static void ReleaseCommand(ICommand command)
	{
		switch (command.CommandType)
		{
		case CommandType.PcmInt16DataSourceVersion1:
			_pcmInt16DataSourceCommandVersion1Pool.Release((PcmInt16DataSourceCommandVersion1)command);
			break;
		case CommandType.PcmInt16DataSourceVersion2:
			_dataSourceVersion2CommandPool.Release((DataSourceVersion2Command)command);
			break;
		case CommandType.PcmFloatDataSourceVersion1:
			_pcmFloatDataSourceCommandVersion1Pool.Release((PcmFloatDataSourceCommandVersion1)command);
			break;
		case CommandType.PcmFloatDataSourceVersion2:
			_dataSourceVersion2CommandPool.Release((DataSourceVersion2Command)command);
			break;
		case CommandType.AdpcmDataSourceVersion1:
			_adpcmDataSourceCommandVersion1Pool.Release((AdpcmDataSourceCommandVersion1)command);
			break;
		case CommandType.AdpcmDataSourceVersion2:
			_dataSourceVersion2CommandPool.Release((DataSourceVersion2Command)command);
			break;
		case CommandType.Volume:
			_volumeCommandPool.Release((VolumeCommand)command);
			break;
		case CommandType.VolumeRamp:
			_volumeRampCommandPool.Release((VolumeRampCommand)command);
			break;
		case CommandType.BiquadFilter:
			_biquadFilterCommandPool.Release((BiquadFilterCommand)command);
			break;
		case CommandType.BiquadFilterFloatCoeff:
			throw new NotImplementedException();
		case CommandType.Mix:
			_mixCommandPool.Release((MixCommand)command);
			break;
		case CommandType.MixRamp:
			_mixRampCommandPool.Release((MixRampCommand)command);
			break;
		case CommandType.MixRampGrouped:
			_mixRampGroupedCommandPool.Release((MixRampGroupedCommand)command);
			break;
		case CommandType.DepopPrepare:
			_depopPrepareCommandPool.Release((DepopPrepareCommand)command);
			break;
		case CommandType.DepopForMixBuffers:
			_depopForMixBuffersCommandPool.Release((DepopForMixBuffersCommand)command);
			break;
		case CommandType.Delay:
			_delayCommandPool.Release((DelayCommand)command);
			break;
		case CommandType.Upsample:
			_upsampleCommandPool.Release((UpsampleCommand)command);
			break;
		case CommandType.DownMixSurroundToStereo:
			_downMixSurroundToStereoCommandPool.Release((DownMixSurroundToStereoCommand)command);
			break;
		case CommandType.AuxiliaryBuffer:
			_auxiliaryBufferCommandPool.Release((AuxiliaryBufferCommand)command);
			break;
		case CommandType.DeviceSink:
			_deviceSinkCommandPool.Release((DeviceSinkCommand)command);
			break;
		case CommandType.CircularBufferSink:
			_circularBufferSinkCommandPool.Release((CircularBufferSinkCommand)command);
			break;
		case CommandType.Reverb:
			_reverbCommandPool.Release((ReverbCommand)command);
			break;
		case CommandType.Reverb3d:
			_reverb3dCommandPool.Release((Reverb3dCommand)command);
			break;
		case CommandType.Performance:
			_performanceCommandPool.Release((PerformanceCommand)command);
			break;
		case CommandType.ClearMixBuffer:
			_clearMixBufferCommandPool.Release((ClearMixBufferCommand)command);
			break;
		case CommandType.CopyMixBuffer:
			_copyMixBufferCommandPool.Release((CopyMixBufferCommand)command);
			break;
		case CommandType.LimiterVersion1:
			_limiterCommandVersion1Pool.Release((LimiterCommandVersion1)command);
			break;
		case CommandType.LimiterVersion2:
			_limiterCommandVersion2Pool.Release((LimiterCommandVersion2)command);
			break;
		case CommandType.MultiTapBiquadFilter:
			_multiTapBiquadFilterCommandPool.Release((MultiTapBiquadFilterCommand)command);
			break;
		case CommandType.MultiTapBiquadFilterFloatCoeff:
			throw new NotImplementedException();
		case CommandType.CaptureBuffer:
			_captureBufferCommandPool.Release((CaptureBufferCommand)command);
			break;
		case CommandType.Compressor:
			_compressorCommandPool.Release((CompressorCommand)command);
			break;
		case CommandType.BiquadFilterAndMix:
			_biquadFilterAndMixCommandPool.Release((BiquadFilterAndMixCommand)command);
			break;
		case CommandType.BiquadFilterAndMixFloatCoeff:
			throw new NotImplementedException();
		case CommandType.MultiTapBiquadFilterAndMix:
			_multiTapBiquadFilterAndMixCommandPool.Release((MultiTapBiquadFilterAndMixCommand)command);
			break;
		case CommandType.MultiTapBiquadFilterAndMixFloatCoef:
			throw new NotImplementedException();
		case CommandType.AuxiliaryBufferGrouped:
			throw new NotImplementedException();
		case CommandType.FillMixBuffer:
			throw new NotImplementedException();
		case CommandType.BiquadFilterCrossFade:
			throw new NotImplementedException();
		case CommandType.MultiTapBiquadFilterCrossFade:
			throw new NotImplementedException();
		case CommandType.FillBuffer:
			_fillBufferCommandPool.Release((FillBufferCommand)command);
			break;
		default:
			throw new NotImplementedException();
		}
	}

	public CommandBuffer(CommandList commandList, ICommandProcessingTimeEstimator commandProcessingTimeEstimator)
	{
		CommandList = commandList;
		EstimatedProcessingTime = 0u;
		_commandProcessingTimeEstimator = commandProcessingTimeEstimator;
	}

	private void AddCommand(ICommand command)
	{
		EstimatedProcessingTime += command.EstimatedProcessingTime;
		CommandList.AddCommand(command);
	}

	public void GenerateClearMixBuffer(int nodeId)
	{
		ClearMixBufferCommand clearMixBufferCommand = _clearMixBufferCommandPool.Allocate().Initialize(nodeId);
		clearMixBufferCommand.EstimatedProcessingTime = _commandProcessingTimeEstimator.Estimate(clearMixBufferCommand);
		AddCommand(clearMixBufferCommand);
	}

	public void GenerateDepopPrepare(Memory<VoiceState> state, Memory<float> depopBuffer, uint bufferCount, uint bufferOffset, int nodeId, bool wasPlaying)
	{
		DepopPrepareCommand depopPrepareCommand = _depopPrepareCommandPool.Allocate().Initialize(state, depopBuffer, bufferCount, bufferOffset, nodeId, wasPlaying);
		depopPrepareCommand.EstimatedProcessingTime = _commandProcessingTimeEstimator.Estimate(depopPrepareCommand);
		AddCommand(depopPrepareCommand);
	}

	public void GeneratePerformance(ref PerformanceEntryAddresses performanceEntryAddresses, PerformanceCommand.Type type, int nodeId)
	{
		PerformanceCommand performanceCommand = _performanceCommandPool.Allocate().Initialize(ref performanceEntryAddresses, type, nodeId);
		performanceCommand.EstimatedProcessingTime = _commandProcessingTimeEstimator.Estimate(performanceCommand);
		AddCommand(performanceCommand);
	}

	public void GenerateVolumeRamp(float previousVolume, float volume, uint bufferIndex, int nodeId)
	{
		VolumeRampCommand volumeRampCommand = _volumeRampCommandPool.Allocate().Initialize(previousVolume, volume, bufferIndex, nodeId);
		volumeRampCommand.EstimatedProcessingTime = _commandProcessingTimeEstimator.Estimate(volumeRampCommand);
		AddCommand(volumeRampCommand);
	}

	public void GenerateDataSourceVersion2(ref VoiceInfo voiceInfo, Memory<VoiceState> state, ushort outputBufferIndex, ushort channelIndex, int nodeId)
	{
		DataSourceVersion2Command dataSourceVersion2Command = _dataSourceVersion2CommandPool.Allocate().Initialize(ref voiceInfo, state, outputBufferIndex, channelIndex, nodeId);
		dataSourceVersion2Command.EstimatedProcessingTime = _commandProcessingTimeEstimator.Estimate(dataSourceVersion2Command);
		AddCommand(dataSourceVersion2Command);
	}

	public void GeneratePcmInt16DataSourceVersion1(ref VoiceInfo voiceInfo, Memory<VoiceState> state, ushort outputBufferIndex, ushort channelIndex, int nodeId)
	{
		PcmInt16DataSourceCommandVersion1 pcmInt16DataSourceCommandVersion = _pcmInt16DataSourceCommandVersion1Pool.Allocate().Initialize(ref voiceInfo, state, outputBufferIndex, channelIndex, nodeId);
		pcmInt16DataSourceCommandVersion.EstimatedProcessingTime = _commandProcessingTimeEstimator.Estimate(pcmInt16DataSourceCommandVersion);
		AddCommand(pcmInt16DataSourceCommandVersion);
	}

	public void GeneratePcmFloatDataSourceVersion1(ref VoiceInfo voiceInfo, Memory<VoiceState> state, ushort outputBufferIndex, ushort channelIndex, int nodeId)
	{
		PcmFloatDataSourceCommandVersion1 pcmFloatDataSourceCommandVersion = _pcmFloatDataSourceCommandVersion1Pool.Allocate().Initialize(ref voiceInfo, state, outputBufferIndex, channelIndex, nodeId);
		pcmFloatDataSourceCommandVersion.EstimatedProcessingTime = _commandProcessingTimeEstimator.Estimate(pcmFloatDataSourceCommandVersion);
		AddCommand(pcmFloatDataSourceCommandVersion);
	}

	public void GenerateAdpcmDataSourceVersion1(ref VoiceInfo voiceInfo, Memory<VoiceState> state, ushort outputBufferIndex, int nodeId)
	{
		AdpcmDataSourceCommandVersion1 adpcmDataSourceCommandVersion = _adpcmDataSourceCommandVersion1Pool.Allocate().Initialize(ref voiceInfo, state, outputBufferIndex, nodeId);
		adpcmDataSourceCommandVersion.EstimatedProcessingTime = _commandProcessingTimeEstimator.Estimate(adpcmDataSourceCommandVersion);
		AddCommand(adpcmDataSourceCommandVersion);
	}

	public void GenerateBiquadFilter(int baseIndex, ref BiquadFilterParameter2 filter, Memory<BiquadFilterState> biquadFilterStateMemory, int inputBufferOffset, int outputBufferOffset, bool needInitialization, int nodeId)
	{
		BiquadFilterCommand biquadFilterCommand = _biquadFilterCommandPool.Allocate().Initialize(baseIndex, ref filter, biquadFilterStateMemory, inputBufferOffset, outputBufferOffset, needInitialization, nodeId);
		biquadFilterCommand.EstimatedProcessingTime = _commandProcessingTimeEstimator.Estimate(biquadFilterCommand);
		AddCommand(biquadFilterCommand);
	}

	public void GenerateMultiTapBiquadFilter(int baseIndex, ReadOnlySpan<BiquadFilterParameter2> filters, Memory<BiquadFilterState> biquadFilterStatesMemory, int inputBufferOffset, int outputBufferOffset, ReadOnlySpan<bool> isInitialized, int nodeId)
	{
		MultiTapBiquadFilterCommand multiTapBiquadFilterCommand = _multiTapBiquadFilterCommandPool.Allocate().Initialize(baseIndex, filters, biquadFilterStatesMemory, inputBufferOffset, outputBufferOffset, isInitialized, nodeId);
		multiTapBiquadFilterCommand.EstimatedProcessingTime = _commandProcessingTimeEstimator.Estimate(multiTapBiquadFilterCommand);
		AddCommand(multiTapBiquadFilterCommand);
	}

	public void GenerateMixRampGrouped(uint mixBufferCount, uint inputBufferIndex, uint outputBufferIndex, ReadOnlySpan<float> previousVolume, ReadOnlySpan<float> volume, Memory<VoiceState> state, int nodeId)
	{
		MixRampGroupedCommand mixRampGroupedCommand = _mixRampGroupedCommandPool.Allocate().Initialize(mixBufferCount, inputBufferIndex, outputBufferIndex, previousVolume, volume, state, nodeId);
		mixRampGroupedCommand.EstimatedProcessingTime = _commandProcessingTimeEstimator.Estimate(mixRampGroupedCommand);
		AddCommand(mixRampGroupedCommand);
	}

	public void GenerateMixRamp(float previousVolume, float volume, uint inputBufferIndex, uint outputBufferIndex, int lastSampleIndex, Memory<VoiceState> state, int nodeId)
	{
		MixRampCommand mixRampCommand = _mixRampCommandPool.Allocate().Initialize(previousVolume, volume, inputBufferIndex, outputBufferIndex, lastSampleIndex, state, nodeId);
		mixRampCommand.EstimatedProcessingTime = _commandProcessingTimeEstimator.Estimate(mixRampCommand);
		AddCommand(mixRampCommand);
	}

	public void GenerateBiquadFilterAndMix(float previousVolume, float volume, uint inputBufferIndex, uint outputBufferIndex, int lastSampleIndex, Memory<VoiceState> state, ref BiquadFilterParameter2 filter, Memory<BiquadFilterState> biquadFilterState, Memory<BiquadFilterState> previousBiquadFilterState, bool needInitialization, bool hasVolumeRamp, bool isFirstMixBuffer, int nodeId)
	{
		BiquadFilterAndMixCommand biquadFilterAndMixCommand = _biquadFilterAndMixCommandPool.Allocate().Initialize(previousVolume, volume, inputBufferIndex, outputBufferIndex, lastSampleIndex, state, ref filter, biquadFilterState, previousBiquadFilterState, needInitialization, hasVolumeRamp, isFirstMixBuffer, nodeId);
		biquadFilterAndMixCommand.EstimatedProcessingTime = _commandProcessingTimeEstimator.Estimate(biquadFilterAndMixCommand);
		AddCommand(biquadFilterAndMixCommand);
	}

	public void GenerateMultiTapBiquadFilterAndMix(float previousVolume, float volume, uint inputBufferIndex, uint outputBufferIndex, int lastSampleIndex, Memory<VoiceState> state, ref BiquadFilterParameter2 filter0, ref BiquadFilterParameter2 filter1, Memory<BiquadFilterState> biquadFilterState0, Memory<BiquadFilterState> biquadFilterState1, Memory<BiquadFilterState> previousBiquadFilterState0, Memory<BiquadFilterState> previousBiquadFilterState1, bool needInitialization0, bool needInitialization1, bool hasVolumeRamp, bool isFirstMixBuffer, int nodeId)
	{
		MultiTapBiquadFilterAndMixCommand multiTapBiquadFilterAndMixCommand = _multiTapBiquadFilterAndMixCommandPool.Allocate().Initialize(previousVolume, volume, inputBufferIndex, outputBufferIndex, lastSampleIndex, state, ref filter0, ref filter1, biquadFilterState0, biquadFilterState1, previousBiquadFilterState0, previousBiquadFilterState1, needInitialization0, needInitialization1, hasVolumeRamp, isFirstMixBuffer, nodeId);
		multiTapBiquadFilterAndMixCommand.EstimatedProcessingTime = _commandProcessingTimeEstimator.Estimate(multiTapBiquadFilterAndMixCommand);
		AddCommand(multiTapBiquadFilterAndMixCommand);
	}

	public void GenerateDepopForMixBuffers(Memory<float> depopBuffer, uint bufferOffset, uint bufferCount, int nodeId, uint sampleRate)
	{
		DepopForMixBuffersCommand depopForMixBuffersCommand = _depopForMixBuffersCommandPool.Allocate().Initialize(depopBuffer, bufferOffset, bufferCount, nodeId, sampleRate);
		depopForMixBuffersCommand.EstimatedProcessingTime = _commandProcessingTimeEstimator.Estimate(depopForMixBuffersCommand);
		AddCommand(depopForMixBuffersCommand);
	}

	public void GenerateCopyMixBuffer(uint inputBufferIndex, uint outputBufferIndex, int nodeId)
	{
		CopyMixBufferCommand copyMixBufferCommand = _copyMixBufferCommandPool.Allocate().Initialize(inputBufferIndex, outputBufferIndex, nodeId);
		copyMixBufferCommand.EstimatedProcessingTime = _commandProcessingTimeEstimator.Estimate(copyMixBufferCommand);
		AddCommand(copyMixBufferCommand);
	}

	public void GenerateMix(uint inputBufferIndex, uint outputBufferIndex, int nodeId, float volume)
	{
		MixCommand mixCommand = _mixCommandPool.Allocate().Initialize(inputBufferIndex, outputBufferIndex, nodeId, volume);
		mixCommand.EstimatedProcessingTime = _commandProcessingTimeEstimator.Estimate(mixCommand);
		AddCommand(mixCommand);
	}

	public void GenerateReverbEffect(uint bufferOffset, ReverbParameter parameter, Memory<ReverbState> state, bool isEnabled, ulong workBuffer, int nodeId, bool isLongSizePreDelaySupported, bool newEffectChannelMappingSupported)
	{
		if (parameter.IsChannelCountValid())
		{
			ReverbCommand reverbCommand = _reverbCommandPool.Allocate().Initialize(bufferOffset, parameter, state, isEnabled, workBuffer, nodeId, isLongSizePreDelaySupported, newEffectChannelMappingSupported);
			reverbCommand.EstimatedProcessingTime = _commandProcessingTimeEstimator.Estimate(reverbCommand);
			AddCommand(reverbCommand);
		}
	}

	public void GenerateReverb3dEffect(uint bufferOffset, Reverb3dParameter parameter, Memory<Reverb3dState> state, bool isEnabled, ulong workBuffer, int nodeId, bool newEffectChannelMappingSupported)
	{
		if (parameter.IsChannelCountValid())
		{
			Reverb3dCommand reverb3dCommand = _reverb3dCommandPool.Allocate().Initialize(bufferOffset, parameter, state, isEnabled, workBuffer, nodeId, newEffectChannelMappingSupported);
			reverb3dCommand.EstimatedProcessingTime = _commandProcessingTimeEstimator.Estimate(reverb3dCommand);
			AddCommand(reverb3dCommand);
		}
	}

	public void GenerateDelayEffect(uint bufferOffset, DelayParameter parameter, Memory<DelayState> state, bool isEnabled, ulong workBuffer, int nodeId, bool newEffectChannelMappingSupported)
	{
		if (parameter.IsChannelCountValid())
		{
			DelayCommand delayCommand = _delayCommandPool.Allocate().Initialize(bufferOffset, parameter, state, isEnabled, workBuffer, nodeId, newEffectChannelMappingSupported);
			delayCommand.EstimatedProcessingTime = _commandProcessingTimeEstimator.Estimate(delayCommand);
			AddCommand(delayCommand);
		}
	}

	public void GenerateLimiterEffectVersion1(uint bufferOffset, LimiterParameter parameter, Memory<LimiterState> state, bool isEnabled, ulong workBuffer, int nodeId)
	{
		if (parameter.IsChannelCountValid())
		{
			LimiterCommandVersion1 limiterCommandVersion = _limiterCommandVersion1Pool.Allocate().Initialize(bufferOffset, parameter, state, isEnabled, workBuffer, nodeId);
			limiterCommandVersion.EstimatedProcessingTime = _commandProcessingTimeEstimator.Estimate(limiterCommandVersion);
			AddCommand(limiterCommandVersion);
		}
	}

	public void GenerateLimiterEffectVersion2(uint bufferOffset, LimiterParameter parameter, Memory<LimiterState> state, Memory<EffectResultState> effectResultState, bool isEnabled, ulong workBuffer, int nodeId)
	{
		if (parameter.IsChannelCountValid())
		{
			LimiterCommandVersion2 limiterCommandVersion = _limiterCommandVersion2Pool.Allocate().Initialize(bufferOffset, parameter, state, effectResultState, isEnabled, workBuffer, nodeId);
			limiterCommandVersion.EstimatedProcessingTime = _commandProcessingTimeEstimator.Estimate(limiterCommandVersion);
			AddCommand(limiterCommandVersion);
		}
	}

	public void GenerateAuxEffect(uint bufferOffset, byte inputBufferOffset, byte outputBufferOffset, ref AuxiliaryBufferAddresses state, bool isEnabled, uint countMax, ulong outputBuffer, ulong inputBuffer, uint updateCount, uint writeOffset, int nodeId)
	{
		if (state.SendBufferInfoBase != 0L && state.ReturnBufferInfoBase != 0L)
		{
			AuxiliaryBufferCommand auxiliaryBufferCommand = _auxiliaryBufferCommandPool.Allocate().Initialize(bufferOffset, inputBufferOffset, outputBufferOffset, ref state, isEnabled, countMax, outputBuffer, inputBuffer, updateCount, writeOffset, nodeId);
			auxiliaryBufferCommand.EstimatedProcessingTime = _commandProcessingTimeEstimator.Estimate(auxiliaryBufferCommand);
			AddCommand(auxiliaryBufferCommand);
		}
	}

	public void GenerateCaptureEffect(uint bufferOffset, byte inputBufferOffset, ulong sendBufferInfo, bool isEnabled, uint countMax, ulong outputBuffer, uint updateCount, uint writeOffset, int nodeId)
	{
		if (sendBufferInfo != 0L)
		{
			CaptureBufferCommand captureBufferCommand = _captureBufferCommandPool.Allocate().Initialize(bufferOffset, inputBufferOffset, sendBufferInfo, isEnabled, countMax, outputBuffer, updateCount, writeOffset, nodeId);
			captureBufferCommand.EstimatedProcessingTime = _commandProcessingTimeEstimator.Estimate(captureBufferCommand);
			AddCommand(captureBufferCommand);
		}
	}

	public void GenerateCompressorEffect(uint bufferOffset, CompressorParameter parameter, Memory<CompressorState> state, Memory<EffectResultState> effectResultState, bool isEnabled, int nodeId)
	{
		if (parameter.IsChannelCountValid())
		{
			CompressorCommand compressorCommand = _compressorCommandPool.Allocate().Initialize(bufferOffset, parameter, state, effectResultState, isEnabled, nodeId);
			compressorCommand.EstimatedProcessingTime = _commandProcessingTimeEstimator.Estimate(compressorCommand);
			AddCommand(compressorCommand);
		}
	}

	public void GenerateVolume(float volume, uint bufferOffset, int nodeId)
	{
		VolumeCommand volumeCommand = _volumeCommandPool.Allocate().Initialize(volume, bufferOffset, nodeId);
		volumeCommand.EstimatedProcessingTime = _commandProcessingTimeEstimator.Estimate(volumeCommand);
		AddCommand(volumeCommand);
	}

	public void GenerateCircularBuffer(uint bufferOffset, CircularBufferSink sink, int nodeId)
	{
		CircularBufferSinkCommand circularBufferSinkCommand = _circularBufferSinkCommandPool.Allocate().Initialize(bufferOffset, ref sink.Parameter, ref sink.CircularBufferAddressInfo, sink.CurrentWriteOffset, nodeId);
		circularBufferSinkCommand.EstimatedProcessingTime = _commandProcessingTimeEstimator.Estimate(circularBufferSinkCommand);
		AddCommand(circularBufferSinkCommand);
	}

	public void GenerateDownMixSurroundToStereo(uint bufferOffset, Span<byte> inputBufferOffset, Span<byte> outputBufferOffset, float[] downMixParameter, int nodeId)
	{
		DownMixSurroundToStereoCommand downMixSurroundToStereoCommand = _downMixSurroundToStereoCommandPool.Allocate().Initialize(bufferOffset, inputBufferOffset, outputBufferOffset, downMixParameter, nodeId);
		downMixSurroundToStereoCommand.EstimatedProcessingTime = _commandProcessingTimeEstimator.Estimate(downMixSurroundToStereoCommand);
		AddCommand(downMixSurroundToStereoCommand);
	}

	public void GenerateUpsample(uint bufferOffset, UpsamplerInfo upsampler, uint inputCount, Span<byte> inputBufferOffset, uint bufferCountPerSample, uint sampleCount, uint sampleRate, int nodeId)
	{
		UpsampleCommand upsampleCommand = _upsampleCommandPool.Allocate().Initialize(bufferOffset, upsampler, inputCount, inputBufferOffset, bufferCountPerSample, sampleCount, sampleRate, nodeId);
		upsampleCommand.EstimatedProcessingTime = _commandProcessingTimeEstimator.Estimate(upsampleCommand);
		AddCommand(upsampleCommand);
	}

	public void GenerateDeviceSink(uint bufferOffset, DeviceSink sink, int sessionId, Memory<float> buffer, int nodeId)
	{
		DeviceSinkCommand deviceSinkCommand = _deviceSinkCommandPool.Allocate().Initialize(bufferOffset, sink, sessionId, buffer, nodeId);
		deviceSinkCommand.EstimatedProcessingTime = _commandProcessingTimeEstimator.Estimate(deviceSinkCommand);
		AddCommand(deviceSinkCommand);
	}

	public void GenerateFillBuffer(SplitterDestination destination, float value, int length, int nodeId)
	{
		FillBufferCommand fillBufferCommand = _fillBufferCommandPool.Allocate().Initialize(destination, length, value, nodeId);
		fillBufferCommand.EstimatedProcessingTime = _commandProcessingTimeEstimator.Estimate(fillBufferCommand);
		AddCommand(fillBufferCommand);
	}
}
