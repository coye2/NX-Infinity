using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using Ryujinx.Common;
using Silk.NET.Vulkan;

namespace Ryujinx.Graphics.Vulkan;

internal sealed class RyuDriverPipelineCache
{
	internal const int MaxBytes = 67108864;

	private const int EnvelopeSize = 44;

	private readonly string _path;

	private readonly uint _vendor;

	private readonly uint _device;

	private readonly byte[] _uuid;

	private bool _preserveRejected;

	internal RyuDriverPipelineCache(string root, uint vendor, uint device, uint driver, byte[] uuid, Guid build)
	{
		_vendor = vendor;
		_device = device;
		_uuid = uuid;
		if (!string.IsNullOrEmpty(root))
		{
			string text = $"{vendor:x8}-{device:x8}-{driver:x8}-{Convert.ToHexString(uuid)}-{build:N}";
			_path = Path.Combine(root, text + ".nxpc");
			RyuPipelineCacheSettings.Identity = text;
			RyuPipelineCacheSettings.Status = "empty";
		}
	}

	internal bool Compatible(ReadOnlySpan<byte> data)
	{
		if (data.Length >= 32 && data.Length <= 67108864 && BinaryPrimitives.ReadUInt32LittleEndian(data) == 32 && BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4)) == 1 && BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(8)) == _vendor && BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(12)) == _device)
		{
			return data.Slice(16, 16).SequenceEqual(_uuid);
		}
		return false;
	}

	internal byte[] Load()
	{
		if (_path == null)
		{
			return Array.Empty<byte>();
		}
		using (RyuStutterTrace.Measure("pipeline.disk_cache.load", 0L))
		{
			try
			{
				if (!File.Exists(_path))
				{
					return Array.Empty<byte>();
				}
				using FileStream fileStream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
				if (fileStream.Length < 76 || fileStream.Length > 67108908)
				{
					return Reject();
				}
				Span<byte> buffer = stackalloc byte[44];
				fileStream.ReadExactly(buffer);
				int num = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(8));
				if (!buffer.Slice(0, 8).SequenceEqual("NXVKPC01"u8) || num != fileStream.Length - 44)
				{
					return Reject();
				}
				byte[] array = new byte[num];
				fileStream.ReadExactly(array);
				if (!Compatible(array) || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(array), buffer.Slice(12, 32)))
				{
					return Reject();
				}
				RyuPipelineCacheSettings.LoadedBytes = array.Length;
				RyuPipelineCacheSettings.Status = "loaded";
				return array;
			}
			catch (IOException)
			{
				return Reject();
			}
			catch (UnauthorizedAccessException)
			{
				return Reject();
			}
		}
	}

	private byte[] Reject()
	{
		_preserveRejected = true;
		RyuPipelineCacheSettings.Status = "rejected_preserved";
		return Array.Empty<byte>();
	}

	internal void Save(ReadOnlySpan<byte> data)
	{
		if (_path == null || !Compatible(data))
		{
			return;
		}
		using (RyuStutterTrace.Measure("pipeline.disk_cache.save", 0L))
		{
			string text = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(_path));
				using (FileStream fileStream = new FileStream(text, FileMode.CreateNew, FileAccess.Write, FileShare.None))
				{
					Span<byte> span = stackalloc byte[44];
					"NXVKPC01"u8.CopyTo(span);
					BinaryPrimitives.WriteInt32LittleEndian(span.Slice(8), data.Length);
					SHA256.HashData(data).CopyTo(span.Slice(12));
					fileStream.Write(span);
					fileStream.Write(data);
					fileStream.Flush(flushToDisk: true);
				}
				if (_preserveRejected)
				{
					if (File.Exists(_path))
					{
						File.Move(_path, _path + ".rejected." + Guid.NewGuid().ToString("N"));
					}
					File.Move(text, _path, overwrite: false);
					_preserveRejected = false;
				}
				else
				{
					File.Move(text, _path, overwrite: true);
				}
				RyuPipelineCacheSettings.SavedBytes = data.Length;
				RyuPipelineCacheSettings.Status = "saved";
			}
			catch (IOException)
			{
				RyuPipelineCacheSettings.Status = "save_unavailable";
			}
			catch (UnauthorizedAccessException)
			{
				RyuPipelineCacheSettings.Status = "save_unavailable";
			}
			finally
			{
				try
				{
					if (File.Exists(text))
					{
						File.Delete(text);
					}
				}
				catch (IOException)
				{
				}
				catch (UnauthorizedAccessException)
				{
				}
			}
		}
	}

	internal unsafe PipelineCache Create(Vk api, Device device)
	{
		byte[] array = Load();
		PipelineCache pPipelineCache;
		fixed (byte* pInitialData = array)
		{
			PipelineCacheCreateInfo pCreateInfo = new PipelineCacheCreateInfo
			{
				SType = StructureType.PipelineCacheCreateInfo,
				InitialDataSize = (nuint)array.Length,
				PInitialData = pInitialData
			};
			Result result = api.CreatePipelineCache(device, in pCreateInfo, null, out pPipelineCache);
			if (result != Result.Success && array.Length != 0)
			{
				Reject();
				pCreateInfo.InitialDataSize = 0u;
				pCreateInfo.PInitialData = null;
				result = api.CreatePipelineCache(device, in pCreateInfo, null, out pPipelineCache);
			}
			result.ThrowOnError();
		}
		return pPipelineCache;
	}

	internal unsafe void Capture(Vk api, Device device, PipelineCache cache)
	{
		if (_path == null)
		{
			return;
		}
		nuint num = 0u;
		if (api.GetPipelineCacheData(device, cache, &num, null) != Result.Success || num < 32 || num > 67108864)
		{
			return;
		}
		byte[] array = new byte[(uint)num];
		fixed (byte* pData = array)
		{
			if (api.GetPipelineCacheData(device, cache, &num, pData) != Result.Success || num > (nuint)array.Length)
			{
				return;
			}
			Save(array.AsSpan(0, (int)num));
		}
	}
}
