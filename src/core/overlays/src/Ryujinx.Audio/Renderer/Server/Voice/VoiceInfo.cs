using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Ryujinx.Audio.Common;
using Ryujinx.Audio.Renderer.Common;
using Ryujinx.Audio.Renderer.Dsp;
using Ryujinx.Audio.Renderer.Parameter;
using Ryujinx.Audio.Renderer.Server.MemoryPool;
using Ryujinx.Audio.Renderer.Server.Types;
using Ryujinx.Common;
using Ryujinx.Common.Memory;
using Ryujinx.Common.Utilities;

namespace Ryujinx.Audio.Renderer.Server.Voice;

[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct VoiceInfo
{
	[StructLayout(LayoutKind.Sequential, Size = 2)]
	private struct BiquadFilterNeedInitializationArrayStruct
	{
	}

	public const int Alignment = 16;

	private static readonly ObjectPool<Memory<VoiceState>[]> voiceStatesPool = new ObjectPool<Memory<VoiceState>[]>(() => new Memory<VoiceState>[6], 256);

	[MarshalAs(UnmanagedType.I1)]
	public bool InUse;

	[MarshalAs(UnmanagedType.I1)]
	public bool IsNew;

	[MarshalAs(UnmanagedType.I1)]
	public bool WasPlaying;

	public SampleFormat SampleFormat;

	public uint SampleRate;

	public uint ChannelsCount;

	public int Id;

	public int NodeId;

	public int MixId;

	public Ryujinx.Audio.Renderer.Server.Types.PlayState PlayState;

	public Ryujinx.Audio.Renderer.Server.Types.PlayState PreviousPlayState;

	public uint Priority;

	public uint SortingOrder;

	public float Pitch;

	public float Volume;

	public float PreviousVolume;

	public Array2<BiquadFilterParameter2> BiquadFilters;

	public uint WaveBuffersCount;

	public uint WaveBuffersIndex;

	public DecodingBehaviour DecodingBehaviour;

	public AddressInfo DataSourceStateAddressInfo;

	public Array4<WaveBuffer> WaveBuffers;

	public Array6<int> ChannelResourceIds;

	public uint SplitterId;

	public SampleRateConversionQuality SrcQuality;

	[MarshalAs(UnmanagedType.I1)]
	public bool VoiceDropFlag;

	[MarshalAs(UnmanagedType.I1)]
	public bool DataSourceStateUnmapped;

	[MarshalAs(UnmanagedType.I1)]
	public bool BufferInfoUnmapped;

	private BiquadFilterNeedInitializationArrayStruct _biquadFilterNeedInitialization;

	public byte FlushWaveBufferCount;

	private static List<BehaviourParameter.ErrorInfo> _waveBufferUpdaterErrorInfosList;

	public Span<bool> BiquadFilterNeedInitialization => SpanHelpers.AsSpan<BiquadFilterNeedInitializationArrayStruct, bool>(ref _biquadFilterNeedInitialization);

	public void Initialize()
	{
		IsNew = false;
		VoiceDropFlag = false;
		DataSourceStateUnmapped = false;
		BufferInfoUnmapped = false;
		FlushWaveBufferCount = 0;
		PlayState = Ryujinx.Audio.Renderer.Server.Types.PlayState.Stopped;
		Priority = 255u;
		Id = 0;
		NodeId = 0;
		SampleRate = 0u;
		SampleFormat = SampleFormat.Invalid;
		ChannelsCount = 0u;
		Pitch = 0f;
		Volume = 0f;
		PreviousVolume = 0f;
		BiquadFilters.AsSpan().Clear();
		WaveBuffersCount = 0u;
		WaveBuffersIndex = 0u;
		MixId = int.MaxValue;
		SplitterId = uint.MaxValue;
		DataSourceStateAddressInfo.Setup(0uL, 0uL);
		InitializeWaveBuffers();
		if (_waveBufferUpdaterErrorInfosList == null)
		{
			_waveBufferUpdaterErrorInfosList = new List<BehaviourParameter.ErrorInfo>();
		}
	}

	private void InitializeWaveBuffers()
	{
		Span<WaveBuffer> span = WaveBuffers.AsSpan();
		for (int i = 0; i < span.Length; i++)
		{
			span[i].StartSampleOffset = 0u;
			span[i].EndSampleOffset = 0u;
			span[i].ShouldLoop = false;
			span[i].IsEndOfStream = false;
			span[i].BufferAddressInfo.Setup(0uL, 0uL);
			span[i].ContextAddressInfo.Setup(0uL, 0uL);
			span[i].IsSendToAudioProcessor = true;
		}
	}

	public readonly bool ShouldSkip()
	{
		if (InUse && WaveBuffersCount != 0 && !DataSourceStateUnmapped && !BufferInfoUnmapped)
		{
			return VoiceDropFlag;
		}
		return true;
	}

	public readonly bool HasAnyDestination()
	{
		if (MixId == int.MaxValue)
		{
			return SplitterId != uint.MaxValue;
		}
		return true;
	}

	private readonly bool ShouldUpdateParameters2(in VoiceInParameter2 parameter)
	{
		if (DataSourceStateAddressInfo.CpuAddress == parameter.DataSourceStateAddress)
		{
			return DataSourceStateAddressInfo.Size != parameter.DataSourceStateSize;
		}
		if (DataSourceStateAddressInfo.CpuAddress == parameter.DataSourceStateAddress && DataSourceStateAddressInfo.Size == parameter.DataSourceStateSize)
		{
			return DataSourceStateUnmapped;
		}
		return true;
	}

	private readonly bool ShouldUpdateParameters1(in VoiceInParameter1 parameter)
	{
		if (DataSourceStateAddressInfo.CpuAddress == parameter.DataSourceStateAddress)
		{
			return DataSourceStateAddressInfo.Size != parameter.DataSourceStateSize;
		}
		if (DataSourceStateAddressInfo.CpuAddress == parameter.DataSourceStateAddress && DataSourceStateAddressInfo.Size == parameter.DataSourceStateSize)
		{
			return DataSourceStateUnmapped;
		}
		return true;
	}

	public void UpdateParameters2(out BehaviourParameter.ErrorInfo outErrorInfo, in VoiceInParameter2 parameter, PoolMapper poolMapper, ref BehaviourInfo behaviourInfo)
	{
		InUse = parameter.InUse;
		Id = parameter.Id;
		NodeId = parameter.NodeId;
		UpdatePlayState(parameter.PlayState);
		SrcQuality = parameter.SrcQuality;
		Priority = parameter.Priority;
		SortingOrder = parameter.SortingOrder;
		SampleRate = parameter.SampleRate;
		SampleFormat = parameter.SampleFormat;
		ChannelsCount = parameter.ChannelCount;
		Pitch = parameter.Pitch;
		Volume = parameter.Volume;
		parameter.BiquadFilters.AsSpan().CopyTo(BiquadFilters.AsSpan());
		WaveBuffersCount = parameter.WaveBuffersCount;
		WaveBuffersIndex = parameter.WaveBuffersIndex;
		if (behaviourInfo.IsFlushVoiceWaveBuffersSupported())
		{
			FlushWaveBufferCount += parameter.FlushWaveBufferCount;
		}
		MixId = parameter.MixId;
		if (behaviourInfo.IsSplitterSupported())
		{
			SplitterId = parameter.SplitterId;
		}
		else
		{
			SplitterId = uint.MaxValue;
		}
		parameter.ChannelResourceIds.AsSpan().CopyTo(ChannelResourceIds.AsSpan());
		DecodingBehaviour decodingBehaviour = DecodingBehaviour.Default;
		if (behaviourInfo.IsDecodingBehaviourFlagSupported())
		{
			decodingBehaviour = parameter.DecodingBehaviourFlags;
		}
		DecodingBehaviour = decodingBehaviour;
		if (parameter.ResetVoiceDropFlag)
		{
			VoiceDropFlag = false;
		}
		if (ShouldUpdateParameters2(in parameter))
		{
			DataSourceStateUnmapped = !poolMapper.TryAttachBuffer(out outErrorInfo, ref DataSourceStateAddressInfo, parameter.DataSourceStateAddress, parameter.DataSourceStateSize);
		}
		else
		{
			outErrorInfo = default;
		}
	}

	public void UpdateParameters1(out BehaviourParameter.ErrorInfo outErrorInfo, in VoiceInParameter1 parameter, PoolMapper poolMapper, ref BehaviourInfo behaviourInfo)
	{
		InUse = parameter.InUse;
		Id = parameter.Id;
		NodeId = parameter.NodeId;
		UpdatePlayState(parameter.PlayState);
		SrcQuality = parameter.SrcQuality;
		Priority = parameter.Priority;
		SortingOrder = parameter.SortingOrder;
		SampleRate = parameter.SampleRate;
		SampleFormat = parameter.SampleFormat;
		ChannelsCount = parameter.ChannelCount;
		Pitch = parameter.Pitch;
		Volume = parameter.Volume;
		BiquadFilters[0] = BiquadFilterHelper.ToBiquadFilterParameter2(parameter.BiquadFilters[0]);
		BiquadFilters[1] = BiquadFilterHelper.ToBiquadFilterParameter2(parameter.BiquadFilters[1]);
		WaveBuffersCount = parameter.WaveBuffersCount;
		WaveBuffersIndex = parameter.WaveBuffersIndex;
		if (behaviourInfo.IsFlushVoiceWaveBuffersSupported())
		{
			FlushWaveBufferCount += parameter.FlushWaveBufferCount;
		}
		MixId = parameter.MixId;
		if (behaviourInfo.IsSplitterSupported())
		{
			SplitterId = parameter.SplitterId;
		}
		else
		{
			SplitterId = uint.MaxValue;
		}
		parameter.ChannelResourceIds.AsSpan().CopyTo(ChannelResourceIds.AsSpan());
		DecodingBehaviour decodingBehaviour = DecodingBehaviour.Default;
		if (behaviourInfo.IsDecodingBehaviourFlagSupported())
		{
			decodingBehaviour = parameter.DecodingBehaviourFlags;
		}
		DecodingBehaviour = decodingBehaviour;
		if (parameter.ResetVoiceDropFlag)
		{
			VoiceDropFlag = false;
		}
		if (ShouldUpdateParameters1(in parameter))
		{
			DataSourceStateUnmapped = !poolMapper.TryAttachBuffer(out outErrorInfo, ref DataSourceStateAddressInfo, parameter.DataSourceStateAddress, parameter.DataSourceStateSize);
		}
		else
		{
			outErrorInfo = default;
		}
	}

	public void UpdatePlayState(Ryujinx.Audio.Renderer.Common.PlayState userPlayState)
	{
		Ryujinx.Audio.Renderer.Server.Types.PlayState playState = (PreviousPlayState = PlayState);
		Ryujinx.Audio.Renderer.Server.Types.PlayState playState2;
		switch (userPlayState)
		{
		case Ryujinx.Audio.Renderer.Common.PlayState.Start:
			playState2 = Ryujinx.Audio.Renderer.Server.Types.PlayState.Started;
			break;
		case Ryujinx.Audio.Renderer.Common.PlayState.Stop:
			if (playState == Ryujinx.Audio.Renderer.Server.Types.PlayState.Stopped)
			{
				return;
			}
			playState2 = Ryujinx.Audio.Renderer.Server.Types.PlayState.Stopping;
			break;
		case Ryujinx.Audio.Renderer.Common.PlayState.Pause:
			playState2 = Ryujinx.Audio.Renderer.Server.Types.PlayState.Paused;
			break;
		default:
			throw new NotImplementedException($"Unhandled PlayState.{userPlayState}");
		}
		PlayState = playState2;
	}

	public void WriteOutStatus2(ref VoiceOutStatus outStatus, in VoiceInParameter2 parameter, ReadOnlySpan<Memory<VoiceState>> voiceStates)
	{
		if (parameter.IsNew || IsNew)
		{
			IsNew = true;
			outStatus.VoiceDropFlag = false;
			outStatus.PlayedWaveBuffersCount = 0u;
			outStatus.PlayedSampleCount = 0uL;
		}
		else
		{
			ref VoiceState reference = ref voiceStates[0].Span[0];
			outStatus.VoiceDropFlag = VoiceDropFlag;
			outStatus.PlayedWaveBuffersCount = reference.WaveBufferConsumed;
			outStatus.PlayedSampleCount = reference.PlayedSampleCount;
		}
	}

	public void WriteOutStatus1(ref VoiceOutStatus outStatus, in VoiceInParameter1 parameter, ReadOnlySpan<Memory<VoiceState>> voiceStates)
	{
		if (parameter.IsNew || IsNew)
		{
			IsNew = true;
			outStatus.VoiceDropFlag = false;
			outStatus.PlayedWaveBuffersCount = 0u;
			outStatus.PlayedSampleCount = 0uL;
		}
		else
		{
			ref VoiceState reference = ref voiceStates[0].Span[0];
			outStatus.VoiceDropFlag = VoiceDropFlag;
			outStatus.PlayedWaveBuffersCount = reference.WaveBufferConsumed;
			outStatus.PlayedSampleCount = reference.PlayedSampleCount;
		}
	}

	public void UpdateWaveBuffers2(out BehaviourParameter.ErrorInfo[] errorInfos, in VoiceInParameter2 parameter, ReadOnlySpan<Memory<VoiceState>> voiceStates, PoolMapper mapper, ref BehaviourInfo behaviourInfo)
	{
		if (parameter.IsNew)
		{
			InitializeWaveBuffers();
			for (int i = 0; i < parameter.ChannelCount; i++)
			{
				voiceStates[i].Span[0].IsWaveBufferValid.Clear();
			}
		}
		ref VoiceState reference = ref voiceStates[0].Span[0];
		Span<WaveBuffer> span = WaveBuffers.AsSpan();
		Span<WaveBufferInternal> span2 = parameter.WaveBuffers.AsSpan();
		_waveBufferUpdaterErrorInfosList.Clear();
		for (int j = 0; j < 4; j++)
		{
			UpdateWaveBuffer(_waveBufferUpdaterErrorInfosList, ref span[j], ref span2[j], parameter.SampleFormat, reference.IsWaveBufferValid[j], mapper, ref behaviourInfo);
		}
		errorInfos = _waveBufferUpdaterErrorInfosList.ToArray();
	}

	public void UpdateWaveBuffers1(out BehaviourParameter.ErrorInfo[] errorInfos, in VoiceInParameter1 parameter, ReadOnlySpan<Memory<VoiceState>> voiceStates, PoolMapper mapper, ref BehaviourInfo behaviourInfo)
	{
		if (parameter.IsNew)
		{
			InitializeWaveBuffers();
			for (int i = 0; i < parameter.ChannelCount; i++)
			{
				voiceStates[i].Span[0].IsWaveBufferValid.Clear();
			}
		}
		ref VoiceState reference = ref voiceStates[0].Span[0];
		Span<WaveBuffer> span = WaveBuffers.AsSpan();
		Span<WaveBufferInternal> span2 = parameter.WaveBuffers.AsSpan();
		_waveBufferUpdaterErrorInfosList.Clear();
		for (int j = 0; j < 4; j++)
		{
			UpdateWaveBuffer(_waveBufferUpdaterErrorInfosList, ref span[j], ref span2[j], parameter.SampleFormat, reference.IsWaveBufferValid[j], mapper, ref behaviourInfo);
		}
		errorInfos = _waveBufferUpdaterErrorInfosList.ToArray();
	}

	private void UpdateWaveBuffer(List<BehaviourParameter.ErrorInfo> errorInfos, ref WaveBuffer waveBuffer, ref WaveBufferInternal inputWaveBuffer, SampleFormat sampleFormat, bool isValid, PoolMapper mapper, ref BehaviourInfo behaviourInfo)
	{
		if (!isValid && waveBuffer.IsSendToAudioProcessor && waveBuffer.BufferAddressInfo.CpuAddress != 0L)
		{
			mapper.ForceUnmap(ref waveBuffer.BufferAddressInfo);
			waveBuffer.BufferAddressInfo.Setup(0uL, 0uL);
		}
		if (inputWaveBuffer.SentToServer && !BufferInfoUnmapped)
		{
			return;
		}
		if (inputWaveBuffer.IsSampleOffsetValid(sampleFormat))
		{
			waveBuffer.IsSendToAudioProcessor = false;
			waveBuffer.StartSampleOffset = inputWaveBuffer.StartSampleOffset;
			waveBuffer.EndSampleOffset = inputWaveBuffer.EndSampleOffset;
			waveBuffer.ShouldLoop = inputWaveBuffer.ShouldLoop;
			waveBuffer.IsEndOfStream = inputWaveBuffer.IsEndOfStream;
			waveBuffer.LoopStartSampleOffset = inputWaveBuffer.LoopFirstSampleOffset;
			waveBuffer.LoopEndSampleOffset = inputWaveBuffer.LoopLastSampleOffset;
			waveBuffer.LoopCount = inputWaveBuffer.LoopCount;
			BufferInfoUnmapped = !mapper.TryAttachBuffer(out var errorInfo, ref waveBuffer.BufferAddressInfo, inputWaveBuffer.Address, inputWaveBuffer.Size);
			if (errorInfo.ErrorCode != ResultCode.Success)
			{
				errorInfos.Add(errorInfo);
			}
			if (sampleFormat == SampleFormat.Adpcm && behaviourInfo.IsAdpcmLoopContextBugFixed() && inputWaveBuffer.ContextAddress != 0L)
			{
				bool flag = mapper.TryAttachBuffer(out var errorInfo2, ref waveBuffer.ContextAddressInfo, inputWaveBuffer.ContextAddress, inputWaveBuffer.ContextSize);
				if (errorInfo2.ErrorCode != ResultCode.Success)
				{
					errorInfos.Add(errorInfo2);
				}
				if (!flag || BufferInfoUnmapped)
				{
					BufferInfoUnmapped = true;
				}
				else
				{
					BufferInfoUnmapped = false;
				}
			}
			else
			{
				waveBuffer.ContextAddressInfo.Setup(0uL, 0uL);
			}
		}
		else
		{
			errorInfos.Add(new BehaviourParameter.ErrorInfo
			{
				ErrorCode = ResultCode.InvalidAddressInfo,
				ExtraErrorInfo = inputWaveBuffer.Address
			});
		}
	}

	private void ResetResources(VoiceContext context)
	{
		Span<int> span = ChannelResourceIds.AsSpan();
		for (int i = 0; i < ChannelsCount; i++)
		{
			int id = span[i];
			ref VoiceChannelResource channelResource = ref context.GetChannelResource(id);
			MemoryMarshal.Cast<VoiceState, byte>(context.GetUpdateStateForDsp(id).Span).Clear();
			channelResource.UpdateState();
		}
	}

	private void FlushWaveBuffers(uint waveBufferCount, Memory<VoiceState>[] voiceStates, uint channelCount)
	{
		uint num = WaveBuffersIndex;
		Span<WaveBuffer> span = WaveBuffers.AsSpan();
		for (int i = 0; i < waveBufferCount; i++)
		{
			span[(int)num].IsSendToAudioProcessor = true;
			for (int j = 0; j < channelCount; j++)
			{
				ref VoiceState reference = ref voiceStates[j].Span[0];
				if (!span[(int)num].IsSendToAudioProcessor || reference.IsWaveBufferValid[(int)num])
				{
					reference.WaveBufferIndex = (reference.WaveBufferIndex + 1) % 4;
					reference.WaveBufferConsumed++;
					reference.IsWaveBufferValid[(int)num] = false;
				}
			}
			span[(int)num].IsSendToAudioProcessor = true;
			num = (num + 1) % 4;
		}
	}

	public bool UpdateParametersForCommandGeneration(Memory<VoiceState>[] voiceStates)
	{
		if (FlushWaveBufferCount != 0)
		{
			FlushWaveBuffers(FlushWaveBufferCount, voiceStates, ChannelsCount);
			FlushWaveBufferCount = 0;
		}
		switch (PlayState)
		{
		case Ryujinx.Audio.Renderer.Server.Types.PlayState.Started:
		{
			Span<WaveBuffer> span2 = WaveBuffers.AsSpan();
			for (int m = 0; m < span2.Length; m++)
			{
				ref WaveBuffer reference4 = ref span2[m];
				if (!reference4.IsSendToAudioProcessor)
				{
					for (int n = 0; n < ChannelsCount; n++)
					{
						voiceStates[n].Span[0].IsWaveBufferValid[m] = true;
					}
					reference4.IsSendToAudioProcessor = true;
				}
			}
			WasPlaying = false;
			ref VoiceState reference5 = ref voiceStates[0].Span[0];
			for (int num = 0; num < reference5.IsWaveBufferValid.Length; num++)
			{
				if (reference5.IsWaveBufferValid[num])
				{
					return true;
				}
			}
			return false;
		}
		case Ryujinx.Audio.Renderer.Server.Types.PlayState.Stopping:
		{
			Span<WaveBuffer> span2 = WaveBuffers.AsSpan();
			for (int j = 0; j < span2.Length; j++)
			{
				span2[j].IsSendToAudioProcessor = true;
				for (int k = 0; k < ChannelsCount; k++)
				{
					ref VoiceState reference2 = ref voiceStates[k].Span[0];
					if (reference2.IsWaveBufferValid[j])
					{
						reference2.WaveBufferIndex = (reference2.WaveBufferIndex + 1) % 4;
						reference2.WaveBufferConsumed++;
					}
					reference2.IsWaveBufferValid[j] = false;
				}
			}
			for (int l = 0; l < ChannelsCount; l++)
			{
				ref VoiceState reference3 = ref voiceStates[l].Span[0];
				reference3.Offset = 0;
				reference3.PlayedSampleCount = 0uL;
				reference3.Pitch.AsSpan().Clear();
				reference3.Fraction = 0f;
				reference3.LoopContext = default;
			}
			PlayState = Ryujinx.Audio.Renderer.Server.Types.PlayState.Stopped;
			WasPlaying = PreviousPlayState == Ryujinx.Audio.Renderer.Server.Types.PlayState.Started;
			return WasPlaying;
		}
		case Ryujinx.Audio.Renderer.Server.Types.PlayState.Stopped:
		case Ryujinx.Audio.Renderer.Server.Types.PlayState.Paused:
		{
			Span<WaveBuffer> span = WaveBuffers.AsSpan();
			for (int i = 0; i < span.Length; i++)
			{
				ref WaveBuffer reference = ref span[i];
				reference.BufferAddressInfo.GetReference(markUsed: true);
				reference.ContextAddressInfo.GetReference(markUsed: true);
			}
			if (SampleFormat == SampleFormat.Adpcm && DataSourceStateAddressInfo.CpuAddress != 0L)
			{
				DataSourceStateAddressInfo.GetReference(markUsed: true);
			}
			WasPlaying = PreviousPlayState == Ryujinx.Audio.Renderer.Server.Types.PlayState.Started;
			return WasPlaying;
		}
		default:
			throw new NotImplementedException($"{PlayState}");
		}
	}

	public bool UpdateForCommandGeneration(VoiceContext context)
	{
		if (IsNew)
		{
			ResetResources(context);
			PreviousVolume = Volume;
			IsNew = false;
		}
		Memory<VoiceState>[] array = voiceStatesPool.Allocate();
		Span<int> span = ChannelResourceIds.AsSpan();
		for (int i = 0; i < ChannelsCount; i++)
		{
			array[i] = context.GetUpdateStateForDsp(span[i]);
		}
		bool result = UpdateParametersForCommandGeneration(array);
		voiceStatesPool.Release(array);
		return result;
	}
}
