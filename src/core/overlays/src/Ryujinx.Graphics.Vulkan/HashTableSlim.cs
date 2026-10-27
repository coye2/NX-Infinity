using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Ryujinx.Graphics.Vulkan;

internal class HashTableSlim<TKey, TValue> where TKey : IRefEquatable<TKey>
{
	private struct Entry
	{
		public int Hash;

		public TKey Key;

		public TValue Value;
	}

	private struct Bucket
	{
		public int Length;

		public Entry[] Entries;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public readonly Span<Entry> AsSpan()
		{
			if (Entries != null)
			{
				return Entries.AsSpan(0, Length);
			}
			return Span<Entry>.Empty;
		}
	}

	private Bucket[] _hashTable = new Bucket[16];

	private int _count;

	public IEnumerable<TKey> Keys
	{
		get
		{
			Bucket[] hashTable = _hashTable;
			for (int i = 0; i < hashTable.Length; i++)
			{
				Bucket bucket = hashTable[i];
				for (int j = 0; j < bucket.Length; j++)
				{
					yield return bucket.Entries[j].Key;
				}
			}
		}
	}

	public IEnumerable<TValue> Values
	{
		get
		{
			Bucket[] hashTable = _hashTable;
			for (int i = 0; i < hashTable.Length; i++)
			{
				Bucket bucket = hashTable[i];
				for (int j = 0; j < bucket.Length; j++)
				{
					yield return bucket.Entries[j].Value;
				}
			}
		}
	}

	public void Add(ref TKey key, TValue value)
	{
		int hashCode = key.GetHashCode();
		if (_count >= _hashTable.Length * 4 && _hashTable.Length < 1048576)
		{
			Bucket[] array = new Bucket[_hashTable.Length * 2];
			Bucket[] hashTable = _hashTable;
			for (int i = 0; i < hashTable.Length; i++)
			{
				Bucket bucket = hashTable[i];
				for (int j = 0; j < bucket.Length; j++)
				{
					Insert(array, bucket.Entries[j]);
				}
			}
			_hashTable = array;
		}
		Insert(_hashTable, new Entry
		{
			Hash = hashCode,
			Key = key,
			Value = value
		});
		_count++;
	}

	private static void Insert(Bucket[] table, Entry entry)
	{
		ref Bucket reference = ref table[entry.Hash & (table.Length - 1)];
		if (reference.Entries == null)
		{
			reference.Entries = new Entry[2];
		}
		else if (reference.Length == reference.Entries.Length)
		{
			Array.Resize(ref reference.Entries, checked(reference.Length * 2));
		}
		reference.Entries[reference.Length++] = entry;
	}

	public bool Remove(ref TKey key)
	{
		int hashCode = key.GetHashCode();
		ref Bucket reference = ref _hashTable[hashCode & (_hashTable.Length - 1)];
		Span<Entry> span = reference.AsSpan();
		for (int i = 0; i < span.Length; i++)
		{
			if (span[i].Hash == hashCode && span[i].Key.Equals(ref key))
			{
				span.Slice(i + 1).CopyTo(span.Slice(i));
				reference.Entries[--reference.Length] = default;
				_count--;
				return true;
			}
		}
		return false;
	}

	public bool TryGetValue(ref TKey key, out TValue value)
	{
		int hashCode = key.GetHashCode();
		Span<Entry> span = _hashTable[hashCode & (_hashTable.Length - 1)].AsSpan();
		for (int i = 0; i < span.Length; i++)
		{
			if (span[i].Hash == hashCode && span[i].Key.Equals(ref key))
			{
				value = span[i].Value;
				return true;
			}
		}
		value = default;
		return false;
	}
}
