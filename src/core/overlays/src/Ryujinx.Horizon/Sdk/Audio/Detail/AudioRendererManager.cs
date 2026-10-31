using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Ryujinx.Audio.Renderer.Device;
using Ryujinx.Audio.Renderer.Server;
using Ryujinx.Common.Logging;
using Ryujinx.Horizon.Common;
using Ryujinx.Horizon.Sdk.Applet;
using Ryujinx.Horizon.Sdk.Sf;
using Ryujinx.Horizon.Sdk.Sf.Cmif;
using Ryujinx.Horizon.Sdk.Sf.Hipc;
using Ryujinx.Memory;

namespace Ryujinx.Horizon.Sdk.Audio.Detail;

internal class AudioRendererManager : IAudioRendererManager, IServiceObject
{
	private const uint InitialRevision = 827737426u;

	private readonly Ryujinx.Audio.Renderer.Server.AudioRendererManager _impl;

	private readonly VirtualDeviceSessionRegistry _registry;

	public AudioRendererManager(Ryujinx.Audio.Renderer.Server.AudioRendererManager impl, VirtualDeviceSessionRegistry registry)
	{
		_impl = impl;
		_registry = registry;
	}

	[CmifCommand(0u)]
	public Result OpenAudioRenderer(out IAudioRenderer renderer, AudioRendererParameterInternal parameter, [CopyHandle] int workBufferHandle, [CopyHandle] int processHandle, ulong workBufferSize, AppletResourceUserId appletResourceId, [ClientProcessId] ulong pid)
	{
		IVirtualMemoryManager memoryManagerByProcessHandle = HorizonStatic.Syscall.GetMemoryManagerByProcessHandle(processHandle);
		ulong transferMemoryAddress = HorizonStatic.Syscall.GetTransferMemoryAddress(workBufferHandle);
		Result result = new Result((int)_impl.OpenAudioRenderer(out var renderer2, memoryManagerByProcessHandle, ref parameter.Configuration, appletResourceId.Id, transferMemoryAddress, workBufferSize, (uint)processHandle));
		if (result.IsSuccess)
		{
			renderer = new AudioRenderer(renderer2, workBufferHandle, processHandle);
		}
		else
		{
			renderer = null;
			HorizonStatic.Syscall.CloseHandle(workBufferHandle);
			HorizonStatic.Syscall.CloseHandle(processHandle);
		}
		return result;
	}

	[CmifCommand(1u)]
	public Result GetWorkBufferSize(out long workBufferSize, AudioRendererParameterInternal parameter)
	{
		if (BehaviourInfo.CheckValidRevision(parameter.Configuration.Revision))
		{
			workBufferSize = (long)Ryujinx.Audio.Renderer.Server.AudioRendererManager.GetWorkBufferSize(ref parameter.Configuration);
			Logger.Debug?.Print(LogClass.ServiceAudio, $"WorkBufferSize is 0x{workBufferSize:x16}.", "GetWorkBufferSize");
			return Result.Success;
		}
		workBufferSize = 0L;
		Logger.Warning?.Print(LogClass.ServiceAudio, $"Library Revision REV{BehaviourInfo.GetRevisionNumber(parameter.Configuration.Revision)} is not supported!", "GetWorkBufferSize");
		return AudioResult.UnsupportedRevision;
	}

	[CmifCommand(2u)]
	public Result GetAudioDeviceService(out IAudioDevice audioDevice, AppletResourceUserId appletResourceId)
	{
		audioDevice = new AudioDevice(_registry, appletResourceId, 827737426u);
		return Result.Success;
	}

	[CmifCommand(3u)]
	public Result OpenAudioRendererForManualExecution(out IAudioRenderer renderer, AudioRendererParameterInternal parameter, ulong workBufferAddress, [CopyHandle] int processHandle, ulong workBufferSize, AppletResourceUserId appletResourceId, [ClientProcessId] ulong pid)
	{
		IVirtualMemoryManager memoryManagerByProcessHandle = HorizonStatic.Syscall.GetMemoryManagerByProcessHandle(processHandle);
		Result result = new Result((int)_impl.OpenAudioRenderer(out var renderer2, memoryManagerByProcessHandle, ref parameter.Configuration, appletResourceId.Id, workBufferAddress, workBufferSize, (uint)processHandle));
		if (result.IsSuccess)
		{
			renderer = new AudioRenderer(renderer2, 0, processHandle);
		}
		else
		{
			renderer = null;
			HorizonStatic.Syscall.CloseHandle(processHandle);
		}
		return result;
	}

	[CmifCommand(4u)]
	public Result GetAudioDeviceServiceWithRevisionInfo(out IAudioDevice audioDevice, AppletResourceUserId appletResourceId, uint revision)
	{
		audioDevice = new AudioDevice(_registry, appletResourceId, revision);
		return Result.Success;
	}

	public IReadOnlyDictionary<int, CommandHandler> GetCommandHandlers()
	{
		return new KeyValuePair<int, CommandHandler>[5]
		{
			KeyValuePair.Create(0, new CommandHandler(OpenAudioRenderer, new CommandArg(CommandArgType.OutObject), new CommandArg(CommandArgType.InArgument, Unsafe.SizeOf<AudioRendererParameterInternal>(), 1), new CommandArg(CommandArgType.InCopyHandle), new CommandArg(CommandArgType.InCopyHandle), new CommandArg(CommandArgType.InArgument, Unsafe.SizeOf<ulong>(), Unsafe.SizeOf<ulong>()), new CommandArg(CommandArgType.InArgument, Unsafe.SizeOf<AppletResourceUserId>(), 8), new CommandArg(CommandArgType.ProcessId))),
			KeyValuePair.Create(1, new CommandHandler(GetWorkBufferSize, new CommandArg(CommandArgType.OutArgument, Unsafe.SizeOf<long>(), Unsafe.SizeOf<long>()), new CommandArg(CommandArgType.InArgument, Unsafe.SizeOf<AudioRendererParameterInternal>(), 1))),
			KeyValuePair.Create(2, new CommandHandler(GetAudioDeviceService, new CommandArg(CommandArgType.OutObject), new CommandArg(CommandArgType.InArgument, Unsafe.SizeOf<AppletResourceUserId>(), 8))),
			KeyValuePair.Create(3, new CommandHandler(OpenAudioRendererForManualExecution, new CommandArg(CommandArgType.OutObject), new CommandArg(CommandArgType.InArgument, Unsafe.SizeOf<AudioRendererParameterInternal>(), 1), new CommandArg(CommandArgType.InArgument, Unsafe.SizeOf<ulong>(), Unsafe.SizeOf<ulong>()), new CommandArg(CommandArgType.InCopyHandle), new CommandArg(CommandArgType.InArgument, Unsafe.SizeOf<ulong>(), Unsafe.SizeOf<ulong>()), new CommandArg(CommandArgType.InArgument, Unsafe.SizeOf<AppletResourceUserId>(), 8), new CommandArg(CommandArgType.ProcessId))),
			KeyValuePair.Create(4, new CommandHandler(GetAudioDeviceServiceWithRevisionInfo, new CommandArg(CommandArgType.OutObject), new CommandArg(CommandArgType.InArgument, Unsafe.SizeOf<AppletResourceUserId>(), 8), new CommandArg(CommandArgType.InArgument, Unsafe.SizeOf<uint>(), Unsafe.SizeOf<uint>())))
		}.ToFrozenDictionary();
	}

	private Result OpenAudioRenderer(ref ServiceDispatchContext context, HipcCommandProcessor processor, ServerMessageRuntimeMetadata runtimeMetadata, ReadOnlySpan<byte> inRawData, ref Span<CmifOutHeader> outHeader)
	{
		IServiceObject[] array = new IServiceObject[1];
		AudioRendererParameterInternal parameter = CommandSerialization.DeserializeArg<AudioRendererParameterInternal>(inRawData, processor.GetInArgOffset(0));
		int workBufferHandle = CommandSerialization.DeserializeCopyHandle(ref context, 0);
		int processHandle = CommandSerialization.DeserializeCopyHandle(ref context, 1);
		ulong workBufferSize = CommandSerialization.DeserializeArg<ulong>(inRawData, processor.GetInArgOffset(1));
		AppletResourceUserId appletResourceId = CommandSerialization.DeserializeArg<AppletResourceUserId>(inRawData, processor.GetInArgOffset(2));
		ulong pid = CommandSerialization.DeserializeClientProcessId(ref context);
		Result result = OpenAudioRenderer(out var renderer, parameter, workBufferHandle, processHandle, workBufferSize, appletResourceId, pid);
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
		array[0] = renderer;
		processor.SetOutObjects(ref context, response, array);
		return Result.Success;
	}

	private Result GetWorkBufferSize(ref ServiceDispatchContext context, HipcCommandProcessor processor, ServerMessageRuntimeMetadata runtimeMetadata, ReadOnlySpan<byte> inRawData, ref Span<CmifOutHeader> outHeader)
	{
		AudioRendererParameterInternal parameter = CommandSerialization.DeserializeArg<AudioRendererParameterInternal>(inRawData, processor.GetInArgOffset(0));
		Result workBufferSize = GetWorkBufferSize(out var workBufferSize2, parameter);
		Span<byte> outRawData;
		if (workBufferSize.IsFailure)
		{
			context.Processor.PrepareForErrorReply(ref context, out outRawData, runtimeMetadata);
			CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
			return workBufferSize;
		}
		context.Processor.PrepareForReply(ref context, out outRawData, runtimeMetadata);
		CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
		if (outRawData.Length < processor.OutRawDataSize)
		{
			return SfResult.InvalidOutRawSize;
		}
		CommandSerialization.SerializeArg(outRawData, processor.GetOutArgOffset(0), workBufferSize2);
		return Result.Success;
	}

	private Result GetAudioDeviceService(ref ServiceDispatchContext context, HipcCommandProcessor processor, ServerMessageRuntimeMetadata runtimeMetadata, ReadOnlySpan<byte> inRawData, ref Span<CmifOutHeader> outHeader)
	{
		IServiceObject[] array = new IServiceObject[1];
		AppletResourceUserId appletResourceId = CommandSerialization.DeserializeArg<AppletResourceUserId>(inRawData, processor.GetInArgOffset(0));
		Result audioDeviceService = GetAudioDeviceService(out var audioDevice, appletResourceId);
		Span<byte> outRawData;
		if (audioDeviceService.IsFailure)
		{
			context.Processor.PrepareForErrorReply(ref context, out outRawData, runtimeMetadata);
			CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
			return audioDeviceService;
		}
		HipcMessageData response = context.Processor.PrepareForReply(ref context, out outRawData, runtimeMetadata);
		CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
		if (outRawData.Length < processor.OutRawDataSize)
		{
			return SfResult.InvalidOutRawSize;
		}
		array[0] = audioDevice;
		processor.SetOutObjects(ref context, response, array);
		return Result.Success;
	}

	private Result OpenAudioRendererForManualExecution(ref ServiceDispatchContext context, HipcCommandProcessor processor, ServerMessageRuntimeMetadata runtimeMetadata, ReadOnlySpan<byte> inRawData, ref Span<CmifOutHeader> outHeader)
	{
		IServiceObject[] array = new IServiceObject[1];
		AudioRendererParameterInternal parameter = CommandSerialization.DeserializeArg<AudioRendererParameterInternal>(inRawData, processor.GetInArgOffset(0));
		ulong workBufferAddress = CommandSerialization.DeserializeArg<ulong>(inRawData, processor.GetInArgOffset(1));
		int processHandle = CommandSerialization.DeserializeCopyHandle(ref context, 0);
		ulong workBufferSize = CommandSerialization.DeserializeArg<ulong>(inRawData, processor.GetInArgOffset(2));
		AppletResourceUserId appletResourceId = CommandSerialization.DeserializeArg<AppletResourceUserId>(inRawData, processor.GetInArgOffset(3));
		ulong pid = CommandSerialization.DeserializeClientProcessId(ref context);
		Result result = OpenAudioRendererForManualExecution(out var renderer, parameter, workBufferAddress, processHandle, workBufferSize, appletResourceId, pid);
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
		array[0] = renderer;
		processor.SetOutObjects(ref context, response, array);
		return Result.Success;
	}

	private Result GetAudioDeviceServiceWithRevisionInfo(ref ServiceDispatchContext context, HipcCommandProcessor processor, ServerMessageRuntimeMetadata runtimeMetadata, ReadOnlySpan<byte> inRawData, ref Span<CmifOutHeader> outHeader)
	{
		IServiceObject[] array = new IServiceObject[1];
		AppletResourceUserId appletResourceId = CommandSerialization.DeserializeArg<AppletResourceUserId>(inRawData, processor.GetInArgOffset(0));
		uint revision = CommandSerialization.DeserializeArg<uint>(inRawData, processor.GetInArgOffset(1));
		Result audioDeviceServiceWithRevisionInfo = GetAudioDeviceServiceWithRevisionInfo(out var audioDevice, appletResourceId, revision);
		Span<byte> outRawData;
		if (audioDeviceServiceWithRevisionInfo.IsFailure)
		{
			context.Processor.PrepareForErrorReply(ref context, out outRawData, runtimeMetadata);
			CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
			return audioDeviceServiceWithRevisionInfo;
		}
		HipcMessageData response = context.Processor.PrepareForReply(ref context, out outRawData, runtimeMetadata);
		CommandHandler.GetCmifOutHeaderPointer(ref outHeader, ref outRawData);
		if (outRawData.Length < processor.OutRawDataSize)
		{
			return SfResult.InvalidOutRawSize;
		}
		array[0] = audioDevice;
		processor.SetOutObjects(ref context, response, array);
		return Result.Success;
	}
}
