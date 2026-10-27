using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NxInfinity.Runtime.Services;

namespace NxInfinity;

public sealed class LibraryFolderMonitor : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();

    private readonly ConcurrentDictionary<string, byte> _dirty = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

    private readonly IReadOnlyList<GameLibraryFolder> _folders;

    private DateTimeOffset _fallback = DateTimeOffset.UtcNow.AddMinutes(5.0);

    public LibraryFolderMonitor(IReadOnlyList<GameLibraryFolder> folders)
    {
        _folders = folders;
        foreach (GameLibraryFolder folder2 in folders)
        {
            GameLibraryFolder folder = folder2;
            FileSystemWatcher fileSystemWatcher = null;
            try
            {
                fileSystemWatcher = new FileSystemWatcher(folder.AbsolutePath)
                {
                    IncludeSubdirectories = folder.Recursive,
                    NotifyFilter = (NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite)
                };
                fileSystemWatcher.Created += Dirty;
                fileSystemWatcher.Deleted += Dirty;
                fileSystemWatcher.Changed += Dirty;
                fileSystemWatcher.Renamed += (object _, RenamedEventArgs _) =>
                {
                    _dirty.TryAdd(folder.Id, 0);
                };
                fileSystemWatcher.Error += (object _, ErrorEventArgs _) =>
                {
                    _dirty.TryAdd(folder.Id, 0);
                };
                fileSystemWatcher.EnableRaisingEvents = true;
                _watchers.Add(fileSystemWatcher);
                void Dirty(object? _, FileSystemEventArgs e)
                {
                    _dirty.TryAdd(folder.Id, 0);
                }
            }
            catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is PlatformNotSupportedException) ? true : false)
            {
                fileSystemWatcher?.Dispose();
            }
        }
    }

    public IReadOnlyList<GameLibraryFolder> TakeChanged(DateTimeOffset now)
    {
        if (now >= _fallback)
        {
            _fallback = now.AddMinutes(5.0);
            _dirty.Clear();
            return _folders;
        }
        return _folders.Where((GameLibraryFolder folder) => _dirty.TryRemove(folder.Id, out var _)).ToArray();
    }

    public void Dispose()
    {
        foreach (FileSystemWatcher watcher in _watchers)
        {
            watcher.Dispose();
        }
        _watchers.Clear();
    }
}
