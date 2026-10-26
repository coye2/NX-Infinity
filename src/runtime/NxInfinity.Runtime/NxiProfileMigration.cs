namespace NxInfinity.Runtime;

/// <summary>Copies a clean legacy Kenji profile once, retaining the original as a backup.</summary>
public static class NxiProfileMigration
{
    public static bool Ensure(string dataRoot, string profileId)
    {
        SafePaths.ValidateIdentifier(profileId, nameof(profileId));
        string root = SafePaths.Absolute(dataRoot);
        string source = Path.Combine(root, "backends", "ryujinx", profileId);
        string destination = Path.Combine(root, "backends", "nxi", profileId);
        SafePaths.RejectLinks(source);
        SafePaths.RejectLinks(destination);
        if (!Directory.Exists(source) || Directory.Exists(destination)) return false;
        string parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        string migrationLock = Path.Combine(parent, ".migration.lock");
        SafePaths.RejectLinks(migrationLock);
        using var migrationLease = Acquire(migrationLock);
        if (Directory.Exists(destination)) return false;
        using var clientLease = Acquire(Path.Combine(source, ".nx-client.lock"));
        using var coreLease = Acquire(Path.Combine(source, ".nx-runtime.lock"));
        foreach (string marker in new[] { ".nx-unclean.json", ".nx-save-transaction.json" })
            if (File.Exists(Path.Combine(source, marker)))
                throw new NxException("recovery_required", "The previous Kenji profile has an interrupted session or save transaction. Its original data is retained; recover it before importing it into NX Infinity Core.");

        string staging = Path.Combine(parent, ".migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            CopyDirectory(source, staging);
            File.WriteAllText(Path.Combine(staging, "NXI-MIGRATION.txt"),
                "Copied from the legacy Kenji profile. The original backends/ryujinx profile was retained.\n");
            Directory.Move(staging, destination);
            return true;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static FileStream Acquire(string path)
    {
        SafePaths.RejectLinks(path);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException exception)
        { throw new NxException("data_in_use", "Close the previous emulator session before migrating its profile.", exception); }
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(source))
        {
            SafePaths.RejectLinks(entry);
            string name = Path.GetFileName(entry);
            if (name is ".nx-client.lock" or ".nx-runtime.lock") continue;
            string target = Path.Combine(destination, name);
            if (Directory.Exists(entry))
            {
                Directory.CreateDirectory(target);
                CopyDirectory(entry, target);
            }
            else File.Copy(entry, target, overwrite: false);
        }
    }
}
