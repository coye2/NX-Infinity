using System;

namespace Ryujinx.Graphics.Gpu.Shader.DiskCache;

internal sealed class CacheLoadProgress
{
	private readonly object _gate = new object();

	private readonly Action<ShaderCacheState, int, int> _publish;

	private int _completed;

	private int _total;

	public CacheLoadProgress(Action<ShaderCacheState, int, int> publish)
	{
		_publish = publish;
	}

	public void Start(int total)
	{
		lock (_gate)
		{
			_completed = 0;
			_total = total;
			_publish(ShaderCacheState.Start, 0, total);
		}
	}

	public void CompleteOne()
	{
		lock (_gate)
		{
			_publish(ShaderCacheState.Loading, ++_completed, _total);
		}
	}

	public void Finish()
	{
		lock (_gate)
		{
			_publish(ShaderCacheState.Loaded, _completed, _total);
		}
	}
}
