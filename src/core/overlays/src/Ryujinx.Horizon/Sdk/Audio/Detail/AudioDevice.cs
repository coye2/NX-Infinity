using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Ryujinx.Audio.Renderer.Device;
using Ryujinx.Audio.Renderer.Server;
using Ryujinx.Horizon.Common;
using Ryujinx.Horizon.Sdk.Applet;
using Ryujinx.Horizon.Sdk.OsTypes;
using Ryujinx.Horizon.Sdk.Sf;
using Ryujinx.Horizon.Sdk.Sf.Cmif;
using Ryujinx.Horizon.Sdk.Sf.Hipc;
using Ryujinx.Memory;

namespace Ryujinx.Horizon.Sdk.Audio.Detail;

internal class AudioDevice : IAudioDevice, IServiceObject, IDisposable
{
	private readonly VirtualDeviceSessionRegistry _registry;

	private readonly VirtualDeviceSession[] _sessions;

	private readonly bool _isUsbDeviceSupported;

	private SystemEventType _audioEvent;

	private SystemEventType _audioInputEvent;

	private SystemEventType _audioOutputEvent;

	public AudioDevice(VirtualDeviceSessionRegistry registry, AppletResourceUserId appletResourceId, uint revision)
	{
		_registry = registry;
		BehaviourInfo behaviourInfo = new BehaviourInfo();
		behaviourInfo.SetUserRevision((int)revision);
		_isUsbDeviceSupported = behaviourInfo.IsAudioUsbDeviceOutputSupported();
		_sessions = registry.GetSessionByAppletResourceId(appletResourceId.Id);
		Os.CreateSystemEvent(out _audioEvent, EventClearMode.AutoClear, interProcess: true);
		Os.CreateSystemEvent(out _audioInputEvent, EventClearMode.AutoClear, interProcess: true);
		Os.CreateSystemEvent(out _audioOutputEvent, EventClearMode.AutoClear, interProcess: true);
	}

	private bool TryGetDeviceByName(out VirtualDeviceSession result, string name, bool ignoreRevLimitation = false)
	{
		result = null;
		VirtualDeviceSession[] sessions = _sessions;
		foreach (VirtualDeviceSession virtualDeviceSession in sessions)
		{
			if (virtualDeviceSession.Device.Name.Equals(name))
			{
				if (!ignoreRevLimitation && !_isUsbDeviceSupported && virtualDeviceSession.Device.IsUsbDevice())
				{
					return false;
				}
				result = virtualDeviceSession;
				return true;
			}
		}
		return false;
	}

	[CmifCommand(0u)]
	public Result ListAudioDeviceName([Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<DeviceName> names, out int nameCount)
	{
		int num = 0;
		VirtualDeviceSession[] sessions = _sessions;
		foreach (VirtualDeviceSession virtualDeviceSession in sessions)
		{
			if (_isUsbDeviceSupported || !virtualDeviceSession.Device.IsUsbDevice())
			{
				if (num >= names.Length)
				{
					break;
				}
				names[num] = new DeviceName(virtualDeviceSession.Device.Name);
				num++;
			}
		}
		nameCount = num;
		return Result.Success;
	}

	[CmifCommand(1u)]
	public Result SetAudioDeviceOutputVolume([Buffer(HipcBufferFlags.In | HipcBufferFlags.MapAlias)] ReadOnlySpan<DeviceName> name, float volume)
	{
		if (name.Length > 0 && TryGetDeviceByName(out var result, name[0].ToString(), ignoreRevLimitation: true))
		{
			if (!_isUsbDeviceSupported && result.Device.IsUsbDevice())
			{
				result = _sessions[0];
			}
			result.Volume = volume;
		}
		return Result.Success;
	}

	[CmifCommand(2u)]
	public Result GetAudioDeviceOutputVolume([Buffer(HipcBufferFlags.In | HipcBufferFlags.MapAlias)] ReadOnlySpan<DeviceName> name, out float volume)
	{
		if (name.Length > 0 && TryGetDeviceByName(out var result, name[0].ToString()))
		{
			volume = result.Volume;
		}
		else
		{
			volume = 0f;
		}
		return Result.Success;
	}

	[CmifCommand(3u)]
	public Result GetActiveAudioDeviceName([Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<DeviceName> name)
	{
		VirtualDevice virtualDevice = _registry.ActiveDevice;
		if (!_isUsbDeviceSupported && virtualDevice.IsUsbDevice())
		{
			virtualDevice = VirtualDeviceSessionRegistry.DefaultDevice;
		}
		if (name.Length > 0)
		{
			name[0] = new DeviceName(virtualDevice.Name);
		}
		return Result.Success;
	}

	[CmifCommand(4u)]
	public Result QueryAudioDeviceSystemEvent([CopyHandle] out int eventHandle)
	{
		eventHandle = Os.GetReadableHandleOfSystemEvent(ref _audioEvent);
		return Result.Success;
	}

	[CmifCommand(5u)]
	public Result GetActiveChannelCount(out int channelCount)
	{
		VirtualDevice virtualDevice = _registry.ActiveDevice;
		if (!_isUsbDeviceSupported && virtualDevice.IsUsbDevice())
		{
			virtualDevice = VirtualDeviceSessionRegistry.DefaultDevice;
		}
		channelCount = (int)virtualDevice.ChannelCount;
		return Result.Success;
	}

	[CmifCommand(6u)]
	public Result ListAudioDeviceNameAuto([Buffer(HipcBufferFlags.Out | HipcBufferFlags.AutoSelect)] Span<DeviceName> names, out int nameCount)
	{
		return ListAudioDeviceName(names, out nameCount);
	}

	[CmifCommand(7u)]
	public Result SetAudioDeviceOutputVolumeAuto([Buffer(HipcBufferFlags.In | HipcBufferFlags.AutoSelect)] ReadOnlySpan<DeviceName> name, float volume)
	{
		return SetAudioDeviceOutputVolume(name, volume);
	}

	[CmifCommand(8u)]
	public Result GetAudioDeviceOutputVolumeAuto([Buffer(HipcBufferFlags.In | HipcBufferFlags.AutoSelect)] ReadOnlySpan<DeviceName> name, out float volume)
	{
		return GetAudioDeviceOutputVolume(name, out volume);
	}

	[CmifCommand(10u)]
	public Result GetActiveAudioDeviceNameAuto([Buffer(HipcBufferFlags.Out | HipcBufferFlags.AutoSelect)] Span<DeviceName> name)
	{
		return GetActiveAudioDeviceName(name);
	}

	[CmifCommand(11u)]
	public Result QueryAudioDeviceInputEvent([CopyHandle] out int eventHandle)
	{
		eventHandle = Os.GetReadableHandleOfSystemEvent(ref _audioInputEvent);
		return Result.Success;
	}

	[CmifCommand(12u)]
	public Result QueryAudioDeviceOutputEvent([CopyHandle] out int eventHandle)
	{
		eventHandle = Os.GetReadableHandleOfSystemEvent(ref _audioOutputEvent);
		return Result.Success;
	}

	[CmifCommand(13u)]
	public Result GetActiveAudioOutputDeviceName([Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<DeviceName> name)
	{
		if (name.Length > 0)
		{
			name[0] = new DeviceName(_registry.ActiveDevice.GetOutputDeviceName());
		}
		return Result.Success;
	}

	[CmifCommand(14u)]
	public Result ListAudioOutputDeviceName([Buffer(HipcBufferFlags.Out | HipcBufferFlags.MapAlias)] Span<DeviceName> names, out int nameCount)
	{
		int num = 0;
		VirtualDeviceSession[] sessions = _sessions;
		foreach (VirtualDeviceSession virtualDeviceSession in sessions)
		{
			if (_isUsbDeviceSupported || !virtualDeviceSession.Device.IsUsbDevice())
			{
				if (num >= names.Length)
				{
					break;
				}
				names[num] = new DeviceName(virtualDeviceSession.Device.GetOutputDeviceName());
				num++;
			}
		}
		nameCount = num;
		return Result.Success;
	}

	[CmifCommand(15u)]
	public Result AcquireAudioOutputDeviceNotification([CopyHandle] out int eventHandle, ulong deviceId)
	{
		eventHandle = 0;
		return AudioResult.NotImplemented;
	}

	[CmifCommand(16u)]
	public Result ReleaseAudioOutputDeviceNotification(ulong deviceId)
	{
		return AudioResult.NotImplemented;
	}

	[CmifCommand(17u)]
	public Result AcquireAudioInputDeviceNotification([CopyHandle] out int eventHandle, ulong deviceId)
	{
		eventHandle = 0;
		return AudioResult.NotImplemented;
	}

	[CmifCommand(18u)]
	public Result ReleaseAudioInputDeviceNotification(ulong deviceId)
	{
		return AudioResult.NotImplemented;
	}

	[CmifCommand(19u)]
	public Result SetAudioDeviceOutputVolumeAutoTuneEnabled(bool enabled)
	{
		return AudioResult.NotImplemented;
	}

	[CmifCommand(20u)]
	public Result IsAudioDeviceOutputVolumeAutoTuneEnabled(out bool enabled)
	{
		enabled = false;
		return AudioResult.NotImplemented;
	}

	protected virtual void Dispose(bool disposing)
	{
		if (disposing)
		{
			Os.DestroySystemEvent(ref _audioEvent);
			Os.DestroySystemEvent(ref _audioInputEvent);
			Os.DestroySystemEvent(ref _audioOutputEvent);
		}
	}

	public void Dispose()
	{
		Dispose(disposing: true);
		GC.SuppressFinalize(this);
	}

	public IReadOnlyDictionary<int, CommandHandler> GetCommandHandlers()
	{
		return new KeyValuePair<int, CommandHandler>[20]
		{
			KeyValuePair.Create(0, new CommandHandler(ListAudioDeviceName, new CommandArg(HipcBufferFlags.Out | HipcBufferFlags.MapAlias, 0), new CommandArg(CommandArgType.OutArgument, Unsafe.SizeOf<int>(), Unsafe.SizeOf<int>()))),
			KeyValuePair.Create(1, new CommandHandler(SetAudioDeviceOutputVolume, new CommandArg(HipcBufferFlags.In | HipcBufferFlags.MapAlias, 0), new CommandArg(CommandArgType.InArgument, Unsafe.SizeOf<float>(), Unsafe.SizeOf<float>()))),
			KeyValuePair.Create(2, new CommandHandler(GetAudioDeviceOutputVolume, new CommandArg(HipcBufferFlags.In | HipcBufferFlags.MapAlias, 0), new CommandArg(CommandArgType.OutArgument, Unsafe.SizeOf<float>(), Unsafe.SizeOf<float>()))),
			KeyValuePair.Create(3, new CommandHandler(GetActiveAudioDeviceName, new CommandArg(HipcBufferFlags.Out | HipcBufferFlags.MapAlias, 0))),
			KeyValuePair.Create(4, new CommandHandler(QueryAudioDeviceSystemEvent, new CommandArg(CommandArgType.OutCopyHandle))),
			KeyValuePair.Create(5, new CommandHandler(GetActiveChannelCount, new CommandArg(CommandArgType.OutArgument, Unsafe.SizeOf<int>(), Unsafe.SizeOf<int>()))),
			KeyValuePair.Create(6, new CommandHandler(ListAudioDeviceNameAuto, new CommandArg(HipcBufferFlags.Out | HipcBufferFlags.AutoSelect, 0), new CommandArg(CommandArgType.OutArgument, Unsafe.SizeOf<int>(), Unsafe.SizeOf<int>()))),
			KeyValuePair.Create(7, new CommandHandler(SetAudioDeviceOutputVolumeAuto, new CommandArg(HipcBufferFlags.In | HipcBufferFlags.AutoSelect, 0), new CommandArg(CommandArgType.InArgument, Unsafe.SizeOf<float>(), Unsafe.SizeOf<float>()))),
			KeyValuePair.Create(8, new CommandHandler(GetAudioDeviceOutputVolumeAuto, new CommandArg(HipcBufferFlags.In | HipcBufferFlags.AutoSelect, 0), new CommandArg(CommandArgType.OutArgument, Unsafe.SizeOf<float>(), Unsafe.SizeOf<float>()))),
			KeyValuePair.Create(10, new CommandHandler(GetActiveAudioDeviceNameAuto, new CommandArg(HipcBufferFlags.Out | HipcBufferFlags.AutoSelect, 0))),
			KeyValuePair.Create(11, new CommandHandler(QueryAudioDeviceInputEvent, new CommandArg(CommandArgType.OutCopyHandle))),
			KeyValuePair.Create(12, new CommandHandler(QueryAudioDeviceOutputEvent, new CommandArg(CommandArgType.OutCopyHandle))),
			KeyValuePair.Create(13, new CommandHandler(GetActiveAudioOutputDeviceName, new CommandArg(HipcBufferFlags.Out | HipcBufferFlags.MapAlias, 0))),
			KeyValuePair.Create(14, new CommandHandler(ListAudioOutputDeviceName, new CommandArg(HipcBufferFlags.Out | HipcBufferFlags.MapAlias, 0), new CommandArg(CommandArgType.OutArgument, Unsafe.SizeOf<int>(), Unsafe.SizeOf<int>()))),
			KeyValuePair.Create(15, new CommandHandler(AcquireAudioOutputDeviceNotification, new CommandArg(CommandArgType.OutCopyHandle), new CommandArg(CommandArgType.InArgument, Unsafe.SizeOf<ulong>(), Unsafe.SizeOf<ulong>()))),
			KeyValuePair.Create(16, new CommandHandler(ReleaseAudioOutputDeviceNotification, new CommandArg(CommandArgType.InArgument, Unsafe.SizeOf<ulong>(), Unsafe.SizeOf<ulong>()))),
			KeyValuePair.Create(17, new CommandHandler(AcquireAudioInputDeviceNotification, new CommandArg(CommandArgType.OutCopyHandle), new CommandArg(CommandArgType.InArgument, Unsafe.SizeOf<ulong>(), Unsafe.SizeOf<ulong>()))),
			KeyValuePair.Create(18, new CommandHandler(ReleaseAudioInputDeviceNotification, new CommandArg(CommandArgType.InArgument, Unsafe.SizeOf<ulong>(), Unsafe.SizeOf<ulong>()))),
			KeyValuePair.Create(19, new CommandHandler(SetAudioDeviceOutputVolumeAutoTuneEnabled, new CommandArg(CommandArgType.InArgument, Unsafe.SizeOf<bool>(), Unsafe.SizeOf<bool>()))),
			KeyValuePair.Create(20, new CommandHandler(IsAudioDeviceOutputVolumeAutoTuneEnabled, new CommandArg(CommandArgType.OutArgument, Unsafe.SizeOf<bool>(), Unsafe.SizeOf<bool>())))
		}.ToFrozenDictionary();
	}

	private Result ListAudioDeviceName(ref ServiceDispatchContext context, HipcCommandProcessor processor, ServerMessageRuntimeMetadata runtimeMetadata, ReadOnlySpan<byte> inRawData, ref Span<CmifOutHeader> outHeader)
	{
		Span<bool> span = stackalloc bool[2];
		Result result = processor.ProcessBuffers(ref context, span, runtimeMetadata);
		if (result.IsFailure)
		{
			return result;
		}
		using WritableRegion writableRegion = CommandSerialization.GetWritableRegion(processor.GetBufferRange(0));
		result = ListAudioDeviceName(MemoryMarshal.Cast<byte, DeviceName>(writableRegion.Memory.Span), out var nameCount);
		Span<byte> outRawData;
		if (result.IsFailure)
		{
			context.Processor.PrepareForErrorReply(ref context, out outRawData, runtimeMetadata);
			CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
			return result;
		}
		HipcMessageData response = context.Processor.PrepareForReply(ref context, out outRawData, runtimeMetadata);
		CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
		if (outRawData.Length < processor.OutRawDataSize)
		{
			return SfResult.InvalidOutRawSize;
		}
		CommandSerialization.SerializeArg(outRawData, processor.GetOutArgOffset(0), nameCount);
		processor.SetOutBuffers(response, span);
		return Result.Success;
	}

	private Result SetAudioDeviceOutputVolume(ref ServiceDispatchContext context, HipcCommandProcessor processor, ServerMessageRuntimeMetadata runtimeMetadata, ReadOnlySpan<byte> inRawData, ref Span<CmifOutHeader> outHeader)
	{
		Span<bool> span = stackalloc bool[2];
		Result result = processor.ProcessBuffers(ref context, span, runtimeMetadata);
		if (result.IsFailure)
		{
			return result;
		}
		ReadOnlySpan<DeviceName> name = MemoryMarshal.Cast<byte, DeviceName>(CommandSerialization.GetReadOnlySpan(processor.GetBufferRange(0)));
		float volume = CommandSerialization.DeserializeArg<float>(inRawData, processor.GetInArgOffset(0));
		result = SetAudioDeviceOutputVolume(name, volume);
		Span<byte> outRawData;
		if (result.IsFailure)
		{
			context.Processor.PrepareForErrorReply(ref context, out outRawData, runtimeMetadata);
			CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
			return result;
		}
		HipcMessageData response = context.Processor.PrepareForReply(ref context, out outRawData, runtimeMetadata);
		CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
		if (outRawData.Length < processor.OutRawDataSize)
		{
			return SfResult.InvalidOutRawSize;
		}
		processor.SetOutBuffers(response, span);
		return Result.Success;
	}

	private Result GetAudioDeviceOutputVolume(ref ServiceDispatchContext context, HipcCommandProcessor processor, ServerMessageRuntimeMetadata runtimeMetadata, ReadOnlySpan<byte> inRawData, ref Span<CmifOutHeader> outHeader)
	{
		Span<bool> span = stackalloc bool[2];
		Result result = processor.ProcessBuffers(ref context, span, runtimeMetadata);
		if (result.IsFailure)
		{
			return result;
		}
		ReadOnlySpan<DeviceName> name = MemoryMarshal.Cast<byte, DeviceName>(CommandSerialization.GetReadOnlySpan(processor.GetBufferRange(0)));
		result = GetAudioDeviceOutputVolume(name, out var volume);
		Span<byte> outRawData;
		if (result.IsFailure)
		{
			context.Processor.PrepareForErrorReply(ref context, out outRawData, runtimeMetadata);
			CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
			return result;
		}
		HipcMessageData response = context.Processor.PrepareForReply(ref context, out outRawData, runtimeMetadata);
		CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
		if (outRawData.Length < processor.OutRawDataSize)
		{
			return SfResult.InvalidOutRawSize;
		}
		CommandSerialization.SerializeArg(outRawData, processor.GetOutArgOffset(0), volume);
		processor.SetOutBuffers(response, span);
		return Result.Success;
	}

	private Result GetActiveAudioDeviceName(ref ServiceDispatchContext context, HipcCommandProcessor processor, ServerMessageRuntimeMetadata runtimeMetadata, ReadOnlySpan<byte> inRawData, ref Span<CmifOutHeader> outHeader)
	{
		Span<bool> span = stackalloc bool[1];
		Result result = processor.ProcessBuffers(ref context, span, runtimeMetadata);
		if (result.IsFailure)
		{
			return result;
		}
		using WritableRegion writableRegion = CommandSerialization.GetWritableRegion(processor.GetBufferRange(0));
		result = GetActiveAudioDeviceName(MemoryMarshal.Cast<byte, DeviceName>(writableRegion.Memory.Span));
		Span<byte> outRawData;
		if (result.IsFailure)
		{
			context.Processor.PrepareForErrorReply(ref context, out outRawData, runtimeMetadata);
			CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
			return result;
		}
		HipcMessageData response = context.Processor.PrepareForReply(ref context, out outRawData, runtimeMetadata);
		CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
		if (outRawData.Length < processor.OutRawDataSize)
		{
			return SfResult.InvalidOutRawSize;
		}
		processor.SetOutBuffers(response, span);
		return Result.Success;
	}

	private Result QueryAudioDeviceSystemEvent(ref ServiceDispatchContext context, HipcCommandProcessor processor, ServerMessageRuntimeMetadata runtimeMetadata, ReadOnlySpan<byte> inRawData, ref Span<CmifOutHeader> outHeader)
	{
		Result result = QueryAudioDeviceSystemEvent(out var eventHandle);
		Span<byte> outRawData;
		if (result.IsFailure)
		{
			context.Processor.PrepareForErrorReply(ref context, out outRawData, runtimeMetadata);
			CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
			return result;
		}
		HipcMessageData response = context.Processor.PrepareForReply(ref context, out outRawData, runtimeMetadata);
		CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
		if (outRawData.Length < processor.OutRawDataSize)
		{
			return SfResult.InvalidOutRawSize;
		}
		CommandSerialization.SerializeCopyHandle(response, 0, eventHandle);
		return Result.Success;
	}

	private Result GetActiveChannelCount(ref ServiceDispatchContext context, HipcCommandProcessor processor, ServerMessageRuntimeMetadata runtimeMetadata, ReadOnlySpan<byte> inRawData, ref Span<CmifOutHeader> outHeader)
	{
		Result activeChannelCount = GetActiveChannelCount(out var channelCount);
		Span<byte> outRawData;
		if (activeChannelCount.IsFailure)
		{
			context.Processor.PrepareForErrorReply(ref context, out outRawData, runtimeMetadata);
			CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
			return activeChannelCount;
		}
		context.Processor.PrepareForReply(ref context, out outRawData, runtimeMetadata);
		CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
		if (outRawData.Length < processor.OutRawDataSize)
		{
			return SfResult.InvalidOutRawSize;
		}
		CommandSerialization.SerializeArg(outRawData, processor.GetOutArgOffset(0), channelCount);
		return Result.Success;
	}

	private Result ListAudioDeviceNameAuto(ref ServiceDispatchContext context, HipcCommandProcessor processor, ServerMessageRuntimeMetadata runtimeMetadata, ReadOnlySpan<byte> inRawData, ref Span<CmifOutHeader> outHeader)
	{
		Span<bool> span = stackalloc bool[2];
		Result result = processor.ProcessBuffers(ref context, span, runtimeMetadata);
		if (result.IsFailure)
		{
			return result;
		}
		using WritableRegion writableRegion = CommandSerialization.GetWritableRegion(processor.GetBufferRange(0));
		result = ListAudioDeviceNameAuto(MemoryMarshal.Cast<byte, DeviceName>(writableRegion.Memory.Span), out var nameCount);
		Span<byte> outRawData;
		if (result.IsFailure)
		{
			context.Processor.PrepareForErrorReply(ref context, out outRawData, runtimeMetadata);
			CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
			return result;
		}
		HipcMessageData response = context.Processor.PrepareForReply(ref context, out outRawData, runtimeMetadata);
		CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
		if (outRawData.Length < processor.OutRawDataSize)
		{
			return SfResult.InvalidOutRawSize;
		}
		CommandSerialization.SerializeArg(outRawData, processor.GetOutArgOffset(0), nameCount);
		processor.SetOutBuffers(response, span);
		return Result.Success;
	}

	private Result SetAudioDeviceOutputVolumeAuto(ref ServiceDispatchContext context, HipcCommandProcessor processor, ServerMessageRuntimeMetadata runtimeMetadata, ReadOnlySpan<byte> inRawData, ref Span<CmifOutHeader> outHeader)
	{
		Span<bool> span = stackalloc bool[2];
		Result result = processor.ProcessBuffers(ref context, span, runtimeMetadata);
		if (result.IsFailure)
		{
			return result;
		}
		ReadOnlySpan<DeviceName> name = MemoryMarshal.Cast<byte, DeviceName>(CommandSerialization.GetReadOnlySpan(processor.GetBufferRange(0)));
		float volume = CommandSerialization.DeserializeArg<float>(inRawData, processor.GetInArgOffset(0));
		result = SetAudioDeviceOutputVolumeAuto(name, volume);
		Span<byte> outRawData;
		if (result.IsFailure)
		{
			context.Processor.PrepareForErrorReply(ref context, out outRawData, runtimeMetadata);
			CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
			return result;
		}
		HipcMessageData response = context.Processor.PrepareForReply(ref context, out outRawData, runtimeMetadata);
		CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
		if (outRawData.Length < processor.OutRawDataSize)
		{
			return SfResult.InvalidOutRawSize;
		}
		processor.SetOutBuffers(response, span);
		return Result.Success;
	}

	private Result GetAudioDeviceOutputVolumeAuto(ref ServiceDispatchContext context, HipcCommandProcessor processor, ServerMessageRuntimeMetadata runtimeMetadata, ReadOnlySpan<byte> inRawData, ref Span<CmifOutHeader> outHeader)
	{
		Span<bool> span = stackalloc bool[2];
		Result result = processor.ProcessBuffers(ref context, span, runtimeMetadata);
		if (result.IsFailure)
		{
			return result;
		}
		ReadOnlySpan<DeviceName> name = MemoryMarshal.Cast<byte, DeviceName>(CommandSerialization.GetReadOnlySpan(processor.GetBufferRange(0)));
		result = GetAudioDeviceOutputVolumeAuto(name, out var volume);
		Span<byte> outRawData;
		if (result.IsFailure)
		{
			context.Processor.PrepareForErrorReply(ref context, out outRawData, runtimeMetadata);
			CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
			return result;
		}
		HipcMessageData response = context.Processor.PrepareForReply(ref context, out outRawData, runtimeMetadata);
		CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
		if (outRawData.Length < processor.OutRawDataSize)
		{
			return SfResult.InvalidOutRawSize;
		}
		CommandSerialization.SerializeArg(outRawData, processor.GetOutArgOffset(0), volume);
		processor.SetOutBuffers(response, span);
		return Result.Success;
	}

	private Result GetActiveAudioDeviceNameAuto(ref ServiceDispatchContext context, HipcCommandProcessor processor, ServerMessageRuntimeMetadata runtimeMetadata, ReadOnlySpan<byte> inRawData, ref Span<CmifOutHeader> outHeader)
	{
		Span<bool> span = stackalloc bool[1];
		Result result = processor.ProcessBuffers(ref context, span, runtimeMetadata);
		if (result.IsFailure)
		{
			return result;
		}
		using WritableRegion writableRegion = CommandSerialization.GetWritableRegion(processor.GetBufferRange(0));
		result = GetActiveAudioDeviceNameAuto(MemoryMarshal.Cast<byte, DeviceName>(writableRegion.Memory.Span));
		Span<byte> outRawData;
		if (result.IsFailure)
		{
			context.Processor.PrepareForErrorReply(ref context, out outRawData, runtimeMetadata);
			CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
			return result;
		}
		HipcMessageData response = context.Processor.PrepareForReply(ref context, out outRawData, runtimeMetadata);
		CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
		if (outRawData.Length < processor.OutRawDataSize)
		{
			return SfResult.InvalidOutRawSize;
		}
		processor.SetOutBuffers(response, span);
		return Result.Success;
	}

	private Result QueryAudioDeviceInputEvent(ref ServiceDispatchContext context, HipcCommandProcessor processor, ServerMessageRuntimeMetadata runtimeMetadata, ReadOnlySpan<byte> inRawData, ref Span<CmifOutHeader> outHeader)
	{
		Result result = QueryAudioDeviceInputEvent(out var eventHandle);
		Span<byte> outRawData;
		if (result.IsFailure)
		{
			context.Processor.PrepareForErrorReply(ref context, out outRawData, runtimeMetadata);
			CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
			return result;
		}
		HipcMessageData response = context.Processor.PrepareForReply(ref context, out outRawData, runtimeMetadata);
		CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
		if (outRawData.Length < processor.OutRawDataSize)
		{
			return SfResult.InvalidOutRawSize;
		}
		CommandSerialization.SerializeCopyHandle(response, 0, eventHandle);
		return Result.Success;
	}

	private Result QueryAudioDeviceOutputEvent(ref ServiceDispatchContext context, HipcCommandProcessor processor, ServerMessageRuntimeMetadata runtimeMetadata, ReadOnlySpan<byte> inRawData, ref Span<CmifOutHeader> outHeader)
	{
		Result result = QueryAudioDeviceOutputEvent(out var eventHandle);
		Span<byte> outRawData;
		if (result.IsFailure)
		{
			context.Processor.PrepareForErrorReply(ref context, out outRawData, runtimeMetadata);
			CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
			return result;
		}
		HipcMessageData response = context.Processor.PrepareForReply(ref context, out outRawData, runtimeMetadata);
		CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
		if (outRawData.Length < processor.OutRawDataSize)
		{
			return SfResult.InvalidOutRawSize;
		}
		CommandSerialization.SerializeCopyHandle(response, 0, eventHandle);
		return Result.Success;
	}

	private Result GetActiveAudioOutputDeviceName(ref ServiceDispatchContext context, HipcCommandProcessor processor, ServerMessageRuntimeMetadata runtimeMetadata, ReadOnlySpan<byte> inRawData, ref Span<CmifOutHeader> outHeader)
	{
		Span<bool> span = stackalloc bool[1];
		Result result = processor.ProcessBuffers(ref context, span, runtimeMetadata);
		if (result.IsFailure)
		{
			return result;
		}
		using WritableRegion writableRegion = CommandSerialization.GetWritableRegion(processor.GetBufferRange(0));
		result = GetActiveAudioOutputDeviceName(MemoryMarshal.Cast<byte, DeviceName>(writableRegion.Memory.Span));
		Span<byte> outRawData;
		if (result.IsFailure)
		{
			context.Processor.PrepareForErrorReply(ref context, out outRawData, runtimeMetadata);
			CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
			return result;
		}
		HipcMessageData response = context.Processor.PrepareForReply(ref context, out outRawData, runtimeMetadata);
		CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
		if (outRawData.Length < processor.OutRawDataSize)
		{
			return SfResult.InvalidOutRawSize;
		}
		processor.SetOutBuffers(response, span);
		return Result.Success;
	}

	private Result ListAudioOutputDeviceName(ref ServiceDispatchContext context, HipcCommandProcessor processor, ServerMessageRuntimeMetadata runtimeMetadata, ReadOnlySpan<byte> inRawData, ref Span<CmifOutHeader> outHeader)
	{
		Span<bool> span = stackalloc bool[2];
		Result result = processor.ProcessBuffers(ref context, span, runtimeMetadata);
		if (result.IsFailure)
		{
			return result;
		}
		using WritableRegion writableRegion = CommandSerialization.GetWritableRegion(processor.GetBufferRange(0));
		result = ListAudioOutputDeviceName(MemoryMarshal.Cast<byte, DeviceName>(writableRegion.Memory.Span), out var nameCount);
		Span<byte> outRawData;
		if (result.IsFailure)
		{
			context.Processor.PrepareForErrorReply(ref context, out outRawData, runtimeMetadata);
			CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
			return result;
		}
		HipcMessageData response = context.Processor.PrepareForReply(ref context, out outRawData, runtimeMetadata);
		CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
		if (outRawData.Length < processor.OutRawDataSize)
		{
			return SfResult.InvalidOutRawSize;
		}
		CommandSerialization.SerializeArg(outRawData, processor.GetOutArgOffset(0), nameCount);
		processor.SetOutBuffers(response, span);
		return Result.Success;
	}

	private Result AcquireAudioOutputDeviceNotification(ref ServiceDispatchContext context, HipcCommandProcessor processor, ServerMessageRuntimeMetadata runtimeMetadata, ReadOnlySpan<byte> inRawData, ref Span<CmifOutHeader> outHeader)
	{
		ulong deviceId = CommandSerialization.DeserializeArg<ulong>(inRawData, processor.GetInArgOffset(0));
		Result result = AcquireAudioOutputDeviceNotification(out var eventHandle, deviceId);
		Span<byte> outRawData;
		if (result.IsFailure)
		{
			context.Processor.PrepareForErrorReply(ref context, out outRawData, runtimeMetadata);
			CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
			return result;
		}
		HipcMessageData response = context.Processor.PrepareForReply(ref context, out outRawData, runtimeMetadata);
		CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
		if (outRawData.Length < processor.OutRawDataSize)
		{
			return SfResult.InvalidOutRawSize;
		}
		CommandSerialization.SerializeCopyHandle(response, 0, eventHandle);
		return Result.Success;
	}

	private Result ReleaseAudioOutputDeviceNotification(ref ServiceDispatchContext context, HipcCommandProcessor processor, ServerMessageRuntimeMetadata runtimeMetadata, ReadOnlySpan<byte> inRawData, ref Span<CmifOutHeader> outHeader)
	{
		ulong deviceId = CommandSerialization.DeserializeArg<ulong>(inRawData, processor.GetInArgOffset(0));
		Result result = ReleaseAudioOutputDeviceNotification(deviceId);
		Span<byte> outRawData;
		if (result.IsFailure)
		{
			context.Processor.PrepareForErrorReply(ref context, out outRawData, runtimeMetadata);
			CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
			return result;
		}
		context.Processor.PrepareForReply(ref context, out outRawData, runtimeMetadata);
		CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
		if (outRawData.Length < processor.OutRawDataSize)
		{
			return SfResult.InvalidOutRawSize;
		}
		return Result.Success;
	}

	private Result AcquireAudioInputDeviceNotification(ref ServiceDispatchContext context, HipcCommandProcessor processor, ServerMessageRuntimeMetadata runtimeMetadata, ReadOnlySpan<byte> inRawData, ref Span<CmifOutHeader> outHeader)
	{
		ulong deviceId = CommandSerialization.DeserializeArg<ulong>(inRawData, processor.GetInArgOffset(0));
		Result result = AcquireAudioInputDeviceNotification(out var eventHandle, deviceId);
		Span<byte> outRawData;
		if (result.IsFailure)
		{
			context.Processor.PrepareForErrorReply(ref context, out outRawData, runtimeMetadata);
			CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
			return result;
		}
		HipcMessageData response = context.Processor.PrepareForReply(ref context, out outRawData, runtimeMetadata);
		CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
		if (outRawData.Length < processor.OutRawDataSize)
		{
			return SfResult.InvalidOutRawSize;
		}
		CommandSerialization.SerializeCopyHandle(response, 0, eventHandle);
		return Result.Success;
	}

	private Result ReleaseAudioInputDeviceNotification(ref ServiceDispatchContext context, HipcCommandProcessor processor, ServerMessageRuntimeMetadata runtimeMetadata, ReadOnlySpan<byte> inRawData, ref Span<CmifOutHeader> outHeader)
	{
		ulong deviceId = CommandSerialization.DeserializeArg<ulong>(inRawData, processor.GetInArgOffset(0));
		Result result = ReleaseAudioInputDeviceNotification(deviceId);
		Span<byte> outRawData;
		if (result.IsFailure)
		{
			context.Processor.PrepareForErrorReply(ref context, out outRawData, runtimeMetadata);
			CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
			return result;
		}
		context.Processor.PrepareForReply(ref context, out outRawData, runtimeMetadata);
		CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
		if (outRawData.Length < processor.OutRawDataSize)
		{
			return SfResult.InvalidOutRawSize;
		}
		return Result.Success;
	}

	private Result SetAudioDeviceOutputVolumeAutoTuneEnabled(ref ServiceDispatchContext context, HipcCommandProcessor processor, ServerMessageRuntimeMetadata runtimeMetadata, ReadOnlySpan<byte> inRawData, ref Span<CmifOutHeader> outHeader)
	{
		bool audioDeviceOutputVolumeAutoTuneEnabled = CommandSerialization.DeserializeArg<bool>(inRawData, processor.GetInArgOffset(0));
		Result result = SetAudioDeviceOutputVolumeAutoTuneEnabled(audioDeviceOutputVolumeAutoTuneEnabled);
		Span<byte> outRawData;
		if (result.IsFailure)
		{
			context.Processor.PrepareForErrorReply(ref context, out outRawData, runtimeMetadata);
			CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
			return result;
		}
		context.Processor.PrepareForReply(ref context, out outRawData, runtimeMetadata);
		CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
		if (outRawData.Length < processor.OutRawDataSize)
		{
			return SfResult.InvalidOutRawSize;
		}
		return Result.Success;
	}

	private Result IsAudioDeviceOutputVolumeAutoTuneEnabled(ref ServiceDispatchContext context, HipcCommandProcessor processor, ServerMessageRuntimeMetadata runtimeMetadata, ReadOnlySpan<byte> inRawData, ref Span<CmifOutHeader> outHeader)
	{
		Result result = IsAudioDeviceOutputVolumeAutoTuneEnabled(out var enabled);
		Span<byte> outRawData;
		if (result.IsFailure)
		{
			context.Processor.PrepareForErrorReply(ref context, out outRawData, runtimeMetadata);
			CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
			return result;
		}
		context.Processor.PrepareForReply(ref context, out outRawData, runtimeMetadata);
		CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
		if (outRawData.Length < processor.OutRawDataSize)
		{
			return SfResult.InvalidOutRawSize;
		}
		CommandSerialization.SerializeArg(outRawData, processor.GetOutArgOffset(0), enabled);
		return Result.Success;
	}
}
