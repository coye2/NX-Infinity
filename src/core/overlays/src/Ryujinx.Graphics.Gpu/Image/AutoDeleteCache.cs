using System.Collections;
using System.Collections.Generic;
using Ryujinx.Common.Logging;

namespace Ryujinx.Graphics.Gpu.Image;

internal class AutoDeleteCache : IEnumerable<Texture>, IEnumerable
{
	private const int MinCountForDeletion = 32;

	private const int MaxCapacity = 2048;

	private const ulong MiB = 1048576uL;

	private const ulong GiB = 1073741824uL;

	private ulong MaxTextureSizeCapacity = 4294967296uL;

	private const ulong MinTextureSizeCapacity = 536870912uL;

	private const ulong DefaultTextureSizeCapacity = 1073741824uL;

	private const ulong TextureSizeCapacity4GiB = 2147483648uL;

	private const ulong TextureSizeCapacity6GiB = 4294967296uL;

	private const ulong TextureSizeCapacity8GiB = 6442450944uL;

	private const ulong TextureSizeCapacity10GiB = 10737418240uL;

	private const ulong TextureSizeCapacity12GiB = 12884901888uL;

	private const float MemoryScaleFactor = 0.5f;

	private ulong _maxCacheMemoryUsage = 1073741824uL;

	private readonly LinkedList<Texture> _textures;

	private ulong _totalSize;

	private HashSet<ShortTextureCacheEntry> _shortCacheBuilder;

	private HashSet<ShortTextureCacheEntry> _shortCache;

	private readonly Dictionary<TextureDescriptor, ShortTextureCacheEntry> _shortCacheLookup;

	public void Initialize(GpuContext context, ulong cpuMemorySize)
	{
		_maxCacheMemoryUsage = TextureCacheBudget.Calculate(context.Capabilities.MaximumGpuMemory, cpuMemorySize);
		Logger.Info?.Print(LogClass.Gpu, $"AutoDelete Cache Allocated VRAM : {_maxCacheMemoryUsage / 1073741824} GiB", "Initialize");
	}

	public AutoDeleteCache()
	{
		_textures = new LinkedList<Texture>();
		_shortCacheBuilder = new HashSet<ShortTextureCacheEntry>();
		_shortCache = new HashSet<ShortTextureCacheEntry>();
		_shortCacheLookup = new Dictionary<TextureDescriptor, ShortTextureCacheEntry>();
	}

	public void Add(Texture texture)
	{
		_totalSize += texture.Size;
		texture.IncrementReferenceCount();
		texture.CacheNode = _textures.AddLast(texture);
		if (_textures.Count > 2048 || (_totalSize > _maxCacheMemoryUsage && _textures.Count >= 32))
		{
			RemoveLeastUsedTexture();
		}
	}

	public void Lift(Texture texture)
	{
		if (texture.CacheNode != null)
		{
			if (texture.CacheNode != _textures.Last)
			{
				_textures.Remove(texture.CacheNode);
				_textures.AddLast(texture.CacheNode);
			}
			if (_totalSize > _maxCacheMemoryUsage && _textures.Count >= 32)
			{
				RemoveLeastUsedTexture();
			}
		}
		else
		{
			Add(texture);
		}
	}

	private void RemoveLeastUsedTexture()
	{
		if (_textures.First != null)
		{
			Texture value = _textures.First.Value;
			_totalSize -= value.Size;
			if (!value.CheckModified(consume: false))
			{
				value.Group.SynchronizeDependents(value);
				value.FlushModified(tracked: false);
			}
			_textures.RemoveFirst();
			value.DecrementReferenceCount();
			value.CacheNode = null;
		}
	}

	public bool Remove(Texture texture, bool flush)
	{
		if (texture.CacheNode == null)
		{
			return false;
		}
		if (flush)
		{
			texture.FlushModified(tracked: false);
		}
		_textures.Remove(texture.CacheNode);
		_totalSize -= texture.Size;
		texture.CacheNode = null;
		return texture.DecrementReferenceCount();
	}

	public Texture FindShortCache(in TextureDescriptor descriptor)
	{
		if (_shortCacheLookup.Count > 0 && _shortCacheLookup.TryGetValue(descriptor, out var value))
		{
			if (value.InvalidatedSequence == value.Texture.InvalidatedSequence)
			{
				return value.Texture;
			}
			_shortCacheLookup.Remove(descriptor);
		}
		return null;
	}

	public void RemoveShortCache(Texture texture)
	{
		if (_shortCache.Remove(texture.ShortCacheEntry) | _shortCacheBuilder.Remove(texture.ShortCacheEntry))
		{
			texture.DecrementReferenceCount();
			if (!texture.ShortCacheEntry.IsAutoDelete)
			{
				_shortCacheLookup.Remove(texture.ShortCacheEntry.Descriptor);
			}
			texture.ShortCacheEntry = null;
		}
	}

	public void AddShortCache(Texture texture, ref TextureDescriptor descriptor)
	{
		ShortTextureCacheEntry shortTextureCacheEntry = new ShortTextureCacheEntry(descriptor, texture);
		_shortCacheBuilder.Add(shortTextureCacheEntry);
		_shortCacheLookup.Add(shortTextureCacheEntry.Descriptor, shortTextureCacheEntry);
		texture.ShortCacheEntry = shortTextureCacheEntry;
		texture.IncrementReferenceCount();
	}

	public void AddShortCache(Texture texture)
	{
		if (texture.ShortCacheEntry != null)
		{
			ShortTextureCacheEntry shortTextureCacheEntry = new ShortTextureCacheEntry(texture);
			_shortCacheBuilder.Add(shortTextureCacheEntry);
			texture.ShortCacheEntry = shortTextureCacheEntry;
			texture.IncrementReferenceCount();
		}
	}

	public void ProcessShortCache()
	{
		HashSet<ShortTextureCacheEntry> shortCache = _shortCache;
		foreach (ShortTextureCacheEntry item in shortCache)
		{
			item.Texture.DecrementReferenceCount();
			if (item.IsAutoDelete)
			{
				Remove(item.Texture, flush: false);
			}
			else
			{
				_shortCacheLookup.Remove(item.Descriptor);
			}
			item.Texture.ShortCacheEntry = null;
		}
		shortCache.Clear();
		_shortCache = _shortCacheBuilder;
		_shortCacheBuilder = shortCache;
	}

	public IEnumerator<Texture> GetEnumerator()
	{
		return _textures.GetEnumerator();
	}

	IEnumerator IEnumerable.GetEnumerator()
	{
		return _textures.GetEnumerator();
	}
}
