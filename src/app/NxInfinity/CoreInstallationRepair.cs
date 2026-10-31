using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NxInfinity.Runtime;

namespace NxInfinity;

public static class CoreInstallationRepair
{
    private sealed record Journal(int Version, string TargetName, string Token, string ManifestSha256);

    private const long MaximumBytes = 2147483648L;

    private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<CoreRepairPlan> InspectAsync(string manifestPath, string sourceDirectory, string? dotnetPath, CancellationToken ct = default(CancellationToken))
    {
        string target = Path.GetDirectoryName(Path.GetFullPath(manifestPath));
        string source = Path.GetFullPath(sourceDirectory);
        RejectLinks(target);
        RejectLinks(source);
        if (Contains(target, source) || Contains(source, target))
        {
            throw new IOException("Choose a separate extracted copy of this exact NX core package.");
        }
        byte[] original = await ManifestAsync(manifestPath, ct);
        string replacementManifest = Path.Combine(source, "backend.json");
        byte[] array = await ManifestAsync(replacementManifest, ct);
        if (!((ReadOnlySpan<byte>)original.AsSpan()).SequenceEqual((ReadOnlySpan<byte>)array))
        {
            throw new IOException("This package has a different build manifest. Repair requires the exact installed build; use the update flow to change versions.");
        }
        BackendLaunch verified = await BackendInstallation.LoadAsync(replacementManifest, dotnetPath, allowUnqualified: true, ct);
        BackendInstallation backendInstallation = JsonSerializer.Deserialize<BackendInstallation>(original, Json);
        List<string> changed = new List<string>();
        foreach (var (relative, hash) in backendInstallation.Files)
        {
            ct.ThrowIfCancellationRequested();
            string path = Path.Combine(target, relative);
            RejectLinks(path);
            bool flag = !File.Exists(path);
            if (!flag)
            {
                flag = !string.Equals(await HashAsync(path, ct), hash, StringComparison.OrdinalIgnoreCase);
            }
            if (flag)
            {
                changed.Add(relative);
            }
        }
        return new CoreRepairPlan(target, source, Convert.ToHexStringLower(SHA256.HashData(original)), verified.BackendId, verified.Revision, changed);
    }

    public static async Task<CoreRepairReceipt> ApplyAsync(CoreRepairPlan plan, string? dotnetPath, CancellationToken ct = default(CancellationToken))
    {
        CoreRepairPlan coreRepairPlan = await InspectAsync(Path.Combine(plan.Target, "backend.json"), plan.Source, dotnetPath, ct);
        if (coreRepairPlan.ManifestSha256 != plan.ManifestSha256 || !coreRepairPlan.ChangedFiles.SequenceEqual(plan.ChangedFiles))
        {
            throw new IOException("The installation changed after the repair review. Review it again.");
        }
        if (plan.ChangedFiles.Count == 0)
        {
            return new CoreRepairReceipt(plan.BackendId, 0, "");
        }
        string directoryName = Path.GetDirectoryName(plan.Target);
        string token = Guid.NewGuid().ToString("N");
        string stage = Path.Combine(directoryName, ".nx-repair-stage-" + token);
        string backup = Path.Combine(directoryName, ".nx-repair-backup-" + token);
        string journalPath = JournalPath(plan.Target);
        RejectLinks(directoryName);
        RejectLinks(journalPath);
        RejectLinks(journalPath + ".lock");
        if (File.Exists(journalPath))
        {
            throw new IOException("An interrupted repair needs recovery before another repair can begin.");
        }
        using (new FileStream(journalPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            if (File.Exists(journalPath))
            {
                throw new IOException("An interrupted repair needs recovery before another repair can begin.");
            }
            bool transactionStarted = false;
            try
            {
                Directory.CreateDirectory(stage);
                Dictionary<string, string?> inventory = await CopyTreeAsync(plan.Target, stage, ct);
                foreach (string changedFile in plan.ChangedFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    string text = Path.Combine(stage, changedFile);
                    Directory.CreateDirectory(Path.GetDirectoryName(text));
                    await CopyFileAsync(Path.Combine(plan.Source, changedFile), text, ct);
                }
                await BackendInstallation.LoadAsync(Path.Combine(stage, "backend.json"), dotnetPath, allowUnqualified: true, ct);
                await VerifyInventoryAsync(plan.Target, inventory, ct);
                ct.ThrowIfCancellationRequested();
                WriteJournal(journalPath, new Journal(1, Path.GetFileName(plan.Target), token, plan.ManifestSha256));
                transactionStarted = true;
                Directory.Move(plan.Target, backup);
                Directory.Move(stage, plan.Target);
                await BackendInstallation.LoadAsync(Path.Combine(plan.Target, "backend.json"), dotnetPath, allowUnqualified: true, CancellationToken.None);
                File.Delete(journalPath);
                return new CoreRepairReceipt(plan.BackendId, plan.ChangedFiles.Count, backup);
            }
            catch
            {
                if (transactionStarted && !Directory.Exists(plan.Target) && Directory.Exists(backup))
                {
                    Directory.Move(backup, plan.Target);
                    File.Delete(journalPath);
                }
                throw;
            }
            finally
            {
                if (!transactionStarted && Directory.Exists(stage))
                {
                    Directory.Delete(stage, recursive: true);
                }
            }
        }
    }

    public static bool HasPending(string target)
    {
        return File.Exists(JournalPath(target));
    }

    public static async Task<string> RecoverAsync(string target, string? dotnetPath, CancellationToken ct = default(CancellationToken))
    {
        target = Path.GetFullPath(target);
        string journalPath = JournalPath(target);
        RejectLinks(target);
        RejectLinks(journalPath);
        RejectLinks(journalPath + ".lock");
        if (!File.Exists(journalPath))
        {
            return "No interrupted installation repair.";
        }
        using (new FileStream(journalPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            if (new FileInfo(journalPath).Length > 4096)
            {
                throw new IOException("The repair record is invalid; files were preserved.");
            }
            Journal journal = JsonSerializer.Deserialize<Journal>(await File.ReadAllTextAsync(journalPath, ct), Json);
            if ((object)journal == null || journal.Version != 1 || journal.TargetName != Path.GetFileName(target) || !Guid.TryParseExact(journal.Token, "N", out var _) || journal.ManifestSha256.Length != 64 || !journal.ManifestSha256.All(Uri.IsHexDigit))
            {
                throw new IOException("The repair record is invalid; files were preserved.");
            }
            string backup = Path.Combine(Path.GetDirectoryName(target), ".nx-repair-backup-" + journal.Token);
            RejectLinks(backup);
            if (!Directory.Exists(target) && Directory.Exists(backup))
            {
                if (Convert.ToHexStringLower(SHA256.HashData(await ManifestAsync(Path.Combine(backup, "backend.json"), ct))) != journal.ManifestSha256)
                {
                    throw new IOException("The original backup no longer matches the repair record. All files were preserved.");
                }
                Directory.Move(backup, target);
                File.Delete(journalPath);
                return "Restored the original installation after an interrupted repair.";
            }
            if (Directory.Exists(target))
            {
                if (Convert.ToHexStringLower(SHA256.HashData(await ManifestAsync(Path.Combine(target, "backend.json"), ct))) != journal.ManifestSha256)
                {
                    throw new IOException("The core changed after repair was interrupted. Current files and backups were preserved for review.");
                }
                await BackendInstallation.LoadAsync(Path.Combine(target, "backend.json"), dotnetPath, allowUnqualified: true, ct);
                File.Delete(journalPath);
                return "Verified the completed repair. The previous installation remains in its backup folder.";
            }
            throw new IOException("The interrupted repair has no recoverable original directory. Choose a verified replacement package.");
        }
    }

    private static string JournalPath(string target)
    {
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(target)), ".nx-repair-" + Path.GetFileName(target) + ".json");
    }

    private static void WriteJournal(string path, Journal journal)
    {
        using FileStream fileStream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(fileStream, journal);
        fileStream.Flush(flushToDisk: true);
    }

    private static async Task<byte[]> ManifestAsync(string path, CancellationToken ct)
    {
        RejectLinks(path);
        bool flag = !File.Exists(path);
        if (!flag)
        {
            long length = new FileInfo(path).Length;
            bool flag2 = ((length <= 0 || length > 4194304) ? true : false);
            flag = flag2;
        }
        if (flag)
        {
            throw new IOException("NX Infinity Core directory containing its original backend.json manifest.");
        }
        return await File.ReadAllBytesAsync(path, ct);
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        string result;
        await using (FileStream stream = File.OpenRead(path))
        {
            result = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        }
        return result;
    }

    private static bool Contains(string root, string path)
    {
        if (!path.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            return path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        return true;
    }

    private static void RejectLinks(string path)
    {
        for (string text = Path.GetFullPath(path); text != null; text = Path.GetDirectoryName(text))
        {
            if (new FileInfo(text).LinkTarget != null || new DirectoryInfo(text).LinkTarget != null)
            {
                throw new IOException("Installation repair requires ordinary local directories, without symbolic links.");
            }
        }
    }

    private static async Task CopyFileAsync(string from, string to, CancellationToken ct)
    {
        RejectLinks(from);
        RejectLinks(to);
        await using (FileStream source = File.OpenRead(from))
        {
            await using FileStream destination = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(destination, ct);
        }
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(to, File.GetUnixFileMode(from) & (UnixFileMode.OtherExecute | UnixFileMode.OtherWrite | UnixFileMode.OtherRead | UnixFileMode.GroupExecute | UnixFileMode.GroupWrite | UnixFileMode.GroupRead | UnixFileMode.UserExecute | UnixFileMode.UserWrite | UnixFileMode.UserRead));
        }
    }

    private static async Task<Dictionary<string, string?>> CopyTreeAsync(string source, string destination, CancellationToken ct)
    {
        Dictionary<string, string?> inventory = new Dictionary<string, string>(StringComparer.Ordinal);
        long bytes = 0L;
        int entries = 0;
        Stack<string> pending = new Stack<string>();
        pending.Push(source);
        while (pending.Count != 0)
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                ct.ThrowIfCancellationRequested();
                RejectLinks(path);
                int num = entries + 1;
                entries = num;
                if (num > 20000)
                {
                    throw new IOException("The core folder contains too many files for a bounded repair.");
                }
                string target = Path.Combine(destination, Path.GetRelativePath(source, path));
                if (Directory.Exists(path))
                {
                    Directory.CreateDirectory(target);
                    pending.Push(path);
                    inventory.Add(Path.GetRelativePath(source, path), null);
                    continue;
                }
                bytes += new FileInfo(path).Length;
                if (bytes > 2147483648u)
                {
                    throw new IOException("The core folder exceeds the 2 GB repair limit. User data was preserved.");
                }
                await CopyFileAsync(path, target, ct);
                Dictionary<string, string?> dictionary = inventory;
                string relativePath = Path.GetRelativePath(source, path);
                dictionary.Add(relativePath, await HashAsync(target, ct));
            }
        }
        return inventory;
    }

    private static async Task VerifyInventoryAsync(string root, Dictionary<string, string?> inventory, CancellationToken ct)
    {
        int count = 0;
        Stack<string> pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count != 0)
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                ct.ThrowIfCancellationRequested();
                RejectLinks(path);
                count++;
                bool flag = !inventory.TryGetValue(Path.GetRelativePath(root, path), out string hash);
                if (!flag)
                {
                    bool flag2;
                    if (Directory.Exists(path))
                    {
                        flag2 = hash != null;
                    }
                    else
                    {
                        bool flag3 = hash == null;
                        if (!flag3)
                        {
                            flag3 = await HashAsync(path, ct) != hash;
                        }
                        flag2 = flag3;
                    }
                    flag = flag2;
                }
                if (flag)
                {
                    throw new IOException("The core folder changed during repair. Close other emulator processes and retry. Originals were preserved.");
                }
                if (Directory.Exists(path))
                {
                    pending.Push(path);
                }
                hash = null;
            }
        }
        if (count != inventory.Count)
        {
            throw new IOException("The core folder changed during repair. Originals were preserved.");
        }
    }
}
