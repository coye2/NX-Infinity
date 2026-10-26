using System.Buffers.Binary;
using NxInfinity.Runtime.Services;

internal static partial class Program
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    private static void LaunchVersionReceipt()
    {
        using var f = new Fixture();
        string path = LibraryFile(f, "version.nro", new string('a', 4096));
        string? version = "controlled-version-1";
        var store = new GameLibraryStore(f.Workspace, null, _ => version);
        var entry = Register(store, path);
        // Reopen the store to prove this works across launcher restarts.
        store = new GameLibraryStore(f.Workspace, null, _ => version);
        var progress = new InlineProgress<ContentHashProgress>();
        store.ResolveForLaunchAsync(entry.EntryId, progress).GetAwaiter().GetResult();
        Check(progress.Values.Count == 0, "Unchanged version unnecessarily re-read game payload.");
        // Same length and write time is insufficient: an OS version change must
        // force hashing and detect replacement bytes before launch.
        var time = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, new string('b', 4096)); File.SetLastWriteTimeUtc(path, time); version = "controlled-version-2";
        Error(ServiceError.Conflict, () => store.ResolveForLaunchAsync(entry.EntryId).GetAwaiter().GetResult());
        Check(store.Get(entry.EntryId).Content == entry.Content, "Conflict changed the registered identity.");
    }

    private static void LaunchVersionFallback()
    {
        using var f = new Fixture(); string path = LibraryFile(f);
        string? version = "controlled-version-1";
        var store = new GameLibraryStore(f.Workspace, null, _ => version);
        var entry = Register(store, path);
        var receipt = Directory.GetFiles(Path.Combine(f.Root, "nx-services/library/verification"), "*.json").Single();
        File.WriteAllText(receipt, "broken");
        var progress = new InlineProgress<ContentHashProgress>();
        store.ResolveForLaunchAsync(entry.EntryId, progress).GetAwaiter().GetResult();
        Check(progress.Values.Count > 0, "Damaged receipt bypassed hashing.");
        version = null; progress = new();
        store.ResolveForLaunchAsync(entry.EntryId, progress).GetAwaiter().GetResult();
        Check(progress.Values.Count > 0, "Unsupported filesystem bypassed hashing.");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { store.ResolveForLaunchAsync(entry.EntryId, cancellationToken: cancel.Token).GetAwaiter().GetResult(); throw new Exception("Canceled launch accepted."); }
        catch (OperationCanceledException) { }
    }

    private static void LaunchVersionJournalParsing()
    {
        foreach ((ushort version, int size, int offset) in new[] { ((ushort)2,60,24), ((ushort)3,76,40) })
        {
            byte[] record = new byte[size]; BinaryPrimitives.WriteUInt32LittleEndian(record,(uint)size);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4),version);
            BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(offset),123456);
            Check(ContentFileVersion.ParseUsn(record) == 123456);
            Check(ContentFileVersion.ParseUsn(record.AsSpan(0,size-1)) is null);
            BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(offset),0);
            Check(ContentFileVersion.ParseUsn(record) is null);
        }
        Check(ContentFileVersion.ParseUsn([]) is null);
        byte[] unknown = new byte[80]; BinaryPrimitives.WriteUInt32LittleEndian(unknown,80);
        BinaryPrimitives.WriteUInt16LittleEndian(unknown.AsSpan(4),4);
        Check(ContentFileVersion.ParseUsn(unknown) is null);
    }

    private static void StartupScanDoesNotRetryUnidentified()
    {
        using var f = new Fixture(); string path = LibraryFile(f, "encrypted.nsp");
        var store = new GameLibraryStore(f.Workspace); var entry = Register(store,path,LibraryContentKind.Unidentified);
        var folder = store.AddFolder(Path.GetDirectoryName(path)!); int inspections = 0;
        Task<ContentRegistrationRequest> Classify(LocalContentCandidate candidate, CancellationToken ct)
        { inspections++; return Task.FromResult(new ContentRegistrationRequest(candidate.AbsolutePath,LibraryContentKind.Unidentified)); }
        Task Observe(GameLibraryEntry game, CancellationToken ct) => Task.CompletedTask;
        var result = store.ImportFoldersAsync([folder],Classify,Observe,retryUnidentified:false).GetAwaiter().GetResult();
        Check(inspections == 0 && result.Unchanged == 1, "Startup retried a known unchanged unidentified container.");
        store.ImportFoldersAsync([folder],Classify,Observe).GetAwaiter().GetResult();
        Check(inspections == 1, "Explicit rescan could not retry native identification.");
        File.AppendAllText(path,"changed");
        store.ImportFoldersAsync([folder],Classify,Observe,retryUnidentified:false).GetAwaiter().GetResult();
        Check(inspections == 2, "Automatic scan ignored changed content.");
    }

    private static void WindowsLaunchVersionIntegration()
    {
        if (!OperatingSystem.IsWindows()) throw new TestUnavailableException("actual Windows journal integration requires Windows");
        using var f = new Fixture(); string path = LibraryFile(f,"windows-version.nro");
        var store = new GameLibraryStore(f.Workspace); var entry = Register(store,path);
        using (var stream = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            if (ContentFileVersion.Read(stream) is null)
                throw new TestUnavailableException("this Windows filesystem provides no usable journal; full hashing remains active");
        }
        var progress = new InlineProgress<ContentHashProgress>();
        store.ResolveForLaunchAsync(entry.EntryId,progress).GetAwaiter().GetResult();
        Check(progress.Values.Count == 0,"Windows journal fast path did not reuse a verified file.");
        var time = File.GetLastWriteTimeUtc(path); string old = File.ReadAllText(path);
        File.WriteAllText(path,new string('z',old.Length)); File.SetLastWriteTimeUtc(path,time);
        Error(ServiceError.Conflict,() => store.ResolveForLaunchAsync(entry.EntryId).GetAwaiter().GetResult());
    }
}
