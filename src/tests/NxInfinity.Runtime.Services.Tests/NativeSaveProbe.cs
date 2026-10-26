using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NxInfinity.Runtime.Services;

internal static partial class Program
{
    // Explicit local integration mode. It accepts only the authored F001 guest's
    // completed native save-bridge report; normal service tests never enter it.
    private static int NativeSaveProbe(string[] args)
    {
        string? outputPath = null;
        var result = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1, ["kind"] = "authored-native-save-service-step",
            ["commercialGameVerified"] = false, ["retailSaveFormatVerified"] = false,
            ["recordedUtc"] = DateTimeOffset.UtcNow, ["passed"] = false,
            ["mutationCompleted"] = false,
            ["scope"] = "Public SaveStore operation over the explicitly selected authored guest native directory after actual adapter discovery. A later genuine guest run must establish acceptance of restored bytes."
        };
        try
        {
            if (args.Length < 2 || args[0] != "--native-save-probe") throw new InvalidOperationException("Invalid probe invocation.");
            var action = args[1];
            if (action is not ("backup" or "restore" or "rollback" or "expect-busy")) throw new InvalidOperationException("Unknown save probe action.");
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 2; i < args.Length; i += 2)
            {
                if (i + 1 == args.Length || !new[] { "--workspace-root", "--backend-root", "--native-report", "--backend-revision", "--report", "--backup-id" }.Contains(args[i]) || !options.TryAdd(args[i], args[i + 1]))
                    throw new InvalidOperationException("Invalid or duplicate save probe option.");
            }
            string Required(string name) => options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new InvalidOperationException("Missing save probe option.");
            static string Absolute(string path) => Path.IsPathFullyQualified(path) && !path.Contains(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                ? Path.GetFullPath(path) : throw new InvalidOperationException("An exact absolute owned path is required.");
            outputPath = Absolute(Required("--report"));
            RefuseProbeLinks(outputPath);
            if (File.Exists(outputPath) || Directory.Exists(outputPath)) throw new InvalidOperationException("Evidence output already exists; use a new filename.");
            var sourcePath = Absolute(Required("--native-report")); RefuseProbeLinks(sourcePath);
            byte[] sourceBytes;
            using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (source.Length > 8 * 1024 * 1024) throw new InvalidOperationException("Native report exceeds the integration limit.");
                using var copy = new MemoryStream(); source.CopyTo(copy); sourceBytes = copy.ToArray();
            }
            using var doc = JsonDocument.Parse(sourceBytes, new JsonDocumentOptions { MaxDepth = 32 });
            var native = doc.RootElement;
            if (native.GetProperty("kind").GetString() != "real-native-authored-save-bridge" || !native.GetProperty("completed").GetBoolean() ||
                native.GetProperty("exitCode").GetInt32() != 0 || native.GetProperty("commercialGameVerified").GetBoolean() ||
                native.GetProperty("dispose").GetProperty("state").GetString() != "disposed" ||
                !native.GetProperty("checks").GetProperty("fully_disposed_runtime_lease_released").GetBoolean())
                throw new InvalidOperationException("A completed, fully disposed authored native save report is required.");
            var counter = native.GetProperty("counter");
            var priorCounter = counter.GetProperty("previous").GetUInt32(); var observedCounter = counter.GetProperty("current").GetUInt32();
            if (!counter.GetProperty("committed").GetBoolean() || !counter.GetProperty("readBack").GetBoolean() || observedCounter != checked(priorCounter + 1))
                throw new InvalidOperationException("The source guest did not commit and read back its authored counter.");
            var hello = native.GetProperty("hello");
            var backendId = hello.GetProperty("backendId").GetString()!;
            var backendRevision = hello.GetProperty("backendRevision").GetString()!;
            var expectedRevision = Required("--backend-revision");
            if (backendId != "ryujinx" || !backendRevision.Equals(expectedRevision, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("This probe requires the explicitly pinned Ryujinx authored fixture adapter.");
            var entries = native.GetProperty("catalog").GetProperty("entries").EnumerateArray().Where(e => e.GetProperty("titleId").GetString() == "010000000000F001").ToArray();
            if (entries.Length != 1) throw new InvalidOperationException("An unambiguous authored F001 native save mapping is required.");
            var entry = entries[0];
            string Text(string name) => entry.GetProperty(name).GetString() ?? throw new InvalidOperationException("Incomplete native mapping.");
            var observation = new NativeSaveObservation(Text("backendId"), backendRevision, Text("titleId"), Text("userId"), Text("saveDataId"),
                Text("type"), Text("formatId"), Text("formatVersion"), Text("relativePath"), entry.GetProperty("existing").GetBoolean(),
                entry.GetProperty("adapterVerified").GetBoolean(), Text("provenance"));
            var sourceHash = Convert.ToHexStringLower(SHA256.HashData(sourceBytes));
            var mapping = NativeSaveMappings.FromNativeObservation(observation, backendId, expectedRevision);
            mapping = mapping with { VerificationEvidence = mapping.VerificationEvidence + "; sourceReportSha256=" + sourceHash + "; authoredGuestCounterAtSourceReport=" + observedCounter };
            var workspaceRoot = Absolute(Required("--workspace-root"));
            var backendRelative = Required("--backend-root");
            if (Path.IsPathRooted(backendRelative) || backendRelative.Contains('\\') || backendRelative.Split('/').Any(p => p is "" or "." or ".." || p.Contains(':')))
                throw new InvalidOperationException("Use the actual backend directory relative to its owned workspace root.");
            var actualRoot = Path.GetFullPath(Path.Combine(workspaceRoot, backendRelative)); RefuseProbeLinks(actualRoot);
            if (!Directory.Exists(actualRoot)) throw new InvalidOperationException("The observed backend root must already exist.");
            var rootFingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(actualRoot)));
            if (native.GetProperty("profileRootSha256").GetString() != rootFingerprint)
                throw new InvalidOperationException("The selected backend root does not match the actual native query's root fingerprint.");
            if (action == "restore" && !Guid.TryParseExact(Required("--backup-id"), "N", out _)) throw new InvalidOperationException("A local ownership receipt ID is required for restore.");
            result["action"] = action; result["sourceNativeReportSha256"] = sourceHash; result["profileRootSha256"] = rootFingerprint;
            result["backendId"] = backendId; result["backendRevision"] = backendRevision; result["identity"] = mapping.Identity;
            result["nativeSaveDataId"] = observation.SaveDataId; result["observedGuestCounterAtSourceReport"] = observedCounter;
            result["nativeHostAssemblySha256"] = native.GetProperty("hostAssemblySha256").GetString();
            result["serviceAssemblySha256"] = ProbeHash(typeof(SaveStore).Assembly.Location);
            result["probeAssemblySha256"] = ProbeHash(Assembly.GetExecutingAssembly().Location);
            var store = new SaveStore(new NxWorkspace(workspaceRoot), backendRelative);
            if (action == "expect-busy")
            {
                try
                {
                    var unexpected = store.CreateBackup(mapping);
                    result["unexpectedBackupId"] = unexpected.Manifest.BackupId;
                    throw new InvalidOperationException("The actual runtime lease was not held; no busy result may be claimed.");
                }
                catch (ServiceException e) when (e.Code == ServiceError.Busy)
                { result["runtimeLeaseBlocked"] = true; result["passed"] = true; }
            }
            else if (action == "backup")
            {
                var backup = store.CreateBackup(mapping); result["mutationCompleted"] = true;
                var verified = store.ListBackups(mapping.Identity).Single(b => b.Manifest.BackupId == backup.Manifest.BackupId);
                result["backup"] = ProbeSnapshot(verified); result["passed"] = true;
            }
            else
            {
                SaveImportReceipt receipt = action == "restore" ? store.RestoreBackup(mapping, Required("--backup-id")) : store.RollbackLastImport(mapping);
                result["mutationCompleted"] = true;
                result["transactionId"] = receipt.TransactionId;
                result["importedBackup"] = ProbeSnapshot(store.InspectBackup(receipt.ImportedBackupId));
                result["preservedBackup"] = ProbeSnapshot(store.InspectBackup(receipt.PreservedBackupId));
                result["preservedOriginalDirectoryPresent"] = Directory.Exists(receipt.PreservedOriginalDirectory);
                result["startupRecoveryMarkerAbsent"] = !File.Exists(Path.Combine(store.BackendDataRoot, SaveStore.PendingTransactionFileName));
                if (result["preservedOriginalDirectoryPresent"] is not true || result["startupRecoveryMarkerAbsent"] is not true)
                    throw new InvalidOperationException("The import did not preserve its expected original/recovery state.");
                result["passed"] = true;
            }
        }
        catch (Exception e)
        {
            result["errorCode"] = e is ServiceException service ? service.Code.ToString() : "InvalidIntegrationStep";
            result["errorType"] = e.GetType().Name;
            Console.Error.WriteLine("Native save integration step failed: " + result["errorCode"] + ". Existing reports, snapshots, and original saves are retained.");
        }
        if (outputPath is not null && !File.Exists(outputPath) && !Directory.Exists(outputPath))
        {
            RefuseProbeLinks(outputPath); Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using var report = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            report.Write(JsonSerializer.SerializeToUtf8Bytes(result, Json)); report.WriteByte((byte)'\n'); report.Flush(true);
        }
        Console.WriteLine(JsonSerializer.Serialize(result, Json));
        return result["passed"] is true ? 0 : 1;
    }

    private static string ProbeHash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(file));
    }
    private static object ProbeSnapshot(SaveBackup backup) => new
    {
        backupId = backup.Manifest.BackupId, manifestSha256 = ProbeHash(Path.Combine(backup.DirectoryPath, "manifest.json")),
        identity = backup.Manifest.Identity, fileCount = backup.Manifest.Files.Count, totalBytes = backup.Manifest.Files.Sum(f => f.Size),
        directories = backup.Manifest.Directories.Count, verificationEvidence = backup.Manifest.VerificationEvidence,
        completePayloadVerified = true
    };
    private static void RefuseProbeLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Integration probe inputs and outputs cannot use links.");
    }
}
