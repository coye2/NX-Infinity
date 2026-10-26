using System.Reflection;
using System.Text.Json;
using NxInfinity.Runtime;

// This process fixture tests SDK contracts only. It does not emulate a console.
if (args.Contains("--native-services-fixture")) return await NativeServicesContractTests.RunFixtureAsync(args[^2], args[^1]);
if (args.Contains("--fixture")) return await Fixture.RunAsync(args.Last());

var scratch = Path.Combine(Path.GetTempPath(), "nx-contract-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
var results = new List<object>();
int failures = 0;
BackendLaunch Launch(string mode = "normal", bool allow = true, string? revision = null) => new()
{
    BackendId = "fixture", Revision = revision ?? new string('0', 40), ExecutablePath = Environment.ProcessPath!,
    Arguments = Environment.ProcessPath!.EndsWith("dotnet") || Environment.ProcessPath.EndsWith("dotnet.exe")
        ? [Assembly.GetExecutingAssembly().Location, "--fixture", mode] : ["--fixture", mode],
    AllowUnqualified = allow, CommandTimeout = TimeSpan.FromMilliseconds(800), StartupTimeout = TimeSpan.FromSeconds(8)
};
async Task Check(string name, Func<Task> test)
{
    try { await test(); Console.WriteLine("PASS " + name); results.Add(new { name, passed = true, category = "contract-fixture" }); }
    catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + " " + e); results.Add(new { name, passed = false, error = e.Message, category = "contract-fixture" }); }
}
static void Assert(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
static async Task Error(string code, Func<Task> action)
{
    try { await action(); } catch (NxException e) { Assert(e.Code == code, $"Expected {code}, got {e.Code}"); return; }
    throw new Exception($"Expected {code} error.");
}
string Root(string name) => Path.Combine(scratch, name);
async Task<NxRuntime> Initialized(string name, string mode = "normal")
{
    var runtime = await NxRuntime.ConnectAsync(Launch(mode));
    await runtime.InitializeAsync(new RuntimeOptions { DataRoot = Root(name), GraphicsBackend = "null", Headless = true });
    return runtime;
}
var content = Path.Combine(scratch, "owned-test-content.txt");
await File.WriteAllTextAsync(content, "SDK fixture only; not game content");

await Check("qualification requires explicit selection", () => Error("unqualified_backend", async () => { await using var r = await NxRuntime.ConnectAsync(Launch(allow: false)); }));
await Check("revision mismatch rejected", () => Error("version_mismatch", async () => { await using var r = await NxRuntime.ConnectAsync(Launch(revision: new string('1', 40))); }));
await Check("wire version mismatch rejected", () => Error("protocol_mismatch", async () => { await using var r = await NxRuntime.ConnectAsync(Launch("wrong-version")); }));
await Check("missing host explicit failure", () => Error("missing_dependency", async () => { await using var r = await NxRuntime.ConnectAsync(Launch() with { ExecutablePath = Path.Combine(scratch, "absent-host") }); }));
await Check("invalid lifecycle never reaches running", async () =>
{
    await using var r = await NxRuntime.ConnectAsync(Launch());
    await Error("invalid_state", () => r.StartAsync());
    Assert(r.State == RuntimeState.Created);
});
await Check("backend/profile roots isolated and exclusive", async () =>
{
    await using var first = await Initialized("exclusive");
    Assert(first.BackendDataRoot == Path.Combine(Root("exclusive"), "backends", "fixture", "default"));
    await using var second = await NxRuntime.ConnectAsync(Launch());
    await Error("data_in_use", () => second.InitializeAsync(new RuntimeOptions { DataRoot = Root("exclusive") }));
    try { using var lease = new FileStream(Path.Combine(first.BackendDataRoot!, ".nx-runtime.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); throw new Exception("Worker runtime lock was not held."); }
    catch (IOException) { }
});
await Check("schema range/type validation", async () =>
{
    await using var r = await Initialized("schema");
    await Error("invalid_argument", () => r.SetSettingAsync("audio.volume", 2.0));
    await Error("invalid_argument", () => r.SetSettingAsync("audio.volume", "bad"));
    await Error("unsupported", () => r.SetSettingAsync("pokemon.give_item", true));
    var set = await r.SetSettingAsync("audio.volume", 0.25);
    Assert(set.Applied && set.EffectiveKnown && set.Effective!.Value.GetDouble() == .25 && set.Persisted!.Value.GetDouble() == .25);
});
await Check("pending settings do not fake effective state", async () =>
{
    await using var r = await Initialized("pending");
    await r.LoadAsync(content); await r.StartAsync();
    var set = await r.SetSettingAsync("graphics.scale", 2);
    Assert(!set.Applied && set.RequiresRestart && set.Effective!.Value.GetInt32() == 1 && set.Persisted!.Value.GetInt32() == 2);
    var get = await r.GetSettingAsync("graphics.scale");
    Assert(get.Effective!.Value.GetInt32() == 1 && get.Persisted!.Value.GetInt32() == 2);
    await r.StopAsync(); await r.DisposeAsync();
    Assert(r.Shutdown!.Graceful);
    await using var restarted = await Initialized("pending");
    var restored = await restarted.GetSettingAsync("graphics.scale");
    Assert(restored.Effective!.Value.GetInt32() == 2);
});
await Check("input range and unsupported extension errors", async () =>
{
    await using var r = await Initialized("input");
    await r.LoadAsync(content); await r.StartAsync();
    await Error("invalid_argument", () => r.SubmitInputAsync(new InputSnapshot { LeftX = double.NaN }));
    await Error("invalid_argument", () => r.SubmitInputAsync(new InputSnapshot { Player = 8 }));
    await r.SubmitInputAsync(new InputSnapshot { Buttons = ["a"], LeftX = 0.5 });
    await Error("unsupported", () => r.InvokeExtensionAsync("fixture.unimplemented", JsonSerializer.SerializeToElement(new { })));
});
await Check("missing content leaves initialized state", async () =>
{
    await using var r = await Initialized("content");
    await Error("invalid_argument", () => r.LoadAsync(Path.Combine(scratch, "absent.nro")));
    Assert(r.State == RuntimeState.Initialized);
});
await Check("genuine-unavailable metrics stay null", async () =>
{
    await using var r = await Initialized("telemetry");
    var t = await r.GetTelemetryAsync();
    Assert(t.EmulationFps is null && t.FrameTimeMs is null && t.Source == "fixture");
    var report = await r.CreateDiagnosticReportAsync();
    Assert(!report.Contains(scratch) && !report.Contains("secret-path") && !report.Contains("prod.keys"));
});
await Check("cancellation faults uncertain commands", async () =>
{
    var r = await Initialized("cancel", "hang-telemetry");
    using var ct = new CancellationTokenSource(80);
    await Error("cancelled", () => r.GetTelemetryAsync(ct.Token));
    Assert(r.State == RuntimeState.Faulted);
    await r.DisposeAsync();
    Assert(!r.Shutdown!.Graceful && File.Exists(Path.Combine(r.BackendDataRoot!, ".nx-unclean.json")));
});
await Check("timeout forces shutdown and recovery marker persists", async () =>
{
    var r = await Initialized("timeout", "hang-telemetry");
    await Error("timeout", () => r.GetTelemetryAsync());
    await r.DisposeAsync();
    Assert(r.Shutdown is { Graceful: false, ForcedTermination: true });
    var marker = Path.Combine(r.BackendDataRoot!, ".nx-unclean.json");
    Assert(File.Exists(marker));
    await using (var blocked = await NxRuntime.ConnectAsync(Launch()))
        await Error("unclean_shutdown", () => blocked.InitializeAsync(new RuntimeOptions { DataRoot = Root("timeout") }));
    Assert(File.Exists(marker), "An unrelated clean fixture disposal must not clear a prior unclean marker.");
    await using var recovery = await NxRuntime.ConnectAsync(Launch());
    await recovery.InitializeAsync(new RuntimeOptions { DataRoot = Root("timeout"), AcknowledgeUncleanShutdown = true });
    await recovery.DisposeAsync(); Assert(!File.Exists(marker));
});
await Check("save transaction blocks core initialization", async () =>
{
    var dir = Path.Combine(Root("transaction"), "backends", "fixture", "default"); Directory.CreateDirectory(dir);
    await File.WriteAllTextAsync(Path.Combine(dir, ".nx-save-transaction.json"), "{}");
    await using var r = await NxRuntime.ConnectAsync(Launch());
    await Error("recovery_required", () => r.InitializeAsync(new RuntimeOptions { DataRoot = Root("transaction") }));
});
await Check("preflight failure releases lease for explicit retry", async () =>
{
    var dir = Path.Combine(Root("retry"), "backends", "fixture", "default"); Directory.CreateDirectory(dir);
    await File.WriteAllTextAsync(Path.Combine(dir, ".nx-unclean.json"), "{\"old\":true}");
    await using var r = await NxRuntime.ConnectAsync(Launch());
    await Error("unclean_shutdown", () => r.InitializeAsync(new RuntimeOptions { DataRoot = Root("retry") }));
    await r.InitializeAsync(new RuntimeOptions { DataRoot = Root("retry"), AcknowledgeUncleanShutdown = true });
    Assert(r.State == RuntimeState.Initialized);
});
await Check("orphan runtime lease preserves previous crash marker", async () =>
{
    var dir = Path.Combine(Root("orphan"), "backends", "fixture", "default"); Directory.CreateDirectory(dir);
    var marker = Path.Combine(dir, ".nx-unclean.json"); const string previous = "{\"oldWorker\":true}";
    await File.WriteAllTextAsync(marker, previous);
    using var orphan = new FileStream(Path.Combine(dir, ".nx-runtime.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    await using (var r = await NxRuntime.ConnectAsync(Launch()))
        await Error("data_in_use", () => r.InitializeAsync(new RuntimeOptions { DataRoot = Root("orphan"), AcknowledgeUncleanShutdown = true }));
    Assert(await File.ReadAllTextAsync(marker) == previous);
});
await Check("rejected initialization restores prior marker", async () =>
{
    var dir = Path.Combine(Root("rejected"), "backends", "fixture", "default"); Directory.CreateDirectory(dir);
    var marker = Path.Combine(dir, ".nx-unclean.json"); const string previous = "{\"earlierCrash\":true}";
    await File.WriteAllTextAsync(marker, previous);
    await using (var r = await NxRuntime.ConnectAsync(Launch("reject-initialize")))
        await Error("missing_dependency", () => r.InitializeAsync(new RuntimeOptions { DataRoot = Root("rejected"), AcknowledgeUncleanShutdown = true }));
    Assert(await File.ReadAllTextAsync(marker) == previous);
});
await Check("unknown settings lifecycle never treated as live", async () =>
{
    await using var r = await NxRuntime.ConnectAsync(Launch("bad-schema"));
    await Error("protocol_mismatch", () => r.GetSettingSchemasAsync());
});
await Check("settings restoration validates full set before mutation", async () =>
{
    var dir = Path.Combine(Root("invalid-store"), "backends", "fixture", "default"); Directory.CreateDirectory(dir);
    await File.WriteAllTextAsync(Path.Combine(dir, "nx-settings.json"), JsonSerializer.Serialize(new
    { backendId = "fixture", revision = new string('0', 40), values = new Dictionary<string, object> { ["audio.volume"] = .25, ["unmapped.setting"] = true } }));
    await using var r = await NxRuntime.ConnectAsync(Launch());
    await Error("settings_migration_required", () => r.InitializeAsync(new RuntimeOptions { DataRoot = Root("invalid-store") }));
    var state = await r.GetSettingAsync("audio.volume");
    Assert(state.Effective!.Value.GetDouble() == 1.0);
});
await Check("applet reply passes a blocked load without deadlock", async () =>
{
    await using var r = await Initialized("interaction", "ask-on-load");
    var loading = r.LoadAsync(content);
    using var limit = new CancellationTokenSource(3000);
    await foreach (var message in r.ReadEventsAsync(limit.Token))
    {
        if (message.Interaction is not { } request) continue;
        await r.RespondToInteractionAsync(new InteractionResponse { InteractionId = request.InteractionId, Accepted = true, Text = "NX" }, limit.Token);
        break;
    }
    await loading;
    Assert(r.State == RuntimeState.Loaded);
});
await Check("guest exit event wins over an in-flight start acknowledgment", async () =>
{
    await using var r = await Initialized("guest-exit", "exit-on-start");
    await r.LoadAsync(content);
    await r.StartAsync();
    Assert(r.State == RuntimeState.Stopped, "A stale running acknowledgment must not undo terminal guest state.");
    await Error("invalid_state", () => r.PauseAsync());
});
await Check("native focus-loss pause wins over an in-flight start acknowledgment", async () =>
{
    await using var r = await Initialized("native-pause", "pause-on-start");
    await r.LoadAsync(content); await r.StartAsync();
    Assert(r.State == RuntimeState.Paused, "Native pause must survive the older start response.");
    await r.PauseAsync(); // The native event already completed this boundary.
    await r.StopAsync(); await r.StopAsync();
    Assert(r.State == RuntimeState.Stopped, "Repeated stop must remain terminal.");
});
await Check("a late native pause cannot resurrect a stopped session", async () =>
{
    await using var r = await Initialized("late-native-pause", "late-pause-after-stop");
    await r.LoadAsync(content); await r.StartAsync(); await r.StopAsync();
    Assert(r.State == RuntimeState.Stopped, "A stale pause resurrected the stopped runtime.");
});
await Check("long content deadline never lengthens stop recovery", async () =>
{
    await using var r = await NxRuntime.ConnectAsync(Launch("slow-content-blocked-stop") with { ContentTimeout = TimeSpan.FromSeconds(4) });
    await r.InitializeAsync(new RuntimeOptions { DataRoot = Root("separate-deadlines"), GraphicsBackend = "null", Headless = true });
    await r.LoadAsync(content); Assert(r.State == RuntimeState.Loaded, "An allowed long load was cut off by the control deadline.");
    var started = System.Diagnostics.Stopwatch.StartNew();
    await Error("timeout", () => r.StopAsync());
    Assert(started.Elapsed < TimeSpan.FromSeconds(3), "Stop inherited the long content deadline.");
    await r.DisposeAsync();
    Assert(r.Shutdown is { ForcedTermination: true, Graceful: false }, "A timed-out native stop was falsely reported clean.");
});
await Check("malformed worker responses fault connection", async () =>
{
    await using var r = await Initialized("malformed", "bad-telemetry");
    await Error("protocol_mismatch", () => r.GetTelemetryAsync());
    Assert(r.State == RuntimeState.Faulted);
});
await Check("oversized responses bounded", async () =>
{
    await using var r = await Initialized("oversized", "oversize-telemetry");
    await Error("protocol_mismatch", () => r.GetTelemetryAsync());
});
await Check("repeated clean create/dispose cycles", async () =>
{
    for (int i = 0; i < 5; i++)
    {
        var r = await Initialized("cycles"); await r.LoadAsync(content); await r.StartAsync();
        await r.PauseAsync(); await r.ResumeAsync(); await r.StopAsync(); await r.DisposeAsync();
        Assert(r.Shutdown!.Graceful && !File.Exists(Path.Combine(r.BackendDataRoot!, ".nx-unclean.json")));
        await r.DisposeAsync();
    }
});
await Check("manifest traversal and artifact tampering rejected", async () =>
{
    var dir = Root("manifest"); Directory.CreateDirectory(dir);
    var manifest = Path.Combine(dir, "backend.json");
    await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(new { manifestVersion = 1, backendId = "nxi", revision = new string('0', 40), entryPoint = "../escape", files = new Dictionary<string, string> { ["../escape"] = "00" } }));
    await Error("invalid_argument", () => BackendInstallation.LoadAsync(manifest, allowUnqualified: true));
    await File.WriteAllTextAsync(Path.Combine(dir, "host"), "not a core");
    await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(new { manifestVersion = 1, backendId = "nxi", revision = new string('0', 40), entryPoint = "host", files = new Dictionary<string, string> { ["host"] = new string('0', 64) } }));
    await Error("version_mismatch", () => BackendInstallation.LoadAsync(manifest, allowUnqualified: true));
});
await Check("managed artifacts rechecked at process startup", async () =>
{
    var dir = Root("recheck"); Directory.CreateDirectory(dir);
    var host = Path.Combine(dir, "host.dll"); await File.WriteAllTextAsync(host, "first");
    var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(host)));
    var manifest = Path.Combine(dir, "backend.json");
    await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(new { manifestVersion = 1, backendId = "nxi", revision = new string('0', 40), entryPoint = "host.dll", managedAssembly = true, files = new Dictionary<string, string> { ["host.dll"] = hash } }));
    var launch = await BackendInstallation.LoadAsync(manifest, Environment.ProcessPath!, true);
    await File.WriteAllTextAsync(host, "tampered");
    await Error("version_mismatch", async () => { await using var r = await NxRuntime.ConnectAsync(launch); });
});
if (!OperatingSystem.IsWindows()) await Check("mutable data refuses symlink escape", async () =>
{
    Directory.CreateDirectory(Root("real")); Directory.CreateSymbolicLink(Root("link"), Root("real"));
    await using var r = await NxRuntime.ConnectAsync(Launch());
    await Error("invalid_argument", () => r.InitializeAsync(new RuntimeOptions { DataRoot = Root("link") }));
});

await NativeServicesContractTests.RunAsync(Check, Root);

var reportPath = args.SkipWhile(x => x != "--report").Skip(1).FirstOrDefault();
if (reportPath is not null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
    await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
    { generatedUtc = DateTimeOffset.UtcNow, suite = "SDK contract tests", mockOnly = true, gameplayEvidence = false, passed = results.Count - failures, failed = failures, results }, new JsonSerializerOptions { WriteIndented = true }));
}
Directory.Delete(scratch, true);
Console.WriteLine($"Contract fixture tests: {results.Count - failures} passed, {failures} failed. No real-core/gameplay claims.");
return failures == 0 ? 0 : 1;

internal static class Fixture
{
    internal static async Task<int> RunAsync(string mode)
    {
        string state = "created";
        FileStream? lease = null;
        var settings = new Dictionary<string, JsonElement> { ["audio.volume"] = JsonSerializer.SerializeToElement(1.0), ["graphics.scale"] = JsonSerializer.SerializeToElement(1) };
        try
        {
            while (await Console.In.ReadLineAsync() is { } line)
            {
                using var doc = JsonDocument.Parse(line); var request = doc.RootElement;
                var id = request.GetProperty("id").GetInt64(); var method = request.GetProperty("method").GetString(); var p = request.GetProperty("params");
                object result;
                if (method == "initialize" && mode == "reject-initialize")
                { Console.WriteLine(JsonSerializer.Serialize(new { v = 1, id, ok = false, error = new { code = "missing_dependency", message = "Fixture deliberately rejects initialization" } })); continue; }
                if (method == "load" && mode == "ask-on-load")
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { v = 1, @event = "interaction.request", data = new { interactionId = "fixture-prompt", kind = "text", details = new { title = "Fixture" }, timeoutSeconds = 3 } }));
                    await Console.Out.FlushAsync();
                    using var response = JsonDocument.Parse((await Console.In.ReadLineAsync())!);
                    if (response.RootElement.GetProperty("method").GetString() != "interaction.respond") return 9;
                    Console.WriteLine(JsonSerializer.Serialize(new { v = 1, id = response.RootElement.GetProperty("id").GetInt64(), ok = true, result = new { accepted = true } }));
                }
                if (method == "telemetry.get" && mode == "hang-telemetry") { await Task.Delay(Timeout.Infinite); return 0; }
                if (method == "load" && mode == "slow-content-blocked-stop") await Task.Delay(1200);
                if (method == "stop" && mode == "slow-content-blocked-stop") { await Task.Delay(Timeout.Infinite); return 0; }
                if (method == "telemetry.get" && mode == "bad-telemetry") { Console.WriteLine("not json"); continue; }
                if (method == "telemetry.get" && mode == "oversize-telemetry") { Console.WriteLine(new string('x', 1024 * 1024 + 1)); continue; }
                switch (method)
                {
                    case "hello": result = new { protocolVersion = 1, backendId = "fixture", backendName = "SDK TEST FIXTURE - NO EMULATION", backendRevision = new string('0', 40), adapterVersion = "test", qualification = "mock-tests-only", capabilities = new[] { "initialize", "load", "start", "pause", "resume", "stop", "dispose", "settings.list", "settings.get", "settings.set", "telemetry.get", "input.update", "interaction.respond" }, limitations = new[] { "No emulation" } }; break;
                    case "initialize":
                        var root = p.GetProperty("dataRoot").GetString()!;
                        lease = new FileStream(Path.Combine(root, ".nx-runtime.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                        state = "initialized"; result = new { state }; break;
                    case "load": state = "loaded"; result = new { state, titleId = "0000000000000000", name = "Fixture" }; break;
                    case "start": case "resume":
                        state = "running"; result = new { state };
                        if (mode == "exit-on-start")
                        {
                            state = "stopped";
                            Console.WriteLine(JsonSerializer.Serialize(new { v = 1, @event = "state.changed", data = new { state } }));
                        }
                        else if (mode == "pause-on-start")
                        {
                            state = "paused";
                            Console.WriteLine(JsonSerializer.Serialize(new { v = 1, @event = "state", data = new { state } }));
                        }
                        break;
                    case "pause": state = "paused"; result = new { state }; break;
                    case "stop":
                        state = "stopped"; result = new { state };
                        if (mode == "late-pause-after-stop")
                        {
                            Console.WriteLine(JsonSerializer.Serialize(new { v = 1, @event = "state", data = new { state = "stopped" } }));
                            Console.WriteLine(JsonSerializer.Serialize(new { v = 1, @event = "state", data = new { state = "paused" } }));
                        }
                        break;
                    case "dispose": state = "disposed"; lease?.Dispose(); lease = null; result = new { state }; break;
                    case "settings.list": result = new { settings = new[]
                    {
                        new { id = "audio.volume", kind = "number", defaultValue = 1.0, minimum = 0, maximum = 1, apply = mode == "bad-schema" ? "unknown" : "live", supported = true },
                        new { id = "graphics.scale", kind = "integer", defaultValue = 1.0, minimum = 1, maximum = 4, apply = "before_load", supported = true }
                    }}; break;
                    case "settings.get": case "settings.set":
                        var settingId = p.GetProperty("id").GetString()!;
                        if (method == "settings.set") settings[settingId] = p.GetProperty("value").Clone();
                        result = new { id = settingId, requested = settings[settingId], effective = settings[settingId], effectiveKnown = true, applied = true, requiresRestart = false }; break;
                    case "telemetry.get": result = new { state, source = "fixture", backendMetrics = new { privatePath = "/secret-path/prod.keys" } }; break;
                    case "input.update": result = new { accepted = true }; break;
                    default: Console.WriteLine(JsonSerializer.Serialize(new { v = 1, id, ok = false, error = new { code = "unsupported", message = "fixture only" } })); continue;
                }
                Console.WriteLine(JsonSerializer.Serialize(new { v = mode == "wrong-version" ? 2 : 1, id, ok = true, result }));
                await Console.Out.FlushAsync();
                if (method == "dispose") return 0;
            }
            return 0;
        }
        finally { lease?.Dispose(); }
    }
}
