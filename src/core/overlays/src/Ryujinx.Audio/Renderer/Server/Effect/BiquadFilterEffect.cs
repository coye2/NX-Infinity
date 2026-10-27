using System;
using System.Runtime.InteropServices;
using Ryujinx.Audio.Renderer.Common;
using Ryujinx.Audio.Renderer.Dsp;
using Ryujinx.Audio.Renderer.Dsp.State;
using Ryujinx.Audio.Renderer.Parameter;
using Ryujinx.Audio.Renderer.Parameter.Effect;
using Ryujinx.Audio.Renderer.Server.MemoryPool;

namespace Ryujinx.Audio.Renderer.Server.Effect;

public class BiquadFilterEffect : BaseEffect
{
	public BiquadFilterEffectParameter2 Parameter;

	public int BiquadFilterEffectVersion;

	public Memory<BiquadFilterState> State { get; }

	public override EffectType TargetEffectType => EffectType.BiquadFilter;

	public BiquadFilterEffect(int version)
	{
		Parameter = default;
		State = new BiquadFilterState[6];
		BiquadFilterEffectVersion = version;
	}

	public override void Update(out BehaviourParameter.ErrorInfo updateErrorInfo, in EffectInParameterVersion1 parameter, PoolMapper mapper)
	{
		Update(out updateErrorInfo, in parameter, mapper);
	}

	public override void Update(out BehaviourParameter.ErrorInfo updateErrorInfo, in EffectInParameterVersion2 parameter, PoolMapper mapper)
	{
		Update(out updateErrorInfo, in parameter, mapper);
	}

	public void Update<T>(out BehaviourParameter.ErrorInfo updateErrorInfo, in T parameter, PoolMapper mapper) where T : unmanaged, IEffectInParameter
	{
		T val;
		BiquadFilterEffectParameter2 parameter2;
		if (BiquadFilterEffectVersion == 2)
		{
			val = parameter;
			parameter2 = MemoryMarshal.Cast<byte, BiquadFilterEffectParameter2>(val.SpecificData)[0];
		}
		else
		{
			val = parameter;
			parameter2 = BiquadFilterHelper.ToBiquadFilterEffectParameter2(MemoryMarshal.Cast<byte, BiquadFilterEffectParameter1>(val.SpecificData)[0]);
		}
		if (parameter2.ChannelCount > 6 || !float.IsFinite(parameter2.Numerator[0]) || !float.IsFinite(parameter2.Numerator[1]) || !float.IsFinite(parameter2.Numerator[2]) || !float.IsFinite(parameter2.Denominator[0]) || !float.IsFinite(parameter2.Denominator[1]))
		{
			updateErrorInfo = new BehaviourParameter.ErrorInfo
			{
				ErrorCode = ResultCode.InvalidUpdateInfo
			};
			return;
		}
		UpdateParameterBase(in parameter);
		Parameter = parameter2;
		val = parameter;
		IsEnabled = val.IsEnabled;
		updateErrorInfo = default;
	}

	public override void UpdateForCommandGeneration()
	{
		UpdateUsageStateForCommandGeneration();
		Parameter.Status = UsageState.Enabled;
	}
}
