using System.Reflection;
using System.Text.Json;
using NxInfinity.Runtime;

// A separate real child process speaks synthetic native-service DTOs. It is not a
// core adapter and cannot establish gameplay, native filesystem, or device proof.
internal static class NativeServicesContractTests
{
    private const string Title = "010000000000FE01";
    private const string OtherTitle = "010000000000FE02";
    private static readonly string Revision = new('c', 40);
    private static readonly string BuildId = new('b', 64);
    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });

    internal static async Task RunAsync(Func<string, Func<Task>, Task> check, Func<string, string> root)
    {
        string Area(string name) => root("native-" + name);
        var content = Area("content-[010000000000FE01]-[v99.0]-" + BuildId + ".nro");
        await File.WriteAllTextAsync(content, "Authored SDK process fixture bytes, not an executable or game.");
        var mountPath = Area("owned-mod"); Directory.CreateDirectory(mountPath);
        var mounts = new[] { new NativeModMount("test-mod", mountPath, true) };

        async Task<NxRuntime> Open(string name, string mode = "normal", string backend = "fixture", bool initialize = true)
        {
            var r = await NxRuntime.ConnectAsync(Launch(mode, backend));
            try
            {
                if (initialize) await r.InitializeAsync(new RuntimeOptions { DataRoot = Area(name), GraphicsBackend = "null", Headless = true });
                return r;
            }
            catch { await r.DisposeAsync(); throw; }
        }
        static async Task<JsonElement> Trace(NxRuntime runtime) =>
            await runtime.InvokeExtensionAsync(runtime.Backend.BackendId + ".trace", Empty);
        static int Calls(JsonElement trace, string operation) => trace.GetProperty("calls").EnumerateArray().Count(x => x.GetString() == operation);

        await check("native mods reject incomplete or unrelated bindings before dispatch", async () =>
        {
            await using var r = await Open("mod-bindings");
            foreach (var build in new[] { "", BuildId[..16], BuildId[..40], BuildId + "0", new string('0', 64), new string('g', 64) })
                await Error("version_mismatch", () => r.ConfigureNativeModsAsync(Title, mounts, build, Revision));
            foreach (var pin in new[] { "", Revision[..8], new string('d', 40), new string('0', 40), new string('z', 40) })
                await Error("version_mismatch", () => r.ConfigureNativeModsAsync(Title, mounts, BuildId, pin));
            Assert(Calls(await Trace(r), "mods.set") == 0, "Invalid build/revision reached the child process.");
        });
        await check("bound native mod DTO keeps normalized exact identity and unobserved status", async () =>
        {
            await using var r = await Open("mod-dto");
            var state = await r.ConfigureNativeModsAsync(Title.ToLowerInvariant(), mounts, BuildId, Revision.ToUpperInvariant());
            Assert(state.Status == "configured_unobserved" && !state.LoaderObserved && state.Mounts.Length == 1);
            var p = (await Trace(r)).GetProperty("lastModParams");
            Assert(p.GetProperty("titleId").GetString() == Title);
            Assert(p.GetProperty("expectedBuildId").GetString() == BuildId.ToUpperInvariant());
            Assert(p.GetProperty("backendRevision").GetString() == Revision);
            Assert(p.GetProperty("mounts")[0].GetProperty("sourcePath").GetString() == mountPath);
        });
        await check("native mod selection rejects duplicate IDs missing roots and unsafe identifiers", async () =>
        {
            await using var r = await Open("mod-inputs");
            await Error("invalid_argument", () => r.ConfigureNativeModsAsync(Title, [mounts[0], mounts[0]], BuildId, Revision));
            await Error("invalid_argument", () => r.ConfigureNativeModsAsync(Title, [new("../escape", mountPath, true)], BuildId, Revision));
            await Error("invalid_argument", () => r.ConfigureNativeModsAsync(Title, [new("missing", Area("no-mod-directory"), true)], BuildId, Revision));
            await Error("invalid_argument", () => r.ConfigureNativeModsAsync(new string('0', 16), mounts, BuildId, Revision));
            Assert(Calls(await Trace(r), "mods.set") == 0);
        });
        await check("native mod writes require initialized before-load state", async () =>
        {
            await using var r = await Open("mod-lifecycle", initialize: false);
            await Error("invalid_state", () => r.ConfigureNativeModsAsync(Title, mounts, BuildId, Revision));
            await r.InitializeAsync(new RuntimeOptions { DataRoot = Area("mod-lifecycle"), GraphicsBackend = "null", Headless = true });
            await r.LoadAsync(content);
            await Error("invalid_state", () => r.ConfigureNativeModsAsync(Title, mounts, BuildId, Revision));
            Assert(Calls(await Trace(r), "mods.set") == 0);
        });
        await check("missing native mod capability fails without issuing a worker command", async () =>
        {
            await using var r = await Open("no-mods", "omit-mods");
            await Error("unsupported", () => r.ConfigureNativeModsAsync(Title, mounts, BuildId, Revision));
            Assert(Calls(await Trace(r), "mods.set") == 0 && r.State == RuntimeState.Initialized);
        });
        await check("advertised native mod rejection stays visible and does not fallback", async () =>
        {
            await using var r = await Open("reject-mods", "reject-mods");
            await Error("unsupported", () => r.ConfigureNativeModsAsync(Title, mounts, BuildId, Revision));
            Assert(Calls(await Trace(r), "mods.set") == 1 && r.State == RuntimeState.Initialized);
        });
        await check("NXI service names dispatch through the single core protocol", async () =>
        {
            await using var r = await Open("nxi-names", backend: "nxi");
            await r.ConfigureNativeModsAsync(Title, mounts, BuildId, Revision);
            await r.GetNativeModsAsync(Title);
            var saves = await r.DiscoverNativeSavesAsync(Title);
            var trace = await Trace(r);
            Assert(Calls(trace, "mods.set") == 1 && Calls(trace, "mods.get") == 1 && Calls(trace, "saves.list") == 1);
            Assert(Calls(trace, "eden.mods.configure") == 0 && saves.Entries[0].BackendId == "nxi");
        });
        if (!OperatingSystem.IsWindows()) await check("native mod source symlinks are rejected before dispatch", async () =>
        {
            var link = Area("mod-link"); Directory.CreateSymbolicLink(link, mountPath);
            await using var r = await Open("mod-link-case");
            await Error("invalid_argument", () => r.ConfigureNativeModsAsync(Title, [new("linked", link, true)], BuildId, Revision));
            Assert(Calls(await Trace(r), "mods.set") == 0);
        });

        foreach (var mode in new[] { "save-parent", "save-absolute", "save-windows", "save-dot", "save-empty-segment", "save-backslash", "save-colon", "save-backend", "save-title-mismatch", "save-null-entries", "save-null-row", "save-oversized" })
        {
            var selected = mode;
            await check("unsafe native catalog rejected: " + selected, async () =>
            {
                await using var r = await Open(selected, selected);
                await Error("protocol_mismatch", () => r.DiscoverNativeSavesAsync(Title));
            });
        }
        await check("native save discovery requires an initialized profile even for an empty catalog", async () =>
        {
            await using var r = await Open("save-created", "save-empty", initialize: false);
            await Error("invalid_state", () => r.DiscoverNativeSavesAsync(Title));
            Assert(Calls(await Trace(r), "saves.list") == 0, "Invalid save query was sent before profile initialization.");
        });
        await check("native save DTO preserves exact identity format and observation limits", async () =>
        {
            await using var r = await Open("save-dto");
            var catalog = await r.DiscoverNativeSavesAsync(Title.ToLowerInvariant());
            var save = catalog.Entries.Single();
            Assert(save.BackendId == "fixture" && save.TitleId == Title && save.UserId.Length == 32 && save.SaveDataId.Length == 16);
            Assert(save.FormatId == "fixture-opaque" && save.FormatVersion == "1" && save.RelativePath == "native/saves/current");
            Assert(!save.Existing && !save.AdapterVerified && save.Provenance.Contains("synthetic", StringComparison.Ordinal));
            Assert(catalog.Limitations.Any(x => x.Contains("no emulation", StringComparison.Ordinal)));
        });
        if (!OperatingSystem.IsWindows()) await check("native save directory symlink cannot escape the actual data root", async () =>
        {
            await using var r = await Open("save-link", "save-link");
            Directory.CreateDirectory(Area("save-outside"));
            Directory.CreateSymbolicLink(Path.Combine(r.BackendDataRoot!, "linked"), Area("save-outside"));
            await Error("invalid_argument", () => r.DiscoverNativeSavesAsync(Title));
        });
        await check("save capability absence is not an empty successful catalog", async () =>
        {
            await using var r = await Open("save-capability", "omit-saves");
            await Error("unsupported", () => r.DiscoverNativeSavesAsync(Title));
            Assert(Calls(await Trace(r), "saves.list") == 0);
        });

        await check("content inspection never infers title build or release from filename", async () =>
        {
            await using var r = await Open("unknown-content", initialize: false);
            foreach (var info in new[] { await r.InspectContentAsync(content), await r.InspectContentAsync(content, false) })
                Assert(info.TitleId is null && info.BuildId is null && info.DisplayVersion is null && !info.IdentityVerified && info.BuildIdSource is null);
            Assert(r.State == RuntimeState.Created && r.BackendDataRoot is null, "Inspection initialized an emulation session.");
        });
        await check("parsed unverified title is not promoted to verified main identity", async () =>
        {
            await using var r = await Open("metadata-attestation", "metadata-title", initialize: false);
            var info = await r.InspectContentAsync(content, false);
            Assert(info.TitleId == Title && !info.IdentityVerified && info.BuildId is null && info.BuildIdSource is null);
            Assert(info.Issues.Single().Code == "fixture_metadata_only");
        });
        foreach (var legacy in new[] { true, false })
        {
            await check("bounded native metadata applies to " + (legacy ? "legacy" : "artwork-option") + " inspection overload", async () =>
            {
                await using var r = await Open("metadata-bound-" + legacy, "metadata-oversized", initialize: false);
                await Error("protocol_mismatch", () => legacy ? r.InspectContentAsync(content) : r.InspectContentAsync(content, false));
            });
        }
        await check("content artwork rejects unsupported malformed and oversized data", async () =>
        {
            await Error("protocol_mismatch", () => Task.FromResult(new ContentArtwork("image/svg+xml", Convert.ToBase64String(new byte[12])).Decode()));
            await Error("protocol_mismatch", () => Task.FromResult(new ContentArtwork("image/png", "not base64!").Decode()));
            await Error("protocol_mismatch", () => Task.FromResult(new ContentArtwork("image/png", new string('A', 350001)).Decode()));
        });
        await check("missing inspection capability stays unsupported", async () =>
        {
            await using var r = await Open("content-capability", "omit-inspect", initialize: false);
            await Error("unsupported", () => r.InspectContentAsync(content, false));
            Assert(Calls(await Trace(r), "content.inspect") == 0);
        });

        await check("input routing DTO retains guest ownership and neutral-release requirement", async () =>
        {
            await using var r = await Open("input-routing");
            await r.LoadAsync(content); await r.StartAsync();
            var devices = await r.GetCoreInputDevicesAsync();
            Assert(devices.InputTarget == "ui" && !devices.Devices.Single().Connected && devices.PhysicalObservation == "synthetic DTO; no physical input");
            await Error("invalid_state", () => r.SubmitInputAsync(new InputSnapshot { Buttons = ["a"] }));
            var acquired = await r.SetInputTargetAsync(true);
            Assert(acquired.InputTarget == "guest" && acquired.RequiresNeutral);
            await Error("invalid_state", () => r.SubmitInputAsync(new InputSnapshot { Buttons = ["a"] }));
            await r.SubmitInputAsync(new InputSnapshot());
            await r.SubmitInputAsync(new InputSnapshot { Buttons = ["a"], LeftX = .5 });
            var released = await r.SetInputTargetAsync(false);
            Assert(released.InputTarget == "ui" && !released.RequiresNeutral);
            Assert(!(await Trace(r)).GetProperty("heldInput").GetBoolean(), "The fixture's ownership response did not clear held input.");
            await Error("invalid_state", () => r.SubmitInputAsync(new InputSnapshot { Buttons = ["a"] }));
            var again = await r.SetInputTargetAsync(true);
            Assert(again.RequiresNeutral, "Reacquisition lost its neutral gate.");
        });
        await check("missing input-routing capability cannot report ownership success", async () =>
        {
            await using var r = await Open("input-capability", "omit-input");
            await Error("unsupported", () => r.SetInputTargetAsync(true));
            Assert(Calls(await Trace(r), "input.routing.set") == 0);
        });
        await check("invalid input assignment and window bounds never reach the worker", async () =>
        {
            await using var r = await Open("native-input-bounds");
            await Error("invalid_argument", () => r.AssignCoreInputAsync(8, "fixture"));
            await Error("invalid_argument", () => r.AssignCoreInputAsync(0, new string('x', 129)));
            await Error("invalid_argument", () => r.SetRenderWindowAsync(new RenderWindowChange { Width = 63 }));
            await Error("invalid_argument", () => r.SetRenderWindowAsync(new RenderWindowChange { Height = 16385 }));
            var trace = await Trace(r);
            Assert(Calls(trace, "input.assign") == 0 && Calls(trace, "window.set") == 0);
        });
    }

    private static BackendLaunch Launch(string mode, string backend) => new()
    {
        BackendId = backend, Revision = Revision, ExecutablePath = Environment.ProcessPath!,
        Arguments = Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? [Assembly.GetExecutingAssembly().Location, "--native-services-fixture", backend, mode]
            : ["--native-services-fixture", backend, mode],
        AllowUnqualified = true, CommandTimeout = TimeSpan.FromSeconds(3), StartupTimeout = TimeSpan.FromSeconds(8)
    };
    private static void Assert(bool value, string message = "Native service contract assertion failed.")
    { if (!value) throw new InvalidOperationException(message); }
    private static async Task Error(string code, Func<Task> action)
    {
        try { await action(); }
        catch (NxException e) { Assert(e.Code == code, $"Expected {code}, got {e.Code}."); return; }
        throw new InvalidOperationException("Expected " + code + " error.");
    }

    internal static async Task<int> RunFixtureAsync(string backend, string mode)
    {
        var calls = new List<string>(); JsonElement? lastModParams = null;
        string state = "created", inputTarget = "ui";
        bool requiresNeutral = false, heldInput = false;
        FileStream? lease = null;
        var modSet = backend == "eden" ? "eden.mods.configure" : "mods.set";
        var modGet = backend == "eden" ? "eden.mods.list" : "mods.get";
        var savesGet = backend == "eden" ? "eden.saves.discover" : "saves.list";
        var capabilities = new HashSet<string>(["initialize", "load", "start", "pause", "resume", "stop", "dispose", "settings.list", "settings.get", "settings.set", "content.inspect", modSet, modGet, savesGet, "input.devices", "input.assign", "input.routing.set", "input.update", "window.get", "window.set", backend + ".trace"]);
        if (mode == "omit-mods") capabilities.Remove(modSet);
        if (mode == "omit-saves") capabilities.Remove(savesGet);
        if (mode == "omit-inspect") capabilities.Remove("content.inspect");
        if (mode == "omit-input") capabilities.Remove("input.routing.set");
        try
        {
            while (await Console.In.ReadLineAsync() is { } line)
            {
                using var doc = JsonDocument.Parse(line);
                var request = doc.RootElement; var id = request.GetProperty("id").GetInt64();
                var method = request.GetProperty("method").GetString()!; var p = request.GetProperty("params");
                if (method != backend + ".trace") calls.Add(method);
                object result;
                if (method == backend + ".trace") result = new { calls, lastModParams, heldInput, requiresNeutral, inputTarget };
                else if (method == "hello") result = new
                {
                    protocolVersion = 1, backendId = backend, backendName = "SYNTHETIC SDK DTO FIXTURE; NO NATIVE CORE",
                    backendRevision = Revision, adapterVersion = "test", qualification = "mock-tests-only",
                    capabilities = capabilities.ToArray(), limitations = new[] { "Synthetic process contract fixture; no emulation or gameplay evidence." }
                };
                else if (method == "initialize")
                {
                    lease = new FileStream(Path.Combine(p.GetProperty("dataRoot").GetString()!, ".nx-runtime.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    state = "initialized"; result = new { state };
                }
                else if (method == "load") { state = "loaded"; result = new { state, name = "Unknown synthetic fixture", format = "nro", identityVerified = false }; }
                else if (method is "start" or "resume") { state = "running"; result = new { state }; }
                else if (method == "pause") { state = "paused"; result = new { state }; }
                else if (method == "stop") { state = "stopped"; result = new { state }; }
                else if (method == "dispose") { lease?.Dispose(); lease = null; state = "disposed"; result = new { state }; }
                else if (method == "settings.list") result = new { settings = Array.Empty<object>() };
                else if (method == modSet || method == modGet)
                {
                    if (mode == "reject-mods") { await Failure(id, "unsupported"); continue; }
                    if (method == modSet) lastModParams = p.Clone();
                    result = new { titleId = Title, mounts = lastModParams?.GetProperty("mounts").Deserialize<NativeModMount[]>(JsonOptions) ?? [], status = "configured_unobserved", loaderObserved = false, requiresRestart = false, evidence = new[] { "Synthetic DTO acknowledgment, not native loader consumption." } };
                }
                else if (method == savesGet)
                {
                    if (mode == "save-null-entries") result = new { entries = (object?)null };
                    else if (mode == "save-null-row") result = new { entries = new object?[] { null } };
                    else if (mode == "save-empty") result = new { entries = Array.Empty<object>() };
                    else if (mode == "save-oversized") result = new { entries = Enumerable.Repeat(new { }, 4097).ToArray() };
                    else
                    {
                        var relativePath = mode switch
                        {
                            "save-parent" => "../outside", "save-absolute" => "/outside/save", "save-windows" => "C:/outside/save",
                            "save-dot" => "native/./save", "save-empty-segment" => "native//save", "save-backslash" => "native\\save",
                            "save-colon" => "native/save:stream", "save-link" => "linked/save", _ => "native/saves/current"
                        };
                        result = new
                        {
                            entries = new[] { new NativeSaveDescriptor
                            {
                                BackendId = mode == "save-backend" ? "unrelated" : backend, TitleId = mode == "save-title-mismatch" ? OtherTitle : Title,
                                UserId = new string('1', 32), SaveDataId = new string('0', 15) + "1", Type = "account", FormatId = "fixture-opaque", FormatVersion = "1",
                                RelativePath = relativePath, Existing = false, AdapterVerified = false, Provenance = "synthetic mapping; not a native filesystem observation"
                            } },
                            limitations = new[] { "synthetic DTO; no emulation" }
                        };
                    }
                }
                else if (method == "content.inspect") result = new
                {
                    titleId = mode == "metadata-title" ? Title : null, format = "nro", kind = "homebrew", name = "Synthetic metadata; no filename inference", identityVerified = false,
                    nativeLoaderStatus = "fixture_metadata_only", issues = Enumerable.Repeat(new ContentIssue("fixture_metadata_only", "info", "Synthetic metadata; no native loader executed."), mode == "metadata-oversized" ? 257 : 1).ToArray(), entries = Array.Empty<object>()
                };
                else if (method == "input.devices" || method == "input.assign") result = new
                {
                    devices = new[] { new CoreInputDevice { Id = "fixture-controller", Name = "Synthetic device DTO", Type = "fixture", Connected = false } },
                    inputTarget, physicalObservation = "synthetic DTO; no physical input"
                };
                else if (method == "input.routing.set")
                {
                    inputTarget = p.GetProperty("target").GetString()!;
                    heldInput = false; requiresNeutral = inputTarget == "guest";
                    result = new { inputTarget, requiresNeutral };
                }
                else if (method == "input.update")
                {
                    var pressed = p.GetProperty("buttons").GetArrayLength() != 0 || new[] { "leftX", "leftY", "rightX", "rightY" }.Any(x => p.GetProperty(x).GetDouble() != 0);
                    if (inputTarget != "guest" || (requiresNeutral && pressed)) { await Failure(id, "invalid_state"); continue; }
                    requiresNeutral = false; heldInput = pressed; result = new { accepted = true };
                }
                else if (method is "window.get" or "window.set") result = new { width = 640, height = 360, visible = false, fullscreen = false };
                else { await Failure(id, "unsupported"); continue; }
                Console.WriteLine(JsonSerializer.Serialize(new { v = 1, id, ok = true, result }, JsonOptions));
                await Console.Out.FlushAsync();
                if (method == "dispose") return 0;
            }
            return 0;
        }
        finally { lease?.Dispose(); }
    }
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    private static async Task Failure(long id, string code)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { v = 1, id, ok = false, error = new { code, message = "Synthetic fixture refusal; no core operation." } }));
        await Console.Out.FlushAsync();
    }
}
